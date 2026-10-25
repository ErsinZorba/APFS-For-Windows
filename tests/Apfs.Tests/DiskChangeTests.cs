using Apfs.IO;
using Apfs.Partitions;
using Apfs.Volume;
using Apfs.Windows.Ipc;
using Apfs.Write;
using DevService = Apfs.Tests.ServiceWriteTests.DevService;

namespace Apfs.Tests;

/// <summary>
/// Başka bir program diski biçimlendirdiğinde (2026-10-06: USB bellek Rufus'la Windows kurulum belleği yapıldı, APFS sürücüsü
/// Gezgin'de kalmaya devam etti) hizmet bunu disk kimliğinden fark etmeli: sürücüyü kaldırmalı, yeni içerik APFS ise onu
/// bağlamalı, yazma oturumundaysa diske hiçbir şey yazmamalı. Kendi yazmalarımız kimliği değiştirmemeli (yanlış alarm yok).
/// </summary>
[Collection("WinFsp")]
public class DiskChangeTests(LinuxFixtures fixtures, TestImages images) : IClassFixture<LinuxFixtures>, IClassFixture<TestImages>
{
    private static string Fingerprint(string path)
    {
        using var dev = new FileImageDevice(path);
        return PartitionLocator.Fingerprint(dev);
    }

