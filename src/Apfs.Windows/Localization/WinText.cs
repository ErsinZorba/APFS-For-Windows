using System.Resources;
using Apfs.Localization;

namespace Apfs;

/// <summary>
/// Windows tarafının (hizmet, tepsi, komut satırı) metinleri. <see cref="U"/>: kullanıcıya dönük (her arayüz dilinde),
/// <see cref="T"/>: teknik (İngilizce ve Türkçe). Ayrıntı: <see cref="CoreText"/>.
/// </summary>
public static class WinText
{
    private static readonly ResourceManager UserResources = new("Apfs.Windows.Resources.User", typeof(WinText).Assembly);
    private static readonly ResourceManager TechResources = new("Apfs.Windows.Resources.Tech", typeof(WinText).Assembly);

    public static string U(string key, params object?[] args) => Languages.Format(UserResources, key, args);

    public static string T(string key, params object?[] args) => Languages.Format(TechResources, key, args);
}
