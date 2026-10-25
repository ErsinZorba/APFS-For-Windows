using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Apfs.Localization;

namespace Apfs.Tests;

/// <summary>
/// Metin kaynaklarının (src/Apfs.Core/Resources, src/Apfs.Windows/Resources) tutarlılığı. Bozuk bir çeviri hizmeti
/// FormatException ile düşürebilir ya da ekranda ham anahtar gösterebilir; bu testler kaynak dosyalarını doğrudan okur.
/// Kullanıcı metinleri (User) arayüz dillerinde, teknik metinler (Tech) yalnız İngilizce (nötr) ve Türkçededir.
/// </summary>
public partial class ResourceTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "APFS-for-Windows.sln"))) return d.FullName;
        throw new InvalidOperationException("depo kökü bulunamadı");
    }

    public static TheoryData<string, string> Sets() => new()
    {
        { "src/Apfs.Core/Resources", "User" }, { "src/Apfs.Core/Resources", "Tech" },
        { "src/Apfs.Windows/Resources", "User" }, { "src/Apfs.Windows/Resources", "Tech" },
    };

    internal static Dictionary<string, string> Load(string path) =>
        XDocument.Load(path).Root!.Elements("data").ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");

    /// <summary>Bir kaynak kümesinin nötr (İngilizce) dosyası ve çevirileri (dil → anahtar → değer).</summary>
    private static (Dictionary<string, string> Neutral, Dictionary<string, Dictionary<string, string>> Translations) LoadSet(string dir, string name)
    {
        string full = Path.Combine(Root, dir);
        var neutral = Load(Path.Combine(full, name + ".resx"));
        var translations = Directory.GetFiles(full, name + ".*.resx")
            .ToDictionary(p => Path.GetFileName(p)[(name.Length + 1)..^".resx".Length], Load);
        return (neutral, translations);
    }

    [GeneratedRegex(@"\{(\d+)(,-?\d+)?(:[^{}]*)?\}")]
    private static partial Regex Hole();

    /// <summary>Yer tutucular, biçim belirteçleriyle ("{0:N0}"); kaçırılmış {{ }} sayılmaz.</summary>
    internal static SortedSet<string> Holes(string s) => [.. Hole().Matches(s.Replace("{{", "").Replace("}}", "")).Select(m => m.Value)];

    [GeneratedRegex("\"\\{\\d+[^{}\"]*\\}\"")]
    private static partial Regex QuotedHole();

    [Theory]
    [MemberData(nameof(Sets))]
    public void TranslationsHaveExactlyTheNeutralKeys(string dir, string name)
    {
        var (neutral, translations) = LoadSet(dir, name);
        foreach (var (lang, values) in translations)
        {
            Assert.True(Languages.Resolve(lang)?.Name == lang, $"{name}.{lang}.resx: desteklenmeyen dil");
            if (name == "Tech") Assert.True(lang == "tr", $"teknik metinler yalnız İngilizce ve Türkçe: {name}.{lang}.resx");
            var missing = neutral.Keys.Except(values.Keys).ToList();
            var extra = values.Keys.Except(neutral.Keys).ToList();
            Assert.True(missing.Count == 0, $"{dir}/{name}.{lang}.resx eksik: {string.Join(", ", missing)}");
            Assert.True(extra.Count == 0, $"{dir}/{name}.{lang}.resx fazla: {string.Join(", ", extra)}");
        }
        Assert.True(translations.ContainsKey("tr"), $"{dir}/{name}.tr.resx yok");
        if (name == "User")
            foreach (var (lang, _) in Languages.Supported.Where(l => l.Name != "en"))
                Assert.True(translations.ContainsKey(lang), $"{dir}/{name}.{lang}.resx yok (her arayüz dilinin kullanıcı metinleri olmalı)");
    }

    [Theory]
    [MemberData(nameof(Sets))]
    public void ValuesAreWellFormedAndConsistent(string dir, string name)
    {
        var (neutral, translations) = LoadSet(dir, name);
        var problems = new List<string>();
        foreach (var (lang, values) in translations.Append(new("en", neutral)))
            foreach (var (key, value) in values)
            {
                string where = $"{dir}/{name} [{lang}] {key}";
                if (string.IsNullOrWhiteSpace(value)) problems.Add($"{where}: boş");
                foreach (string esc in new[] { "\\\"", "\\n", "\\t", "\\r" })
                    if (value.Contains(esc, StringComparison.Ordinal)) problems.Add($"{where}: kaçış dizisi {esc} (C# metni çözülmeden kopyalanmış)");
                var holes = Holes(value);
                if (neutral.TryGetValue(key, out var en) && !holes.SetEquals(Holes(en)))
                    problems.Add($"{where}: yer tutucular İngilizceden farklı ({string.Join(" ", holes)} / {string.Join(" ", Holes(en))})");
                int count = holes.Count == 0 ? 0 : holes.Max(h => int.Parse(Hole().Match(h).Groups[1].Value, CultureInfo.InvariantCulture)) + 1;
                try { _ = string.Format(CultureInfo.GetCultureInfo(lang), value, Enumerable.Repeat<object>(12345, count).ToArray()); }
                catch (FormatException e) { problems.Add($"{where}: biçimlendirilemiyor ({e.Message})"); }
            }
        if (name == "Tech")
            foreach (var (key, tr) in translations.GetValueOrDefault("tr") ?? [])
            {
                // CheckReport.HideNames yalnız ASCII çift tırnak arasını gizler: tırnaklı ad yer tutucuları iki dilde de aynı kalmalı
                var a = QuotedHole().Matches(tr).Select(m => m.Value).Order();
                var b = QuotedHole().Matches(neutral.GetValueOrDefault(key, "")).Select(m => m.Value).Order();
                if (!a.SequenceEqual(b)) problems.Add($"{dir}/Tech {key}: tırnaklı ad yer tutucuları dillerde farklı (ad gizleme bozulur)");
            }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [GeneratedRegex("\\b(CoreText|WinText)\\.(U|T)\\(\\s*\"([^\"]+)\"")]
    private static partial Regex Call();

    [Fact]
    public void EveryKeyUsedInCodeExistsAndEveryKeyIsUsed()
    {
        var used = new Dictionary<string, HashSet<string>>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            foreach (Match m in Call().Matches(File.ReadAllText(file)))
            {
                string set = (m.Groups[1].Value == "CoreText" ? "src/Apfs.Core/Resources/" : "src/Apfs.Windows/Resources/") + (m.Groups[2].Value == "U" ? "User" : "Tech");
                (used.TryGetValue(set, out var keys) ? keys : used[set] = []).Add(m.Groups[3].Value);
            }
        }
        var problems = new List<string>();
        foreach (var (dir, name) in new[] { ("src/Apfs.Core/Resources", "User"), ("src/Apfs.Core/Resources", "Tech"), ("src/Apfs.Windows/Resources", "User"), ("src/Apfs.Windows/Resources", "Tech") })
        {
            var defined = Load(Path.Combine(Root, dir, name + ".resx")).Keys.ToHashSet();
            var inCode = used.GetValueOrDefault($"{dir}/{name}") ?? [];
            problems.AddRange(inCode.Except(defined).Select(k => $"{dir}/{name}.resx: kodda kullanılan anahtar yok: {k}"));
            problems.AddRange(defined.Except(inCode).Select(k => $"{dir}/{name}.resx: kullanılmayan anahtar: {k}"));
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>Türkçe harf taşıyabilen ama metin olmayan dosyalar: dillerin kendi dillerindeki adları, ad karşılaştırma kodu.</summary>
    private static readonly string[] TurkishAllowedFiles =
        ["src/Apfs.Core/Localization/Languages.cs", "src/Apfs.Core/Util/CaseFolding.cs", "src/Apfs.Core/Util/ApfsNames.cs"];

    /// <summary>Protokol anahtarları (VolumeStatus.State, Notice.Level): tepsi bunlara bakar, çevrilmez.</summary>
    private static readonly HashSet<string> TurkishProtocolKeys = ["\"bağlı\"", "\"uyarı\""];

    /// <summary>
    /// Kullanıcıya ya da günlüğe giden her metin kaynaklardan gelir: kodda Türkçe harfli metin sabiti kalmamalı (yorumlar hariç).
    /// Sınır: yalnız Türkçe harf (çğıöşü…) taşıyan metni yakalar; "dosya yok" gibi yalnız ASCII harfli Türkçeyi göremez.
    /// </summary>
    [Fact]
    public void NoHardcodedTurkishText()
    {
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(Root, file).Replace('\\', '/');
            if (rel.Contains("/obj/") || rel.Contains("/bin/") || TurkishAllowedFiles.Contains(rel)) continue;
            foreach (var (line, literal) in StringLiterals(File.ReadAllText(file)))
                if (literal.IndexOfAny("çğıöşüÇĞİÖŞÜ".ToCharArray()) >= 0 && !TurkishProtocolKeys.Contains(literal))
                    problems.Add($"{rel}:{line}: {literal}");
        }
        Assert.True(problems.Count == 0, "kodda Türkçe metin (kaynağa taşıyın):\n" + string.Join("\n", problems));
    }

    /// <summary>
    /// C# kaynağındaki metin sabitleri (satır, ön ek ve tırnaklarıyla); yorumlar ve karakter sabitleri atlanır. Ara değerli
    /// metnin deliklerindeki iç içe sabitler ayrıca döner (delik metni dış sabitin içinde de kalır).
    /// </summary>
    internal static List<(int Line, string Text)> StringLiterals(string s)
    {
        var result = new List<(int, string)>();
        int line = 1;
        Scan(0, inHole: false);
        return result;

        // s[i..] kodunu tarar; inHole ise eşleşen '}'de durur ve onun konumunu döndürür.
        int Scan(int i, bool inHole)
        {
            int depth = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\n') { line++; i++; }
                else if (c == '/' && At(i + 1, '/')) { while (i < s.Length && s[i] != '\n') i++; }
                else if (c == '/' && At(i + 1, '*'))
                {
                    int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    end = end < 0 ? s.Length : end + 2;
                    line += s.AsSpan(i, end - i).Count('\n');
                    i = end;
                }
                else if (c == '\'')
                {
                    i++;
                    while (i < s.Length && s[i] != '\'') i += s[i] == '\\' ? 2 : 1;
                    i++;
                }
                else if (c == '"' || (c is '$' or '@' && (At(i + 1, '"') || At(i + 1, '$') || At(i + 1, '@'))))
                    i = Literal(i);
                else if (inHole && c is '(' or '[' or '{') { depth++; i++; }
                else if (inHole && c is ')' or ']') { depth--; i++; }
                else if (inHole && c == '}') { if (depth == 0) return i; depth--; i++; }
                else i++;
            }
            return i;
        }

        int Literal(int start)
        {
            int startLine = line, i = start, dollars = 0;
            bool verbatim = false;
            while (s[i] is '$' or '@') { verbatim |= s[i] == '@'; dollars += s[i] == '$' ? 1 : 0; i++; }
            int quotes = 0;
            while (At(i + quotes, '"')) quotes++;
            bool raw = quotes >= 3;
            if (raw) i += quotes;
            else { quotes = 1; i++; }
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\n') { line++; i++; }
                else if (!raw && !verbatim && c == '\\') i += 2;
                else if (c == '"')
                {
                    if (raw)
                    {
                        int n = 0;
                        while (At(i + n, '"')) n++;
                        i += n;
                        if (n >= quotes) break;
                    }
                    else if (verbatim && At(i + 1, '"')) i += 2;
                    else { i++; break; }
                }
                else if (dollars > 0 && c == '{')
                {
                    // ham metinde delik $ sayısı kadar '{' ile açılır; normal metinde "{{" kaçıştır
                    int n = 0;
                    while (At(i + n, '{')) n++;
                    if (raw ? n < dollars : n == 2) { i += n; continue; }
                    i = Scan(i + (raw ? dollars : 1), inHole: true) + 1;
                }
                else i++;
            }
            result.Add((startLine, s[start..Math.Min(i, s.Length)]));
            return i;
        }

        bool At(int i, char c) => i < s.Length && s[i] == c;
    }

    /// <summary>
    /// `apfs dump`ın çıkış kodu, satırlardaki sorun işaretini (Dump.ProblemMarker: "SORUN:" / "PROBLEM:") sayar; işaret iki
    /// dilde de aynı değerlerde geçmeli, yoksa bir dilde sorunlu disk "temiz" (çıkış kodu 0) görünür.
    /// </summary>
    [Fact]
    public void DumpProblemMarkerIsInTheSameValuesInEveryLanguage()
    {
        var (neutral, translations) = LoadSet("src/Apfs.Core/Resources", "Tech");
        var tr = translations["tr"];
        string enMarker = neutral["Dump.ProblemMarker"], trMarker = tr["Dump.ProblemMarker"];
        var mismatched = neutral.Keys.Where(k => k != "Dump.ProblemMarker"
            && neutral[k].Contains(enMarker, StringComparison.Ordinal) != tr[k].Contains(trMarker, StringComparison.Ordinal)).ToList();
        Assert.True(mismatched.Count == 0, "sorun işareti dillerde farklı: " + string.Join(", ", mismatched));
        Assert.Contains(neutral.Keys, k => k != "Dump.ProblemMarker" && neutral[k].Contains(enMarker, StringComparison.Ordinal));
    }
}
