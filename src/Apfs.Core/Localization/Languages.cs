using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;

namespace Apfs.Localization;

/// <summary>
/// Programın arayüz dilleri. Nötr kaynak dili İngilizcedir: çevirisi olmayan dil İngilizce görünür. Teknik metinlerin
/// (günlük ayrıntıları, komut satırı, denetim çıktıları) yalnız İngilizce ve Türkçesi vardır.
/// </summary>
public static partial class Languages
{
    /// <summary>Desteklenen diller: kültür adı ve dilin kendi dilindeki adı (dil seçim menüsü için).</summary>
    public static IReadOnlyList<(string Name, string NativeName)> Supported { get; } =
    [
        ("en", "English"), ("tr", "Türkçe"), ("de", "Deutsch"), ("fr", "Français"), ("es", "Español"),
        ("pt-BR", "Português (Brasil)"), ("it", "Italiano"), ("ja", "日本語"), ("zh-Hans", "简体中文"), ("ko", "한국어"),
    ];

    [GeneratedRegex("^[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8}){0,3}$")]
    private static partial Regex CultureName();

    /// <summary>
    /// Bir kültür adını desteklenen dile çevirir: "de-AT" → de, "zh-CN" → zh-Hans, "pt-BR" → pt-BR. Desteklenmiyorsa,
    /// adı geçersizse ya da boşsa null. Dışarıdan gelen değer (kanal isteği, kayıt defteri) için güvenlidir.
    /// </summary>
    public static CultureInfo? Resolve(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 35 || !CultureName().IsMatch(name)) return null;
        CultureInfo culture;
        try { culture = CultureInfo.GetCultureInfo(name); }
        catch (CultureNotFoundException) { return null; }
        return Resolve(culture);
    }

    /// <summary>Kültürü (ya da ata kültürlerinden ilkini) desteklenen dile çevirir; hiçbiri desteklenmiyorsa null.</summary>
    public static CultureInfo? Resolve(CultureInfo culture)
    {
        for (var c = culture; !Equals(c, CultureInfo.InvariantCulture); c = c.Parent)
            foreach (var (supported, _) in Supported)
                if (string.Equals(c.Name, supported, StringComparison.OrdinalIgnoreCase))
                    return CultureInfo.GetCultureInfo(supported);
        return null;
    }

    /// <summary>
    /// Kaynaktan metin alır ve biçimlendirir. Asla hata fırlatmaz: anahtar geçerli dilde yoksa İngilizce (nötr) metin,
    /// o da yoksa anahtarın kendisi; biçim bozuksa biçimlenmemiş metin ve argümanlar.
    /// </summary>
    public static string Format(ResourceManager resources, string key, object?[] args)
    {
        string? text;
        try { text = resources.GetString(key, CultureInfo.CurrentUICulture) ?? resources.GetString(key, CultureInfo.InvariantCulture); }
        catch (MissingManifestResourceException) { text = null; }
        if (text is null) return args.Length == 0 ? key : key + ": " + string.Join(", ", args);
        try { return string.Format(CultureInfo.CurrentCulture, text, args); }
        catch (FormatException) { return args.Length == 0 ? text : text + " (" + string.Join(", ", args) + ")"; }
    }
}
