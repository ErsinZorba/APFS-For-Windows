using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Apfs.Crypto;

/// <summary>
/// AES-XTS-128 (IEEE 1619). APFS'te veri 512 baytlık birimler halinde şifrelenir; her birimin tweak'i
/// 16 baytlık little-endian sektör numarasıdır. Şifreleme yönü (<see cref="EncryptUnits"/>) yalnız şifreli birime yazma
/// oturumunda kullanılır. Hız için tüm birimler toplu işlenir: tweak'ler tek AES çağrısıyla şifrelenir, maske bir kerede kurulur ve veri tek ECB
/// çağrısıyla çözülür (birim başına çağrı yapan ilk sürüm gerçek diskte 92 MB/sn ile darboğazdı).
/// </summary>
public sealed class AesXts : IDisposable
{
    public const int UnitSize = 512;
    private const int BlocksPerUnit = UnitSize / 16;

    private readonly Aes _dataKey;
    private readonly Aes _tweakKey;
    private readonly Lock _lock = new();

    public AesXts(ReadOnlySpan<byte> key1, ReadOnlySpan<byte> key2)
    {
        if (key1.Length != 16 || key2.Length != 16)
            throw new ApfsException(ApfsErrorCode.Unsupported, "yalnızca AES-XTS-128 destekleniyor");
        _dataKey = Aes.Create();
        _dataKey.Key = key1.ToArray();
        _tweakKey = Aes.Create();
        _tweakKey.Key = key2.ToArray();
    }

    /// <summary>Birden çok ardışık 512 baytlık birimi çözer; ilk birimin sektör numarası <paramref name="firstSector"/>.</summary>
    public void DecryptUnits(Span<byte> data, ulong firstSector) => Process(data, firstSector, encrypt: false);

    /// <summary>Birden çok ardışık 512 baytlık birimi şifreler; ilk birimin sektör numarası <paramref name="firstSector"/>.</summary>
    public void EncryptUnits(Span<byte> data, ulong firstSector) => Process(data, firstSector, encrypt: true);

    private void Process(Span<byte> data, ulong firstSector, bool encrypt)
    {
        if (data.Length % UnitSize != 0)
            throw new ArgumentException("XTS verisi 512 baytın katı olmalı", nameof(data));
        if (data.IsEmpty) return;
        int units = data.Length / UnitSize;

        byte[] tweaks = ArrayPool<byte>.Shared.Rent(units * 16);
        byte[] mask = ArrayPool<byte>.Shared.Rent(data.Length);
        byte[] tmp = ArrayPool<byte>.Shared.Rent(data.Length);
        try
        {
            var tw = tweaks.AsSpan(0, units * 16);
            tw.Clear();
            for (int u = 0; u < units; u++)
                BinaryPrimitives.WriteUInt64LittleEndian(tw.Slice(u * 16, 8), firstSector + (ulong)u);

            var m = mask.AsSpan(0, data.Length);
            var t = tmp.AsSpan(0, data.Length);
            lock (_lock)
            {
                _tweakKey.EncryptEcb(tw, tw, PaddingMode.None);

                // Her birim için 32 blokluk maske: T, T·α, T·α², … (GF(2^128), little-endian, iki ulong ile)
                var m64 = MemoryMarshal.Cast<byte, ulong>(m);
                var tw64 = MemoryMarshal.Cast<byte, ulong>(tw);
                for (int u = 0; u < units; u++)
                {
                    ulong lo = tw64[u * 2], hi = tw64[u * 2 + 1];
                    int baseIdx = u * BlocksPerUnit * 2;
                    for (int b = 0; b < BlocksPerUnit; b++)
                    {
                        m64[baseIdx + b * 2] = lo;
                        m64[baseIdx + b * 2 + 1] = hi;
                        ulong carry = hi >> 63;
                        hi = (hi << 1) | (lo >> 63);
                        lo = (lo << 1) ^ (0x87UL & (0 - carry));
                    }
                }

                Xor(data, m, t);
                if (encrypt) _dataKey.EncryptEcb(t, t, PaddingMode.None);
                else _dataKey.DecryptEcb(t, t, PaddingMode.None);
                Xor(t, m, data);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tmp.AsSpan(0, data.Length)); // düz metin kopyası bellekte kalmasın
            ArrayPool<byte>.Shared.Return(tweaks);
            ArrayPool<byte>.Shared.Return(mask);
            ArrayPool<byte>.Shared.Return(tmp);
        }
    }

    /// <summary>dst = a XOR b (vektörlü).</summary>
    private static void Xor(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> dst)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var va = MemoryMarshal.Cast<byte, Vector<byte>>(a);
            var vb = MemoryMarshal.Cast<byte, Vector<byte>>(b);
            var vd = MemoryMarshal.Cast<byte, Vector<byte>>(dst);
            for (int k = 0; k < va.Length; k++) vd[k] = va[k] ^ vb[k];
            i = va.Length * Vector<byte>.Count;
        }
        for (; i < a.Length; i++) dst[i] = (byte)(a[i] ^ b[i]);
    }

    public void Dispose()
    {
        _dataKey.Dispose();
        _tweakKey.Dispose();
    }
}
