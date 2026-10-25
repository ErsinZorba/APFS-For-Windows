using System.Globalization;
using Apfs.Localization;
using Microsoft.Win32;

namespace Apfs.Windows.Localization;

/// <summary>
/// Kullanıcının elle seçtiği arayüz dili (HKCU\Software\APFS for Windows, "Language"). Değer yoksa ya da geçersizse
/// Windows'un görüntü dili kullanılır.
/// </summary>
public static class LanguageSetting
{
    public const string DefaultKeyPath = @"Software\APFS for Windows";
    private const string ValueName = "Language";

    /// <summary>Kayıt yolu; yalnız testler değiştirir.</summary>
    public static string KeyPath { get; set; } = DefaultKeyPath;

    /// <summary>Kayıtlı ve desteklenen dil; yoksa null ("Windows dili").</summary>
    public static CultureInfo? Load()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return Languages.Resolve(key?.GetValue(ValueName) as string);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    /// <summary>Seçimi kaydeder; null "Windows dili"ne döner (değer silinir).</summary>
    public static void Save(CultureInfo? language)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (language is null) key.DeleteValue(ValueName, throwOnMissingValue: false);
        else key.SetValue(ValueName, language.Name, RegistryValueKind.String);
    }

    /// <summary>
    /// Geçerli arayüz dili: kayıtlı seçim, yoksa Windows'un görüntü dili (desteklenmiyorsa İngilizce). Süreç kendi dilini
    /// ayarlamadan önce çağrılmalıdır (sonra <see cref="CultureInfo.CurrentUICulture"/> artık Windows'unki değildir).
    /// </summary>
    public static CultureInfo Effective() =>
        Load() ?? Languages.Resolve(CultureInfo.CurrentUICulture) ?? CultureInfo.GetCultureInfo("en");
}
