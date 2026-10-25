using System.Buffers.Binary;
using Apfs.Container;
using Apfs.Diagnostics;
using Apfs.Util;

namespace Apfs.Tests;

/// <summary>
/// Faz 0.5: tam denetleyici. Temiz görüntülerde 0 hata; her bozulma türünde ilgili hata. Bozmalar sayaçları tutarlı bırakacak
/// şekilde yapılır (sağlama toplamları yenilenir), böylece yakalanan şey yalnızca hedeflenen tutarsızlıktır.
/// </summary>
public class FullCheckTests(LinuxFixtures fixtures) : IClassFixture<LinuxFixtures>
{
    public static TheoryData<string> AllFixtures() => [.. LinuxFixtures.Names];

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void CleanFixturesHaveNoErrors(string name)
    {
        using var c = TestImages.Open(fixtures.Get(name));
        var r = FullCheck.Run(c);
        Assert.True(r.IsClean, string.Join("\n", r.Errors));
        Assert.Empty(r.Warnings);
        Assert.Contains(r.Info, l => l.Contains("birebir", StringComparison.Ordinal));
    }

    [Fact]
    public void PopulatedLinuxImageReadsExactlyAsLinuxWroteIt()
    {
        var manifest = LinuxFixtures.Manifest("pop-ci-64m");
        using var c = TestImages.Open(fixtures.Get("pop-ci-64m"));
        var v = c.OpenVolumes()[0].Volume!;
        int seen = 0;
        void Walk(Apfs.Volume.Inode dir, string prefix)
        {
            foreach (var e in v.ListDirectory(dir.Id))
            {
                var ino = v.GetInode(e.FileId);
                string p = prefix.Length == 0 ? e.Name : $"{prefix}/{e.Name}";
                if (ino.IsDirectory) { Walk(ino, p); continue; }
                if (!ino.IsRegularFile) continue;
                var data = ReaderTests.ReadAll(v, ino);
                Assert.True(manifest.TryGetValue(p, out var expected), $"Linux listesinde yok: {p}");
                Assert.Equal(expected, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)));
                seen++;
            }
        }
        Walk(v.Root, "");
        Assert.Equal(manifest.Count, seen);
    }

    [Fact]
    public void LeakedBlockWithConsistentCountersIsDetected()
    {
        string path = fixtures.Copy("mk-ci-64m");
        var (bitmapAddr, bit) = FindBit(path, used: false);
        AdjustAllocation(path, bitmapAddr, bit, setUsed: true);
        var r = Check(path);
        Assert.Contains(r.Errors, e => e.Contains("sızıntı", StringComparison.Ordinal));
        Assert.DoesNotContain(r.Errors, e => e.Contains("sayacı", StringComparison.Ordinal) && e.Contains("alan yöneticisi", StringComparison.Ordinal));
    }

    [Fact]
    public void UsedBlockMarkedFreeIsDetectedAsDangerous()
    {
        string path = fixtures.Copy("mk-ci-64m");
        // Birimin omap nesnesinin bloğunu "boş" işaretle (sayaçlar tutarlı)
        ulong victim;
        using (var c = TestImages.Open(path))
            victim = c.OpenVolumes()[0].Volume!.Omap.Tree.EnumerateNodes().First().Paddr;
        var (bitmapAddr, _) = FindBit(path, used: false); // yalnızca ilk parçanın bitmap adresi için
        AdjustAllocation(path, bitmapAddr, (int)victim, setUsed: false);
        var r = Check(path);
        Assert.Contains(r.Errors, e => e.Contains("TEHLİKELİ", StringComparison.Ordinal));
    }

    [Fact]
    public void WrongVolumeAllocCountIsDetected()
    {
        string path = fixtures.Copy("mk-ci-64m");
        ulong apsb;
        using (var c = TestImages.Open(path)) apsb = c.Omap.Resolve(c.Superblock.FsOids[0], c.Xid).Paddr;
        PatchObject(path, apsb, b => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(88), BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(88)) + 1));
        Assert.Contains(Check(path).Errors, e => e.Contains("ayrılan blok sayacı", StringComparison.Ordinal));
    }

    [Fact]
    public void WrongFileCounterIsDetected()
    {
        string path = fixtures.Copy("mk-ci-64m");
        ulong apsb;
        using (var c = TestImages.Open(path)) apsb = c.Omap.Resolve(c.Superblock.FsOids[0], c.Xid).Paddr;
        PatchObject(path, apsb, b => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(184), 3));
        Assert.Contains(Check(path).Errors, e => e.Contains("sayaçlar", StringComparison.Ordinal));
    }

    [Fact]
    public void StaleBlockZeroIsOnlyAWarning()
    {
        string path = fixtures.Copy("mk-ci-64m");
        PatchObject(path, 0, b => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), 0)); // blok 0'ın xid'i eski
        var r = Check(path);
        Assert.True(r.IsClean, string.Join("\n", r.Errors));
        Assert.Contains(r.Warnings, w => w.Contains("düzgün kapatılmamış", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("fs", "şifresiz birimde OBJ_ENCRYPTED işaretli fs düğümü")]
    [InlineData("omap", "OBJ_ENCRYPTED işaretli omap düğümü")]
    [InlineData("extentref", "OBJ_ENCRYPTED işaretli extentref düğümü")]
    public void EncryptedObjectFlagOnUnencryptedVolumeNodeIsDetected(string tree, string expected)
    {
        // apfsck object.c: OBJ_ENCRYPTED ⇔ birim şifreli ∧ alt tür FSTREE; şifresiz birimin hiçbir düğümünde olmaz
        string path = fixtures.Copy("pop-ci-64m");
        ulong victim;
        using (var c = TestImages.Open(path))
        {
            var v = c.OpenVolumes()[0].Volume!;
            Assert.Contains(FullCheck.Run(c).Info, l => l.Contains("OBJ_ENCRYPTED işaretli 0", StringComparison.Ordinal));
            victim = tree switch
            {
                "fs" => v.Omap.Resolve(v.RootTreeOid, c.Xid).Paddr,
                "omap" => Bin.U64(v.Omap.Raw, 48),
                _ => Bin.U64(v.RawSuperblock, 144),
            };
        }
        PatchObject(path, victim, b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24),
            BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(24)) | ApfsConstants.ObjEncrypted));
        var r = Check(path);
        Assert.Contains(r.Errors, e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void BuilderImageFailsCleanlyNotWithCrash()
    {
        using var images = new TestImages();
        using var c = TestImages.Open(images.Get("basic").Path);
        var r = FullCheck.Run(c);
        Assert.False(r.IsClean); // alan yöneticisi bilinçli olarak eksik
    }

    // ---------------------------------------------------------------------------------------------------------------

    private static CheckReport Check(string path)
    {
        using var c = TestImages.Open(path);
        return FullCheck.Run(c);
    }

    /// <summary>İlk parçanın bitmap'inde istenen durumdaki ilk biti bulur (blok 2000'den sonra).</summary>
    private static (ulong BitmapAddr, int Bit) FindBit(string path, bool used)
    {
        using var c = TestImages.Open(path);
        var sm = Spaceman.Read(c, Checkpoint.Read(c));
        var ch = sm.MainChunks()[0];
        var bm = sm.ReadChunkBitmap(ch);
        for (int i = 2000; i < ch.BlockCount; i++)
            if (((bm[i >> 3] >> (i & 7)) & 1) == (used ? 1 : 0)) return (ch.BitmapAddr, i);
        throw new InvalidOperationException("uygun bit yok");
    }

    /// <summary>İlk parçada bir bloğun bitmap bitini değiştirir ve parça bilgisini, CIB'yi ve alan yöneticisinin aygıt sayacını tutarlı günceller.</summary>
    private static void AdjustAllocation(string path, ulong bitmapAddr, int bit, bool setUsed)
    {
        ulong cib, sm;
        using (var c = TestImages.Open(path))
        {
            var cp = Checkpoint.Read(c);
            cib = Spaceman.Read(c, cp).MainCibAddresses()[0];
            sm = cp.Find(c.Superblock.SpacemanOid)!.Value.Paddr;
        }
        int delta = setUsed ? -1 : +1;
        PatchRaw(path, bitmapAddr, b => { if (setUsed) b[bit >> 3] |= (byte)(1 << (bit & 7)); else b[bit >> 3] &= (byte)~(1 << (bit & 7)); });
        PatchObject(path, cib, b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(40 + 20), (uint)(BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(40 + 20)) + delta)));
        PatchObject(path, sm, b => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(72), (ulong)((long)BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(72)) + delta)));
    }

    private static void PatchRaw(string path, ulong block, Action<byte[]> change)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        var b = new byte[4096];
        fs.Position = (long)block * 4096;
        fs.ReadExactly(b);
        change(b);
        fs.Position = (long)block * 4096;
        fs.Write(b);
    }

    /// <summary>Tek bloklu bir nesneyi değiştirir ve sağlama toplamını yeniler.</summary>
    internal static void PatchObject(string path, ulong block, Action<byte[]> change) =>
        PatchRaw(path, block, b => { change(b); BinaryPrimitives.WriteUInt64LittleEndian(b, Fletcher64.Compute(b)); });
}
