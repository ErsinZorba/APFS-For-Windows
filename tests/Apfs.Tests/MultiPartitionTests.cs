using Apfs.Diagnostics;
using Apfs.ImageBuilder;
using Apfs.IO;
using Apfs.Partitions;
using Apfs.Windows;
using Apfs.Windows.Ipc;
using DevService = Apfs.Tests.ServiceWriteTests.DevService;

namespace Apfs.Tests;

/// <summary>
/// Birden çok APFS bölümlü disk (2026-10-11: kullanıcının test belleği iki bölüm: şifresiz + şifreli). Yazma bölüm başınadır:
/// bir bölümü yazmaya açmak/kapatmak öbür bölümün sürücüsüne ve oturumuna dokunmaz; diski bırakan her yol bütün bölümleri kapatır.
/// </summary>
[Collection("WinFsp")]
public class MultiPartitionTests(LinuxFixtures fixtures) : IClassFixture<LinuxFixtures>
{
    /// <summary>Bölüm 0 = mk-ci-64m (boş), bölüm 1 = pop-ci-64m (Linux'un doldurduğu) tek GPT görüntüsünde.</summary>
    private string TwoPartitionImage()
    {
        string path = Path.Combine(Path.GetTempPath(), $"iki-bolum-{Guid.NewGuid():N}.img");
        GptDisk.Wrap([fixtures.Get("mk-ci-64m"), fixtures.Get("pop-ci-64m")], path);
        return path;
    }

    private static Task<DiskStatus> BothMounted(DevService svc) =>
        svc.WaitDisk(d => d.Volumes.Count(v => v.State == VolumeStatus.Mounted && v.MountPoint is not null) == 2);

    private static VolumeStatus In(DiskStatus d, int container) => d.Volumes.Single(v => v.Container == container);

    private static void AssertClean(string path, int container)
    {
        using var c = SourceOpener.OpenContainer(path, container);
        var r = FullCheck.Run(c);
        Assert.True(r.IsClean, $"bölüm {container}: " + string.Join("\n", r.Errors));
        Assert.Empty(r.Warnings); // oturum düzgün kapandı (blok 0 güncel)
    }

