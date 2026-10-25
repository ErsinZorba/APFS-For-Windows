using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Apfs.Crypto;

/// <summary>RFC 3394 AES anahtar açma. Bütünlük değeri (0xA6A6A6A6A6A6A6A6) tutmazsa null döner: yanlış parola veya bozuk veri.</summary>
public static class AesKeyWrap
{
    private const ulong DefaultIv = 0xA6A6A6A6A6A6A6A6;

    public static byte[]? Unwrap(ReadOnlySpan<byte> kek, ReadOnlySpan<byte> wrapped)
    {
        if (kek.Length is not (16 or 24 or 32) || wrapped.Length < 24 || wrapped.Length % 8 != 0) return null;
        int n = wrapped.Length / 8 - 1;
        ulong a = BinaryPrimitives.ReadUInt64BigEndian(wrapped);
        var r = wrapped[8..].ToArray();
        using var aes = Aes.Create();
        aes.Key = kek.ToArray();
        Span<byte> b = stackalloc byte[16];
        for (int j = 5; j >= 0; j--)
        {
            for (int i = n; i >= 1; i--)
            {
                ulong t = (ulong)(n * j + i);
                BinaryPrimitives.WriteUInt64BigEndian(b, a ^ t);
                r.AsSpan((i - 1) * 8, 8).CopyTo(b[8..]);
                aes.DecryptEcb(b, b, PaddingMode.None);
                a = BinaryPrimitives.ReadUInt64BigEndian(b);
                b[8..].CopyTo(r.AsSpan((i - 1) * 8, 8));
            }
        }
        b.Clear();
        if (a != DefaultIv)
        {
            CryptographicOperations.ZeroMemory(r);
            return null;
        }
        return r;
    }
}
