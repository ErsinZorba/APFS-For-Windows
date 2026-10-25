using System.Security.Cryptography;
using Apfs.Crypto;

namespace Apfs.Tests;

/// <summary>
/// M6: AES-XTS, RFC 3394 anahtar açma ve PBKDF2; bağımsız vektörlerle (tools/gen_crypto_vectors.py, OpenSSL) ve
/// standartların kendi örnekleriyle doğrulanır.
/// </summary>
public class CryptoPrimitiveTests
{
    private static readonly string VectorsFile = Path.Combine(AppContext.BaseDirectory, "data", "crypto", "vectors.txt");

    private static IEnumerable<string[]> Lines(string kind) =>
        File.ReadAllLines(VectorsFile).Where(l => l.StartsWith(kind + " ")).Select(l => l.Split(' '));

    [Fact]
    public void XtsMatchesOpenSsl()
    {
        int n = 0;
        foreach (var p in Lines("xts"))
        {
            var k1 = Convert.FromHexString(p[1]);
            var k2 = Convert.FromHexString(p[2]);
            ulong sector = ulong.Parse(p[3]);
            var plain = Convert.FromHexString(p[4]);
            var data = Convert.FromHexString(p[5]);
            using var xts = new AesXts(k1, k2);
            xts.DecryptUnits(data, sector);
            Assert.Equal(Convert.ToHexString(plain), Convert.ToHexString(data));
            n++;
        }
        Assert.True(n >= 10);
    }

    [Fact]
    public void XtsEncryptionMatchesOpenSslAndRoundTrips()
    {
        int n = 0;
        foreach (var p in Lines("xts"))
        {
            var k1 = Convert.FromHexString(p[1]);
            var k2 = Convert.FromHexString(p[2]);
            ulong sector = ulong.Parse(p[3]);
            var data = Convert.FromHexString(p[4]);
            using var xts = new AesXts(k1, k2);
            xts.EncryptUnits(data, sector);
            Assert.Equal(p[5], Convert.ToHexString(data), ignoreCase: true);
            n++;
        }
        Assert.True(n >= 10);

        // Çok birimli gidiş-dönüş (tweak'ler birim başına artar)
        var rnd = new Random(1619);
        var plain = new byte[512 * 37];
        rnd.NextBytes(plain);
        var buf = (byte[])plain.Clone();
        using var x = new AesXts(RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(16));
        x.EncryptUnits(buf, 123_456);
        Assert.False(buf.AsSpan().SequenceEqual(plain));
        x.DecryptUnits(buf.AsSpan(512 * 5), 123_456 + 5); // ortadan başlayan çözme de aynı tweak'leri kullanır
        x.DecryptUnits(buf.AsSpan(0, 512 * 5), 123_456);
        Assert.True(buf.AsSpan().SequenceEqual(plain));
    }

    [Fact]
    public void XtsIeee1619Vector1()
    {
        // IEEE 1619-2007 Ek B, vektör 1: anahtarlar sıfır, sektör 0, 32 bayt sıfır
        // XTS her 16 baytlık bloğu yalnızca kendi tweak'iyle şifreler; 512 baytlık birimin ilk 32 baytı standarttaki sonucu verir.
        var unit = new byte[512];
        Convert.FromHexString("917CF69EBD68B2EC9B9FE9A3EADDA692CD43D2F59598ED858C02C2652FBF922E").CopyTo(unit, 0);
        using var xts = new AesXts(new byte[16], new byte[16]);
        xts.DecryptUnits(unit, 0);
        Assert.Equal(new byte[32], unit.AsSpan(0, 32).ToArray());
    }

    [Fact]
    public void XtsThroughputIsNotTheBottleneck()
    {
        // Gerçek USB HDD'de 72 MB/sn ölçüldü; şifre çözme bunun çok üstünde olmalı ki darboğaz olmasın.
        using var xts = new AesXts(RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(16));
        var data = new byte[32 << 20];
        xts.DecryptUnits(data, 0); // ısınma
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 4; i++) xts.DecryptUnits(data, (ulong)i * 65536);
        double mbps = 128 / sw.Elapsed.TotalSeconds;
        Assert.True(mbps > 300, $"AES-XTS yalnızca {mbps:0} MB/sn");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "apfs-xts-speed.txt"), $"{mbps:0}");
    }

    [Fact]
    public void KeyUnwrapMatchesOpenSslAndRejectsWrongKey()
    {
        int n = 0;
        foreach (var p in Lines("wrap"))
        {
            var kek = Convert.FromHexString(p[1]);
            var key = Convert.FromHexString(p[2]);
            var wrapped = Convert.FromHexString(p[3]);
            Assert.Equal(Convert.ToHexString(key), Convert.ToHexString(AesKeyWrap.Unwrap(kek, wrapped)!));
            var wrongKek = (byte[])kek.Clone();
            wrongKek[0] ^= 1;
            Assert.Null(AesKeyWrap.Unwrap(wrongKek, wrapped)); // yanlış parola durumu
            n++;
        }
        Assert.True(n >= 10);
    }

    [Fact]
    public void KeyUnwrapRfc3394Section41()
    {
        var kek = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");
        var wrapped = Convert.FromHexString("1FA68B0A8112B447AEF34BD8FB5A7B829D3E862371D2CFE5");
        Assert.Equal("00112233445566778899AABBCCDDEEFF", Convert.ToHexString(AesKeyWrap.Unwrap(kek, wrapped)!));
    }

    [Fact]
    public void Pbkdf2MatchesPythonIncludingTurkishPassword()
    {
        int n = 0;
        foreach (var p in Lines("pbkdf2"))
        {
            var dk = Rfc2898DeriveBytes.Pbkdf2(Convert.FromHexString(p[1]), Convert.FromHexString(p[2]), int.Parse(p[3]), HashAlgorithmName.SHA256, 32);
            Assert.Equal(p[4].ToUpperInvariant(), Convert.ToHexString(dk));
            n++;
        }
        Assert.True(n >= 4);
    }
}