    private static string PartitionHash(string path, int container)
    {
        using var dev = new FileImageDevice(path);
        var p = PartitionLocator.FindApfsPartitions(dev)[container];
        var buf = new byte[p.Length];
        dev.Read(p.Offset, buf);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(buf));
    }

    [Fact]
    public void WrapPutsContainersInSeparatePartitions()
    {
        string path = TwoPartitionImage();
        try
        {
            using var dev = new FileImageDevice(path);
            var parts = PartitionLocator.FindApfsPartitions(dev);
            Assert.Equal(2, parts.Count);
            Assert.Equal(0, parts[1].Offset % 4096);
            using var a = SourceOpener.OpenContainer(path, 0);
            using var b = SourceOpener.OpenContainer(path, 1);
            Assert.True(b.OpenVolumes()[0].Volume!.Lookup("orta.bin").IsRegularFile);
            Assert.True(FullCheck.Run(a).IsClean);
            Assert.True(FullCheck.Run(b).IsClean);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task EnablingOnePartitionLeavesTheOtherMountedAndUntouched()
    {
        string path = TwoPartitionImage();
        try
        {
            string before0 = PartitionHash(path, 0);
            using (var svc = new DevService(path))
            {
                var d = await BothMounted(svc);
                string a = In(d, 0).MountPoint!;
                var on = await svc.Send<UnlockResponse>(new Request("set-writable", Disk: d.Id, Enable: true, Container: 1));
                Assert.True(on.Ok, on.Message);
                var rw = await svc.WaitDisk(x => x.Volumes.Any(v => v.Container == 1 && v.Writable && v.MountPoint is not null));
                Assert.Equal(a, In(rw, 0).MountPoint); // öbür bölümün sürücüsü hiç kalkmadı
                Assert.False(In(rw, 0).Writable);
                Assert.True(Directory.Exists(a + "\\"));
                string b = In(rw, 1).MountPoint! + "\\";
                File.WriteAllText(b + "ikinci-bolum.txt", "yalnız bu bölüm yazılabilir");
                Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(a + "\\yasak.txt", "x"));

                var off = await svc.Send<UnlockResponse>(new Request("set-writable", Disk: d.Id, Enable: false, Force: true, Container: 1));
                Assert.True(off.Ok, off.Message);
                await svc.WaitDisk(x => x.Volumes.Count(v => v.State == VolumeStatus.Mounted && !v.Writable) == 2);

                // Kapanış blok 0'ı yazdı; kimlik tazelendiği için sonraki taramalar (15 sn) diski "değişti" saymamalı
                await Task.Delay(17_000);
                var st = await svc.Send<StatusResponse>(new Request("status"));
                Assert.Equal(2, st.Disks.Single().Volumes.Count(v => v.State == VolumeStatus.Mounted));
                Assert.DoesNotContain(st.Notices, n => n.Text.Contains("başka bir program", StringComparison.Ordinal));
                Assert.Equal("yalnız bu bölüm yazılabilir", File.ReadAllText(In(st.Disks.Single(), 1).MountPoint + "\\ikinci-bolum.txt"));
            }
            AssertClean(path, 1);
            Assert.Equal(before0, PartitionHash(path, 0)); // salt okunur bölümün tek baytı değişmedi
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task BothPartitionsWritableThenEjectClosesEveryHandle()
    {
        string path = TwoPartitionImage();
        try
        {
            var data = new byte[2_000_000];
            new Random(11).NextBytes(data);
            using (var svc = new DevService(path))
            {
                var d = await BothMounted(svc);
                foreach (int c in new[] { 0, 1 })
                    Assert.True((await svc.Send<UnlockResponse>(new Request("set-writable", Disk: d.Id, Enable: true, Container: c))).Ok);
                var rw = await svc.WaitDisk(x => x.Volumes.Count(v => v.Writable && v.MountPoint is not null) == 2);
                File.WriteAllBytes(In(rw, 0).MountPoint + "\\bir.bin", data);
                File.WriteAllBytes(In(rw, 1).MountPoint + "\\iki.bin", data); // commit zamanlayıcısından önce çıkar
                var ej = await svc.Send<UnlockResponse>(new Request("eject", Disk: d.Id, Force: true));
                Assert.True(ej.Ok, ej.Message);
                // Bütün tanıtıcılar (salt okunur + iki yazma) kapandı: görüntü özel erişimle açılabiliyor
                using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            }
            foreach (int c in new[] { 0, 1 }) AssertClean(path, c);
            using var c0 = SourceOpener.OpenContainer(path, 0);
            using var c1 = SourceOpener.OpenContainer(path, 1);
            var v0 = c0.OpenVolumes()[0].Volume!;
            var v1 = c1.OpenVolumes()[0].Volume!;
            Assert.Equal(data, ReaderTests.ReadAll(v0, v0.Lookup("bir.bin")));
            Assert.Equal(data, ReaderTests.ReadAll(v1, v1.Lookup("iki.bin")));
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// Eleştiri bulgusu 1: bir bölümün Close'u blok 0'ı yazar (disk kimliği değişir). Kimlik tazelenmeden araya giren tarama diski
    /// "başka bir program değiştirdi" sayarsa ÖBÜR bölümün oturumunu kaydetmeden terk ederdi. Pencere geliştirme kancasıyla 4 sn'ye
    /// açılır ve tam o sırada tarama istenir: öbür bölüm yazılabilir kalmalı, verisi diske ulaşmalı.
    /// </summary>
    [Fact]
    public async Task ClosingOnePartitionDuringScanKeepsTheOtherSession()
    {
        string path = TwoPartitionImage();
        try
        {
            var data = new byte[1_500_000];
            new Random(5).NextBytes(data);
            using (var svc = new DevService(path, new Dictionary<string, string> { ["APFS_DEV_CLOSE_DELAY_MS"] = "4000" }))
            {
                var d = await BothMounted(svc);
                foreach (int c in new[] { 0, 1 })
                    Assert.True((await svc.Send<UnlockResponse>(new Request("set-writable", Disk: d.Id, Enable: true, Container: c))).Ok);
                var rw = await svc.WaitDisk(x => x.Volumes.Count(v => v.Writable && v.MountPoint is not null) == 2);
                string a = In(rw, 0).MountPoint + "\\";

                var closing = svc.Send<UnlockResponse>(new Request("set-writable", Disk: d.Id, Enable: false, Force: true, Container: 1));
                await Task.Delay(1500); // Close bitti, kimlik henüz tazelenmedi
                File.WriteAllBytes(a + "pencerede.bin", data); // A'nın henüz kaydedilmemiş değişikliği
                var scan = svc.Send<UnlockResponse>(new Request("rescan"));
                Assert.True((await closing).Ok);
                await scan;

                var st = await svc.Send<StatusResponse>(new Request("status"));
                Assert.DoesNotContain(st.Notices, n => n.Text.Contains("başka bir program", StringComparison.Ordinal));
                var disk = Assert.Single(st.Disks);
                Assert.True(In(disk, 0).Writable, "öbür bölümün oturumu terk edildi");
                Assert.Equal(data, File.ReadAllBytes(a + "pencerede.bin"));
                Assert.True((await svc.Send<UnlockResponse>(new Request("eject", Disk: d.Id, Force: true))).Ok);
            }
            AssertClean(path, 0);
            using var c0 = SourceOpener.OpenContainer(path, 0);
            var v0 = c0.OpenVolumes()[0].Volume!;
            Assert.Equal(data, ReaderTests.ReadAll(v0, v0.Lookup("pencerede.bin")));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task WipedWhileOnePartitionWritableWritesNothing()
    {
        string path = TwoPartitionImage();
        try
        {
            using var svc = new DevService(path);
            var d = await BothMounted(svc);
            Assert.True((await svc.Send<UnlockResponse>(new Request("set-writable", Disk: d.Id, Enable: true, Container: 1))).Ok);
            var rw = await svc.WaitDisk(x => x.Volumes.Any(v => v.Container == 1 && v.Writable && v.MountPoint is not null));
            string b = In(rw, 1).MountPoint + "\\";
            using (var fs = new FileStream(b + "son.bin", FileMode.Create, FileAccess.Write))
            {
                fs.Write(new byte[100_000]);
                fs.Flush(true);
            }
            await Task.Delay(2000);

            long length = new FileInfo(path).Length;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                fs.Write(new byte[length]);
                fs.Flush(true);
            }
            for (int i = 0; i < 225; i++)
            {
                if ((await svc.Send<StatusResponse>(new Request("status"))).Disks.Count == 0) break;
                await Task.Delay(200);
            }
            Assert.Empty((await svc.Send<StatusResponse>(new Request("status"))).Disks);
            var after = File.ReadAllBytes(path);
            int firstNonZero = Array.FindIndex(after, x => x != 0);
            Assert.True(firstNonZero < 0, $"bırakılan oturum diske yazdı (ilk sıfır olmayan bayt {firstNonZero})");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ProtocolStaysCompatibleBothWays()
    {
        // Eski istemcinin isteği (Container yok) → ilk bölüm
        var old = System.Text.Json.JsonSerializer.Deserialize<Request>("""{"op":"set-writable","disk":"x","enable":true}""", PipeProtocol.Json)!;
        Assert.Equal(0, old.Container);
        // Eski hizmetin durumu (Container/WriteBlockReason yok) okunur
        var st = System.Text.Json.JsonSerializer.Deserialize<VolumeStatus>("""{"index":0,"name":"a","state":"bağlı"}""", PipeProtocol.Json)!;
        Assert.Equal(0, st.Container);
        Assert.Null(st.WriteBlockReason);
        // Yeni alanlar yazılır ve geri okunur
        string json = System.Text.Json.JsonSerializer.Serialize(new VolumeStatus(0, "a", "bağlı", null, null, null, Container: 1, WriteBlockReason: "neden"), PipeProtocol.Json);
        var back = System.Text.Json.JsonSerializer.Deserialize<VolumeStatus>(json, PipeProtocol.Json)!;
        Assert.Equal(1, back.Container);
        Assert.Equal("neden", back.WriteBlockReason);
    }
}
