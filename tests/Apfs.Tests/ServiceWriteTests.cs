using System.Diagnostics;
using Apfs.Diagnostics;
using Apfs.Windows.Ipc;

namespace Apfs.Tests;

/// <summary>
/// Faz 4.1–4.2: hizmet geliştirme modunda (görüntü, yönetici gerekmez) tepsinin kanalıyla sürülür: "set-writable" aç → Windows'tan
/// yaz → "set-writable" kapat → disk salt okunur yeniden bağlanır; görüntü tam denetimden geçer. Kapının reddettiği disk (şifreli)
/// salt okunur kalır ve nedeni durumda görünür.
/// </summary>
[Collection("WinFsp")]
public class ServiceWriteTests(LinuxFixtures fixtures, TestImages images) : IClassFixture<LinuxFixtures>, IClassFixture<TestImages>
{
    private static string ServicePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string tfm = dir.Name, config = dir.Parent!.Name;
        return Path.Combine(dir.Parent!.Parent!.Parent!.Parent!.Parent!.FullName, "src", "Apfs.Service", "bin", config, tfm, "apfs-service.exe");
    }

    internal sealed class DevService : IDisposable
    {
        private readonly Process _p;
        public string Pipe { get; } = "APFStest" + Guid.NewGuid().ToString("N")[..8];

        public DevService(string image, IReadOnlyDictionary<string, string>? environment = null)
        {
            var psi = new ProcessStartInfo(ServicePath(), $"--console --pipe {Pipe} --image \"{image}\"")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var (k, v) in environment ?? new Dictionary<string, string>()) psi.Environment[k] = v;
            _p = Process.Start(psi)!;
            // çıktı borusu dolup süreci durdurmasın; satırlar (konsol kipinde günlük stderr'e gider) denetim için saklanır
            _p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (_output) _output.Add(e.Data); };
            _p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_output) _output.Add(e.Data); };
            _p.BeginOutputReadLine();
            _p.BeginErrorReadLine();
        }

        private readonly List<string> _output = [];

        /// <summary>Hizmetin şimdiye kadarki konsol çıktısı (günlük satırları dahil).</summary>
        public string Output
        {
            get { lock (_output) return string.Join("\n", _output); }
        }

        public async Task<T> Send<T>(Request r)
        {
            PipeProtocol.PipeName = Pipe;
            for (int i = 0; ; i++)
            {
                try { return (await PipeProtocol.SendAsync<T>(r, TimeSpan.FromSeconds(60)))!; }
                catch (Exception) when (i < 50) { await Task.Delay(200); } // hizmet henüz kanalı açmadı
            }
        }

        public async Task<DiskStatus> WaitDisk(Func<DiskStatus, bool> ready)
        {
            for (int i = 0; i < 150; i++)
            {
                var st = await Send<StatusResponse>(new Request("status"));
                var d = st.Disks.FirstOrDefault();
                if (d is not null && ready(d)) return d;
                await Task.Delay(200);
            }
            throw new TimeoutException("disk beklenen duruma gelmedi");
        }

        public void Dispose()
        {
            try { if (!_p.HasExited) _p.Kill(); _p.WaitForExit(30_000); } catch (InvalidOperationException) { }
            _p.Dispose();
        }
    }

    [Fact]
    public async Task EnableWriteThenDisableThroughPipe()
    {
        string path = fixtures.Copy("pop-ci-64m");
        using (var svc = new DevService(path))
        {
            var ro = await svc.WaitDisk(d => d.Volumes.Any(v => v.State == VolumeStatus.Mounted));
            Assert.False(ro.Writable);
            Assert.False(ro.Volumes[0].Writable);

            var on = await svc.Send<UnlockResponse>(new Request("set-writable", Disk: ro.Id, Enable: true));
            Assert.True(on.Ok, on.Message);
            var rw = await svc.WaitDisk(d => d.Writable && d.Volumes[0].MountPoint is not null);
            string root = rw.Volumes[0].MountPoint + "\\";
            File.WriteAllText(root + "hizmetten.txt", "tepsi üzerinden yazıldı");
            Directory.CreateDirectory(root + "Yeni Klasör");
            File.Move(root + "orta.bin", root + "Yeni Klasör\\orta.bin");

            var off = await svc.Send<UnlockResponse>(new Request("set-writable", Disk: ro.Id, Enable: false, Force: true));
            Assert.True(off.Ok, off.Message);
            var back = await svc.WaitDisk(d => !d.Writable && d.Volumes.Any(v => v.State == VolumeStatus.Mounted));
            Assert.False(back.Volumes[0].Writable);
            // salt okunur bağlamada da görünür
            Assert.Equal("tepsi üzerinden yazıldı", File.ReadAllText(back.Volumes[0].MountPoint + "\\hizmetten.txt"));
            Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(back.Volumes[0].MountPoint + "\\yasak.txt", "x"));
        }
        using var c = TestImages.Open(path);
        var r = FullCheck.Run(c);
        Assert.True(r.IsClean, string.Join("\n", r.Errors));
        Assert.Empty(r.Warnings); // oturum düzgün kapandı (blok 0 güncel)
        var v = c.OpenVolumes()[0].Volume!;
        Assert.Equal("tepsi üzerinden yazıldı"u8.ToArray(), ReaderTests.ReadAll(v, v.Lookup("hizmetten.txt")));
        Assert.True(v.Lookup("Yeni Klasör/orta.bin").IsRegularFile);
        DevOutput.Save(path, "hizmet-yazma");
    }

    [Fact]
    public async Task EjectWhileWritableCommitsEverything()
    {
        string path = fixtures.Copy("mk-ci-64m");
        using (var svc = new DevService(path))
        {
            var ro = await svc.WaitDisk(d => d.Volumes.Any(v => v.State == VolumeStatus.Mounted));
            Assert.True((await svc.Send<UnlockResponse>(new Request("set-writable", Disk: ro.Id, Enable: true))).Ok);
            var rw = await svc.WaitDisk(d => d.Writable);
            var data = new byte[3_000_000];
            new Random(9).NextBytes(data);
            File.WriteAllBytes(Path.Combine(rw.Volumes[0].MountPoint + "\\", "son-saniye.bin"), data); // commit zamanlayıcısından önce çıkar
            var ej = await svc.Send<UnlockResponse>(new Request("eject", Disk: ro.Id, Force: true));
            Assert.True(ej.Ok, ej.Message);
            using var c = TestImages.Open(path); // aygıt bırakıldı: dosya açılabilmeli
            var r = FullCheck.Run(c);
            Assert.True(r.IsClean, string.Join("\n", r.Errors));
            Assert.Empty(r.Warnings);
            var v = c.OpenVolumes()[0].Volume!;
            Assert.Equal(data, ReaderTests.ReadAll(v, v.Lookup("son-saniye.bin")));
        }
    }

    [Fact]
    public async Task RefusedDiskStaysReadOnlyWithReason()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sifreli-{Guid.NewGuid():N}.img");
        File.Copy(images.Get("encrypted").Path, path);
        try
        {
            using var svc = new DevService(path);
            var d = await svc.WaitDisk(x => x.Volumes.Count > 0);
            var resp = await svc.Send<UnlockResponse>(new Request("set-writable", Disk: d.Id, Enable: true));
            Assert.False(resp.Ok);
            Assert.Contains("şifreli", resp.Message, StringComparison.Ordinal);
            var after = await svc.WaitDisk(x => x.WriteBlockReason is not null);
            Assert.False(after.Writable);
            Assert.Contains("şifreli", after.WriteBlockReason!, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    /// <summary>1.2.1: kapı tam denetimden dolayı reddederse denetimin ayrıntısı günlüğe yazılır (yalnız neden özeti değil).</summary>
    [Fact]
    public async Task GateRefusalDetailIsLogged()
    {
        string path = fixtures.Copy("mk-ci-64m");
        ReaperFixture.InjectIdleReapList(path, changeReaper: b => ReaperFixture.W64(b, 88, 1234)); // silme sürüyor
        using var svc = new DevService(path);
        var d = await svc.WaitDisk(x => x.Volumes.Any(v => v.State == VolumeStatus.Mounted));
        var resp = await svc.Send<UnlockResponse>(new Request("set-writable", Disk: d.Id, Enable: true));
        Assert.False(resp.Ok);
        Assert.Contains("tutarsızlık", resp.Message, StringComparison.Ordinal);
        Assert.Contains("denetim: reaper meşgul: nesne 1234 siliniyor", svc.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// 2.6: şifreli birim kilitliyken yazmaya açılmaz ("önce kilidini açın"); kilit bir kez açılınca yazmaya açma ve "Salt okunura
    /// dön" parolayı yeniden sormaz (VEK kopyası hizmette). Günlükte parola ve anahtar izi yoktur.
    /// </summary>
    [Fact]
    public async Task EncryptedVolumeUnlockedOnceIsWritableAndReturnsWithoutPassword()
    {
        string path = fixtures.Copy("enc-pop-ci-64m");
        string output;
        using (var svc = new DevService(path))
        {
            var locked = await svc.WaitDisk(d => d.Volumes.Any(v => v.State == VolumeStatus.NeedsPassword));
            var refused = await svc.Send<UnlockResponse>(new Request("set-writable", Disk: locked.Id, Enable: true));
            Assert.False(refused.Ok);
            Assert.Contains("önce kilidini açın", refused.Message, StringComparison.Ordinal);
            await svc.WaitDisk(d => d.Volumes.Any(v => v.State == VolumeStatus.NeedsPassword));

            var wrong = await svc.Send<UnlockResponse>(new Request("unlock", Disk: locked.Id, Volume: 0, Password: "yanlış parola"));
            Assert.False(wrong.Ok);
            var ok = await svc.Send<UnlockResponse>(new Request("unlock", Disk: locked.Id, Volume: 0, Password: ImageBuilder.ImageEncryptor.Password));
            Assert.True(ok.Ok, ok.Message);
            var ro = await svc.WaitDisk(d => d.Volumes.Any(v => v.State == VolumeStatus.Mounted));

            var on = await svc.Send<UnlockResponse>(new Request("set-writable", Disk: ro.Id, Enable: true));
            Assert.True(on.Ok, on.Message);
            var rw = await svc.WaitDisk(d => d.Writable && d.Volumes[0].MountPoint is not null);
            string root = rw.Volumes[0].MountPoint + "\\";
            File.WriteAllText(root + "şifreli-yazıldı.txt", "şifreli birime Windows'tan");
            Assert.Equal("merhaba dünya\n", File.ReadAllText(root + "Belgeler\\merhaba.txt"));

            var off = await svc.Send<UnlockResponse>(new Request("set-writable", Disk: ro.Id, Enable: false, Force: true));
            Assert.True(off.Ok, off.Message);
            // Parola sorulmadan salt okunur bağlanır
            var back = await svc.WaitDisk(d => !d.Writable && d.Volumes.Any(v => v.State == VolumeStatus.Mounted));
            Assert.DoesNotContain(back.Volumes, v => v.State == VolumeStatus.NeedsPassword);
            Assert.Equal("şifreli birime Windows'tan", File.ReadAllText(back.Volumes[0].MountPoint + "\\şifreli-yazıldı.txt"));
            output = svc.Output;
        }
        using var c = TestImages.Open(path);
        var r = FullCheck.Run(c, keys: TestKeys.Of(c));
        Assert.True(r.IsClean, string.Join("\n", r.Errors));
        Assert.Empty(r.Warnings);
        var v = c.OpenVolumes(keys: TestKeys.Of(c))[0].Volume!;
        Assert.Equal("şifreli birime Windows'tan"u8.ToArray(), ReaderTests.ReadAll(v, v.Lookup("şifreli-yazıldı.txt")));

        // Günlükte parola ve anahtar yok (anahtar onaltılık ve base64 biçimiyle aranır)
        Assert.Contains("yazılabilir bağlandı", output, StringComparison.Ordinal); // günlük gerçekten yakalandı
        var vek = Apfs.Crypto.VolumeKeys.Unlock(c, c.VolumeUuid(c.Superblock.FsOids[0]), ImageBuilder.ImageEncryptor.Password);
        Assert.DoesNotContain(ImageBuilder.ImageEncryptor.Password, output, StringComparison.Ordinal);
        Assert.DoesNotContain("yanlış parola", output, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(vek.AsSpan(0, 8)), output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(vek.AsSpan(0, 9)), output, StringComparison.Ordinal);
        DevOutput.Save(path, "hizmet-yazma-sifreli");
    }
}
