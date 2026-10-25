using System.Text;

namespace Apfs.Util;

/// <summary>
/// macOS'ta geçerli olup Windows'ta yasak olan karakterleri Unicode özel kullanım alanına eşler (SFM, "Services for Mac" eşlemesi;
/// Windows'un ve macOS'un SMB paylaşımlarında Mac dosya adları için kullandığı standart). Örnek: "a:b" → "ab".
/// Böylece bu dosyalar Gezgin'de görünür, açılır ve NTFS'e kopyalanabilir.
/// </summary>
public static class WindowsNames
{
    private const string Forbidden = "\"*:<>?\\|"; // sırasıyla U+F020 … U+F027
    private const char TrailingSpace = '';
    private const char TrailingPeriod = '';

    /// <summary>APFS adını Windows'ta gösterilecek ada çevirir.</summary>
    public static string ToWindows(string name)
    {
        if (!NeedsMapping(name)) return name;
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (c is >= '\u0001' and <= '\u001F') sb.Append((char)(0xF000 + c));
            else if (Forbidden.IndexOf(c) is int k and >= 0) sb.Append((char)(0xF020 + k));
            else sb.Append(c);
        }
        // Windows sondaki boşluk ve noktaları siler; bu yüzden sondakiler de eşlenir.
        for (int i = sb.Length - 1; i >= 0 && sb[i] is ' ' or '.'; i--)
            sb[i] = sb[i] == ' ' ? TrailingSpace : TrailingPeriod;
        return sb.ToString();
    }

    /// <summary>Windows'tan gelen adı APFS adına geri çevirir.</summary>
    public static string FromWindows(string name)
    {
        bool any = false;
        foreach (char c in name) if (c is >= '' and <= '') { any = true; break; }
        if (!any) return name;
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            sb.Append(c switch
            {
                >= '' and <= '' => (char)(c - 0xF000),
                >= '' and <= '' => Forbidden[c - 0xF020],
                TrailingSpace => ' ',
                TrailingPeriod => '.',
                _ => c,
            });
        }
        return sb.ToString();
    }

    private static bool NeedsMapping(string name)
    {
        if (name.Length == 0) return false;
        if (name[^1] is ' ' or '.') return name is not "." and not "..";
        foreach (char c in name)
            if (c < ' ' || Forbidden.IndexOf(c) >= 0) return true;
        return false;
    }
}
