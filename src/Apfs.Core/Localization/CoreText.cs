using System.Resources;
using Apfs.Localization;

namespace Apfs;

/// <summary>
/// Core ve Mount metinleri. <see cref="U"/>: kullanıcıya dönük (her arayüz dilinde), <see cref="T"/>: teknik (İngilizce ve
/// Türkçe). Değerler <c>string.Format</c> biçimindedir; anahtarların varlığını ve çevirilerin tutarlılığını ResourceTests denetler.
/// </summary>
public static class CoreText
{
    private static readonly ResourceManager UserResources = new("Apfs.Resources.User", typeof(CoreText).Assembly);
    private static readonly ResourceManager TechResources = new("Apfs.Resources.Tech", typeof(CoreText).Assembly);

    public static string U(string key, params object?[] args) => Languages.Format(UserResources, key, args);

    public static string T(string key, params object?[] args) => Languages.Format(TechResources, key, args);
}
