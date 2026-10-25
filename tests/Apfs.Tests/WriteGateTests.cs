using System.Buffers.Binary;
using System.Reflection;
using Apfs.IO;
using Apfs.Write;

namespace Apfs.Tests;

/// <summary>Faz 1.1: yazılabilir aygıtlar yalnızca yazma izniyle kurulur; izin yalnızca yazma kapısından çıkar.</summary>
public class WriteGateTests(LinuxFixtures fixtures, TestImages images) : IClassFixture<LinuxFixtures>, IClassFixture<TestImages>
{
    private static readonly Assembly[] ProductAssemblies =
        [typeof(IBlockDevice).Assembly, typeof(Apfs.Windows.RawDiskDevice).Assembly, typeof(Apfs.Mount.ApfsMount).Assembly];

    [Fact]
    public void WriteGrantCannotBeConstructedPublicly()
    {
        Assert.Empty(typeof(WriteGrant).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void EveryWritableDeviceRequiresAGrant()
    {
        var types = ProductAssemblies.SelectMany(a => a.GetTypes())
            .Where(t => typeof(IWritableBlockDevice).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract).ToList();
        Assert.NotEmpty(types);
        foreach (var t in types)
            foreach (var ctor in t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                Assert.True(ctor.GetParameters().Any(p => p.ParameterType == typeof(WriteGrant)),
                    $"{t.Name} yapıcısı yazma izni istemiyor");
    }

    [Fact]
    public void ReadOnlyInterfaceIsStillWriteFree()
    {
        // IBlockDevice'a yazma eklenmedi; yazma ayrı arayüzde
        Assert.DoesNotContain(typeof(IBlockDevice).GetMembers(), m => m.Name.Contains("Write", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(StructureTests.AllFixtures), MemberType = typeof(StructureTests))]
    public void CleanUnencryptedFixturesPassTheGate(string name)
    {
        using var c = TestImages.Open(fixtures.Get(name));
        var g = WriteGate.Evaluate(c);
        Assert.True(g.Allowed, string.Join("; ", g.Reasons) + (g.Report is null ? "" : string.Join("; ", g.Report.Errors)));
        Assert.Equal(c.Superblock.Uuid, g.Grant!.ContainerUuid);
        Assert.Equal(c.Xid, g.Grant.Xid);
    }

    [Fact]
    public void EncryptedVolumeIsRefusedWithPlainReason()
    {
        using var c = TestImages.Open(images.Get("encrypted").Path);
        var g = WriteGate.Evaluate(c);
        Assert.False(g.Allowed);
        Assert.Null(g.Grant);
        Assert.Contains(g.Reasons, r => r.Contains("şifreli", StringComparison.Ordinal));
    }

    [Fact]
    public void SnapshotVolumeIsRefused()
    {
        string path = fixtures.Copy("mk-ci-64m");
        PatchVolume(path, b => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(216), 1));
        using var c = TestImages.Open(path);
        var g = WriteGate.Evaluate(c);
        Assert.False(g.Allowed);
        Assert.Contains(g.Reasons, r => r.Contains("anlık görüntü", StringComparison.Ordinal));
    }

    [Fact]
    public void BackupRoleIsRefused()
    {
        string path = fixtures.Copy("mk-ci-64m");
        PatchVolume(path, b => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(964), Apfs.Container.ApfsConstants.RoleBackup));
        using var c = TestImages.Open(path);
        var g = WriteGate.Evaluate(c);
        Assert.False(g.Allowed);
        Assert.Contains(g.Reasons, r => r.Contains("yedek", StringComparison.Ordinal));
    }

    [Fact]
    public void UnfinishedEncryptionIsRefusedWithPlainReasonAndNotCalledALeak()
    {
        // Gerçek Disk 2'de görüldü: apfs_er_state_oid dolu (macOS'un şifreleme işlemi bitmemiş)
        string path = fixtures.Copy("mk-ci-64m");
        PatchVolume(path, b => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(976), 12345));
        using var c = TestImages.Open(path);
        var g = WriteGate.Evaluate(c);
        Assert.False(g.Allowed);
        Assert.Contains(g.Reasons, r => r.Contains("şifreleme işlemi henüz bitmemiş", StringComparison.Ordinal));
        var report = Apfs.Diagnostics.FullCheck.Run(c);
        Assert.True(report.OwnershipIncomplete);
        Assert.DoesNotContain(report.Errors, e => e.Contains("sızıntı", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, w => w.Contains("şifreleme işlemi", StringComparison.Ordinal));
    }

    [Fact]
    public void IncompleteOwnershipCheckRefusesEvenWithoutOtherReason()
    {
        // snap_meta_ext dolu ama başka kural tetiklenmiyor: sahiplik eksik denetlendiği için yine de reddedilmeli
        string path = fixtures.Copy("mk-ci-64m");
        PatchVolume(path, b => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(1000), 777));
        using var c = TestImages.Open(path);
        var g = WriteGate.Evaluate(c);
        Assert.False(g.Allowed);
        Assert.Contains(g.Reasons, r => r.Contains("çözümleyemediği", StringComparison.Ordinal));
    }

    [Fact]
    public void InconsistentContainerIsRefusedAfterFullCheck()
    {
        string path = fixtures.Copy("mk-ci-64m");
        PatchVolume(path, b => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(184), 7)); // dosya sayacı yanlış
        using var c = TestImages.Open(path);
        var g = WriteGate.Evaluate(c);
        Assert.False(g.Allowed);
        Assert.NotNull(g.Report);
        Assert.False(g.Report.IsClean);
    }

    [Fact]
    public void FileImageWriterWritesAndFlushes()
    {
        string path = fixtures.Copy("mk-ci-64m");
        var grant = new WriteGrant(Guid.Empty, 0);
        var data = new byte[4096];
        Random.Shared.NextBytes(data);
        using (var w = new FileImageWriter(path, grant))
        {
            w.Write(10_000 * 4096L, data);
            w.Flush();
            var back = new byte[4096];
            w.Read(10_000 * 4096L, back);
            Assert.Equal(data, back);
            Assert.Throws<ApfsException>(() => w.Write(w.Length - 100, data));
        }
    }

    private static void PatchVolume(string path, Action<byte[]> change)
    {
        ulong apsb;
        using (var c = TestImages.Open(path)) apsb = c.Omap.Resolve(c.Superblock.FsOids[0], c.Xid).Paddr;
        FullCheckTests.PatchObject(path, apsb, change);
    }
}
