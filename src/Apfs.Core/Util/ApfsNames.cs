using System.Text;

namespace Apfs.Util;

/// <summary>
/// APFS ad karşılaştırma anahtarları. APFS adları UTF-8 saklar ve (normalizasyon duyarsız birimlerde) NFD'ye göre karşılaştırır;
/// büyük/küçük harf duyarsız birimlerde ayrıca harf katlaması uygular. Windows çoğunlukla NFC gönderdiği için
/// "ş" gibi harfler iki farklı bayt dizisiyle gelebilir; anahtarlar bu farkı yok eder.
/// </summary>
public static class ApfsNames
{
    /// <summary>Normalizasyon anahtarı: NFD.</summary>
    public static string NormalizationKey(string name) => SafeNormalize(name, NormalizationForm.FormD);

    /// <summary>Büyük/küçük harf ve normalizasyon duyarsız anahtar: NFD → tam harf katlaması → NFD.</summary>
    public static string FoldKey(string name) =>
        SafeNormalize(CaseFolding.Fold(SafeNormalize(name, NormalizationForm.FormD)), NormalizationForm.FormD);

    /// <summary>
    /// Dizin girdisi ad karması (j_drec_hashed_key_t): (duyarsızsa katlanmış) NFD ad → UTF-32LE → CRC32C (başlangıç 0xFFFFFFFF,
    /// son tersleme yok) → alt 22 bit. Okuyucu aramada bunu KULLANMAZ; yalnızca `apfs check` teşhisi içindir.
    /// </summary>
    public static uint Hash(string name, bool caseInsensitive)
    {
        string n = caseInsensitive ? FoldKey(name) : NormalizationKey(name);
        uint crc = 0xFFFFFFFF;
        Span<byte> cp = stackalloc byte[4];
        foreach (var rune in n.EnumerateRunes())
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(cp, rune.Value);
            foreach (byte b in cp)
            {
                crc ^= b;
                for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0x82F63B78 ^ (crc >> 1) : crc >> 1;
            }
        }
        return crc & 0x3FFFFF;
    }

    /// <summary>
    /// Geçersiz Unicode (eşleşmemiş vekil karakter) içeren adlarda normalizasyon hata verir; o durumda ad olduğu gibi kullanılır.
    /// Bozuk bir ad yalnızca birebir aramayla bulunur, program çökmez.
    /// </summary>
    private static string SafeNormalize(string s, NormalizationForm form)
    {
        try
        {
            return s.IsNormalized(form) ? s : s.Normalize(form);
        }
        catch (ArgumentException)
        {
            return s;
        }
    }
}
