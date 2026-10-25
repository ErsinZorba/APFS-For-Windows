using System.Diagnostics;
using Apfs.ImageBuilder;
using Apfs.Util;

namespace Apfs.Tests;

/// <summary>M2: 4 GB'ı aşan dosyalar, büyük klasörler ve önbellekler.</summary>
public class LargeAndCacheTests(TestImages images) : IClassFixture<TestImages>
{
    [Fact]
    public void ReadsAcrossFourGigabyteBoundaryMatchGroundTruth()
    {
        var (path, builder) = images.Get("large");
        using var c = TestImages.Open(path);
        var v = c.OpenVolumes()[0].Volume!;
        var ino = v.Lookup("/Videolar/tatil.mov");
        Assert.Equal((ulong)Scenarios.LargeVideoSize, ino.DataSize);
        Assert.True(v.GetExtents(ino.PrivateId).Length >= 7, "video birden çok parçaya bölünmüş olmalı");

        var truth = builder.Volumes[0].Enumerate().Single(e => e.Path == "Videolar/tatil.mov").Content!;
        var reader = v.OpenFile(ino);
        long[] offsets =
        [
            0, (1L << 20) - 10, (1L << 32) - 100, 1L << 32, (1L << 32) + (1 << 20) - 3,
            5L * (1 << 30) + 7, Scenarios.LargeVideoSize - 5000, Scenarios.LargeVideoSize - 1,
        ];
        foreach (long off in offsets)
        {
            var actual = new byte[10_000];
            int n = reader.Read((ulong)off, actual);
            Assert.Equal((int)Math.Min(10_000, Scenarios.LargeVideoSize - off), n);
            var expected = new byte[n];
            truth.Read(off, expected);
            Assert.True(expected.AsSpan().SequenceEqual(actual.AsSpan(0, n)), $"ofset {off}");
        }
    }

    [Fact]
    public void ExtentBoundariesAreReadCorrectly()
    {
        var (path, builder) = images.Get("large");
        using var c = TestImages.Open(path);
        var v = c.OpenVolumes()[0].Volume!;
        var ino = v.Lookup("/Videolar/tatil.mov");
        var truth = builder.Volumes[0].Enumerate().Single(e => e.Path == "Videolar/tatil.mov").Content!;
        var reader = v.OpenFile(ino);
        foreach (var ext in v.GetExtents(ino.PrivateId).Skip(1))
        {
            // Her parça sınırının iki yanını tek okumada kapsa
            long off = (long)ext.LogicalOffset - 3000;
            var actual = new byte[8000];
            reader.Read((ulong)off, actual);
            var expected = new byte[8000];
            truth.Read(off, expected);
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"parça sınırı {ext.LogicalOffset}");
        }
    }

    [Fact]
    public void BigDirectoryLookupsAreFastAndCorrect()
    {
        var (path, _) = images.Get("bigdir");
        using var c = TestImages.Open(path);
        var v = c.OpenVolumes()[0].Volume!;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 10_000; i++)
        {
            var ino = v.Lookup($"/Fotoğraflar/IMG_{i:D5}.jpg");
            Assert.Equal((ulong)$"foto {i}\n".Length, ino.DataSize);
        }
        sw.Stop();
        Assert.Equal(10_000, v.ListDirectory(v.Lookup("/Fotoğraflar").Id).Count);
        // Önbellek olmadan bu döngü O(n²) idi (dakikalar); şimdi saniyenin altında olmalı. Gevşek sınır: yavaş makinelerde de geçsin.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"10.000 arama {sw.Elapsed.TotalSeconds:0.0} sn sürdü");
    }

    [Fact]
    public void LruCacheEvictsByWeight()
    {
        var cache = new LruCache<int, int[]>(10, a => a.Length);
        cache.Add(1, new int[4]);
        cache.Add(2, new int[4]);
        Assert.True(cache.TryGet(1, out _)); // 1 en yeni kullanılan olur
        cache.Add(3, new int[4]);             // toplam 12 > 10: en eski kullanılan (2) atılmalı
        Assert.True(cache.TryGet(1, out _));
        Assert.False(cache.TryGet(2, out _));
        Assert.True(cache.TryGet(3, out _));
        cache.Add(4, new int[11]);            // tek başına sınırı aşan değer önbelleğe alınmaz
        Assert.False(cache.TryGet(4, out _));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void ConcurrentReadsReturnSameData()
    {
        var (path, builder) = images.Get("large");
        using var c = TestImages.Open(path);
        var v = c.OpenVolumes()[0].Volume!;
        var truth = builder.Volumes[0].Enumerate().Single(e => e.Path == "orta.bin").Content!;
        var reader = v.OpenFile(v.Lookup("/orta.bin"));
        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            long off = (long)i * 4_000_003 % (truth.Length - 70_000);
            var a = new byte[65_536];
            reader.Read((ulong)off, a);
            var e = new byte[65_536];
            truth.Read(off, e);
            Assert.True(e.AsSpan().SequenceEqual(a), $"ofset {off}");
        });
    }
}
