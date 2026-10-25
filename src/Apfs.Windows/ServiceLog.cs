namespace Apfs.Windows;

/// <summary>
/// Arka planda (hizmet veya WinFsp Launcher altında) çalışırken mesajların kaybolmaması için basit günlük:
/// %ProgramData%\APFS for Windows\log.txt. Bu dosyaya yazılamıyorsa (ör. hizmet değil normal kullanıcıyken)
/// %LOCALAPPDATA%\APFS for Windows\log.txt kullanılır. Parola veya dosya içeriği asla yazılmaz.
/// </summary>
public static class ServiceLog
{
    private const long MaxBytes = 1 << 20;
    private static readonly Lock Gate = new();

    public static string PathName { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "APFS for Windows", "log.txt");

    public static string FallbackPathName { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "APFS for Windows", "log.txt");

    /// <summary>Geliştirme (konsol) modunda satırlar ayrıca standart hataya yazılır.</summary>
    public static bool EchoToConsole { get; set; }

    public static void Write(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}";
        if (EchoToConsole) Console.Error.WriteLine(line);
        lock (Gate)
        {
            if (!TryAppend(PathName, line)) TryAppend(FallbackPathName, line);
        }
    }

    private static bool TryAppend(string path, string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxBytes)
                File.Move(path, path + ".1", overwrite: true);
            File.AppendAllText(path, line + Environment.NewLine);
            return true;
        }
        catch (IOException) { return false; } // günlük yazılamazsa asıl işi engellememeli
        catch (UnauthorizedAccessException) { return false; }
    }
}