    /// <summary>Hizmet görüntüyü açık tutarken yerinde ezer (fiziksel diskte başka bir programın yazması gibi).</summary>
    private static void Overwrite(string path, byte[] content)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        fs.Write(content);
        fs.SetLength(content.Length);
        fs.Flush(true);
    }

    // ---------------- Kimlik ----------------

    [Fact]
    public void FingerprintSurvivesOwnWritesUntilClose()
    {
        var (path, grant) = FsOpsTests.Prepare(fixtures, "pop-ci-64m");
        string before = Fingerprint(path);
        Assert.NotEqual("", before);
        using (var dev = new FileImageWriter(path, grant))
        using (var s = WriteSession.Open(dev, grant))
        {
            var v = s.Volume(0);
            ulong dir = v.CreateDirectory(Inode.RootDirId, "kimlik");
            ulong id = v.CreateFile(dir, "veri.bin");
            for (int i = 0; i < 5; i++)
            {
                v.Write(id, (ulong)i * 300_000, new byte[300_000]);
                s.Commit();
                Assert.Equal(before, PartitionLocator.Fingerprint(dev)); // oturum boyunca kimlik aynı: yanlış alarm yok
            }
            // Close blok 0'ı yazar → kimlik değişir. Hizmet Close'u yalnızca diski izlemeden çıkardıktan sonra çağırır ve
            // yeniden bağlarken kimliği tazeler (MountManager.ReleaseDisk/Reattach).
            s.Close();
        }
        Assert.NotEqual(before, Fingerprint(path));
    }

    [Fact]
    public void FingerprintDistinguishesContainersAndWipes()
    {
        // Linux fikstürlerinin hepsi aynı sabit UUID'yi taşır (yalnız UUID'ye bakan kimlik bunları ayıramazdı)
        Assert.NotEqual(Fingerprint(fixtures.Get("mk-ci-64m")), Fingerprint(fixtures.Get("pop-ci-64m")));

        string bare = fixtures.Copy("mk-ci-64m");
        Overwrite(bare, new byte[new FileInfo(bare).Length]);
        Assert.Equal("", Fingerprint(bare));

        // GPT'li görüntü: bölüm konumu kimlikte; GPT başlığı silinince APFS yok
        string gpt = Path.Combine(Path.GetTempPath(), $"gpt-{Guid.NewGuid():N}.img");
        File.Copy(images.Get("basic", gpt: true).Path, gpt);
        try
        {
            string fp = Fingerprint(gpt);
            Assert.NotEqual("", fp);
            Assert.False(fp.StartsWith("0+", StringComparison.Ordinal), fp);
            Assert.Equal(fp, Fingerprint(gpt));
            using (var fs = new FileStream(gpt, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                fs.Position = 512;
                fs.Write(new byte[512]); // GPT başlığı (LBA 1)
            }
            Assert.Equal("", Fingerprint(gpt));
        }
        finally { File.Delete(gpt); }
    }

    // ---------------- Hizmet ----------------

    private static async Task WaitGone(DevService svc)
    {
        for (int i = 0; i < 225; i++) // 45 sn: 15 sn'lik tarama + pay
        {
            var st = await svc.Send<StatusResponse>(new Request("status"));
            if (st.Disks.Count == 0) return;
            await Task.Delay(200);
        }
        throw new TimeoutException("içeriği değişen disk bırakılmadı");
    }

    private static async Task<IReadOnlyList<Notice>> Notices(DevService svc) =>
        (await svc.Send<StatusResponse>(new Request("status"))).Notices;

    [Fact]
    public async Task WipedDiskDisappears()
    {
        string path = fixtures.Copy("pop-ci-64m");
        using var svc = new DevService(path);
        var d = await svc.WaitDisk(x => x.Volumes.Any(v => v.State == VolumeStatus.Mounted));
        string root = d.Volumes[0].MountPoint + "\\";
        Assert.True(File.Exists(root + "orta.bin"));

        Overwrite(path, new byte[new FileInfo(path).Length]);
        await WaitGone(svc);
        Assert.False(Directory.Exists(root));
        Assert.Contains(await Notices(svc), n => n.Text.Contains("başka bir program tarafından değiştirildi", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiskReplacedByAnotherApfsIsRemounted()
    {
        string path = fixtures.Copy("mk-ci-64m");
        using var svc = new DevService(path);
        var d = await svc.WaitDisk(x => x.Volumes.Any(v => v.State == VolumeStatus.Mounted));
        Assert.False(File.Exists(d.Volumes[0].MountPoint + "\\orta.bin"));

        Overwrite(path, File.ReadAllBytes(fixtures.Get("pop-ci-64m")));
        await svc.WaitDisk(x => x.Volumes.Any(v => v.State == VolumeStatus.Mounted && File.Exists(v.MountPoint + "\\orta.bin")));
    }

    [Fact]
    public async Task WritableDiskIsAbandonedWithoutWritingWhenWiped()
    {
        string path = fixtures.Copy("pop-ci-64m");
        using var svc = new DevService(path);
        var ro = await svc.WaitDisk(x => x.Volumes.Any(v => v.State == VolumeStatus.Mounted));
        var on = await svc.Send<UnlockResponse>(new Request("set-writable", Disk: ro.Id, Enable: true));
        Assert.True(on.Ok, on.Message);
        var rw = await svc.WaitDisk(x => x.Writable && x.Volumes[0].MountPoint is not null);
        string root = rw.Volumes[0].MountPoint + "\\";

        // En az bir tarama (15 sn) boyunca yaz: kendi yazmalarımız diski "değişti" göstermemeli
        var until = DateTime.UtcNow.AddSeconds(17);
        for (int i = 0; DateTime.UtcNow < until; i++)
        {
            File.WriteAllBytes(root + $"yazma-{i}.bin", new byte[200_000]);
            await Task.Delay(1000);
        }
        var still = await svc.WaitDisk(_ => true);
        Assert.True(still.Writable, "kendi yazmalarımız yanlış alarm verdi");

        using (var fs = new FileStream(root + "son.bin", FileMode.Create, FileAccess.Write))
        {
            fs.Write(new byte[100_000]);
            fs.Flush(true); // sürücüde Flush → bekleyen değişiklikler kaydedilir
        }
        await Task.Delay(2000);

        long length = new FileInfo(path).Length;
        Overwrite(path, new byte[length]);
        await WaitGone(svc);

        // Hizmet ne commit ne blok 0 yazdı: görüntü sıfır kaldı
        var after = File.ReadAllBytes(path);
        Assert.Equal(length, after.LongLength);
        int firstNonZero = Array.FindIndex(after, b => b != 0);
        Assert.True(firstNonZero < 0, $"bırakılan oturum diske yazdı (ilk sıfır olmayan bayt {firstNonZero})");
        Assert.Contains(await Notices(svc), n => n.Text.Contains("yeni içeriği bozmamak için", StringComparison.Ordinal));
    }
}
