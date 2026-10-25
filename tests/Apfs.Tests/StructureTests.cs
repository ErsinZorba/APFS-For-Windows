using System.Buffers.Binary;
using Apfs.Container;
using Apfs.Diagnostics;
using Apfs.Util;

namespace Apfs.Tests;

/// <summary>
/// Faz 0.3: yazıcının dayanacağı iç yapıların (checkpoint, alan yöneticisi, boş kuyruklar, reaper) okunması. Gerçek boyutlu Linux
/// görüntülerinde sayaçlar birbirini tutmalı; bilerek bozulmuş kopyalarda tutarsızlık yakalanmalı.
/// </summary>
public class StructureTests(LinuxFixtures fixtures) : IClassFixture<LinuxFixtures>
{
    public static TheoryData<string> AllFixtures() => [.. LinuxFixtures.Names];

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void CheckpointIsCompleteAndSpacemanCountersAgree(string name)
    {
        using var c = TestImages.Open(fixtures.Get(name));
        var cp = Checkpoint.Read(c);
        Assert.True(cp.IsValid, string.Join("; ", cp.Problems));
        Assert.NotNull(cp.Find(c.Superblock.SpacemanOid));
        Assert.NotNull(cp.Find(c.Superblock.ReaperOid));

        var sm = Spaceman.Read(c, cp);
        Assert.Equal(c.Superblock.BlockCount, sm.Main.BlockCount);
        var chunks = sm.MainChunks();
        Assert.Equal(sm.Main.ChunkCount, (ulong)chunks.Count);
        Assert.Equal(sm.Main.FreeCount, sm.CountFreeFromBitmaps());
        Assert.Equal(sm.Main.FreeCount, chunks.Aggregate(0UL, (s, ch) => s + ch.FreeCount));
        // Alan yöneticisinin sayacı, okuyucunun gösterdiği boş alanla aynı olmalı
        Assert.Equal((long)sm.Main.FreeCount * c.BlockSize, c.FreeBytes);
        // Boş kuyruklar sayaçlarıyla tutarlı
        for (int q = 0; q < 3; q++)
            Assert.Equal(sm.FreeQueues[q].Count, sm.ReadFreeQueue(q).Aggregate(0UL, (s, e) => s + e.Length));
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void DumpReportsNoProblems(string name)
    {
        using var c = TestImages.Open(fixtures.Get(name));
        var lines = ContainerDump.Lines(c).ToList();
        Assert.DoesNotContain(lines, l => l.Contains("SORUN:", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("durum: boşta", StringComparison.Ordinal));
    }

    [Fact]
    public void MultiChunkFixtureHasSeveralChunks()
    {
        using var c = TestImages.Open(fixtures.Get("mk-ci-1g"));
        var sm = Spaceman.Read(c, Checkpoint.Read(c));
        Assert.Equal(8, sm.MainChunks().Count);
    }

    [Fact]
    public void FlippedBitmapBitIsDetected()
    {
        string path = fixtures.Copy("mk-ci-64m");
        ulong bitmapAddr;
        using (var c = TestImages.Open(path))
        {
            var sm = Spaceman.Read(c, Checkpoint.Read(c));
            bitmapAddr = sm.MainChunks().First(ch => ch.BitmapAddr != 0).BitmapAddr;
        }
        // Bitmap bloklarında başlık yok: sağlama toplamı da yok, bu yüzden yalnızca sayaç karşılaştırması yakalar
        Patch(path, (long)bitmapAddr * 4096 + 1000, b => b[0] ^= 0x01, recomputeChecksumOfBlock: false);
        using var c2 = TestImages.Open(path);
        Assert.Contains(ContainerDump.Lines(c2), l => l.Contains("SORUN: boş blok sayaçları", StringComparison.Ordinal));
    }

    [Fact]
    public void EphemeralObjectFromAnotherTransactionIsDetected()
    {
        string path = fixtures.Copy("mk-ci-64m");
        ulong smPaddr;
        using (var c = TestImages.Open(path))
            smPaddr = Checkpoint.Read(c).Find(c.Superblock.SpacemanOid)!.Value.Paddr;
        // Alan yöneticisinin xid'ini değiştir ve sağlama toplamını yeniden hesapla: nesne sağlam ama başka bir işleme ait
        Patch(path, (long)smPaddr * 4096 + 16, b => BinaryPrimitives.WriteUInt64LittleEndian(b, BinaryPrimitives.ReadUInt64LittleEndian(b) + 5),
            recomputeChecksumOfBlock: true, blockStart: (long)smPaddr * 4096);
        using var c2 = TestImages.Open(path);
        var cp = Checkpoint.Read(c2);
        Assert.Contains(cp.Problems, p => p.Contains("son işlemin parçası değil", StringComparison.Ordinal));
    }

    [Fact]
    public void CheckpointMappingOutOfOrderIsDetected()
    {
        string path = fixtures.Copy("mk-ci-64m");
        ulong mapPaddr;
        using (var c = TestImages.Open(path))
            mapPaddr = Checkpoint.Read(c).MapBlocks[0];
        // İlk eşlemenin fiziksel adresini bir blok kaydır (sağlama toplamı yeniden hesaplanır)
        long entry0Paddr = (long)mapPaddr * 4096 + 40 + 32;
        Patch(path, entry0Paddr, b => BinaryPrimitives.WriteUInt64LittleEndian(b, BinaryPrimitives.ReadUInt64LittleEndian(b) + 1),
            recomputeChecksumOfBlock: true, blockStart: (long)mapPaddr * 4096);
        using var c2 = TestImages.Open(path);
        Assert.False(Checkpoint.Read(c2).IsValid);
    }

    [Fact]
    public void BuilderImagesStillOpenForReading()
    {
        // Builder görüntüsünün alan yöneticisi eksiktir (bilinçli); yeni yapı okuyucuları okuma yolunu etkilememeli.
        using var images = new TestImages();
        var (path, _) = images.Get("basic");
        using var c = TestImages.Open(path);
        Assert.Equal("Test Diski", c.OpenVolumes()[0].Volume!.Name);
        Assert.NotEmpty(ContainerDump.Lines(c));
    }

    /// <summary>Görüntüde 8 baytlık bir alanı değiştirir; istenirse içinde bulunduğu nesnenin (tek blok) sağlama toplamını yeniler.</summary>
    internal static void Patch(string path, long offset, Action<byte[]> change, bool recomputeChecksumOfBlock, long blockStart = -1)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        var field = new byte[8];
        fs.Position = offset;
        fs.ReadExactly(field);
        change(field);
        fs.Position = offset;
        fs.Write(field);
        if (!recomputeChecksumOfBlock) return;
        var block = new byte[4096];
        fs.Position = blockStart;
        fs.ReadExactly(block);
        BinaryPrimitives.WriteUInt64LittleEndian(block, Fletcher64.Compute(block));
        fs.Position = blockStart;
        fs.Write(block);
    }
}
