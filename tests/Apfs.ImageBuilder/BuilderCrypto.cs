using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Apfs.ImageBuilder;

/// <summary>
/// Test görüntüleri için APFS şifrelemesi (yalnızca ŞİFRELEME yönü; okuyucudaki şifre çözme kodundan bağımsız yazıldı).
/// AES-XTS-128 (512 baytlık birim, tweak = sektör), RFC 3394 anahtar sarma, KEK/VEK DER kayıtları ve HMAC.
/// Hepsi tools/gen_crypto_vectors.py'nin bağımsız (OpenSSL) vektörleriyle ayrıca doğrulanır.
/// </summary>
public static class BuilderCrypto
{
    public static void XtsEncrypt(byte[] key1, byte[] key2, Span<byte> data, ulong firstSector)
    {
        using var k1 = Aes.Create();
        k1.Key = key1;
        using var k2 = Aes.Create();
        k2.Key = key2;
        var t = new byte[16];
        var block = new byte[16];
        for (int u = 0; u < data.Length / 512; u++)
        {
            Array.Clear(t);
            BinaryPrimitives.WriteUInt64LittleEndian(t, firstSector + (ulong)u);
            k2.EncryptEcb(t, t, PaddingMode.None);
            for (int b = 0; b < 32; b++)
            {
                var span = data.Slice(u * 512 + b * 16, 16);
                for (int i = 0; i < 16; i++) block[i] = (byte)(span[i] ^ t[i]);
                k1.EncryptEcb(block, block, PaddingMode.None);
                for (int i = 0; i < 16; i++) span[i] = (byte)(block[i] ^ t[i]);
                // t ← t·α (GF(2^128), little-endian)
                byte carry = (byte)(t[15] >> 7);
                for (int i = 15; i > 0; i--) t[i] = (byte)((t[i] << 1) | (t[i - 1] >> 7));
                t[0] = (byte)((t[0] << 1) ^ (carry != 0 ? 0x87 : 0));
            }
        }
    }

    /// <summary>RFC 3394 AES anahtar sarma.</summary>
    public static byte[] Wrap(byte[] kek, byte[] key)
    {
        int n = key.Length / 8;
        ulong a = 0xA6A6A6A6A6A6A6A6;
        var r = (byte[])key.Clone();
        using var aes = Aes.Create();
        aes.Key = kek;
        var b = new byte[16];
        for (int j = 0; j <= 5; j++)
            for (int i = 1; i <= n; i++)
            {
                BinaryPrimitives.WriteUInt64BigEndian(b, a);
                Array.Copy(r, (i - 1) * 8, b, 8, 8);
                aes.EncryptEcb(b, b, PaddingMode.None);
                a = BinaryPrimitives.ReadUInt64BigEndian(b) ^ (ulong)(n * j + i);
                Array.Copy(b, 8, r, (i - 1) * 8, 8);
            }
        var result = new byte[8 + key.Length];
        BinaryPrimitives.WriteUInt64BigEndian(result, a);
        r.CopyTo(result, 8);
        return result;
    }

    /// <summary>KEK kaydı: parolayla (PBKDF2-SHA256) sarılmış KEK.</summary>
    public static byte[] KekBlob(Guid userUuid, byte[] kek, string password, byte[] salt16, int iterations, byte[] hmacSalt8)
    {
        var dk = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt16, iterations, HashAlgorithmName.SHA256, 32);
        var inner = Concat(
            Tlv(0x80, [0]),
            Tlv(0x81, Raw(userUuid)),
            Tlv(0x82, Info(userUuid)),
            Tlv(0x83, Wrap(dk, kek)),
            Tlv(0x84, BigEndianUInt((ulong)iterations)),
            Tlv(0x85, salt16));
        return Blob(inner, hmacSalt8);
    }

    /// <summary>VEK kaydı: KEK ile sarılmış VEK.</summary>
    public static byte[] VekBlob(Guid volumeUuid, byte[] vek, byte[] kek, byte[] hmacSalt8)
    {
        var inner = Concat(
            Tlv(0x80, [0]),
            Tlv(0x81, Raw(volumeUuid)),
            Tlv(0x82, Info(volumeUuid)),
            Tlv(0x83, Wrap(kek, vek)));
        return Blob(inner, hmacSalt8);
    }

    public static byte[] Raw(Guid g)
    {
        var b = new byte[16];
        g.TryWriteBytes(b, bigEndian: true, out _);
        return b;
    }

    /// <summary>Anahtar bilgisi: bayraklar (u32 LE; 0 = 256 bit), 2 bayt, uuid.</summary>
    private static byte[] Info(Guid g) => [0, 0, 0, 0, 0, 0, .. Raw(g)];

    /// <summary>SEQUENCE { [0] 0, [1] HMAC, [2] tuz, [3] iç kayıt }; HMAC anahtarı SHA256(çerez || tuz), veri [3] öğesinin tamamı.</summary>
    private static byte[] Blob(byte[] innerContent, byte[] hmacSalt8)
    {
        var a3 = Tlv(0xA3, innerContent);
        var hmacKey = SHA256.HashData([0x01, 0x16, 0x20, 0x17, 0x15, 0x05, .. hmacSalt8]);
        var hmac = HMACSHA256.HashData(hmacKey, a3);
        return Tlv(0x30, Concat(Tlv(0x80, [0]), Tlv(0x81, hmac), Tlv(0x82, hmacSalt8), a3));
    }

    private static byte[] BigEndianUInt(ulong v)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, v);
        int skip = 0;
        while (skip < 7 && b[skip] == 0) skip++;
        return b[skip..];
    }

    private static byte[] Tlv(byte tag, byte[] value)
    {
        byte[] len = value.Length < 0x80 ? [(byte)value.Length]
            : value.Length < 0x100 ? [0x81, (byte)value.Length]
            : [0x82, (byte)(value.Length >> 8), (byte)value.Length];
        return [tag, .. len, .. value];
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
