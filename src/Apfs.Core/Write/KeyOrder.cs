using Apfs.Util;

namespace Apfs.Write;

/// <summary>
/// B-ağacı anahtar sıralamaları (apfsck keycmp ile aynı kurallar). Ağaçta kayıtlar bu sırayla durmalıdır; yanlış sıra hem aramayı
/// bozar hem de diski "bozuk" gösterir.
/// </summary>
internal static class KeyOrder
{
    /// <summary>omap_key_t: (oid, xid).</summary>
    public static int Omap(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int c = Bin.U64(a, 0).CompareTo(Bin.U64(b, 0));
        return c != 0 ? c : Bin.U64(a, 8).CompareTo(Bin.U64(b, 8));
    }

    /// <summary>spaceman_free_queue_key_t: (xid, paddr).</summary>
    public static int FreeQueue(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => Omap(a, b);

    /// <summary>j_phys_ext_key_t: fiziksel adres (j_key'in nesne kimliği kısmı).</summary>
    public static int ExtentRef(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => Fs(a, b);

    private const ulong IdMask = 0x0FFFFFFFFFFFFFFF;

    /// <summary>
    /// Dosya sistemi ağacı: (nesne kimliği, tür, türe özgü sayı, ad). Sayı: karmalı dizin girdisinde ad karması (uzunluk bitleri
    /// maskelenir), extent'te mantıksal ofset, kardeş bağlantısında kardeş kimliği; ad: dizin girdisi ve öznitelik adı (bayt bayt).
    /// </summary>
    public static int Fs(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        ulong ha = Bin.U64(a, 0), hb = Bin.U64(b, 0);
        int c = (ha & IdMask).CompareTo(hb & IdMask);
        if (c != 0) return c;
        c = (ha >> 60).CompareTo(hb >> 60);
        if (c != 0) return c;
        var type = (Volume.JType)(ha >> 60);
        c = Number(type, a).CompareTo(Number(type, b));
        if (c != 0) return c;
        var na = Name(type, a);
        var nb = Name(type, b);
        return na.SequenceCompareTo(nb) switch { < 0 => -1, > 0 => 1, _ => 0 };
    }

    private static ulong Number(Volume.JType type, ReadOnlySpan<byte> k) => type switch
    {
        Volume.JType.DirRec when IsHashedDrec(k) => Bin.U32(k, 8) & ~0x3FFu,
        Volume.JType.FileExtent or Volume.JType.SiblingLink => Bin.U64(k, 8),
        Volume.JType.FileInfo => Bin.U64(k, 8) & 0x00FFFFFFFFFFFFFF,
        _ => 0,
    };

    /// <summary>Ad (sondaki NUL hariç; karşılaştırma C strcmp gibi NUL'a kadar).</summary>
    private static ReadOnlySpan<byte> Name(Volume.JType type, ReadOnlySpan<byte> k)
    {
        ReadOnlySpan<byte> s = type switch
        {
            Volume.JType.DirRec => IsHashedDrec(k) ? k[12..] : k[10..],
            Volume.JType.Xattr or Volume.JType.SnapName => k[10..],
            _ => [],
        };
        int nul = s.IndexOf((byte)0);
        return nul >= 0 ? s[..nul] : s;
    }

    /// <summary>Karmalı (j_drec_hashed_key_t) mı: anahtar uzunluğu 12 + ad uzunluğu ise.</summary>
    internal static bool IsHashedDrec(ReadOnlySpan<byte> k) =>
        k.Length >= 12 && 12 + (int)(Bin.U32(k, 8) & 0x3FF) == k.Length;
}
