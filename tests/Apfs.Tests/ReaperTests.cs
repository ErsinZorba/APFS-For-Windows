using Apfs.Container;
using Apfs.Diagnostics;
using Apfs.IO;
using Apfs.Volume;
using Apfs.Write;

namespace Apfs.Tests;

/// <summary>
/// 1.2.1: macOS bir birimi sildikten sonra kapsayıcıda boş bir reap list bırakır (kullanıcının HDD'si, 2026-10-25). Böyle
/// bir kapsayıcı yazmaya açılabilmeli, yazıcı listeyi her commit'te olduğu gibi taşımalı; gerçekten süren bir silme ya da
/// tanınmayan bir geçici nesne ise yazmayı kapatmalı. Çıktılar APFS_DEV_OUT ile apfsck'ye verilir.
/// </summary>
public class ReaperTests(LinuxFixtures fixtures) : IClassFixture<LinuxFixtures>
{
    [Theory]
    [InlineData("mk-ci-64m")]
    [InlineData("pop-ci-64m")]
    public void IdleReapListPassesCheckAndGate(string name)
    {
        string path = fixtures.Copy(name);
        ulong listOid = ReaperFixture.InjectIdleReapList(path);
        DevOutput.Save(path, $"reaper-bos-{name}");
        using var c = TestImages.Open(path);
        var cp = Checkpoint.Read(c);
        Assert.True(cp.IsValid, string.Join("; ", cp.Problems));
        Assert.NotNull(cp.Find(listOid));
        var r = FullCheck.Run(c);
        Assert.True(r.IsClean, string.Join("\n", r.Errors));
        Assert.Empty(r.WriteBlockers);
        var g = WriteGate.Evaluate(c);
        Assert.True(g.Allowed, string.Join("; ", g.Reasons));
        // apfs dump listeyi gösterir
        var dump = ContainerDump.Lines(c).ToList();
        Assert.Contains(dump, l => l.Contains($"liste {listOid} ", StringComparison.Ordinal) && l.Contains("sayı 0", StringComparison.Ordinal));
        Assert.Contains("  durum: boşta", dump);
    }

    [Theory]
    [InlineData("mk-ci-64m")]
    [InlineData("pop-ci-64m")]
    [InlineData("enc-pop-ci-64m")]
    public void CommitsCarryTheReapListUnchanged(string name)
    {
        string path = fixtures.Copy(name);
        ulong listOid = ReaperFixture.InjectIdleReapList(path);
        byte[] before;
        ulong startXid;
        WriteGrant grant;
        using (var c = TestImages.Open(path))
        {
            before = Checkpoint.Read(c).ReadEphemeral(listOid);
            startXid = c.Xid;
            var g = WriteGate.Evaluate(c, TestKeys.Of(c));
            Assert.True(g.Allowed, string.Join("; ", g.Reasons) + (g.Report is null ? "" : string.Join("; ", g.Report.Errors)));
            grant = g.Grant!;
        }

        var payload = new byte[300_000];
        new Random(7).NextBytes(payload);
        using (var dev = new FileImageWriter(path, grant))
        using (var s = WriteSession.Open(dev, grant))
        {
            var v = s.Volume(0, TestKeys.First(s.Container));
            ulong dir = v.CreateDirectory(Inode.RootDirId, "reaper deneme ğüş");
            for (int i = 0; i < 20; i++)
            {
                ulong f = v.CreateFile(dir, $"dosya-{i}.bin");
                v.Write(f, 0, payload.AsSpan(0, 1000 + i * 9000));
                if (i % 4 == 3) v.Delete(dir, $"dosya-{i - 2}.bin");
                s.Commit();
                AssertListCarried(dev, listOid, before, startXid + (ulong)i + 1);
            }
            s.Close();
        }
        DevOutput.Save(path, $"reaper-commit-{name}");
        using (var c = TestImages.Open(path))
        {
            Assert.Equal(startXid + 21, c.Xid);
            var r = FullCheck.Run(c, keys: TestKeys.Of(c));
            Assert.True(r.IsClean, string.Join("\n", r.Errors));
            Assert.Empty(r.Warnings);
            Assert.True(WriteGate.Evaluate(c, TestKeys.Of(c)).Allowed);
        }
    }

