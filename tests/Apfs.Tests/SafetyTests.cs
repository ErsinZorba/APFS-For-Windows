using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using Apfs.IO;
using Apfs.Util;
using Apfs.Windows;

namespace Apfs.Tests;

/// <summary>Risk 1 (diske yazma) ve Risk 2 (bozuk disk) için güvenlik testleri.</summary>
public class SafetyTests(TestImages images) : IClassFixture<TestImages>
{
    [Fact]
    public void BlockDeviceInterfaceHasNoWriteMembers()
    {
        var members = typeof(IBlockDevice).GetMembers().Select(m => m.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["Length", "Read", "SectorSize", "get_Length", "get_SectorSize"], members);
    }

    [Fact]
    public void RawDiskIsOpenedWithGenericReadOnly()
    {
        Assert.Equal(0x80000000u, RawDiskDevice.DesiredAccess);
    }

    [Fact]
    public void ReadingEverythingLeavesImageUnchanged()
    {
        var (path, _) = images.Get("basic");
        string before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        DateTime mtime = File.GetLastWriteTimeUtc(path);
        using (var c = TestImages.Open(path))
        {
            var v = c.OpenVolumes()[0].Volume!;
            void Walk(Volume.Inode dir)
            {
                foreach (var e in v.ListDirectory(dir.Id))
                {
                    var ino = v.GetInode(e.FileId);
                    if (ino.IsDirectory) Walk(ino);
                    else if (ino.IsRegularFile) ReaderTests.ReadAll(v, ino);
                }
            }
            Walk(v.Root);
        }
        Assert.Equal(before, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void Fletcher64MatchesStepwiseReference()
    {
        var rnd = new Random(7);
        foreach (int size in new[] { 4096, 16384, 65536 })
        {
            var block = new byte[size];
            rnd.NextBytes(block);
            // Spesifikasyondaki biçim: her adımda mod
            ulong s1 = 0, s2 = 0;
            for (int i = 8; i < size; i += 4)
            {
                s1 = (s1 + BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(i))) % 0xFFFFFFFF;
                s2 = (s2 + s1) % 0xFFFFFFFF;
            }
            ulong c1 = 0xFFFFFFFF - ((s1 + s2) % 0xFFFFFFFF);
            ulong c2 = 0xFFFFFFFF - ((s1 + c1) % 0xFFFFFFFF);
            Assert.Equal((c2 << 32) | c1, Fletcher64.Compute(block));
        }
    }

    [Fact]
    public void NotApfsFileIsReportedClearly()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, new byte[1 << 20]);
            using var dev = new FileImageDevice(path);
            var ex = Assert.Throws<ApfsException>(() => Container.ApfsContainer.Open(dev));
            Assert.Equal(ApfsErrorCode.NotApfs, ex.Code);
            Assert.Contains("APFS biçiminde değil", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CorruptBlockZeroFallsBackToCheckpointCopy()
    {
        string path = images.Copy("basic");
        Corrupt(path, offset: 4000, value: 0x5A); // blok 0'ın sonu: sağlama toplamı bozulur, imza kalır
        using var c = TestImages.Open(path);
        Assert.Contains(c.Warnings, w => w.Contains("Blok 0"));
        Assert.Equal("Test Diski", c.OpenVolumes()[0].Volume!.Name);
    }

    [Fact]
    public void AllSuperblocksCorruptGivesNoValidCheckpoint()
    {
        string path = images.Copy("basic");
        Corrupt(path, 4000, 0x5A);            // blok 0
        Corrupt(path, 2 * 4096 + 4000, 0x5A); // checkpoint alanındaki kopya
        var ex = Assert.Throws<ApfsException>(() => TestImages.Open(path));
        Assert.Equal(ApfsErrorCode.NoValidCheckpoint, ex.Code);
    }

    [Fact]
    public void CorruptMetadataBlockGivesChecksumErrorNotCrash()
    {
        string path = images.Copy("basic");
        // Görüntüdeki her metadata bloğunu sırayla bozup her seferinde yalnızca ApfsException alındığını doğrula.
        var original = File.ReadAllBytes(path);
        int blocks = original.Length / 4096;
        int checkedBlocks = 0;
        for (int b = 1; b < blocks; b++)
        {
            var block = original.AsSpan(b * 4096, 4096);
            if (!Fletcher64.Verify(block)) continue; // veri bloğu
            var copy = (byte[])original.Clone();
            copy[b * 4096 + 200] ^= 0xFF;
            File.WriteAllBytes(path, copy);
            try
            {
                using var c = TestImages.Open(path);
                foreach (var r in c.OpenVolumes())
                {
                    if (r.Volume is not { } v) continue;
                    TraverseAll(v);
                }
            }
            catch (ApfsException) { /* beklenen: anlaşılır hata */ }
            checkedBlocks++;
        }
        Assert.True(checkedBlocks >= 8, $"yalnızca {checkedBlocks} metadata bloğu bulundu");
    }

    [Fact]
    public void TruncatedImageGivesApfsException()
    {
        var (src, _) = images.Get("basic");
        string path = Path.Combine(Path.GetTempPath(), $"trunc-{Guid.NewGuid():N}.img");
        try
        {
            File.WriteAllBytes(path, File.ReadAllBytes(src).AsSpan(0, 40 * 4096).ToArray());
            var ex = Record.Exception(() =>
            {
                using var c = TestImages.Open(path);
                foreach (var r in c.OpenVolumes()) if (r.Volume is { } v) TraverseAll(v);
            });
            Assert.True(ex is null or ApfsException, ex?.ToString());
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(@"\\.\PhysicalDrive2", 2)]
    [InlineData("PhysicalDrive12", 12)]
    [InlineData("physicaldrive0", 0)]
    [InlineData(@"C:\disk.img", null)]
    [InlineData(@"\\.\PhysicalDrive2\..\x", null)]
    [InlineData(@"\\server\share\PhysicalDrive2", null)]
    public void LauncherOnlyAcceptsPhysicalDrives(string input, int? expected)
    {
        Assert.Equal(expected, SourceOpener.ParsePhysicalDrive(input));
    }

    internal static void TraverseAll(Volume.ApfsVolume v)
    {
        void Walk(Volume.Inode dir, int depth)
        {
            if (depth > 64) return;
            foreach (var e in v.ListDirectory(dir.Id))
            {
                var ino = v.TryGetInode(e.FileId);
                if (ino is null) continue;
                if (ino.IsDirectory) Walk(ino, depth + 1);
                else if (ino.IsRegularFile)
                {
                    var r = v.OpenFile(ino);
                    var buf = new byte[Math.Min(r.Length, 1 << 22)];
                    r.Read(0, buf);
                }
            }
        }
        Walk(v.Root, 0);
    }

    private static void Corrupt(string path, long offset, byte value)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        fs.Position = offset;
        int old = fs.ReadByte();
        fs.Position = offset;
        fs.WriteByte((byte)(old ^ value));
    }
}
