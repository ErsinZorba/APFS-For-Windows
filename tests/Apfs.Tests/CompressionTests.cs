using System.Security.Cryptography;
using Apfs.Compression;

namespace Apfs.Tests;

/// <summary>
/// M4: LZFSE ve LZVN çözücüleri, Apple'ın referans kodlayıcısıyla üretilmiş vektörlerle doğrulanır
/// (tools/gen_lzfse_vectors.py). Çözülen verinin boyutu ve SHA-256'sı orijinaliyle aynı olmalıdır.
/// </summary>
public class CompressionTests
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "data", "compression");

    public static IEnumerable<object[]> Vectors() =>
        File.ReadAllLines(Path.Combine(Dir, "manifest.txt"))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split(' '))
            .Select(p => new object[] { p[0], int.Parse(p[1]), p[2] });

    [Theory]
    [MemberData(nameof(Vectors))]
    public void LzfseDecodesAppleEncoderOutput(string name, int size, string sha256)
    {
        var comp = File.ReadAllBytes(Path.Combine(Dir, name + ".lzfse"));
        var output = new byte[size];
        int n = Lzfse.Decode(comp, output);
        Assert.Equal(size, n);
        Assert.Equal(sha256, Convert.ToHexString(SHA256.HashData(output)));
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void LzvnDecodesAppleEncoderOutput(string name, int size, string sha256)
    {
        string path = Path.Combine(Dir, name + ".lzvn");
        if (!File.Exists(path)) return; // yalnızca küçük girdilerin LZVN biçimi var
        var output = new byte[size];
        int n = Lzvn.Decode(File.ReadAllBytes(path), output);
        Assert.Equal(size, n);
        Assert.Equal(sha256, Convert.ToHexString(SHA256.HashData(output)));
    }

    [Fact]
    public void LzfseOutputLargerThanExpectedIsRejected()
    {
        var comp = File.ReadAllBytes(Path.Combine(Dir, "text-64k.lzfse"));
        var ex = Assert.Throws<ApfsException>(() => Lzfse.Decode(comp, new byte[1000]));
        Assert.Equal(ApfsErrorCode.Corrupt, ex.Code);
    }

    [Fact]
    public void CorruptedStreamsNeverCrashOrHang()
    {
        // Her vektörü rastgele bozup çözücüye ver: ya düzgün çıktı ya ApfsException; başka istisna veya donma olmamalı.
        var rnd = new Random(1234);
        var files = Directory.GetFiles(Dir, "*.lzfse").Concat(Directory.GetFiles(Dir, "*.lzvn")).ToArray();
        int trials = 0;
        foreach (var f in files)
        {
            var orig = File.ReadAllBytes(f);
            if (orig.Length < 8) continue;
            for (int t = 0; t < 150; t++)
            {
                var b = (byte[])orig.Clone();
                int flips = rnd.Next(1, 6);
                for (int k = 0; k < flips; k++) b[rnd.Next(b.Length)] ^= (byte)rnd.Next(1, 256);
                if (rnd.Next(4) == 0) b = b.AsSpan(0, rnd.Next(b.Length)).ToArray(); // kesik akış
                var output = new byte[1 << 20];
                try
                {
                    if (f.EndsWith(".lzvn")) Lzvn.Decode(b, output.AsSpan(0, 5000));
                    else Lzfse.Decode(b, output);
                }
                catch (ApfsException) { }
                trials++;
            }
        }
        Assert.True(trials > 1000);
    }
}
