using Apfs.Util;

namespace Apfs.Container;

/// <summary>
/// Kapsayıcının reaper'ı (nx_reaper_phys) ve reap list'i (nx_reap_list_phys). macOS bir birimi ya da anlık görüntüyü reaper
/// üzerinden parça parça siler; silme bitince liste nesnesini boş olarak yerinde bırakır (kullanıcının HDD'si, 2026-10-25:
/// sonraki 4 / tamamlanan 3, baş = kuyruk = liste, liste sayısı 1; liste count 0, first/last 0xFFFFFFFF, free 1, gövdede eski
/// girdiler). Boşta sayılan iki durum (apfsck super.c parse_reaper'ın kabul ettikleri, üstüne kendi güvenlik koşullarımız):
/// <list type="bullet">
/// <item>liste yok: baş, kuyruk, liste sayısı ve işlenen nesne 0;</item>
/// <item>tek boş liste: işlenen nesne 0, CONTINUE kapalı, tamamlanan + 1 = sonraki, birime bağlı değil, baş = kuyruk, liste sayısı
/// 1; liste checkpoint'te, türü NX_REAP_LIST, next/flags/count 0, first/last 0xFFFFFFFF, max = (blok − 64) / 40. Gövde (eski
/// girdiler, free) denetlenmez; yazıcı listeyi olduğu gibi taşır.</item>
/// </list>
/// Başka her durum meşgul ya da bozuktur (<see cref="Problem"/>); tam denetim hata verir, yazma kapısı kapanır.
/// </summary>
public sealed class ReaperState
{
    public const uint FlagContinue = 0x2;
    private const int ListHeader = 64, ListEntry = 40;

    private ReaperState(byte[] raw, IReadOnlyList<(CheckpointMapping Map, byte[] Raw)> lists, string? problem)
    {
        Raw = raw;
        Lists = lists;
        Problem = problem;
    }

    /// <summary>Reaper nesnesi (ham, sağlaması doğrulanmış).</summary>
    public byte[] Raw { get; }

    /// <summary>Reaper'ın gösterdiği liste (boşta durumda en fazla bir tane).</summary>
    public IReadOnlyList<(CheckpointMapping Map, byte[] Raw)> Lists { get; }

    /// <summary>null: boşta. Aksi hâlde neden ("reaper meşgul: …" ya da "reaper listesi …").</summary>
    public string? Problem { get; }

    public bool IsIdle => Problem is null;

    /// <summary>Reaper checkpoint haritasında olmalıdır (yoksa okuma istisna atar).</summary>
    public static ReaperState Read(Checkpoint cp, NxSuperblock sb)
    {
        var r = cp.ReadEphemeral(sb.ReaperOid);
        ulong next = Bin.U64(r, 32), completed = Bin.U64(r, 40), head = Bin.U64(r, 48), tail = Bin.U64(r, 56);
        uint flags = Bin.U32(r, 64), count = Bin.U32(r, 68);
        ulong fsOid = Bin.U64(r, 80), oid = Bin.U64(r, 88);
        var lists = new List<(CheckpointMapping, byte[])>();

        string? problem = null;
        if (oid != 0) problem = $"reaper meşgul: nesne {oid} siliniyor";
        else if ((flags & FlagContinue) != 0) problem = "reaper meşgul: yarım kalmış bir silme var";
        else if (head == 0)
        {
            if (tail != 0 || count != 0) problem = "reaper meşgul: liste göstergeleri tutarsız";
        }
        else if (completed + 1 != next) problem = $"reaper meşgul: bitmemiş silme var (sonraki {next}, tamamlanan {completed})";
        else if (fsOid != 0) problem = $"reaper meşgul: birim {fsOid} için silme kaydı var";
        else if (head != tail || count != 1) problem = $"reaper meşgul: birden çok liste (baş {head}, kuyruk {tail}, sayı {count})";
        else if (cp.Find(head) is not { } m) problem = $"reaper listesi {head} checkpoint haritasında yok";
        else
        {
            var l = cp.ReadEphemeral(m);
            lists.Add((m, l));
            if (m.ObjectType != ApfsConstants.TypeNxReapList || m.Subtype != 0) problem = $"reaper listesi {head}: tür yanlış";
            else if (l.Length != sb.BlockSize || Bin.U32(l, 44) != (sb.BlockSize - ListHeader) / ListEntry)
                problem = $"reaper listesi {head}: en fazla girdi sayısı yanlış";
            else if (Bin.U64(l, 32) != 0 || Bin.U32(l, 40) != 0 || Bin.U32(l, 48) != 0
                     || Bin.U32(l, 52) != uint.MaxValue || Bin.U32(l, 56) != uint.MaxValue)
                problem = $"reaper meşgul: liste {head} boş değil";
        }
        return new ReaperState(r, lists, problem);
    }

    /// <summary>
    /// Yazıcının commit'te yeniden yazmadığı geçici nesneler. Yazıcı yalnız şunları yazar: reaper, onun listesi, alan yöneticisi ve
    /// boş kuyruk ağaçlarının düğümleri (alt tür SPACEMAN_FREE_QUEUE; boşalan düğümler bilerek düşer). Başka bir tür commit'te
    /// sessizce kaybolurdu; böyle bir kapsayıcıya yazılmaz.
    /// </summary>
    public IEnumerable<CheckpointMapping> NotRewrittenByWriter(Checkpoint cp, NxSuperblock sb) =>
        cp.Mappings.Where(m => m.Oid != sb.ReaperOid && m.Oid != sb.SpacemanOid
            && !Lists.Any(l => l.Map.Oid == m.Oid)
            && !(m.ObjectType is ApfsConstants.TypeBTree or ApfsConstants.TypeBTreeNode && m.Subtype == ApfsConstants.TypeSpacemanFreeQueue));
}
