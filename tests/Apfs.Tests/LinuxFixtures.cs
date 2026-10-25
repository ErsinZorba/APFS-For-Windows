using System.IO.Compression;

namespace Apfs.Tests;

/// <summary>
/// apfsprogs/mkapfs ile üretilmiş, apfsck'den temiz geçmiş gerçek boyutlu görüntüler (tests/Apfs.Tests/data/linux, üretici:
/// tools/make-linux-fixtures.sh). Builder görüntülerinden farkı: alan yöneticisi, iç havuz, boş kuyruklar ve reaper tamdır.
/// </summary>
public sealed class LinuxFixtures : IDisposable
{
    /// <summary>mk-*: mkapfs ile boş biçimlendirilmiş; pop-*: linux-apfs-rw sürücüsüyle doldurulmuş (tools/make-linux-populated.sh).</summary>
    public static readonly string[] Names = ["mk-ci-64m", "mk-cs-64m", "mk-ns-64m", "mk-ci-1g", "pop-ci-64m"];

    /// <summary>Linux'un yazarken çıkardığı "sha256  ./yol" listesi.</summary>
    public static Dictionary<string, string> Manifest(string name)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "data", "linux", name + ".manifest")))
        {
            int sep = line.IndexOf("  ", StringComparison.Ordinal);
            d[line[(sep + 4)..]] = line[..sep].ToUpperInvariant();
        }
        return d;
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apfs-linux-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Dictionary<string, string> _paths = [];
    private readonly Lock _lock = new();

    public LinuxFixtures() => Directory.CreateDirectory(_dir);

    /// <summary>
    /// Açılmış (salt okunur kullanılacak) görüntünün yolu. "enc-" önekli ad, aynı görüntünün ilk birimi
    /// <see cref="ImageBuilder.ImageEncryptor"/> ile şifrelenmiş kopyasıdır (parola ImageEncryptor.Password; bkz. TestKeys).
    /// </summary>
    public string Get(string name)
    {
        if (name.StartsWith("enc-", StringComparison.Ordinal))
        {
            string plain = Get(name[4..]);
            lock (_lock)
            {
                if (_paths.TryGetValue(name, out var e)) return e;
                string dst2 = Path.Combine(_dir, name + ".img");
                ImageBuilder.ImageEncryptor.Encrypt(plain, dst2);
                return _paths[name] = dst2;
            }
        }
        lock (_lock)
        {
            if (_paths.TryGetValue(name, out var p)) return p;
            string src = Path.Combine(AppContext.BaseDirectory, "data", "linux", name + ".img.gz");
            string dst = Path.Combine(_dir, name + ".img");
            using (var input = new GZipStream(File.OpenRead(src), CompressionMode.Decompress))
            using (var output = File.Create(dst))
                input.CopyTo(output);
            return _paths[name] = dst;
        }
    }

    /// <summary>Değiştirilebilir kopya (bozma testleri için).</summary>
    public string Copy(string name)
    {
        string dst = Path.Combine(_dir, $"{name}-{Guid.NewGuid():N}.img");
        File.Copy(Get(name), dst);
        return dst;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
