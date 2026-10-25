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
        string source = o.Require(0, WinText.T("Cli.ArgSource"));
        string? mountPoint = o.Positional.Count > 1 ? ParseMountPoint(o.Positional[1]) : null;

        using var container = SourceOpener.OpenContainer(source, o.Partition);
        var volumes = SelectVolumes(container, o.Has("--volume-set") ? o.Volume : null, o.Has("--all"), Console.Error.WriteLine, Passwords.FromOptions(o));
        if (volumes.Count == 0)
            throw new ApfsException(ApfsErrorCode.NotFound, WinText.T("Cli.NoMountableVolume"));
        if (mountPoint is not null && volumes.Count > 1)
            throw new CliUsageException(WinText.T("Cli.MultipleVolumesNeedIndex"));

        var mounts = new List<ApfsMount>();
        try
        {
            foreach (var volume in volumes)
            {
                var mount = ApfsMount.Mount(volume, mountPoint, msg => Console.Error.WriteLine(WinText.T("Cli.MountWarning", msg)));
                mount.FileSystem.Stats.Enabled = o.Has("--stats");
                mounts.Add(mount);
                Console.WriteLine(WinText.T("Cli.Mounted", mount.MountPoint, volume.Name, (volume.RoleName.Length > 0 ? $" [{volume.RoleName}]" : ""), (mount.FileSystem.KernelReadOnlyFlagSet ? "" : WinText.T("Cli.KernelFlagNotSet"))));
            }
            Console.WriteLine(WinText.T("Cli.PressCtrlC"));
            using var stop = new ManualResetEventSlim();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
            stop.Wait();
            Console.WriteLine(WinText.T("Cli.Unmounting"));
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
                report(WinText.T("Cli.VolumeNotMounted", r.Index, r.Error!.Message));
                continue;
            }
            if (index is null && !includeSystem && !v.IsUserVisible)
            {
                report(WinText.T("Cli.InternalVolumeSkipped", r.Index, v.Name, v.RoleName));
                continue;
            }
            result.Add(v);
        }
        if (index is int i && result.Count == 0 && opened.All(r => r.Index != i))
            throw new ApfsException(ApfsErrorCode.InvalidArgument, WinText.T("Cli.NoSuchVolume", i));
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
            ServiceLog.Write(WinText.T("Cli.LauncherRejectedLog", disk));
            throw new CliUsageException(WinText.T("Cli.LauncherPhysicalOnly"));
        }
        string? mountPoint;
        try
        {
            mountPoint = o.Positional.Count > 1 ? ParseMountPoint(o.Positional[1]) : null;
        }
        catch (CliUsageException e)
        {
            ServiceLog.Write(WinText.T("Cli.LauncherRejected", disk, e.Message));
            throw;
        }
        return new LauncherService(disk, mountPoint, o.Has("--volume-set") ? o.Volume : null).Run();
    }

    private static string? ParseMountPoint(string s)
    {
        if (s == "*") return null;
        if (!DriveLetterRegex().IsMatch(s)) throw new CliUsageException(WinText.T("Cli.InvalidDriveLetter", s));
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
                if (volumes.Count == 0) throw new ApfsException(ApfsErrorCode.NotFound, WinText.T("Cli.NoMountableVolume"));
                foreach (var volume in volumes)
                {
                    var m = ApfsMount.Mount(volume, volumes.Count == 1 ? mountPoint : null, msg => ServiceLog.Write($"{disk}: {msg}"));
                    _mounts.Add(m);
                    ServiceLog.Write(WinText.T("Cli.LauncherMounted", disk, volume.Name, m.MountPoint));
                }
            }
            catch (Exception e)
            {
                ServiceLog.Write(WinText.T("Cli.LauncherMountFailed", disk, e.Message));
                foreach (var m in _mounts) m.Dispose();
                _container?.Dispose();
                throw;
            }
        }

        protected override void OnStop()
        {
            foreach (var m in _mounts) m.Dispose();
            _container?.Dispose();
            ServiceLog.Write(WinText.T("Cli.LauncherUnmounted", disk));
        }
    }
}
