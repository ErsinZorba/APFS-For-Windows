using Apfs.Container;
using Apfs.Util;
using Apfs.Volume;

namespace Apfs.Diagnostics;

/// <summary>Tam denetimin sonucu. Hatalar diskin tutarsız olduğunu, uyarılar olağan dışı ama zararsız durumları gösterir.</summary>
public sealed class CheckReport
{
    private const int MaxErrors = 500;

    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<string> Info { get; } = [];
    public int SuppressedErrors { get; private set; }

    /// <summary>Yazıcının bu sürümde güncel tutamadığı özellikler (ör. klasör boyut istatistikleri); yazma kapısı bunlarda kapanır.</summary>
    public List<string> WriteBlockers { get; } = [];

    /// <summary>Bir birimde çözümlenmeyen yapılar var (bitmemiş şifreleme işlemi, anlık görüntü, mühür…); blok sahipliği tam denetlenemedi.</summary>
    public bool OwnershipIncomplete { get; internal set; }
    public bool IsClean => Errors.Count == 0;

    /// <summary>Tırnak içindeki adları (dosya/klasör adları) "…" ile değiştirir; kişisel adlar günlüğe ve rapora girmesin.</summary>
    public static string HideNames(string s) => System.Text.RegularExpressions.Regex.Replace(s, "\"[^\"]*\"", "\"…\"");

    internal void Error(string s)
    {
        if (Errors.Count < MaxErrors) Errors.Add(s);
        else SuppressedErrors++;
    }
}

/// <summary>
/// `apfs check --full`: kapsayıcının yapısal tutarlılığını denetler; yazma kapısı da bunu kullanır. Salt okunurdur.
/// Kapsam (apfsck'nin bir alt kümesi; geliştirmede asıl hakem apfsck'dir):
/// <list type="bullet">
/// <item>checkpoint tamamlığı (bkz. <see cref="Checkpoint"/>) ve blok 0 kopyası;</item>
/// <item>alan yöneticisi sayaçları, boş kuyruklar;</item>
/// <item><b>blok sahipliği</b>: her blok ya tam bir kez kullanılıyor ya da boş; sonuç diskteki bitmap'lerle (ana aygıt ve iç havuz)
/// bit bit karşılaştırılır;</item>
/// <item>nesne haritası kayıtlarının hepsi kullanılıyor; birim blok sayacı;</item>
/// <item>dosya sistemi: dizin girdisi → inode, tür, ad karması, klasör çocuk sayısı, bağlantı sayısı, birim sayaçları,
/// veri akışı extent'leri (0'dan başlar, boşluksuz, ayrılan boyutla eşit), dstream ve fiziksel extent başvuru sayıları.</item>
/// </list>
/// Kilidi açılmamış şifreli birimlerin dosya sistemi ağacı okunamaz; bloklarının sahipliği nesne haritasından ve extentref
/// ağacından (ikisi de şifresiz) yine denetlenir.
/// Şifreli birim kuralları (apfsck btree.c: omap kaydının ENCRYPTED bayrağı iki yönde; içerik denetimi apfsck'de yok, macOS'un
/// gerçek birimlerinden çıkarıldı — fixtures/crypto-survey.txt): her fs ağacı düğümünün omap kaydı ENCRYPTED taşır, şifresiz
/// birimde hiçbiri taşımaz; düğümün kendi o_type'ında OBJ_ENCRYPTED yalnız şifreli birimin fs ağacı düğümlerinde vardır (apfsck
/// object.c; iki yönlü; birimin omap, extentref ve snap_meta düğümlerinde asla; fs ağacı ancak anahtarla denetlenir);
/// anahtarla açılmış birimde (şifrelemesi sonradan değiştirilmemişse — ENC_ROLLED birimde şifresiz kalmış
/// eski veri crypto_id 0 taşır, Seagate'te 10 extent) delik olmayan her dosya/öznitelik extent'inin crypto_id'si 0 ya da 4 değildir
/// ve FEXT_CRYPTO_ID_IS_TWEAK bayrağını taşır; tek anahtarlı (ONEKEY) birimde yalnız CRYPTO_SW_ID'nin (4) boş crypto_state kaydı
/// olabilir (macOS onu koyar; başkası dosya başına anahtar demektir).
/// </summary>
public static class FullCheck
{
    /// <param name="keys">Kilidi açılmış birim anahtarları (parola yeniden sorulmaz); şifreli birimin dosya sistemi ancak anahtarla denetlenir.</param>
    public static CheckReport Run(ApfsContainer c, Func<VolumeLockInfo, string?>? passwords = null, IEnumerable<Crypto.VolumeKey>? keys = null)
    {
        var r = new CheckReport();
        try
        {
            RunCore(c, passwords, keys, r);
        }
        catch (ApfsException e) when (e.Code is not ApfsErrorCode.IoError)
        {
            r.Error($"denetim tamamlanamadı: {e.Detail ?? e.Message}");
        }
        return r;
    }

