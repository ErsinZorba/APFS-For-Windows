using System.Diagnostics;
using Apfs.IO;
using Apfs.Volume;
using Apfs.Write;
using Xunit.Abstractions;

namespace Apfs.Tests;

/// <summary>Geliştirme: yazma motorunun hız ölçümü (APFS_PERF=1 ile koşar; aksi hâlde hemen döner).</summary>
public class PerfProbeTests(LinuxFixtures fixtures, ITestOutputHelper output) : IClassFixture<LinuxFixtures>
{
    [Fact]
    public void MeasureCreateAndWrite()
    {
        if (Environment.GetEnvironmentVariable("APFS_PERF") != "1") return;
        var (path, grant) = FsOpsTests.Prepare(fixtures, "mk-ci-1g");
        using var dev = new FileImageWriter(path, grant);
        using var s = WriteSession.Open(dev, grant);
        var v = s.Volume(0);
        var view = v.View(s.Container);
        var sw = Stopwatch.StartNew();
        ulong d = v.CreateDirectory(Inode.RootDirId, "çok");
        for (int i = 0; i < 2000; i++)
        {
            v.CreateFile(d, $"dosya-{i}.dat");
            if (i % 500 == 499) output.WriteLine($"{i + 1} dosya: {sw.Elapsed.TotalSeconds:N2} sn");
        }
        output.WriteLine($"2000 oluşturma: {sw.Elapsed.TotalSeconds:N2} sn");
        sw.Restart();
        for (int i = 0; i < 2000; i++) view.FindEntry(d, $"dosya-{i}.dat"); // görüntüden arama
        output.WriteLine($"2000 görüntü araması: {sw.Elapsed.TotalSeconds:N2} sn");
        sw.Restart();
        ulong f = v.CreateFile(Inode.RootDirId, "büyük.bin");
        var chunk = new byte[1 << 20];
        for (int i = 0; i < 300; i++) v.Write(f, (ulong)i << 20, chunk);
        output.WriteLine($"300 MB yazma: {sw.Elapsed.TotalSeconds:N2} sn");
        sw.Restart();
        s.Commit();
        output.WriteLine($"commit: {sw.Elapsed.TotalSeconds:N2} sn");
    }
}
