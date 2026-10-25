using Apfs.Container;
using Apfs.ImageBuilder;
using Apfs.IO;
using Apfs.Partitions;

namespace Apfs.Tests;

/// <summary>Test görüntülerini bir kez üretip geçici klasörde tutar.</summary>
public sealed class TestImages : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apfs-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Dictionary<string, (string Path, ApfsImageBuilder Builder)> _images = [];
    private readonly Lock _lock = new();

    public TestImages() => Directory.CreateDirectory(_dir);

    public (string Path, ApfsImageBuilder Builder) Get(string scenario, bool gpt = false)
    {
        string key = scenario + (gpt ? "-gpt" : "");
        lock (_lock)
        {
            if (_images.TryGetValue(key, out var v)) return v;
            var b = Scenarios.Create(scenario);
            b.WithGpt = gpt;
            string path = Path.Combine(_dir, key + ".img");
            b.Write(path);
            return _images[key] = (path, b);
        }
    }

    /// <summary>Görüntünün değiştirilebilir bir kopyası (bozma testleri için).</summary>
    public string Copy(string scenario)
    {
        var (src, _) = Get(scenario);
        string dst = Path.Combine(_dir, $"{scenario}-{Guid.NewGuid():N}.img");
        File.Copy(src, dst);
        return dst;
    }

    public static ApfsContainer Open(string path)
    {
        var dev = new FileImageDevice(path);
        try
        {
            var parts = PartitionLocator.FindApfsPartitions(dev);
            Assert.NotEmpty(parts);
            var p = parts[0];
            IBlockDevice window = p.Offset == 0 ? dev : new OffsetDevice(dev, p.Offset, p.Length, ownsInner: true);
            return ApfsContainer.Open(window, ownsDevice: true);
        }
        catch
        {
            dev.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