    private static void RunCore(ApfsContainer c, Func<VolumeLockInfo, string?>? passwords, IEnumerable<Crypto.VolumeKey>? keys, CheckReport r)
    {
        var sb = c.Superblock;
        var reader = c.Reader;
        foreach (var w in c.Warnings) r.Error($"kapsayıcı: {w}");

        // ---- checkpoint ----
        var cp = Checkpoint.Read(c);
        foreach (var p in cp.Problems) r.Error($"checkpoint: {p}");
        if (!cp.IsValid) return;

        var block0 = reader.ReadBlock(0);
        if (!Fletcher64.Verify(block0)) r.Warnings.Add("blok 0'daki süper blok kopyası bozuk");
        else if (Bin.U64(block0, 16) != sb.Xid) r.Warnings.Add($"blok 0 kopyası eski (xid {Bin.U64(block0, 16)}); disk düzgün kapatılmamış");

        // ---- alan yöneticisi ----
        var sm = Spaceman.Read(c, cp);
        var chunks = sm.MainChunks();
        if (sm.Main.BlockCount != sb.BlockCount) r.Error("alan yöneticisi: blok sayısı kapsayıcıdan farklı");
        if ((ulong)chunks.Count != sm.Main.ChunkCount) r.Error("alan yöneticisi: parça sayısı tutmuyor");
        if (sm.Tier2.BlockCount != 0) r.Error("alan yöneticisi: ikinci katman (Fusion) desteklenmiyor");

        var used = new BlockSet(sb.BlockCount);
        var ipUsed = new BlockSet(sm.IpBlockCount);
        void Mark(ulong start, ulong count, string what)
        {
            if (count == 0) return;
            if (start >= sb.BlockCount || count > sb.BlockCount - start) { r.Error($"{what}: blok aralığı {start}+{count} kapsayıcının dışında"); return; }
            if (used.Mark(start, count) is ulong dup) r.Error($"{what}: blok {dup} iki kez kullanılıyor");
        }
        void MarkIp(ulong paddr, ulong count, string what)
        {
            if (paddr < sm.IpBase || paddr - sm.IpBase + count > sm.IpBlockCount) { r.Error($"{what}: blok {paddr} iç havuzun dışında"); return; }
            if (ipUsed.Mark(paddr - sm.IpBase, count) is ulong dup) r.Error($"{what}: iç havuz bloğu {sm.IpBase + dup} iki kez kullanılıyor");
        }

        Mark(0, 1, "blok 0");
        Mark(sb.XpDescBase, sb.XpDescBlocks, "checkpoint tanımlayıcı halkası");
        Mark(sb.XpDataBase, sb.XpDataBlocks, "checkpoint veri halkası");
        Mark(sm.IpBmBase, sm.IpBmBlockCount, "iç havuz bitmap halkası");
        Mark(sm.IpBase, sm.IpBlockCount, "iç havuz");
        Mark(sb.KeylockerStart, sb.KeylockerBlockCount, "kapsayıcı anahtar deposu");
        Mark(sb.MkbLockerStart, sb.MkbLockerBlockCount, "ortam anahtar deposu");
        if (sb.KeylockerStart != 0)
        {
            // Birim anahtar depoları ('recs') da kapsayıcının bloklarıdır; yerleri kapsayıcı anahtar deposundaki etiket-3 kayıtlarında
            foreach (var e in Crypto.VolumeKeys.LoadContainerKeybag(c))
                if (e.Tag == Crypto.VolumeKeys.TagVolumeUnlockRecords)
                {
                    if (e.Data.Length < 16) { r.Error("kapsayıcı anahtar deposu: birim anahtar deposu kaydı kısa"); continue; }
                    Mark(Bin.U64(e.Data, 0), Bin.U64(e.Data, 8), "birim anahtar deposu");
                }
        }
        if (sb.EfiJumpstart != 0) MarkEfi(reader, sb.EfiJumpstart, Mark, r);
        if (sb.BlockedOutCount != 0) r.Error("kapsayıcı yeniden boyutlandırılıyor (engelli aralık); desteklenmiyor");
        if (sb.EvictMappingTreeOid != 0) r.Error("kapsayıcıda tahliye eşleme ağacı var; desteklenmiyor");

        foreach (var ch in chunks) if (ch.BitmapAddr != 0) MarkIp(ch.BitmapAddr, 1, "parça bitmap'i");
        foreach (var cib in sm.MainCibAddresses()) MarkIp(cib, 1, "CIB");
        for (uint i = 0; i < sm.Main.CabCount; i++) MarkIp(sm.ArrayU64(sm.Main.AddrOffset, i), 1, "CAB");

        CheckFreeQueues(sb, sm, Mark, MarkIp, r);

        if (cp.Find(sb.ReaperOid) is null) r.Error("reaper checkpoint haritasında yok");
        else
        {
            var reaper = ReaperState.Read(cp, sb);
            if (reaper.Problem is { } p) r.Error(p);
            foreach (var m in reaper.NotRewrittenByWriter(cp, sb))
                r.WriteBlockers.Add($"tanınmayan geçici nesne: oid {m.Oid}, tür 0x{m.Type:x8}/{m.Subtype}");
        }

        // ---- kapsayıcı nesne haritası ----
        var omapRecords = MarkObjectMap(c.Omap, sb.OmapOid, Mark, "kapsayıcı omap", out _);
        var fsLatest = new Dictionary<ulong, ObjectMap.Record>();
        foreach (var rec in omapRecords)
        {
            if (rec.Xid > sb.Xid) { r.Error($"kapsayıcı omap: kayıt {rec.Oid} gelecekteki bir işlemden (xid {rec.Xid})"); continue; }
            if (rec.Size != sb.BlockSize) r.Error($"kapsayıcı omap: kayıt {rec.Oid} boyutu {rec.Size}");
            if (!fsLatest.TryAdd(rec.Oid, rec)) r.Error($"kapsayıcı omap: nesne {rec.Oid} için birden çok kayıt (anlık görüntü desteklenmiyor)");
            Mark(rec.Paddr, 1, $"birim süper bloğu {rec.Oid}");
        }
        foreach (ulong oid in sb.FsOids)
            if (!fsLatest.ContainsKey(oid)) r.Error($"birim {oid} kapsayıcı omap'inde yok");
        if (fsLatest.Count != sb.FsOids.Count) r.Error("kapsayıcı omap'inde birim olmayan kayıtlar var");

        // ---- birimler ----
        var opened = c.OpenVolumes(passwords, keys);
        for (int i = 0; i < sb.FsOids.Count; i++)
        {
            ulong oid = sb.FsOids[i];
            if (!fsLatest.TryGetValue(oid, out var loc)) continue;
            var apsb = reader.ReadObject(loc.Paddr, ApfsConstants.TypeFs, oid);
            var vol = opened.FirstOrDefault(x => x.Index == i)?.Volume;
            string label = $"birim \"{Bin.Utf8Z(Bin.Slice(apsb, 704, 256))}\"";
            CheckVolume(c, apsb, vol, label, Mark, r);
        }

        // ---- bitmap karşılaştırması ----
        CompareMain(sm, chunks, used, r);
        CompareIp(sm, ipUsed, r);
    }

