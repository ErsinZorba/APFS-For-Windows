using System.Security.Cryptography;
using Apfs.Container;
using Apfs.Util;

namespace Apfs.Crypto;

/// <summary>Keybag girdisi (keybag_entry_t).</summary>
internal readonly record struct KeybagEntry(Guid Uuid, ushort Tag, byte[] Data);

/// <summary>
/// APFS yazılım şifrelemesinin anahtar zinciri (Apple File System Reference, "Encryption"):
/// kapsayıcı keybag'i (nx_keylocker; kapsayıcı UUID'siyle AES-XTS) → birimin VEK kaydı ve birim keybag'inin yeri;
/// birim keybag'i (birim UUID'siyle AES-XTS) → kullanıcı/kurtarma anahtarı KEK kayıtları ve parola ipucu.
/// Parola → PBKDF2-HMAC-SHA256 → KEK'i açar (RFC 3394) → KEK, VEK'i açar → VEK birimi çözer (AES-XTS-128).
/// </summary>
public static class VolumeKeys
{
    public const ushort TagVolumeKey = 2;
    public const ushort TagVolumeUnlockRecords = 3;
    public const ushort TagPassphraseHint = 4;

    private const uint ContainerKeybagType = 0x6B657973; // 'keys'
    private const uint VolumeKeybagType = 0x72656373;    // 'recs'
    private const ulong MaxKeybagBlocks = 16;

    /// <summary>Birimin parola ipucu; yoksa veya okunamazsa null.</summary>
    public static string? GetPasswordHint(ApfsContainer container, Guid volumeUuid)
    {
        try
        {
            var recs = LoadVolumeKeybag(container, volumeUuid);
            var hint = recs.FirstOrDefault(e => e.Tag == TagPassphraseHint && e.Uuid == volumeUuid);
            return hint.Data is null ? null : Bin.Utf8Z(hint.Data);
        }
        catch (ApfsException)
        {
            return null;
        }
    }

