using System.Security.Cryptography;
using Apfs.ImageBuilder;

namespace Apfs.Tests;

/// <summary>M4: decmpfs ile sıkıştırılmış dosyalar (öznitelik içi ve kaynak çatalı; zlib, LZVN, LZFSE, kopya).</summary>
public class CompressedFileTests(TestImages images) : IClassFixture<TestImages>
{
    [Theory]
    [InlineData("compressed")]
    [InlineData("compressed-lzfse")]
    public void CompressedFilesMatchGroundTruth(string scenario)
    {
        var (path, builder) = images.Get(scenario);
        using var c = TestImages.Open(path);
        var v = c.OpenVolumes()[0].Volume!;
        int checkedFiles = 0;
        foreach (var e in builder.Volumes[0].Enumerate().Where(e => e.Kind == ExpectedKind.File))
        {
            if (e.Path == "desteklenmeyen.bin") continue;
            var ino = v.Lookup(e.Path);
            var truth = e.Content!.ToArray();
            Assert.Equal((ulong)truth.Length, v.GetFileSize(ino));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(truth)), Convert.ToHexString(SHA256.HashData(ReaderTests.ReadAll(v, ino))));
            checkedFiles++;
        }
        Assert.True(checkedFiles >= 3);
    }

    [Theory]
    [InlineData("compressed", "/zlib-büyük.txt")]
    [InlineData("compressed", "/lzvn-karışık.bin")]
    [InlineData("compressed-lzfse", "/lzfse-büyük.txt")]
    [InlineData("compressed-lzfse", "/kopya-çatal.bin")]
    public void RandomReadsAcrossBlockBoundaries(string scenario, string file)
    {
        var (path, builder) = images.Get(scenario);
        using var c = TestImages.Open(path);
        var v = c.OpenVolumes()[0].Volume!;
        var truth = builder.Volumes[0].Enumerate().Single(e => "/" + e.Path == file).Content!.ToArray();
        var reader = v.OpenFile(v.Lookup(file));
        var rnd = new Random(file.Length);
        var offsets = new List<int> { 0, 65536 - 10, 65536, 131072 - 1, truth.Length - 7 };
        for (int i = 0; i < 40; i++) offsets.Add(rnd.Next(truth.Length));
        foreach (int off in offsets.Where(o => o >= 0 && o < truth.Length))
        {
            var buf = new byte[rnd.Next(1, 150_000)];
            int n = reader.Read((ulong)off, buf);
            Assert.Equal(Math.Min(buf.Length, truth.Length - off), n);
            Assert.True(truth.AsSpan(off, n).SequenceEqual(buf.AsSpan(0, n)), $"{file} @ {off}");
        }
    }

    [Fact]
    public void UnsupportedCompressionIsListedButReportsClearError()
    {
        var (path, _) = images.Get("compressed-lzfse");
        using var c = TestImages.Open(path);
        var v = c.OpenVolumes()[0].Volume!;
        var ino = v.Lookup("/desteklenmeyen.bin");
        Assert.True(ino.IsCompressed);
        Assert.Equal(5000UL, v.GetFileSize(ino)); // listede doğru boyutla görünür
        var ex = Assert.Throws<ApfsException>(() => v.OpenFile(ino));
        Assert.Equal(ApfsErrorCode.Unsupported, ex.Code);
        Assert.Contains("desteklenmeyen bir sıkıştırma türü", ex.Message);
        Assert.Contains("LZBITMAP", ex.Message);
    }

    [Fact]
    public void CompressedFilesAreFlaggedAndUncompressedSizeComesFromInode()
    {
        var (path, _) = images.Get("compressed");
        using var c = TestImages.Open(path);
        var v = c.OpenVolumes()[0].Volume!;
        var ino = v.Lookup("/zlib-büyük.txt");
        Assert.True(ino.IsCompressed);
        Assert.True(ino.HasUncompressedSize);
        Assert.False(ino.HasDataStream); // veri fork'u boş, içerik kaynak çatalında
        Assert.False(v.Lookup("/düz.txt").IsCompressed);
    }
}