    // ------------------------------------------------------------------------------------------------------------------

    private static void MarkEfi(ObjectReader reader, ulong paddr, Action<ulong, ulong, string> mark, CheckReport r)
    {
        mark(paddr, 1, "EFI sürücüsü");
        var efi = reader.ReadObject(paddr, 0x14);
        uint n = Bin.U32(efi, 44);
        if (176 + (long)n * 16 > efi.Length) { r.Error("EFI sürücüsü: extent sayısı bloğa sığmıyor"); return; }
        for (uint i = 0; i < n; i++)
            mark(Bin.U64(efi, (int)(176 + i * 16)), Bin.U64(efi, (int)(184 + i * 16)), "EFI sürücüsü verisi");
    }

    private static void CheckFreeQueues(NxSuperblock sb, Spaceman sm, Action<ulong, ulong, string> mark,
        Action<ulong, ulong, string> markIp, CheckReport r)
    {
        string[] names = ["iç havuz", "ana aygıt", "ikinci katman"];
        for (int q = 0; q < 3; q++)
        {
            var info = sm.FreeQueues[q];
            if (q == Spaceman.SfqTier2)
            {
                if (info.TreeOid != 0 || info.Count != 0) r.Error("ikinci katman boş kuyruğu dolu (Fusion desteklenmiyor)");
                continue;
            }
            ulong total = 0, oldest = 0;
            foreach (var e in sm.ReadFreeQueue(q))
            {
                if (e.Xid > sb.Xid) r.Error($"boş kuyruk ({names[q]}): kayıt gelecekteki bir işlemden (xid {e.Xid})");
                if (e.Length == 0) r.Error($"boş kuyruk ({names[q]}): uzunluğu 0 olan kayıt");
                if (oldest == 0 || e.Xid < oldest) oldest = e.Xid;
                total += e.Length;
                bool inIp = e.Paddr >= sm.IpBase && e.Paddr < sm.IpBase + sm.IpBlockCount;
                if (q == Spaceman.SfqIp)
                {
                    if (!inIp) r.Error($"boş kuyruk (iç havuz): blok {e.Paddr} iç havuzun dışında");
                    else markIp(e.Paddr, e.Length, "boş kuyruk (iç havuz)");
                }
                else
                {
                    if (inIp) r.Error($"boş kuyruk (ana aygıt): blok {e.Paddr} iç havuzun içinde");
                    else mark(e.Paddr, e.Length, "boş kuyruk (ana aygıt)");
                }
            }
            if (total != info.Count) r.Error($"boş kuyruk ({names[q]}): sayaç {info.Count}, kayıtlar {total}");
            if (oldest != info.OldestXid) r.Error($"boş kuyruk ({names[q]}): en eski xid {info.OldestXid}, kayıtlarda {oldest}");
        }
    }

    /// <summary>omap nesnesini ve ağacının bütün düğümlerini işaretler; kayıtları döndürür. OBJ_ENCRYPTED taşıyan düğümler <paramref name="flagged"/>'e eklenir.</summary>
    private static List<ObjectMap.Record> MarkObjectMap(ObjectMap omap, ulong omapPaddr, Action<ulong, ulong, string> mark, string what, out ulong nodeCount,
        List<ulong>? flagged = null)
    {
        mark(omapPaddr, 1, what);
        nodeCount = 0;
        foreach (var n in omap.Tree.EnumerateNodes())
        {
            mark(n.Paddr, 1, $"{what} düğümü");
            if ((n.ObjType & ApfsConstants.ObjEncrypted) != 0) flagged?.Add(n.Paddr);
            nodeCount++;
        }
        if (Bin.U32(omap.Raw, 36) != 0 || Bin.U64(omap.Raw, 56) != 0)
            throw new ApfsException(ApfsErrorCode.Unsupported, $"{what}: anlık görüntü var");
        return omap.AllRecords().ToList();
    }

