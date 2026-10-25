using System.Security.Cryptography;
using Apfs.ImageBuilder;
using Apfs.Partitions;
using Apfs.Volume;

namespace Apfs.Tests;

/// <summary>Okuyucunun sonuçlarını, görüntüyü üreten tarafın "olması gereken" içeriğiyle birebir karşılaştırır.</summary>
public class ReaderTests(TestImages images) : IClassFixture<TestImages>
{
    [Theory]
    [InlineData("basic")]
    [InlineData("manyfiles")]
    [InlineData("sparse")]
    [InlineData("unicode")]
    [InlineData("unicode-cs")]
    public void EveryEntryMatchesGroundTruth(string scenario)
    {
        var (path, builder) = images.Get(scenario);
        using var c = TestImages.Open(path);
        var volumes = c.OpenVolumes();
        Assert.Equal(builder.Volumes.Count, volumes.Count);
        for (int i = 0; i < volumes.Count; i++)
        {
            var v = volumes[i].Volume ?? throw volumes[i].Error!;
            var spec = builder.Volumes[i];
            Assert.Equal(spec.Name, v.Name);
            Assert.Equal(spec.CaseInsensitive, v.IsCaseInsensitive);

            var expected = spec.Enumerate().ToList();
            var actualPaths = new List<string>();
            CollectPaths(v, v.Root, "", actualPaths);
            Assert.Equal(expected.Select(e => e.Path).Order(StringComparer.Ordinal), actualPaths.Order(StringComparer.Ordinal));

            foreach (var e in expected)
            {
                var ino = v.Lookup(e.Path);
                switch (e.Kind)
                {
                    case ExpectedKind.Directory:
                        Assert.True(ino.IsDirectory, e.Path);
                        break;
                    case ExpectedKind.Symlink:
                        Assert.True(ino.IsSymlink, e.Path);
                        Assert.Equal(e.SymlinkTarget, v.ReadSymlinkTarget(ino.Id));
                        break;
                    case ExpectedKind.File:
                        Assert.True(ino.IsRegularFile, e.Path);
                        Assert.Equal((ulong)e.Content!.Length, v.GetFileSize(ino));
                        Assert.Equal(e.Hidden, ino.IsHidden);
                        Assert.Equal(Sha(e.Content.ToArray()), Sha(ReadAll(v, ino)));
                        break;
                }
            }
        }
    }

    [Fact]
    public void RandomOffsetReadsMatchGroundTruth()
    {
        var (path, builder) = images.Get("basic");
        using var c = TestImages.Open(path);
        var v = c.OpenVolumes()[0].Volume!;
        var rnd = new Random(42);
        foreach (var e in builder.Volumes[0].Enumerate().Where(e => e.Kind == ExpectedKind.File && e.Content!.Length > 0))
        {
            var reader = v.OpenFile(v.Lookup(e.Path));
            var truth = e.Content!.ToArray();
            for (int k = 0; k < 50; k++)
            {
                int off = rnd.Next(truth.Length);
                int len = rnd.Next(1, 20_000);
                var buf = new byte[len];
                int n = reader.Read((ulong)off, buf);
                Assert.Equal(Math.Min(len, truth.Length - off), n);
                Assert.True(truth.AsSpan(off, n).SequenceEqual(buf.AsSpan(0, n)), $"{e.Path} @ {off}+{len}");
            }
            Assert.Equal(0, reader.Read((ulong)truth.Length, new byte[10]));
        }
    }

    [Fact]
    public void SparseHolesReadAsZeros()
    {
        var (path, _) = images.Get("sparse");
        using var c = TestImages.Open(path);
        var v = c.OpenVolumes()[0].Volume!;
        var data = ReadAll(v, v.Lookup("/örtük-boşluk.bin"));
        Assert.All(data.AsSpan(4096, 524_288 - 4096).ToArray(), b => Assert.Equal(0, b));
        Assert.Equal(0xBB, data[524_288]);
        Assert.All(data.AsSpan(528_384).ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void ManyFilesProducesMultiLevelTree()
    {
        var (path, _) = images.Get("manyfiles");
        using var c = TestImages.Open(path);
        var v = c.OpenVolumes()[0].Volume!;
        Assert.Equal(100, v.ListDirectory(v.Lookup("/klasör07").Id).Count());
        Assert.Equal("klasör 19 dosya 99\n", System.Text.Encoding.UTF8.GetString(ReadAll(v, v.Lookup("/klasör19/dosya099.txt"))));
    }

    [Fact]
    public void GptWrappedImageIsFound()
    {
        var (path, _) = images.Get("basic", gpt: true);
        using var dev = new Apfs.IO.FileImageDevice(path);
        var parts = PartitionLocator.FindApfsPartitions(dev);
        var p = Assert.Single(parts);
        Assert.Equal(40 * 512, p.Offset);
        using var c = TestImages.Open(path);
        Assert.Equal("Test Diski", c.OpenVolumes()[0].Volume!.Name);
    }

    [Fact]
    public void HardLinkSharesInode()
    {
        var (path, _) = images.Get("basic");
        using var c = TestImages.Open(path);
        var v = c.OpenVolumes()[0].Volume!;
        var a = v.Lookup("/Belgeler/rapor.txt");
        var b = v.Lookup("/Belgeler/rapor-kopya.txt");
        Assert.Equal(a.Id, b.Id);
        Assert.Equal(2, a.ChildrenOrLinks);
    }

    [Fact]
    public void MissingPathGivesNotFound()
    {
        var (path, _) = images.Get("basic");
        using var c = TestImages.Open(path);
        var v = c.OpenVolumes()[0].Volume!;
        var ex = Assert.Throws<ApfsException>(() => v.Lookup("/yok/böyle-bir-şey"));
        Assert.Equal(ApfsErrorCode.NotFound, ex.Code);
    }

    internal static byte[] ReadAll(ApfsVolume v, Inode ino)
    {
        var r = v.OpenFile(ino);
        var buf = new byte[r.Length];
        int n = r.Read(0, buf);
        Assert.Equal(buf.Length, n);
        return buf;
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

    private static void CollectPaths(ApfsVolume v, Inode dir, string prefix, List<string> acc)
    {
        foreach (var e in v.ListDirectory(dir.Id))
        {
            string p = prefix.Length == 0 ? e.Name : $"{prefix}/{e.Name}";
            acc.Add(p);
            var ino = v.GetInode(e.FileId);
            if (ino.IsDirectory) CollectPaths(v, ino, p, acc);
        }
    }
}
