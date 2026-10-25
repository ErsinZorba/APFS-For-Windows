using System.Security.Principal;
using Microsoft.Win32;

namespace Apfs.Cli;

/// <summary>
/// Fiziksel diskleri yönetici olmayan kullanıcıların da bağlayabilmesi için WinFsp Launcher kaydı.
/// Program önce yalnızca yöneticilerin yazabildiği Program Files altına kopyalanır; SYSTEM yetkisiyle çalışan
/// Launcher'ın kullanıcı tarafından değiştirilebilen bir dosyayı çalıştırmaması için bu gereklidir.
/// </summary>
internal static class LauncherRegistration
{
    private const string ServiceName = "apfs";
    private const string ServicesKey = @"SOFTWARE\WinFsp\Services\" + ServiceName; // 32 bit görünümde: WOW6432Node altında
    private static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "APFS for Windows");

    public static int Register()
    {
        RequireAdmin();
        string? winfsp = WinFspInstallDir() ?? throw new ApfsException(ApfsErrorCode.Unsupported, WinText.T("Cli.WinFspMissing"));

        string source = AppContext.BaseDirectory;
        if (!string.Equals(Path.GetFullPath(source).TrimEnd('\\'), InstallDir, StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(InstallDir);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(source, file);
                string dest = Path.Combine(InstallDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
            }
        }

        using var hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using var key = hklm32.CreateSubKey(ServicesKey, writable: true);
        key.SetValue("Executable", Path.Combine(InstallDir, "apfs.exe"), RegistryValueKind.String);
        key.SetValue("CommandLine", "launcher-mount %1 %2", RegistryValueKind.String);
        // Herkes örnek başlatıp durdurabilir; komut satırı yalnızca APFS içeren fiziksel diskleri kabul eder.
        key.SetValue("Security", "D:P(A;;RPWPLC;;;WD)", RegistryValueKind.String);
        key.SetValue("JobControl", 1, RegistryValueKind.DWord);

        string launchctl = Path.Combine(winfsp, "bin", "launchctl-x64.exe");
        Console.WriteLine(WinText.T("Cli.Registered", InstallDir));
        Console.WriteLine(WinText.T("Cli.RegisterMountHint"));
        Console.WriteLine($"  \"{launchctl}\" start {ServiceName} disk2 PhysicalDrive2 X:");
        Console.WriteLine(WinText.T("Cli.RegisterUnmountHint"));
        Console.WriteLine($"  \"{launchctl}\" stop {ServiceName} disk2");
        return 0;
    }

    public static int Unregister()
    {
        RequireAdmin();
        using var hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        hklm32.DeleteSubKeyTree(ServicesKey, throwOnMissingSubKey: false);
        Console.WriteLine(WinText.T("Cli.Unregistered"));
        if (Directory.Exists(InstallDir) &&
            !string.Equals(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\'), InstallDir, StringComparison.OrdinalIgnoreCase))
        {
            Directory.Delete(InstallDir, recursive: true);
            Console.WriteLine(WinText.T("Cli.Deleted", InstallDir));
        }
        return 0;
    }

    internal static string? WinFspInstallDir()
    {
        using var hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using var key = hklm32.OpenSubKey(@"SOFTWARE\WinFsp");
        return key?.GetValue("InstallDir") as string;
    }

    private static void RequireAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator))
            throw new ApfsException(ApfsErrorCode.AccessDenied, WinText.T("Cli.AdminRequired"));
    }
}