    /// <summary>Parolayla birimin VEK'ini (32 bayt: XTS anahtar1 + anahtar2) açar. Yanlış parolada WrongPassword hatası.</summary>
    public static byte[] Unlock(ApfsContainer container, Guid volumeUuid, string password)
    {
        var bag = LoadContainerKeybag(container);
        var vekEntry = bag.FirstOrDefault(e => e.Tag == TagVolumeKey && e.Uuid == volumeUuid);
        if (vekEntry.Data is null)
            throw new ApfsException(ApfsErrorCode.Unsupported, CoreText.T("Crypto.VolumeKeyNotInContainer"));
        var recs = LoadVolumeKeybag(container, volumeUuid);

        var pw = System.Text.Encoding.UTF8.GetBytes(password);
        try
        {
            foreach (var rec in recs.Where(e => e.Tag == TagVolumeUnlockRecords))
            {
                var kek = TryUnwrapKek(rec.Data, pw);
                if (kek is null) continue;
                try
                {
                    var vek = UnwrapVek(vekEntry.Data, kek);
                    if (vek is not null) return vek;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(kek);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pw);
        }
        string? hint = recs.FirstOrDefault(e => e.Tag == TagPassphraseHint && e.Uuid == volumeUuid).Data is { } h ? Bin.Utf8Z(h) : null;
        throw new ApfsException(ApfsErrorCode.WrongPassword, hint is null ? null : CoreText.T("Crypto.PasswordHint", hint));
    }

    // ---- Keybag'ler ----

    internal static List<KeybagEntry> LoadContainerKeybag(ApfsContainer container)
    {
        var sb = container.Superblock;
        if (sb.KeylockerStart == 0 || sb.KeylockerBlockCount == 0)
            throw new ApfsException(ApfsErrorCode.Unsupported, CoreText.T("Crypto.NoKeybag"));
        return LoadKeybag(container, sb.KeylockerStart, sb.KeylockerBlockCount, sb.Uuid, ContainerKeybagType);
    }

    internal static List<KeybagEntry> LoadVolumeKeybag(ApfsContainer container, Guid volumeUuid)
    {
        var bag = LoadContainerKeybag(container);
        var loc = bag.FirstOrDefault(e => e.Tag == TagVolumeUnlockRecords && e.Uuid == volumeUuid);
        if (loc.Data is null || loc.Data.Length < 16)
            throw new ApfsException(ApfsErrorCode.Unsupported, CoreText.T("Crypto.NoUnlockRecords"));
        return LoadKeybag(container, Bin.U64(loc.Data, 0), Bin.U64(loc.Data, 8), volumeUuid, VolumeKeybagType);
    }

    /// <summary>Keybag bloklarını okur; şifreliyse (uuid, uuid) anahtarlarıyla AES-XTS çözer, sağlama ve türü doğrular.</summary>
    private static List<KeybagEntry> LoadKeybag(ApfsContainer container, ulong start, ulong count, Guid uuid, uint type)
    {
        if (count == 0 || count > MaxKeybagBlocks || start >= container.Reader.BlockCount || start + count > container.Reader.BlockCount)
            throw ApfsException.Corrupt(CoreText.T("Crypto.KeybagLocationInvalid"));
        uint bs = container.BlockSize;
        var data = new byte[count * bs];
        for (ulong i = 0; i < count; i++)
            container.Reader.ReadBlock(start + i).CopyTo(data, (int)(i * bs));

        if (Bin.U32(data, 24) != type)
        {
            var key = uuid.ToByteArray(bigEndian: true);
            using var xts = new AesXts(key, key);
            xts.DecryptUnits(data, start * (bs / AesXts.UnitSize));
        }
        for (ulong i = 0; i < count; i++)
            if (!Fletcher64.Verify(data.AsSpan((int)(i * bs), (int)bs)))
                throw new ApfsException(ApfsErrorCode.ChecksumMismatch, CoreText.T("Crypto.KeybagCorrupt"));
        if (Bin.U32(data, 24) != type)
            throw ApfsException.Corrupt(CoreText.T("Crypto.KeybagUnexpectedType"));

        // kb_locker_t: sürüm, girdi sayısı, bayt sayısı, 8 bayt dolgu; girdiler 16 bayta hizalı
        ushort nkeys = Bin.U16(data, 34);
        var entries = new List<KeybagEntry>(nkeys);
        int pos = 48;
        for (int k = 0; k < nkeys; k++)
        {
            var id = Bin.Uuid(data, pos);
            ushort tag = Bin.U16(data, pos + 16);
            ushort len = Bin.U16(data, pos + 18);
            entries.Add(new KeybagEntry(id, tag, Bin.Slice(data, pos + 24, len).ToArray()));
            pos += (24 + len + 15) & ~15;
        }
        return entries;
    }

    // ---- KEK / VEK kayıtları (DER) ----

    private readonly record struct BlobHeader(byte[] Uuid, uint Flags);

    /// <summary>Ortak başlık: SEQUENCE { [0] sürüm, [1] HMAC, [2] tuz, [3] { [0], [1] uuid, [2] bilgi(bayraklar), ... } }.</summary>
    private static BlobHeader ParseHeader(ref Der inner)
    {
        inner.Read(0x80);
        var uuid = inner.Read(0x81).ToArray();
        var info = inner.Read(0x82);
        uint flags = info.Length >= 4 ? (uint)(info[0] | info[1] << 8 | info[2] << 16 | info[3] << 24) : 0;
        return new BlobHeader(uuid, flags);
    }

    private static ReadOnlySpan<byte> OpenBlob(ReadOnlySpan<byte> blob)
    {
        var outer = new Der(blob);
        var seq = new Der(outer.Read(0x30));
        seq.Read(0x80);
        seq.Read(0x81); // HMAC (doğrulanmaz: Apple'ın bazı kayıtlarında tutmadığı biliniyor; anahtar bütünlüğü RFC 3394 ile denetlenir)
        seq.Read(0x82);
        return seq.Read(0xA3);
    }

    private static byte[]? TryUnwrapKek(byte[] blob, byte[] password)
    {
        var inner = new Der(OpenBlob(blob));
        var hdr = ParseHeader(ref inner);
        var wrapped = inner.Read(0x83);
        ulong iterations = Der.ToUInt(inner.Read(0x84));
        var salt = inner.Read(0x85);
        if (iterations is 0 or > 100_000_000) throw ApfsException.Corrupt(CoreText.T("Crypto.KekIterationsInvalid"));

        bool aes128 = (hdr.Flags & 2) != 0;
        var dk = Rfc2898DeriveBytes.Pbkdf2(password, salt, (int)iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            return aes128
                ? AesKeyWrap.Unwrap(dk.AsSpan(0, 16), wrapped[..Math.Min(24, wrapped.Length)])
                : AesKeyWrap.Unwrap(dk, wrapped);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dk);
        }
    }