    private static void CheckVolume(ApfsContainer c, byte[] apsb, ApfsVolume? vol, string label, Action<ulong, ulong, string> mark, CheckReport r)
    {
        var reader = c.Reader;
        ulong blockSize = c.BlockSize;
        ulong volOmapPaddr = Bin.U64(apsb, 128);
        ulong extentRefOid = Bin.U64(apsb, 144), snapMetaOid = Bin.U64(apsb, 152);
        ulong allocCount = Bin.U64(apsb, 88);
        // Bu yapıların blokları çözümlenmiyor: sahiplik denetimi o birim için eksik kalır (yazma kapısı bu birimleri zaten reddeder)
        bool unsupported = false;
        if (Bin.U64(apsb, 216) != 0) { unsupported = true; r.Warnings.Add($"{label}: anlık görüntüsü var; anlık görüntü blokları denetlenmedi"); }
        foreach (var (off, name) in new[] { (976, "bitmemiş bir şifreleme işlemi (macOS sürdürüyor)"), (1024, "bütünlük meta verisi"), (1032, "fext ağacı"), (1000, "anlık görüntü uzantısı") })
            if (Bin.U64(apsb, off) != 0) { unsupported = true; r.Warnings.Add($"{label}: {name} var; bu yapının blokları denetlenmedi"); }
        if (unsupported) r.OwnershipIncomplete = true;

        ulong counted = 0;
        void MarkVol(ulong start, ulong count, string what) { mark(start, count, $"{label}: {what}"); counted += count; }

        // Birim nesne haritası ve kayıtları (fs ağacı düğümleri)
        var omap = vol?.Omap ?? new ObjectMap(reader, volOmapPaddr);
        var flaggedOmap = new List<ulong>();
        var records = MarkObjectMap(omap, volOmapPaddr, MarkVol, "omap", out _, flaggedOmap);
        FlagErrors(r, label, "OBJ_ENCRYPTED işaretli omap düğümü (blok)", flaggedOmap);
        var recordOids = new HashSet<ulong>();
        bool encrypted = (Bin.U64(apsb, 264) & 3) != ApfsConstants.ApfsFsUnencrypted;
        bool cryptoInProgress = Bin.U64(apsb, 976) != 0; // er_state: macOS düğümleri hâlâ şifreliyor/çözüyor olabilir
        int unflagged = 0;
        foreach (var rec in records)
        {
            if (rec.Xid > c.Xid) { r.Error($"{label}: omap kaydı {rec.Oid} gelecekteki bir işlemden"); continue; }
            if (!recordOids.Add(rec.Oid)) r.Error($"{label}: nesne {rec.Oid} için birden çok omap kaydı");
            if ((rec.Flags & ApfsConstants.OmapValDeleted) != 0) r.Error($"{label}: silinmiş işaretli omap kaydı {rec.Oid}");
            if (rec.Size != blockSize) r.Error($"{label}: omap kaydı {rec.Oid} boyutu {rec.Size}");
            if (!encrypted && (rec.Flags & ApfsConstants.OmapValEncrypted) != 0)
                r.Error($"{label}: şifresiz birimde şifreli işaretli düğüm {rec.Oid}");
            if (encrypted && !cryptoInProgress && (rec.Flags & ApfsConstants.OmapValEncrypted) == 0 && unflagged++ < 5)
                r.Error($"{label}: şifreli birimde şifreli işaretlenmemiş düğüm {rec.Oid}");
            MarkVol(rec.Paddr, 1, $"fs düğümü {rec.Oid}");
        }

        // Fiziksel ağaçlar
        var phys = new List<(ulong Paddr, ulong Len, int Refcnt)>();
        if (extentRefOid != 0)
        {
            var tree = new BTree.BTree(reader, extentRefOid, ApfsConstants.TypeBlockRefTree, null);
            var flagged = new List<ulong>();
            foreach (var n in tree.EnumerateNodes())
            {
                MarkVol(n.Paddr, 1, "extentref düğümü");
                if ((n.ObjType & ApfsConstants.ObjEncrypted) != 0) flagged.Add(n.Paddr);
            }
            FlagErrors(r, label, "OBJ_ENCRYPTED işaretli extentref düğümü (blok)", flagged);
            foreach (var e in tree.Scan(_ => 0))
            {
                if (e.Key.Length < 8 || e.Value.Length < 20) { r.Error($"{label}: extentref kaydı kısa"); continue; }
                ulong paddr = Bin.U64(e.Key, 0) & 0x0FFFFFFFFFFFFFFF;
                ulong len = Bin.U64(e.Value, 0) & 0x0FFFFFFFFFFFFFFF;
                int refcnt = Bin.I32(e.Value, 16);
                if (len == 0) r.Error($"{label}: uzunluğu 0 olan fiziksel extent {paddr}");
                if (refcnt <= 0) r.Error($"{label}: fiziksel extent {paddr} başvuru sayısı {refcnt}");
                phys.Add((paddr, len, refcnt));
                MarkVol(paddr, len, $"dosya verisi (blok {paddr})");
            }
        }
        if (snapMetaOid != 0)
        {
            var tree = new BTree.BTree(reader, snapMetaOid, ApfsConstants.TypeSnapMetaTree, null);
            var flagged = new List<ulong>();
            foreach (var n in tree.EnumerateNodes())
            {
                MarkVol(n.Paddr, 1, "snap_meta düğümü");
                if ((n.ObjType & ApfsConstants.ObjEncrypted) != 0) flagged.Add(n.Paddr);
            }
            FlagErrors(r, label, "OBJ_ENCRYPTED işaretli snap_meta düğümü (blok)", flagged);
        }

        if (counted != allocCount)
        {
            string msg = $"{label}: ayrılan blok sayacı {allocCount}, sayılan {counted}";
            if (unsupported) r.Warnings.Add(msg + " (fark denetlenmeyen yapılardan olabilir)"); else r.Error(msg);
        }

        if (vol is null)
        {
            r.Warnings.Add($"{label}: açılamadı (şifreli olabilir); dosya sistemi ağacı denetlenmedi, blok sahipliği denetlendi");
            r.Info.Add($"{label}: fs düğümlerinin OBJ_ENCRYPTED işareti denetlenmedi (birim açılmadı)");
            return;
        }

        // fs ağacı düğümleri ile omap kayıtları birebir; OBJ_ENCRYPTED ⇔ birim şifreli (er_state doluyken macOS düğümleri dönüştürüyor olabilir)
        var nodeOids = new HashSet<ulong>();
        var wrongFlag = new List<ulong>();
        int fsNodes = 0, fsFlagged = 0;
        foreach (var n in vol.FsTree.EnumerateNodes())
        {
            if (!nodeOids.Add(n.Oid)) r.Error($"{label}: fs ağacı düğümü {n.Oid} iki kez kullanılıyor");
            fsNodes++;
            bool flag = (n.ObjType & ApfsConstants.ObjEncrypted) != 0;
            if (flag) fsFlagged++;
            if (flag != encrypted) wrongFlag.Add(n.Oid);
        }
        r.Info.Add($"{label}: fs düğümleri {fsNodes}, OBJ_ENCRYPTED işaretli {fsFlagged}");
        if (!cryptoInProgress)
            FlagErrors(r, label, encrypted ? "şifreli birimde OBJ_ENCRYPTED işaretsiz fs düğümü" : "şifresiz birimde OBJ_ENCRYPTED işaretli fs düğümü", wrongFlag);
        foreach (ulong oid in recordOids)
            if (!nodeOids.Contains(oid)) r.Error($"{label}: omap kaydı {oid} hiçbir düğüm tarafından kullanılmıyor");

        CheckFsTree(c, vol, label, phys, r);
    }

