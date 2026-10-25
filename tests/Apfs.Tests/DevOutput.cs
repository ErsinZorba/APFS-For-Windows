namespace Apfs.Tests;

/// <summary>
/// Geliştirme: APFS_DEV_OUT ortam değişkeni bir klasörü gösteriyorsa yazma testlerinin ürettiği görüntüler oraya kopyalanır;
/// sonra bağımsız denetleyicilerle (apfsck, Mac fsck_apfs) sınanır. Değişken yoksa hiçbir şey yapmaz.
/// </summary>
internal static class DevOutput
{
    public static void Save(string imagePath, string label)
    {
        string? dir = Environment.GetEnvironmentVariable("APFS_DEV_OUT");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        File.Copy(imagePath, Path.Combine(dir, label + ".img"), overwrite: true);
    }
}
