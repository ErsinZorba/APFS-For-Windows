using System.Text.RegularExpressions;
using Apfs.Container;
using Apfs.Mount;
using Apfs.Volume;
using Apfs.Windows;

namespace Apfs.Cli;

/// <summary>"mount" ve WinFsp Launcher'ın çağırdığı "launcher-mount" komutları.</summary>
internal static partial class MountCommand
{
    [GeneratedRegex("^[A-Za-z]:$")]
    private static partial Regex DriveLetterRegex();

    public static int Run(CliOptions o)
    {
        string source = o.Require(0, "kaynak");
        string? mountPoint = o.Positional.Count > 1 ? ParseMountPoint(o.Positional[1]) : null;

        using var container = SourceOpener.OpenContainer(source, o.Partition);
        var volumes = SelectVolumes(container, o.Has("--volume-set") ? o.Volume : null, o.Has("--all"), Console.Error.WriteLine, Passwords.FromOptions(o));
        if (volumes.Count == 0)
            throw new ApfsException(ApfsErrorCode.NotFound, "bağlanabilecek birim yok");
        if (mountPoint is not null && volumes.Count > 1)
            throw new CliUsageException("diskte birden çok birim var; sürücü harfi verecekseniz --volume N ile birini seçin");

        var mounts = new List<ApfsMount>();
        try
        {
            foreach (var volume in volumes)
            {
                var mount = ApfsMount.Mount(volume, mountPoint, msg => Console.Error.WriteLine($"Uyarı: {msg}"));
                mount.FileSystem.Stats.Enabled = o.Has("--stats");
                mounts.Add(mount);
                Console.WriteLine($"Bağlandı: {mount.MountPoint}  \"{volume.Name}\"{(volume.RoleName.Length > 0 ? $" [{volume.RoleName}]" : "")}  (salt okunur{(mount.FileSystem.KernelReadOnlyFlagSet ? "" : ", çekirdek bayrağı ayarlanamadı")})");
            }
            Console.WriteLine("Ayırmak için Ctrl+C'ye basın.");
            using var stop = new ManualResetEventSlim();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
            stop.Wait();
            Console.WriteLine("Ayrılıyor...");
            foreach (var m in mounts.Where(m => m.FileSystem.Stats.Enabled))
                foreach (var line in m.FileSystem.Stats.Report()) Console.WriteLine($"{m.MountPoint} {line}");
        }
        finally
        {
            foreach (var m in mounts) m.Dispose();
        }
        return 0;
    }

    /// <summary>
    /// Bağlanacak birimleri seçer. Açılamayan (bozuk, şifreli, desteklenmeyen) birimler diğerlerini engellemez; nedeni bildirilir.
    /// </summary>
    internal static List<ApfsVolume> SelectVolumes(ApfsContainer c, int? index, bool includeSystem, Action<string> report,
        Func<VolumeLockInfo, string?>? passwords = null)
    {
        var result = new List<ApfsVolume>();
        var opened = c.OpenVolumes(passwords);
        foreach (var r in opened)
        {
            if (index is int want && r.Index != want) continue;
            if (r.Volume is not { } v)
            {
                report($"Birim {r.Index} bağlanmadı: {r.Error!.Message}");
                continue;
            }
            if (index is null && !includeSystem && !v.IsUserVisible)
            {
                report($"Birim {r.Index} \"{v.Name}\" [{v.RoleName}] macOS'un iç birimi olduğu için bağlanmadı (göstermek için --all).");
                continue;
            }
            result.Add(v);
        }
        if (index is int i && result.Count == 0 && opened.All(r => r.Index != i))
            throw new ApfsException(ApfsErrorCode.InvalidArgument, $"birim {i} yok");
        return result;
    }

    /// <summary>
    /// WinFsp Launcher (LocalSystem) tarafından başlatılır. Yetki sınırı: yalnızca \\.\PhysicalDriveN kabul edilir ve
    /// disk bir APFS bölümü içermelidir. Görüntü dosyası yolu kabul edilmez (SYSTEM yetkisiyle başkasının dosyasını okutmamak için).
    /// </summary>
    public static int RunFromLauncher(CliOptions o)
    {
        string disk = o.Require(0, "disk");
        if (SourceOpener.ParsePhysicalDrive(disk) is null)
        {
            ServiceLog.Write($"Reddedildi: \"{disk}\" bir fiziksel disk değil; arka planda yalnızca PhysicalDriveN bağlanabilir.");
            throw new CliUsageException("Launcher üzerinden yalnızca PhysicalDriveN biçiminde fiziksel disk bağlanabilir");
        }
        string? mountPoint;
        try
        {
            mountPoint = o.Positional.Count > 1 ? ParseMountPoint(o.Positional[1]) : null;
        }
        catch (CliUsageException e)
        {
            ServiceLog.Write($"{disk}: reddedildi: {e.Message}");
            throw;
        }
        return new LauncherService(disk, mountPoint, o.Has("--volume-set") ? o.Volume : null).Run();
    }

    private static string? ParseMountPoint(string s)
    {
        if (s == "*") return null;
        if (!DriveLetterRegex().IsMatch(s)) throw new CliUsageException($"geçersiz sürücü harfi: {s} (örnek: X:)");
        return s.ToUpperInvariant();
    }

    /// <summary>WinFsp hizmet çatısı: Launcher durdurduğunda OnStop çağrılır.</summary>
    private sealed class LauncherService(string disk, string? mountPoint, int? volumeIndex) : Fsp.Service("apfs")
    {
        private ApfsContainer? _container;
        private readonly List<ApfsMount> _mounts = [];

        protected override void OnStart(string[] args)
        {
            try
            {
                _container = SourceOpener.OpenContainer(disk);
                var volumes = SelectVolumes(_container, volumeIndex, includeSystem: false, msg => ServiceLog.Write($"{disk}: {msg}"));
                if (volumes.Count == 0) throw new ApfsException(ApfsErrorCode.NotFound, "bağlanabilecek birim yok");
                foreach (var volume in volumes)
                {
                    var m = ApfsMount.Mount(volume, volumes.Count == 1 ? mountPoint : null, msg => ServiceLog.Write($"{disk}: {msg}"));
                    _mounts.Add(m);
                    ServiceLog.Write($"{disk}: \"{volume.Name}\" {m.MountPoint} olarak bağlandı (salt okunur).");
                }
            }
            catch (Exception e)
            {
                ServiceLog.Write($"{disk}: bağlanamadı: {e.Message}");
                foreach (var m in _mounts) m.Dispose();
                _container?.Dispose();
                throw;
            }
        }

        protected override void OnStop()
        {
            foreach (var m in _mounts) m.Dispose();
            _container?.Dispose();
            ServiceLog.Write($"{disk}: ayrıldı.");
        }
    }
}