    private static byte[]? UnwrapVek(byte[] blob, byte[] kek)
    {
        var inner = new Der(OpenBlob(blob));
        var hdr = ParseHeader(ref inner);
        var wrapped = inner.Read(0x83);
        if ((hdr.Flags & 2) != 0)
        {
            // 128 bit VEK: ikinci yarı SHA256(VEK || kayıttaki uuid)'nin ilk 16 baytı
            var vek16 = AesKeyWrap.Unwrap(kek, wrapped[..Math.Min(24, wrapped.Length)]);
            if (vek16 is null) return null;
            var full = new byte[32];
            vek16.CopyTo(full, 0);
            var hash = SHA256.HashData([.. vek16, .. hdr.Uuid]);
            hash.AsSpan(0, 16).CopyTo(full.AsSpan(16));
            CryptographicOperations.ZeroMemory(vek16);
            return full;
        }
        return AesKeyWrap.Unwrap(kek, wrapped);
    }
}

/// <summary>
/// Kilidi açılmış bir birimin anahtarı (VEK). Parola yeniden sorulmadan birimi yeniden açmak (yazmaya açma/kapama sonrası) için
/// bellekte tutulan kopya; <see cref="Dispose"/> baytları sıfırlar. Günlüğe veya diske yazılmaz.
/// </summary>
public sealed class VolumeKey : IDisposable
{
    private readonly byte[] _vek;
    private bool _disposed;

    private VolumeKey(Guid volumeUuid, byte[] vek)
    {
        if (vek.Length != 32) throw new ArgumentException(CoreText.T("Crypto.VekLength"), nameof(vek));
        VolumeUuid = volumeUuid;
        _vek = vek;
    }

    /// <summary>Anahtarın ait olduğu birim.</summary>
    public Guid VolumeUuid { get; }

    /// <summary>Parolayla birimin anahtarını açar (yanlış parolada WrongPassword hatası, ipucuyla).</summary>
    public static VolumeKey Unlock(ApfsContainer container, Guid volumeUuid, string password) =>
        new(volumeUuid, VolumeKeys.Unlock(container, volumeUuid, password));

    /// <summary>Testler ve araçlar için: verilen VEK baytlarının kopyasıyla anahtar.</summary>
    internal static VolumeKey FromBytes(Guid volumeUuid, ReadOnlySpan<byte> vek) => new(volumeUuid, vek.ToArray());

    /// <summary>Bağımsız kopya (ayrı ayrı bırakılabilir).</summary>
    public VolumeKey Clone()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new VolumeKey(VolumeUuid, (byte[])_vek.Clone());
    }

    /// <summary>Bu anahtarla şifreleyici/çözücü (kendi VEK kopyasını alır).</summary>
    internal VolumeCrypto CreateCrypto(uint blockSize)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new VolumeCrypto((byte[])_vek.Clone(), blockSize);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_vek);
    }
}

/// <summary>Kilidi açılmış bir birimin şifre çözücüsü (VEK ile AES-XTS-128); yazma oturumunda şifreleyicisi.</summary>
public sealed class VolumeCrypto : IDisposable
{
    private readonly AesXts _xts;
    private readonly uint _unitsPerBlock;

    public VolumeCrypto(byte[] vek, uint blockSize)
    {
        _xts = new AesXts(vek.AsSpan(0, 16), vek.AsSpan(16, 16));
        _unitsPerBlock = blockSize / AesXts.UnitSize;
        CryptographicOperations.ZeroMemory(vek);
    }

    /// <summary>Metadata bloğu (fs-tree düğümü): tweak = fiziksel blok × (blok / 512) + birim.</summary>
    public void DecryptBlock(Span<byte> block, ulong paddr) => _xts.DecryptUnits(block, paddr * _unitsPerBlock);

    /// <summary>
    /// Dosya verisi: tweak = crypto_id × (blok / 512) + extent başından itibaren 512 baytlık birim sırası.
    /// <paramref name="data"/> extent içinde <paramref name="unitIndex"/>. birimden başlar ve 512'nin katıdır.
    /// </summary>
    public void DecryptData(Span<byte> data, ulong cryptoId, ulong unitIndex) => _xts.DecryptUnits(data, cryptoId * _unitsPerBlock + unitIndex);

    /// <summary>Metadata bloğunu (fs ağacı düğümü; sağlaması düz metin üzerinden alınmış) şifreler: tweak = fiziksel blok × (blok / 512) + birim.</summary>
    public void EncryptBlock(Span<byte> block, ulong paddr) => _xts.EncryptUnits(block, paddr * _unitsPerBlock);

    /// <summary>Dosya verisini şifreler: tweak = crypto_id × (blok / 512) + extent başından itibaren 512 baytlık birim sırası.</summary>
    public void EncryptData(Span<byte> data, ulong cryptoId, ulong unitIndex) => _xts.EncryptUnits(data, cryptoId * _unitsPerBlock + unitIndex);

    public void Dispose() => _xts.Dispose();
}
