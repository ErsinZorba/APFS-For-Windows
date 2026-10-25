using System.Diagnostics;
using System.Security.Cryptography;
using Apfs.Diagnostics;
using Apfs.IO;
using Apfs.Mount;
using Apfs.Write;
using Xunit.Abstractions;

namespace Apfs.Tests;

/// <summary>
/// Faz 3.4: gerçekçi Windows iş yükleri (robocopy, eşzamanlı kopyalar, disk dolu). Varsayılan boyutlar küçüktür;
/// APFS_LONG=1 ile büyür. Sonuçlar ve hız çıktıya yazılır.
/// </summary>
[Collection("WinFsp")]
public class WindowsWorkloadTests(LinuxFixtures fixtures, ITestOutputHelper output) : IClassFixture<LinuxFixtures>
{
    private static bool Long => Environment.GetEnvironmentVariable("APFS_LONG") == "1";

    /// <summary>Kaynak ağaç: Türkçe adlı iç içe klasörler, çok sayıda küçük dosya ve birkaç büyük dosya.</summary>
    private static string MakeSourceTree(int smallFiles, int bigMb)
    {
        string root = Path.Combine(Path.GetTempPath(), $"apfs-agac-{Guid.NewGuid():N}");
        var rnd = new Random(77);
        string[] dirs = ["Belgeler", "Fotoğraflar\\2024 Yaz", "Müzik\\Şarkılar", "Proje\\kaynak\\çekirdek", "ığüşöç İĞÜŞÖÇ"];
        foreach (var d in dirs) Directory.CreateDirectory(Path.Combine(root, d));
        for (int i = 0; i < smallFiles; i++)
        {
            var b = new byte[rnd.Next(0, 20_000)];
            rnd.NextBytes(b);
            File.WriteAllBytes(Path.Combine(root, dirs[i % dirs.Length], $"dosya-{i}-ğ.dat"), b);
        }
        var big = new byte[bigMb * 1024 * 1024];
        rnd.NextBytes(big);
        File.WriteAllBytes(Path.Combine(root, "Proje", "büyük-arşiv.zip"), big);
        return root;
    }

    private static int Robocopy(string from, string to)
    {
        var psi = new ProcessStartInfo("robocopy", $"\"{from}\" \"{to}\" /E /R:0 /W:0 /NFL /NDL /NJH /NJS /NP")
        { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    private static Dictionary<string, string> HashTree(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))), StringComparer.Ordinal);

    [Fact]
    public void RobocopyInAndOutAndConcurrentCopies()
    {
        int small = Long ? 10_000 : 1_500, bigMb = Long ? 300 : 40;
        var (path, grant) = FsOpsTests.Prepare(fixtures, "mk-ci-1g");
        string src = MakeSourceTree(small, bigMb);
        string back = Path.Combine(Path.GetTempPath(), $"apfs-geri-{Guid.NewGuid():N}");
        var errors = new List<string>();
        try
        {
            var expected = HashTree(src);
            using (var dev = new FileImageWriter(path, grant))
            {
                var s = WriteSession.Open(dev, grant);
                using var m = ApfsWritableMount.Mount(s, 0, null, e => { lock (errors) errors.Add(e); });
                string root = m.MountPoint + "\\";

                var sw = Stopwatch.StartNew();
                Assert.True(Robocopy(src, root + "Kopya") < 8, "robocopy içeri başarısız");
                double inSec = sw.Elapsed.TotalSeconds;
                long bytes = expected.Keys.Sum(k => new System.IO.FileInfo(Path.Combine(src, k)).Length);
                output.WriteLine($"İçeri: {expected.Count} dosya, {bytes / 1048576.0:N1} MB, {inSec:N1} sn ({bytes / 1048576.0 / inSec:N1} MB/sn)");

                sw.Restart();
                Assert.True(Robocopy(root + "Kopya", back) < 8, "robocopy dışarı başarısız");
                output.WriteLine($"Dışarı: {sw.Elapsed.TotalSeconds:N1} sn");
                Assert.Equal(expected, HashTree(back));

                // Eşzamanlı iki kopya (farklı klasörlere)
                sw.Restart();
                int r1 = 0, r2 = 0;
                Parallel.Invoke(() => r1 = Robocopy(src + "\\Belgeler", root + "Eşzamanlı-1"),
                                () => r2 = Robocopy(src + "\\Müzik", root + "Eşzamanlı-2"));
                Assert.True(r1 < 8 && r2 < 8, "eşzamanlı kopyalama başarısız");
                output.WriteLine($"Eşzamanlı: {sw.Elapsed.TotalSeconds:N1} sn");
                Assert.Equal(Directory.GetFiles(src + "\\Belgeler").Length, Directory.GetFiles(root + "Eşzamanlı-1").Length);
            }
            Assert.Empty(errors);

            using var c = TestImages.Open(path);
            var r = FullCheck.Run(c);
            Assert.True(r.IsClean, string.Join("\n", r.Errors));
            var v = c.OpenVolumes()[0].Volume!;
            foreach (var (rel, hash) in expected.Take(Long ? int.MaxValue : 300))
                Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(ReaderTests.ReadAll(v, v.Lookup("Kopya/" + rel.Replace('\\', '/'))))));
            DevOutput.Save(path, "is-yuku-robocopy");
        }
        finally
        {
            Directory.Delete(src, true);
            if (Directory.Exists(back)) Directory.Delete(back, true);
        }
    }

    [Fact]
    public void DiskFullIsReportedAndDiskStaysConsistent()
    {
        var (path, grant) = FsOpsTests.Prepare(fixtures, "mk-ci-64m");
        using (var dev = new FileImageWriter(path, grant))
        {
            var s = WriteSession.Open(dev, grant);
            using var m = ApfsWritableMount.Mount(s, 0, null);
            string root = m.MountPoint + "\\";
            var chunk = new byte[1024 * 1024];
            new Random(3).NextBytes(chunk);
            var ex = Record.Exception(() =>
            {
                using var fs = new FileStream(root + "dolu.bin", FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough);
                for (int i = 0; i < 200; i++) fs.Write(chunk);
            });
            Assert.IsAssignableFrom<IOException>(ex);
            Assert.True(new DriveInfo(m.MountPoint).AvailableFreeSpace < 4L * 1024 * 1024 + 70L * 1024 * 1024);
            File.Delete(root + "dolu.bin"); // yer geri gelsin
            File.WriteAllText(root + "sonra.txt", "disk dolduktan sonra yazıldı");
        }
        using var c = TestImages.Open(path);
        var r = FullCheck.Run(c);
        Assert.True(r.IsClean, string.Join("\n", r.Errors));
        var v = c.OpenVolumes()[0].Volume!;
        Assert.Equal("disk dolduktan sonra yazıldı"u8.ToArray(), ReaderTests.ReadAll(v, v.Lookup("sonra.txt")));
        Assert.Throws<ApfsException>(() => v.Lookup("dolu.bin"));
        DevOutput.Save(path, "is-yuku-disk-dolu");
    }
}
