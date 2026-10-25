using System.Text;

namespace Apfs.Util;

/// <summary>
/// Unicode tam harf katlaması (CaseFolding.txt, C + F; Türkçe'ye özel T hariç).
/// Kültürden tamamen bağımsızdır: makinenin dili tr-TR olsa da "I" → "i", "ı" değişmez, "İ" → "i̇".
/// <c>ToUpperInvariant</c>/<c>ToLowerInvariant</c> bilinçli olarak kullanılmaz: "ı"yı "I" yapar ve APFS'ten farklı davranır.
/// </summary>
internal static partial class CaseFolding
{
    public static string Fold(string s)
    {
        StringBuilder? sb = null;
        int i = 0;
        foreach (var rune in s.EnumerateRunes())
        {
            int idx = Array.BinarySearch(Keys, rune.Value);
            if (idx >= 0)
            {
                sb ??= new StringBuilder(s.Length + 8).Append(s, 0, i);
                foreach (int cp in Values[idx]) sb.Append(char.ConvertFromUtf32(cp));
            }
            else
            {
                sb?.Append(rune.ToString());
            }
            i += rune.Utf16SequenceLength;
        }
        return sb?.ToString() ?? s;
    }
}
