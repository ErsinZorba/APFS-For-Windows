using Apfs.Diagnostics;
using Apfs.IO;
using Apfs.Write;

namespace Apfs.Tests;

/// <summary>
/// Faz 1.4/1.6: alan yöneticisi yazıcısı ve checkpoint commit'i. Boş commit'ler (yalnızca xid ilerler; dolu görüntüde önceki
/// işlemin boş kuyruğu bitmap'e geri verilir, parça bitmap'i/CIB yeni kopyalara, iç havuz bitmap'i halkada yeni yuvaya yazılır).
/// Her commit sonrası görüntü sıfırdan açılır ve tam denetimden geçer; ayrıca içerik değişmemelidir.
/// </summary>
public class CommitTests(LinuxFixtures fixtures) : IClassFixture<LinuxFixtures>
{
    [Theory]
    [InlineData("mk-ci-64m")]
    [InlineData("mk-ci-1g")]
    [InlineData("pop-ci-64m")]
    public void EmptyCommitsKeepContainerConsistent(string name)
    {
        string path = fixtures.Copy(name);
        ulong startXid;
        WriteGrant grant;
        using (var c = TestImages.Open(path))
        {
            startXid = c.Xid;
            var g = WriteGate.Evaluate(c);
            Assert.True(g.Allowed, string.Join("; ", g.Reasons));
            grant = g.Grant!;
        }

        using (var dev = new FileImageWriter(path, grant))
        using (var s = WriteSession.Open(dev, grant))
        {
            for (int i = 0; i < 20; i++)
            {
                s.Commit();
                AssertClean(dev, startXid + (ulong)i + 1, expectStaleBlockZero: true);
            }
            s.Close();
        }
        AssertClean(path, startXid + 21, expectStaleBlockZero: false);
        DevOutput.Save(path, $"commit-bos-{name}");

        if (name.StartsWith("pop", StringComparison.Ordinal))
        {
            // İçerik değişmemiş olmalı
            var manifest = LinuxFixtures.Manifest(name);
            using var c = TestImages.Open(path);
            var v = c.OpenVolumes()[0].Volume!;
            foreach (var (p, hash) in manifest.Take(200))
            {
                var data = ReaderTests.ReadAll(v, v.Lookup(p));
                Assert.Equal(hash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)));
            }
        }
    }

    [Fact]
    public void SessionRefusesWhenDiskChangedSinceGrant()
    {
        string path = fixtures.Copy("mk-ci-64m");
        WriteGrant grant;
        using (var c = TestImages.Open(path)) grant = WriteGate.Evaluate(c).Grant!;
        using (var dev = new FileImageWriter(path, grant))
        using (var s = WriteSession.Open(dev, grant)) s.Close(); // disk ilerledi
        using var dev2 = new FileImageWriter(path, grant);
        var ex = Assert.Throws<ApfsException>(() => WriteSession.Open(dev2, grant));
        Assert.Contains("değişmiş", ex.Message);
    }

    [Fact]
    public void AbandonedSessionLeavesLastCommit()
    {
        string path = fixtures.Copy("mk-ci-64m");
        WriteGrant grant;
        ulong xid;
        using (var c = TestImages.Open(path)) { grant = WriteGate.Evaluate(c).Grant!; xid = c.Xid; }
        using (var dev = new FileImageWriter(path, grant))
        using (var s = WriteSession.Open(dev, grant))
        {
            s.Commit();
            // Close çağrılmadan bırakılır (çökme gibi)
        }
        AssertClean(path, xid + 1, expectStaleBlockZero: true);
        DevOutput.Save(path, "commit-yarim-mk-ci-64m");
    }

    internal static void AssertClean(string path, ulong expectedXid, bool expectStaleBlockZero)
    {
        using var c = TestImages.Open(path);
        AssertClean(c, expectedXid, expectStaleBlockZero);
    }

    /// <summary>Oturum açıkken: yazıcının kendi aygıtı üzerinden, sıfırdan açılmış kapsayıcıyla denetler.</summary>
    internal static void AssertClean(IBlockDevice dev, ulong expectedXid, bool expectStaleBlockZero)
    {
        using var c = Apfs.Container.ApfsContainer.Open(dev, ownsDevice: false);
        AssertClean(c, expectedXid, expectStaleBlockZero);
    }

    private static void AssertClean(Apfs.Container.ApfsContainer c, ulong expectedXid, bool expectStaleBlockZero)
    {
        Assert.Equal(expectedXid, c.Xid);
        var r = FullCheck.Run(c);
        Assert.True(r.IsClean, string.Join("\n", r.Errors));
        if (expectStaleBlockZero) Assert.Contains(r.Warnings, w => w.Contains("düzgün kapatılmamış", StringComparison.Ordinal));
        else Assert.Empty(r.Warnings);
    }
}
