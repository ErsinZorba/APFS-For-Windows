using Apfs.Diagnostics;
using Apfs.Volume;
using Apfs.Write;

namespace Apfs.Tests;

/// <summary>Faz 2.3–2.5: silme, ad değiştirme/taşıma, nitelikler. Linux'un yazdığı sert bağlantı, öznitelik akışı ve seyrek dosyalar dahil.</summary>
public class NamespaceOpsTests(LinuxFixtures fixtures) : IClassFixture<LinuxFixtures>
{
    private static ulong Id(VolumeWriter v, string path)
    {
        ulong cur = Inode.RootDirId;
        foreach (var part in path.Split('/')) cur = (v.FindEntry(cur, part) ?? throw new ApfsException(ApfsErrorCode.NotFound, path)).FileId;
        return cur;
    }

    private static ApfsVolume Reopen(string path, out Apfs.Container.ApfsContainer c)
    {
        c = TestImages.Open(path);
        var r = FullCheck.Run(c, keys: TestKeys.Of(c));
        Assert.True(r.IsClean, string.Join("\n", r.Errors));
        return c.OpenVolumes(keys: TestKeys.Of(c))[0].Volume!;
    }

    [Theory]
    [InlineData("pop-ci-64m")]
    [InlineData("enc-pop-ci-64m")]
    public void DeleteLinuxFilesIncludingHardLinksStreamsAndSparse(string fixture)
    {
        var (path, grant) = FsOpsTests.Prepare(fixtures, fixture);
        ulong freeBefore, filesBefore;
        using (var c0 = TestImages.Open(path)) { freeBefore = (ulong)c0.FreeBytes; filesBefore = c0.OpenVolumes(keys: TestKeys.Of(c0))[0].Volume!.NumFiles; }

        FsOpsTests.Run(path, grant, (s, v) =>
        {
            v.Delete(Inode.RootDirId, "sert-baglanti.txt");          // iki bağlantıdan biri
            Assert.Equal("merhaba dünya\n", ReadText(v, Id(v, "Belgeler/merhaba.txt")));
            s.Commit();
            v.Delete(Id(v, "Belgeler"), "merhaba.txt");              // son bağlantı
            v.Delete(Inode.RootDirId, "orta.bin");                    // öznitelik akışlı
            v.Delete(Inode.RootDirId, "seyrek.bin");                  // seyrek
            v.Delete(Inode.RootDirId, "buyuk.bin");                   // 1 MiB veri
            v.Delete(Inode.RootDirId, "sembolik");                    // sembolik bağ
            v.Delete(Inode.RootDirId, "bos.txt");
            v.Delete(Inode.RootDirId, "Boş Klasör");
            Assert.Throws<ApfsException>(() => v.Delete(Inode.RootDirId, "Belgeler")); // dolu klasör
            var ex = Assert.Throws<ApfsException>(() => v.Delete(Inode.RootDirId, "cok"));
            Assert.Equal(ApfsErrorCode.DirectoryNotEmpty, ex.Code);
            s.Commit();
            // Klasörü içeriğiyle birlikte sil (önce içi)
            ulong cok = Id(v, "cok");
            foreach (var e in v.ListDirectory(cok)) v.Delete(cok, e.Name);
            v.Delete(Inode.RootDirId, "cok");
        });

        var vol = Reopen(path, out var c);
        using (c)
        {
            Assert.Throws<ApfsException>(() => vol.Lookup("orta.bin"));
            Assert.Throws<ApfsException>(() => vol.Lookup("cok"));
            Assert.Throws<ApfsException>(() => vol.Lookup("Belgeler/merhaba.txt"));
            Assert.True(vol.Lookup("Belgeler/Alt Klasör/alt.txt").IsRegularFile);
            Assert.True(vol.NumFiles < filesBefore - 1000);
            Assert.True((ulong)c.FreeBytes > freeBefore + 1_000_000, "silinen verinin yeri geri gelmeli");
        }
        DevOutput.Save(path, fixture.StartsWith("enc-") ? "silme-linux-enc" : "silme-linux");
    }