    /// <summary>OBJ_ENCRYPTED kuralının hataları: ilk 5 nesne adıyla, fazlası toplam sayıyla.</summary>
    private static void FlagErrors(CheckReport r, string label, string what, List<ulong> ids)
    {
        foreach (ulong id in ids.Take(5)) r.Error($"{label}: {what} {id}");
        if (ids.Count > 5) r.Error($"{label}: {what}: toplam {ids.Count}");
    }

    private sealed class InodeFacts(Inode ino)
    {
        public Inode Inode { get; } = ino;
        public int Drecs;
        public int Children;
    }

    private static void CheckFsTree(ApfsContainer c, ApfsVolume v, string label, List<(ulong Paddr, ulong Len, int Refcnt)> phys, CheckReport r)
    {
        int badCrypto = 0, cryptoStates = 0;
        bool tweakRules = v.IsEncrypted && (v.IncompatibleFeatures & ApfsConstants.ApfsIncompatEncRolled) == 0;
        ulong blockSize = c.BlockSize;
        var inodes = new Dictionary<ulong, InodeFacts>();
        var drecs = new List<(ulong Parent, DirEntry Entry)>();
        var dstreamRefs = new Dictionary<ulong, uint>();
        var extents = new Dictionary<ulong, List<(ulong Logical, ulong Len, ulong Phys)>>();
        var xattrStreams = new List<(ulong Owner, ulong StreamId, ulong Size, ulong Alloc)>();
        ulong maxId = 0;
        var blockers = new HashSet<string>(StringComparer.Ordinal);

        foreach (var e in v.AllRecords())
        {
            ulong id = JKey.ObjId(e.Key);
            var type = JKey.Type(e.Key);
            if (type is not JType.Extent and not JType.SnapMetadata) maxId = Math.Max(maxId, id);
            switch (type)
            {
                case JType.Inode:
                    inodes[id] = new InodeFacts(Inode.Parse(id, e.Value));
                    if ((inodes[id].Inode.InternalFlags & 0x2) != 0) blockers.Add("klasör boyut istatistikleri");
                    break;
                case JType.DirStats:
                    blockers.Add("klasör boyut istatistikleri");
                    break;
                case JType.FileInfo:
                    blockers.Add("dosya özeti kayıtları");
                    break;
                case JType.DirRec:
                    var de = ApfsVolume.ParseDirRec(e.Key, e.Value);
                    if (de is null) { r.Error($"{label}: adsız dizin girdisi (klasör {id})"); break; }
                    drecs.Add((id, de));
                    int hashedLen = (int)(Bin.U32(e.Key, 8) & 0x3FF);
                    if (12 + hashedLen == e.Key.Length && ApfsNames.Hash(de.Name, v.IsCaseInsensitive) != Bin.U32(e.Key, 8) >> 10)
                        r.Error($"{label}: \"{de.Name}\" ad karması yanlış");
                    break;
                case JType.DstreamId:
                    if (e.Value.Length < 4) { r.Error($"{label}: dstream kaydı {id} kısa"); break; }
                    dstreamRefs[id] = Bin.U32(e.Value, 0);
                    break;
                case JType.CryptoState:
                    // CRYPTO_SW_ID'nin yer tutucusu: anahtarsız (key_len 0); dosyaların default_crypto_id'si bunu gösterir
                    if (id != CryptoSwId || e.Value.Length < 24 || Bin.U16(e.Value, 22) != 0) cryptoStates++;
                    break;
                case JType.FileExtent:
                    if (e.Key.Length < 16 || e.Value.Length < 24) { r.Error($"{label}: extent kaydı kısa ({id})"); break; }
                    ulong len = Bin.U64(e.Value, 0) & 0x00FFFFFFFFFFFFFF;
                    if (tweakRules && Bin.U64(e.Value, 8) != 0)
                    {
                        if (Bin.U64(e.Value, 16) is 0 or CryptoSwId && badCrypto++ < 5)
                            r.Error($"{label}: veri akışı {id}: şifreli birimde extent'in crypto_id'si {Bin.U64(e.Value, 16)}");
                        if ((Bin.U64(e.Value, 0) >> 56 & FextCryptoIdIsTweak) == 0 && badCrypto++ < 5)
                            r.Error($"{label}: veri akışı {id}: şifreli birimde extent'te FEXT_CRYPTO_ID_IS_TWEAK bayrağı yok");
                    }
                    if (len == 0 || len % blockSize != 0) r.Error($"{label}: veri akışı {id}: extent uzunluğu {len}");
                    if (!extents.TryGetValue(id, out var list)) extents[id] = list = [];
                    list.Add((Bin.U64(e.Key, 8), len, Bin.U64(e.Value, 8)));
                    break;
                case JType.Xattr:
                    if (e.Value.Length >= 4 && (Bin.U16(e.Value, 0) & ApfsVolume.XattrDataStream) != 0)
                    {
                        if (e.Value.Length < 4 + 24) { r.Error($"{label}: öznitelik akışı kaydı kısa ({id})"); break; }
                        xattrStreams.Add((id, Bin.U64(e.Value, 4), Bin.U64(e.Value, 12), Bin.U64(e.Value, 20)));
                    }
                    break;
            }
        }

        if (cryptoStates > 0 && (v.FsFlags & ApfsConstants.ApfsFsOneKey) != 0)
            r.Error($"{label}: tek anahtarlı şifreli birimde {cryptoStates} dosya başına anahtar (crypto_state) kaydı");
        foreach (var b in blockers) r.WriteBlockers.Add($"{label}: {b}");
        if (!inodes.ContainsKey(Inode.RootDirId)) r.Error($"{label}: kök klasör yok");

        // Dizin girdileri → inode
        foreach (var (parent, de) in drecs)
        {
            // Kök klasör ve özel klasörün girdileri, inode'u olmayan sanal üstte (1) durur
            if (parent != Inode.RootDirParent)
            {
                if (!inodes.TryGetValue(parent, out var p)) { r.Error($"{label}: \"{de.Name}\" girdisinin klasörü ({parent}) yok"); continue; }
                if (!p.Inode.IsDirectory) r.Error($"{label}: \"{de.Name}\" girdisinin üstü klasör değil");
                p.Children++;
            }
            else if (de.FileId is not (Inode.RootDirId or PrivateDirId)) r.Error($"{label}: \"{de.Name}\" sanal üst klasörde ama kök değil");
            if (!inodes.TryGetValue(de.FileId, out var t)) { r.Error($"{label}: \"{de.Name}\" girdisinin inode'u ({de.FileId}) yok"); continue; }
            t.Drecs++;
            if (ExpectedType(t.Inode) != de.Type) r.Error($"{label}: \"{de.Name}\" girdi türü inode ile uyuşmuyor");
        }

        long files = 0, dirs = 0, symlinks = 0, others = 0;
        var owners = new Dictionary<ulong, uint>();
        foreach (var (id, f) in inodes)
        {
            var ino = f.Inode;
            if (ino.IsDirectory)
            {
                if (id != Inode.RootDirId && id != PrivateDirId) dirs++;
                if (ino.ChildrenOrLinks != f.Children) r.Error($"{label}: klasör {id} çocuk sayısı {ino.ChildrenOrLinks}, girdiler {f.Children}");
                if (id != Inode.RootDirId && id != PrivateDirId && f.Drecs != 1) r.Error($"{label}: klasör {id} {f.Drecs} yerden gösteriliyor");
            }
            else
            {
                if (ino.IsRegularFile) files++;
                else if (ino.IsSymlink) symlinks++;
                else others++;
                if (ino.ChildrenOrLinks != f.Drecs) r.Error($"{label}: inode {id} bağlantı sayısı {ino.ChildrenOrLinks}, girdiler {f.Drecs}");
            }
            if (ino.HasDataStream)
            {
                if (!ino.IsRegularFile) r.Error($"{label}: inode {id} dosya değil ama veri akışı var");
                owners[ino.PrivateId] = owners.GetValueOrDefault(ino.PrivateId) + 1;
                CheckStream(label, $"inode {id}", extents.GetValueOrDefault(ino.PrivateId), ino.DataSize, ino.AllocatedSize, r);
            }
        }
        // Başvuru sayıları: dosya veri akışlarının dstream_id kaydı vardır ve sayısı sahiplerle aynıdır (klonlarda 2+).
        foreach (var (sid, n) in owners)
        {
            if (dstreamRefs.TryGetValue(sid, out uint refcnt)) { if (refcnt != n) r.Error($"{label}: veri akışı {sid} başvuru sayısı {refcnt}, sahip {n}"); }
            else if (extents.ContainsKey(sid)) r.Error($"{label}: veri akışı {sid} için başvuru kaydı yok");
        }
        // Öznitelik veri akışları paylaşılamaz ve dstream_id kaydı taşımaz (apfsck: "xattrs can't be cloned")
        var xattrOwners = new Dictionary<ulong, int>();
        foreach (var (owner, sid, size, alloc) in xattrStreams)
        {
            xattrOwners[sid] = xattrOwners.GetValueOrDefault(sid) + 1;
            if (owners.ContainsKey(sid)) r.Error($"{label}: veri akışı {sid} hem dosyaya hem özniteliğe ait");
            if (dstreamRefs.ContainsKey(sid)) r.Error($"{label}: öznitelik akışı {sid} için başvuru kaydı olmamalı");
            CheckStream(label, $"öznitelik akışı {sid} (inode {owner})", extents.GetValueOrDefault(sid), size, alloc, r);
        }
        foreach (var (sid, n) in xattrOwners)
            if (n != 1) r.Error($"{label}: öznitelik akışı {sid} {n} yerden kullanılıyor");
        foreach (var sid in dstreamRefs.Keys)
            if (!owners.ContainsKey(sid)) r.Error($"{label}: sahipsiz veri akışı kaydı {sid}");
        foreach (var sid in extents.Keys)
            if (!owners.ContainsKey(sid) && !xattrOwners.ContainsKey(sid)) r.Error($"{label}: sahipsiz extent'ler (veri akışı {sid})");

        CheckPhysicalExtents(label, extents, phys, blockSize, r);

        if (files != (long)v.NumFiles || dirs != (long)v.NumDirectories || symlinks != (long)v.NumSymlinks || others != (long)v.NumOtherFsObjects)
            r.Error($"{label}: sayaçlar dosya/klasör/sembolik/diğer {v.NumFiles}/{v.NumDirectories}/{v.NumSymlinks}/{v.NumOtherFsObjects}, " +
                $"sayılan {files}/{dirs}/{symlinks}/{others}");
        if (v.NextObjId <= maxId) r.Error($"{label}: sonraki nesne kimliği {v.NextObjId}, kullanılan en büyük {maxId}");
        r.Info.Add($"{label}: {files} dosya, {dirs} klasör, {symlinks} sembolik bağ, {extents.Count} veri akışı, {phys.Count} fiziksel extent");
    }

