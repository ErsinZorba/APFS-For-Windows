using Apfs.Container;
using Apfs.Diagnostics;
using Apfs.IO;
using Apfs.Volume;
using Apfs.Write;

namespace Apfs.Tests;

/// <summary>
/// Faz 2: dosya sistemi işlemleri. Her senaryo bir görüntü kopyasında yapılır, commit'lenir, görüntü sıfırdan açılıp okuma
/// yoluyla (ApfsVolume) doğrulanır ve tam denetimden geçer. APFS_DEV_OUT ile çıktılar apfsck'ye verilir.
/// </summary>
public class FsOpsTests(LinuxFixtures fixtures) : IClassFixture<LinuxFixtures>
{
    public static TheoryData<string> Fixtures() => ["mk-ci-64m", "mk-cs-64m", "mk-ns-64m", "pop-ci-64m", "enc-mk-cs-64m", "enc-pop-ci-64m"];

    internal static (string Path, WriteGrant Grant) Prepare(LinuxFixtures fixtures, string name)
    {
        string path = fixtures.Copy(name);
        using var c = TestImages.Open(path);
        var g = WriteGate.Evaluate(c, TestKeys.Of(c));
        Assert.True(g.Allowed, string.Join("; ", g.Reasons));
        return (path, g.Grant!);
    }

    internal static void Run(string path, WriteGrant grant, Action<WriteSession, VolumeWriter> body, bool close = true)
    {
        using var dev = new FileImageWriter(path, grant);
        using var s = WriteSession.Open(dev, grant);
        body(s, s.Volume(0, TestKeys.First(s.Container)));
        if (close) s.Close(); else s.Commit();
    }

    internal static void AssertCleanFull(string path)
    {
        using var c = TestImages.Open(path);
        var r = FullCheck.Run(c, keys: TestKeys.Of(c));
        Assert.True(r.IsClean, string.Join("\n", r.Errors));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void CreateDirectoriesAndEmptyFiles(string name)
    {
        var (path, grant) = Prepare(fixtures, name);
        ulong filesBefore, dirsBefore;
        using (var c = TestImages.Open(path)) { var v = c.OpenVolumes(keys: TestKeys.Of(c))[0].Volume!; filesBefore = v.NumFiles; dirsBefore = v.NumDirectories; }

        Run(path, grant, (s, v) =>
        {
            ulong a = v.CreateDirectory(Inode.RootDirId, "Yeni Klasör");
            ulong b = v.CreateDirectory(a, "İç içe ığüşöç");
            v.CreateFile(b, "boş dosya.txt");
            v.CreateFile(a, "ĞÜŞİÖÇ.txt");
            s.Commit();
            // ikinci işlem: aynı oturumda devam
            for (int i = 0; i < 300; i++) v.CreateFile(a, $"dosya-{i:D3}.txt");
            v.CreateDirectory(Inode.RootDirId, "İkinci");
        });

        AssertCleanFull(path);
        using var c2 = TestImages.Open(path);
        var vol = c2.OpenVolumes(keys: TestKeys.Of(c2))[0].Volume!;
        Assert.True(vol.Lookup("Yeni Klasör/İç içe ığüşöç/boş dosya.txt").IsRegularFile);
        Assert.True(vol.Lookup("Yeni Klasör/ĞÜŞİÖÇ.txt").IsRegularFile);
        Assert.Equal(302, vol.ListDirectory(vol.Lookup("Yeni Klasör").Id).Count);
        Assert.True(vol.Lookup("İkinci").IsDirectory);
        Assert.Equal(filesBefore + 302, vol.NumFiles);
        Assert.Equal(dirsBefore + 3, vol.NumDirectories);
        Assert.Equal(0UL, vol.GetFileSize(vol.Lookup("Yeni Klasör/ĞÜŞİÖÇ.txt")));
        DevOutput.Save(path, $"olustur-{name}");
    }

    [Fact]
    public void SessionViewReflectsChangesBeforeCommit()
    {
        var (path, grant) = Prepare(fixtures, "pop-ci-64m");
        Run(path, grant, (s, v) =>
        {
            var view = v.View(s.Container);
            var orta = view.Lookup("orta.bin");                // önbelleğe girsin
            _ = view.ListDirectory(Inode.RootDirId);
            ulong d = v.CreateDirectory(Inode.RootDirId, "Görüntü");
            ulong f = v.CreateFile(d, "a.txt");
            v.Write(f, 0, "merhaba"u8);
            Assert.Equal("merhaba"u8.ToArray(), ReaderTests.ReadAll(view, view.Lookup("Görüntü/a.txt")));
            Assert.Contains(view.ListDirectory(Inode.RootDirId), e => e.Name == "Görüntü");
            v.Write(orta.Id, 0, "XYZ"u8);
            Assert.Equal("XYZ"u8.ToArray(), ReaderTests.ReadAll(view, view.Lookup("orta.bin")).AsSpan(0, 3).ToArray());
            v.Rename(Inode.RootDirId, "orta.bin", d, "orta2.bin", false);
            Assert.Throws<ApfsException>(() => view.Lookup("orta.bin"));
            Assert.Equal(3145728UL, view.GetFileSize(view.Lookup("Görüntü/orta2.bin")));
            s.Commit();
            v.Delete(d, "a.txt");
            Assert.Throws<ApfsException>(() => view.Lookup("Görüntü/a.txt"));
            // sembolik bağ görüntüden okunur ve hedefi çözülür
            string target = view.ReadSymlinkTarget(view.Lookup("sembolik").Id)!;
            Assert.Equal("merhaba dünya\n"u8.ToArray(), ReaderTests.ReadAll(view, view.Lookup(target)));
        });
        AssertCleanFull(path);
    }

    [Fact]
    public void DuplicateNamesAreRefusedByVolumeRules()
    {
        var (ci, grantCi) = Prepare(fixtures, "mk-ci-64m");
        Run(ci, grantCi, (_, v) =>
        {
            v.CreateFile(Inode.RootDirId, "Belge.txt");
            Assert.Throws<ApfsException>(() => v.CreateFile(Inode.RootDirId, "belge.TXT")); // harf duyarsız
            Assert.Throws<ApfsException>(() => v.CreateDirectory(Inode.RootDirId, "BELGE.txt"));
        });
        var (cs, grantCs) = Prepare(fixtures, "mk-cs-64m");
        Run(cs, grantCs, (_, v) =>
        {
            v.CreateFile(Inode.RootDirId, "Belge.txt");
            v.CreateFile(Inode.RootDirId, "belge.txt"); // harf duyarlı: ayrı dosya
        });
        AssertCleanFull(ci);
        AssertCleanFull(cs);
    }

    [Fact]
    public void InvalidNamesAreRefused()
    {
        var (path, grant) = Prepare(fixtures, "mk-ci-64m");
        Run(path, grant, (_, v) =>
        {
            foreach (var bad in new[] { "", ".", "..", "a/b", "x\0y", new string('ş', 128) })
                Assert.Throws<ApfsException>(() => v.CreateFile(Inode.RootDirId, bad));
        });
        AssertCleanFull(path);
    }
}
