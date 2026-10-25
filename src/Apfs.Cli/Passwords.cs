using Apfs.Volume;

namespace Apfs.Cli;

/// <summary>
/// Şifreli birimler için parola sağlayıcısı. Parola komut satırı argümanı olarak ALINMAZ (işlem listesinde görünürdü):
/// --password-prompt konsoldan yankısız okur, --password-stdin standart girdinin ilk satırını kullanır.
/// </summary>
internal static class Passwords
{
    public static Func<VolumeLockInfo, string?>? FromOptions(CliOptions o)
    {
        if (o.Has("--password-stdin"))
        {
            // Konsolun kod sayfasına (ör. 857) bırakılırsa Türkçe harfler bozulur: girdiyi açıkça UTF-8 oku (BOM varsa atlanır).
            using var reader = new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            string? line = reader.ReadLine()?.TrimEnd('\r');
            return _ => line;
        }
        if (o.Has("--password-prompt"))
            return Prompt;
        return null;
    }

    private static string? Prompt(VolumeLockInfo info)
    {
        Console.Error.WriteLine(WinText.T("Cli.VolumeEncrypted", info.Name, (info.Hint is null ? "" : WinText.T("Cli.PasswordHint", info.Hint))));
        Console.Error.Write(WinText.T("Cli.PasswordPrompt"));
        var chars = new List<char>();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Escape) { Console.Error.WriteLine(); return null; }
            if (k.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); continue; }
            if (!char.IsControl(k.KeyChar)) chars.Add(k.KeyChar);
        }
        Console.Error.WriteLine();
        var s = new string([.. chars]);
        chars.Clear();
        return s;
    }
}