    /// <summary>Liste aynı oid'de, gövdesi birebir (yalnız sağlama ve xid değişir); reaper hâlâ onu gösteriyor; denetim temiz.</summary>
    private static void AssertListCarried(IBlockDevice dev, ulong listOid, byte[] before, ulong expectedXid)
    {
        using var c = ApfsContainer.Open(dev, ownsDevice: false);
        Assert.Equal(expectedXid, c.Xid);
        var cp = Checkpoint.Read(c);
        Assert.True(cp.IsValid, string.Join("; ", cp.Problems));
        var after = cp.ReadEphemeral(listOid);
        Assert.Equal(expectedXid, BitConverter.ToUInt64(after, 16));
        Assert.True(before.AsSpan(24).SequenceEqual(after.AsSpan(24)), "listenin gövdesi değişti");
        Assert.True(before.AsSpan(8, 8).SequenceEqual(after.AsSpan(8, 8)), "listenin oid'i değişti");
        var reaper = cp.ReadEphemeral(c.Superblock.ReaperOid);
        Assert.Equal(listOid, BitConverter.ToUInt64(reaper, 48));
        Assert.Equal(listOid, BitConverter.ToUInt64(reaper, 56));
        var r = FullCheck.Run(c, keys: TestKeys.Of(c));
        Assert.True(r.IsClean, string.Join("\n", r.Errors));
    }

    public static TheoryData<string> BusyCases() =>
    [
        "nesne işleniyor", "bekleyen silme", "devam bayrağı", "baş ≠ kuyruk", "liste sayısı 2", "birime ait",
        "listede girdi", "liste first", "liste next", "liste flags", "liste max", "liste yok",
    ];

    [Theory]
    [MemberData(nameof(BusyCases))]
    public void BusyOrMalformedReaperKeepsTheGateClosed(string kind)
    {
        string path = fixtures.Copy("mk-ci-64m");
        Action<byte[]>? reaper = null, list = null;
        switch (kind)
        {
            case "nesne işleniyor": reaper = b => ReaperFixture.W64(b, 88, 1234); break;
            case "bekleyen silme": reaper = b => ReaperFixture.W64(b, 32, 5); break;
            case "devam bayrağı": reaper = b => ReaperFixture.W32(b, 64, ReaperFixture.FlagBhm | ReaperFixture.FlagContinue); break;
            case "baş ≠ kuyruk": reaper = b => ReaperFixture.W64(b, 56, BitConverter.ToUInt64(b, 48) + 1); break;
            case "liste sayısı 2": reaper = b => ReaperFixture.W32(b, 68, 2); break;
            case "birime ait": reaper = b => ReaperFixture.W64(b, 80, 1026); break;
            case "listede girdi": list = b => ReaperFixture.W32(b, 48, 1); break;
            case "liste first": list = b => ReaperFixture.W32(b, 52, 0); break;
            case "liste next": list = b => ReaperFixture.W64(b, 32, 99); break;
            case "liste flags": list = b => ReaperFixture.W32(b, 40, 1); break;
            case "liste max": list = b => ReaperFixture.W32(b, 44, 99); break;
            case "liste yok": reaper = b => { ReaperFixture.W64(b, 48, 999_999); ReaperFixture.W64(b, 56, 999_999); }; break;
        }
        ReaperFixture.InjectIdleReapList(path, reaper, list);
        using var c = TestImages.Open(path);
        var r = FullCheck.Run(c);
        Assert.Contains(r.Errors, e => e.Contains("reaper", StringComparison.Ordinal));
        var g = WriteGate.Evaluate(c);
        Assert.False(g.Allowed);
        Assert.Null(g.Grant);
    }

    [Fact]
    public void UnknownEphemeralTypeBlocksGateAndSession()
    {
        string path = fixtures.Copy("mk-ci-64m");
        WriteGrant grant;
        using (var c0 = TestImages.Open(path)) grant = WriteGate.Evaluate(c0).Grant!;
        ReaperFixture.InjectOrphanEphemeral(path, 0x7E); // xid değişmez: eski izin hâlâ bu checkpoint'i gösterir

        using (var c = TestImages.Open(path))
        {
            var r = FullCheck.Run(c);
            Assert.True(r.IsClean, string.Join("\n", r.Errors));
            Assert.Contains(r.WriteBlockers, b => b.Contains("geçici nesne", StringComparison.Ordinal));
            Assert.False(WriteGate.Evaluate(c).Allowed);
        }

        // Kapı atlanmış olsa bile yazıcı hiçbir şey yazmadan reddeder
        byte[] image = File.ReadAllBytes(path);
        using (var dev = new FileImageWriter(path, grant))
        {
            var ex = Assert.Throws<ApfsException>(() => WriteSession.Open(dev, grant));
            Assert.Contains("geçici nesne", ex.Message, StringComparison.Ordinal);
        }
        Assert.True(image.AsSpan().SequenceEqual(File.ReadAllBytes(path)), "yazıcı reddederken diske yazdı");
    }

    [Fact]
    public void LoggedCheckErrorsHideNames()
    {
        string s = CheckReport.HideNames("klasör \"Özel Belgeler\" içinde \"maaş.xlsx\": bağlantı sayısı yanlış");
        Assert.DoesNotContain("Özel", s, StringComparison.Ordinal);
        Assert.DoesNotContain("maaş", s, StringComparison.Ordinal);
        Assert.Contains("bağlantı sayısı yanlış", s, StringComparison.Ordinal);
    }
}