    [Theory]
    [InlineData("pop-ci-64m")]
    [InlineData("enc-pop-ci-64m")]
    public void RenameAndMoveIncludingCaseOnlyHardLinkAndReplace(string fixture)
    {
        var (path, grant) = FsOpsTests.Prepare(fixtures, fixture);
        FsOpsTests.Run(path, grant, (s, v) =>
        {
            ulong belgeler = Id(v, "Belgeler");
            v.Rename(Inode.RootDirId, "orta.bin", belgeler, "orta-taşındı.bin", replace: false);       // taşı + ad
            v.Rename(Inode.RootDirId, "Boş Klasör", Inode.RootDirId, "BOŞ KLASÖR", replace: false);     // yalnız harf
            v.Rename(Inode.RootDirId, "sert-baglanti.txt", belgeler, "bağlantı-2.txt", replace: false); // sert bağlantı
            v.Rename(Inode.RootDirId, "derin", belgeler, "derin", replace: false);                      // klasör taşı
            ulong a = v.CreateFile(Inode.RootDirId, "a.txt");
            v.Write(a, 0, "yeni içerik"u8);
            v.Rename(Inode.RootDirId, "a.txt", Inode.RootDirId, "bos.txt", replace: true);              // üzerine yaz
            Assert.Equal(ApfsErrorCode.AlreadyExists,
                Assert.Throws<ApfsException>(() => v.Rename(Inode.RootDirId, "bos.txt", Inode.RootDirId, "buyuk.bin", false)).Code);
            Assert.Throws<ApfsException>(() => v.Rename(belgeler, "derin", Id(v, "Belgeler/derin/a"), "x", false)); // kendi içine
            s.Commit();
            v.Rename(belgeler, "bağlantı-2.txt", Inode.RootDirId, "geri.txt", false);
        });

        var vol = Reopen(path, out var c);
        using (c)
        {
            Assert.True(vol.Lookup("Belgeler/orta-taşındı.bin").IsRegularFile);
            Assert.True(vol.Lookup("BOŞ KLASÖR").IsDirectory);
            Assert.Equal("BOŞ KLASÖR", vol.ListDirectory(Inode.RootDirId).Single(e => e.Name.StartsWith("BOŞ", StringComparison.Ordinal)).Name);
            Assert.True(vol.Lookup("Belgeler/derin/a").IsDirectory);
            Assert.Equal("yeni içerik"u8.ToArray(), ReaderTests.ReadAll(vol, vol.Lookup("bos.txt")));
            // sert bağlantının iki adı aynı içeriği gösterir
            Assert.Equal(vol.Lookup("geri.txt").Id, vol.Lookup("Belgeler/merhaba.txt").Id);
            Assert.Equal("merhaba dünya\n"u8.ToArray(), ReaderTests.ReadAll(vol, vol.Lookup("geri.txt")));
        }
        DevOutput.Save(path, fixture.StartsWith("enc-") ? "adlandir-linux-enc" : "adlandir-linux");
    }

    [Theory]
    [InlineData("mk-ci-64m")]
    [InlineData("enc-mk-ci-64m")]
    public void TimesAndHiddenFlag(string fixture)
    {
        var (path, grant) = FsOpsTests.Prepare(fixtures, fixture);
        long t = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc).Subtract(DateTime.UnixEpoch).Ticks * 100;
        FsOpsTests.Run(path, grant, (_, v) =>
        {
            ulong f = v.CreateFile(Inode.RootDirId, "zaman.txt");
            v.SetTimes(f, t, t + 1_000_000_000, null, t + 2_000_000_000);
            v.SetHidden(f, true);
        });
        var vol = Reopen(path, out var c);
        using (c)
        {
            var ino = vol.Lookup("zaman.txt");
            Assert.Equal(t, ino.CreateTimeNs);
            Assert.Equal(t + 1_000_000_000, ino.ModTimeNs);
            Assert.Equal(t + 2_000_000_000, ino.AccessTimeNs);
            Assert.True(ino.IsHidden);
        }
        DevOutput.Save(path, fixture.StartsWith("enc-") ? "nitelik-enc" : "nitelik");
    }

    private static string ReadText(VolumeWriter v, ulong id)
    {
        var b = new byte[v.GetSize(id)];
        v.Read(id, 0, b);
        return System.Text.Encoding.UTF8.GetString(b);
    }
}
