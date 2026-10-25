using Apfs.Util;

namespace Apfs.Volume;

/// <summary>
/// Bir klasörün bellek içi dizini. Arama sırası:
/// 1. Birebir ad (her zaman önce; aynı anahtara düşen birden çok ad varsa doğru olanı seçer).
/// 2. Büyük/küçük harf duyarsız birimde: katlanmış anahtar (NFD + tam Unicode katlaması).
///    Duyarlı birimde: NFD anahtarı; o da tutmazsa ve TEK bir aday varsa katlanmış anahtar
///    (Windows uygulamaları adı farklı harf büyüklüğüyle isteyebilir; birden çok aday varsa tahmin yapılmaz).
/// Arama B-ağacındaki ad karmasına dayanmaz: karma hesabındaki olası bir Unicode sürüm farkı dosyayı "bulunamaz" yapmaz.
/// Yazma oturumunun görüntüsünde dizin yerinde güncellenir (<see cref="SessionAdd"/>/<see cref="SessionRemove"/>).
/// </summary>
public sealed class DirectoryIndex
{
    private readonly bool _caseInsensitive;
    private readonly List<DirEntry> _entries;
    private readonly Dictionary<string, DirEntry> _exact;
    private readonly Dictionary<string, List<DirEntry>> _normalized;
    private readonly Dictionary<string, List<DirEntry>> _folded;

    public DirectoryIndex(List<DirEntry> entries, bool caseInsensitive)
    {
        _entries = entries;
        _caseInsensitive = caseInsensitive;
        _exact = new(entries.Count, StringComparer.Ordinal);
        _normalized = new(entries.Count, StringComparer.Ordinal);
        _folded = new(entries.Count, StringComparer.Ordinal);
        foreach (var e in entries) Index(e);
    }

    public IReadOnlyList<DirEntry> Entries => _entries;

    private void Index(DirEntry e)
    {
        _exact.TryAdd(e.Name, e);
        AddCandidate(_normalized, ApfsNames.NormalizationKey(e.Name), e);
        AddCandidate(_folded, ApfsNames.FoldKey(e.Name), e);
    }

    public DirEntry? Find(string name)
    {
        if (_exact.TryGetValue(name, out var exact)) return exact;
        if (_caseInsensitive)
            return _folded.TryGetValue(ApfsNames.FoldKey(name), out var f) ? f[0] : null; // birim geçerliyse çakışma olmaz
        if (_normalized.TryGetValue(ApfsNames.NormalizationKey(name), out var n) && n.Count == 1) return n[0];
        return _folded.TryGetValue(ApfsNames.FoldKey(name), out var g) && g.Count == 1 ? g[0] : null; // birden çok adayda null
    }

    /// <summary>
    /// Birimin KATI kuralıyla çakışan girdi: harf duyarsız birimde katlanmış ad, normalleştirmeye duyarsız birimde NFD,
    /// tam duyarlı birimde bayt bayt. (Arama kolaylığı uygulanmaz.)
    /// </summary>
    public DirEntry? FindCollision(string name, bool normalizationInsensitive)
    {
        if (_caseInsensitive) return _folded.TryGetValue(ApfsNames.FoldKey(name), out var f) ? f[0] : null;
        if (normalizationInsensitive) return _normalized.TryGetValue(ApfsNames.NormalizationKey(name), out var n) ? n[0] : null;
        return _exact.TryGetValue(name, out var e) ? e : null;
    }

    private static void AddCandidate(Dictionary<string, List<DirEntry>> map, string key, DirEntry e)
    {
        if (map.TryGetValue(key, out var list)) list.Add(e); else map[key] = [e];
    }

    private static void RemoveCandidate(Dictionary<string, List<DirEntry>> map, string key, string name)
    {
        if (!map.TryGetValue(key, out var list)) return;
        list.RemoveAll(x => x.Name == name);
        if (list.Count == 0) map.Remove(key);
    }

    /// <summary>Yazma oturumu: girdi eklendi.</summary>
    internal void SessionAdd(DirEntry e)
    {
        SessionRemove(e.Name);
        _entries.Add(e);
        Index(e);
    }

    /// <summary>Yazma oturumu: girdi silindi (diskteki gerçek adıyla).</summary>
    internal void SessionRemove(string name)
    {
        if (_entries.RemoveAll(x => x.Name == name) == 0) return;
        _exact.Remove(name);
        foreach (var other in _entries) if (other.Name == name) _exact.TryAdd(name, other);
        RemoveCandidate(_normalized, ApfsNames.NormalizationKey(name), name);
        RemoveCandidate(_folded, ApfsNames.FoldKey(name), name);
    }
}