    /// <summary>CRYPTO_SW_ID: yazılım şifrelemeli birimin VEK'ine ayrılmış kimlik; veri extent'inin tweak'i olamaz.</summary>
    private const ulong CryptoSwId = 4;

    /// <summary>j_file_extent_val_t bayrağı: crypto_id alanı bir tweak'tir.</summary>
    private const ulong FextCryptoIdIsTweak = 0x1;

    /// <summary>Özel klasör (private-dir): sert bağlantılar ve silinmeyi bekleyen dosyalar için.</summary>
    private const ulong PrivateDirId = 3;

    private static DirEntryType ExpectedType(Inode i) => (i.Mode & Inode.ModeTypeMask) switch
    {
        Inode.ModeDir => DirEntryType.Directory,
        Inode.ModeRegular => DirEntryType.Regular,
        Inode.ModeSymlink => DirEntryType.Symlink,
        0x1000 => DirEntryType.Fifo,
        0x2000 => DirEntryType.CharDevice,
        0x6000 => DirEntryType.BlockDevice,
        0xC000 => DirEntryType.Socket,
        _ => DirEntryType.Unknown,
    };

    private static void CheckStream(string label, string what, List<(ulong Logical, ulong Len, ulong Phys)>? list, ulong size, ulong alloc, CheckReport r)
    {
        ulong next = 0;
        if (list is not null)
        {
            list.Sort((a, b) => a.Logical.CompareTo(b.Logical));
            foreach (var x in list)
            {
                if (x.Logical != next) { r.Error($"{label}: {what}: extent'ler boşluklu ya da üst üste (ofset {x.Logical}, beklenen {next})"); break; }
                next += x.Len;
            }
        }
        if (next != alloc) r.Error($"{label}: {what}: ayrılan boyut {alloc}, extent'ler {next}");
        if (size > alloc) r.Error($"{label}: {what}: boyut {size} ayrılandan ({alloc}) büyük");
    }

