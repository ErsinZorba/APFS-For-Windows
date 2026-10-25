using System.Diagnostics;
using Apfs.Diagnostics;

namespace Apfs.Tests;

/// <summary>
/// Faz 3.5: yazılabilir bağlı ve sürekli yazan GERÇEK bir süreç zorla öldürülür. Görüntü son kalıcı checkpoint ile açılmalı ve
/// tam denetimden 0 hatayla geçmelidir (blok 0 eski kalır: "düzgün kapatılmamış" uyarısı beklenir).
/// </summary>
[Collection("WinFsp")]
public class ProcessKillTests(LinuxFixtures fixtures) : IClassFixture<LinuxFixtures>
{
    private static string DevWriterPath()
    {
        // tests/Apfs.Tests/bin/<yapılandırma>/<tfm>/ → tests/Apfs.DevWriter/bin/<yapılandırma>/<tfm>/apfs-devwriter.exe
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string tfm = dir.Name, config = dir.Parent!.Name;
        return Path.Combine(dir.Parent!.Parent!.Parent!.Parent!.FullName, "Apfs.DevWriter", "bin", config, tfm, "apfs-devwriter.exe");
    }

    [Theory]
    [InlineData(1500)]
    [InlineData(4000)]
    [InlineData(7000)]
    public void KilledWriterLeavesConsistentDisk(int killAfterMs)
    {
        string path = fixtures.Copy("mk-ci-1g");
        ulong startXid;
        using (var c0 = TestImages.Open(path)) startXid = c0.Xid;

        var psi = new ProcessStartInfo(DevWriterPath(), $"\"{path}\"") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        string? first = p.StandardOutput.ReadLine();
        // Dikkat: hata çıktısı yalnızca başarısızlıkta okunur (ReadToEnd süreç kapanana kadar bekler)
        if (first?.StartsWith("HAZIR", StringComparison.Ordinal) != true)
        {
            p.Kill();
            Assert.Fail($"sürücü başlamadı: {first} {p.StandardError.ReadToEnd()}");
        }
        string root = first![6..].Trim() + Path.DirectorySeparatorChar;

        // Yazan taraf (test süreci): sürücü öldürülene kadar dosya yazar, siler
        using var stop = new CancellationTokenSource();
        string? writerEnd = null;
        var writer = new Thread(() =>
        {
            var data = new byte[256 * 1024];
            var rnd = new Random(killAfterMs);
            try
            {
                for (long i = 0; !stop.IsCancellationRequested; i++)
                {
                    rnd.NextBytes(data);
                    string dir = Path.Combine(root, $"yük-{i / 50}");
                    Directory.CreateDirectory(dir);
                    File.WriteAllBytes(Path.Combine(dir, $"dosya-{i}.bin"), data);
                    if (i % 7 == 0 && i > 10) File.Delete(Path.Combine(root, $"yük-{(i - 10) / 50}", $"dosya-{i - 10}.bin"));
                }
            }
            catch (Exception e) { writerEnd = e.GetType().Name; } // sürücü gitti: beklenen (tür bilgi için)
        }) { IsBackground = true };
        writer.Start();
        Thread.Sleep(killAfterMs);
        p.Kill();
        Assert.True(p.WaitForExit(60_000), "sürücü süreci kapanmadı");
        stop.Cancel();
        Assert.True(writer.Join(60_000), "yazan iş parçacığı durmadı");

        using var c = TestImages.Open(path);
        Assert.True(c.Xid >= startXid);
        var r = FullCheck.Run(c);
        Assert.True(r.IsClean, string.Join("\n", r.Errors));
        // Kalıcı olan her dosya tam 256 KiB olmalı (yarım yazılmış dosya yalnızca son işlemde olabilir; o işlem kalıcı değil)
        var v = c.OpenVolumes()[0].Volume!;
        int files = 0;
        foreach (var d in v.ListDirectory(Apfs.Volume.Inode.RootDirId).Where(e => e.Name.StartsWith("yük-", StringComparison.Ordinal)))
            foreach (var f in v.ListDirectory(d.FileId))
            {
                ulong size = v.GetFileSize(v.GetInode(f.FileId));
                Assert.True(size is 0 or 256 * 1024, $"{f.Name}: {size} bayt");
                files++;
            }
        DevOutput.Save(path, $"oldurme-{killAfterMs}");
    }
}