    /// <summary>
    /// Her dosya extent'i bir fiziksel extent kaydının içinde olmalı ve her fiziksel extent'in her bloğunu tam olarak başvuru
    /// sayısı kadar dosya extent'i göstermeli (klonlarda 2+).
    /// </summary>
    private static void CheckPhysicalExtents(string label, Dictionary<ulong, List<(ulong Logical, ulong Len, ulong Phys)>> extents,
        List<(ulong Paddr, ulong Len, int Refcnt)> phys, ulong blockSize, CheckReport r)
    {
        // Dosya extent'lerinin kapsama sayısı: süpürme çizgisi
        var events = new List<(ulong Pos, int Delta)>();
        foreach (var list in extents.Values)
            foreach (var x in list)
                if (x.Phys != 0)
                {
                    events.Add((x.Phys, +1));
                    events.Add((x.Phys + x.Len / blockSize, -1));
                }
        events.Sort((a, b) => a.Pos != b.Pos ? a.Pos.CompareTo(b.Pos) : a.Delta.CompareTo(b.Delta));
        var segments = new List<(ulong Start, ulong End, int Count)>();
        int cur = 0;
        ulong last = 0;
        foreach (var (pos, delta) in events)
        {
            if (cur > 0 && pos > last) segments.Add((last, pos, cur));
            cur += delta;
            last = pos;
        }

        phys.Sort((a, b) => a.Paddr.CompareTo(b.Paddr));
        for (int i = 1; i < phys.Count; i++)
            if (phys[i].Paddr < phys[i - 1].Paddr + phys[i - 1].Len) r.Error($"{label}: fiziksel extent'ler üst üste ({phys[i].Paddr})");

        // Her fiziksel kaydın her bloğu, başvuru sayısı kadar dosya extent'iyle kapsanmalı. Bir dosya extent'i birden çok bitişik
        // fiziksel kayda yayılabilir (apfsck de böyle sayar); bu yüzden kapsama parçaları kayıt sınırlarında bölünür.
        int errors = 0, s0 = 0;
        ulong segTotal = 0, matched = 0;
        foreach (var seg in segments) segTotal += seg.End - seg.Start;
        foreach (var p in phys)
        {
            ulong pEnd = p.Paddr + p.Len, covered = 0;
            while (s0 < segments.Count && segments[s0].End <= p.Paddr) s0++;
            for (int k = s0; k < segments.Count && segments[k].Start < pEnd; k++)
            {
                var seg = segments[k];
                ulong a = Math.Max(seg.Start, p.Paddr), b = Math.Min(seg.End, pEnd);
                if (a >= b) continue;
                if (seg.Count != p.Refcnt && errors++ < 5) r.Error($"{label}: fiziksel extent {p.Paddr} başvuru sayısı {p.Refcnt}, gösteren {seg.Count}");
                covered += b - a;
            }
            matched += covered;
            if (covered != p.Len && errors++ < 5) r.Error($"{label}: fiziksel extent {p.Paddr}+{p.Len} bloklarının bir kısmını hiçbir dosya göstermiyor");
        }
        if (matched != segTotal && errors++ < 5) r.Error($"{label}: {segTotal - matched} blok dosya verisi ama fiziksel extent kaydı yok");
    }

    // ---- bitmap karşılaştırması ----

    private static void CompareMain(Spaceman sm, IReadOnlyList<ChunkInfo> chunks, BlockSet used, CheckReport r)
    {
        ulong leaked = 0, freeButUsed = 0, sumFree = 0;
        var examples = new List<string>();
        foreach (var ch in chunks)
        {
            var bm = sm.ReadChunkBitmap(ch);
            uint free = Spaceman.CountFree(bm, ch.BlockCount);
            sumFree += free;
            if (free != ch.FreeCount) r.Error($"alan yöneticisi: parça {ch.Addr} boş sayısı {ch.FreeCount}, bitmap {free}");
            for (uint i = 0; i < ch.BlockCount; i++)
            {
                bool onDisk = (bm[i >> 3] & (1 << (int)(i & 7))) != 0;
                bool expected = used.Get(ch.Addr + i);
                if (onDisk == expected) continue;
                if (onDisk) leaked++; else freeButUsed++;
                if (examples.Count < 5) examples.Add($"blok {ch.Addr + i} {(onDisk ? "dolu işaretli ama kullanılmıyor" : "kullanılıyor ama boş işaretli")}");
            }
        }
        if (sumFree != sm.Main.FreeCount) r.Error($"alan yöneticisi: aygıt boş sayacı {sm.Main.FreeCount}, bitmap'ler {sumFree}");
        if (freeButUsed > 0) r.Error($"alan yöneticisi: {freeButUsed} blok kullanılıyor ama boş işaretli (TEHLİKELİ)");
        if (leaked > 0)
        {
            // Denetlenmeyen yapılar varsa dolu ama sahipsiz görünen bloklar onlarındır; sızıntı denemez
            if (r.OwnershipIncomplete) r.Warnings.Add($"alan yöneticisi: {leaked} blok denetlenmeyen yapılara ait görünüyor (dolu işaretli, sahibi çözümlenmedi)");
            else r.Error($"alan yöneticisi: {leaked} blok dolu işaretli ama hiçbir yapı kullanmıyor (sızıntı)");
        }
        if (freeButUsed > 0 || (leaked > 0 && !r.OwnershipIncomplete)) r.Error($"alan yöneticisi: örnekler: {string.Join("; ", examples)}");
        r.Info.Add($"alan yöneticisi: {sm.Main.BlockCount} blok, {sm.Main.FreeCount} boş; bitmap ile sahiplik " +
            (freeButUsed + leaked == 0 ? "birebir" : freeButUsed == 0 && r.OwnershipIncomplete ? $"denetlenen yapılarda birebir ({leaked} blok denetlenmeyen yapılarda)" : "FARKLI"));
    }

    private static void CompareIp(Spaceman sm, BlockSet ipUsed, CheckReport r)
    {
        var ip = sm.ReadIpBitmap();
        ulong diff = 0;
        for (ulong i = 0; i < sm.IpBlockCount; i++)
        {
            bool onDisk = (ip[i >> 3] & (1 << (int)(i & 7))) != 0;
            if (onDisk != ipUsed.Get(i)) diff++;
        }
        if (diff > 0) r.Error($"iç havuz: {diff} blokta bitmap ile kullanım farklı");
    }

    /// <summary>Bellek içi blok kümesi (1 bit = 1 blok).</summary>
    private sealed class BlockSet(ulong count)
    {
        private readonly ulong[] _bits = new ulong[(count + 63) / 64];

        public bool Get(ulong b) => b < count && (_bits[b >> 6] & (1UL << (int)(b & 63))) != 0;

        /// <summary>Aralığı işaretler; zaten işaretli bir blok varsa ilkini döndürür (yine de tamamını işaretler).</summary>
        public ulong? Mark(ulong start, ulong len)
        {
            ulong? dup = null;
            for (ulong b = start; b < start + len; b++)
            {
                ref ulong w = ref _bits[b >> 6];
                ulong bit = 1UL << (int)(b & 63);
                if ((w & bit) != 0) dup ??= b;
                w |= bit;
            }
            return dup;
        }
    }
}
