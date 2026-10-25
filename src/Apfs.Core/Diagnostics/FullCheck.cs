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
            r.Error(CoreText.T("Check.Incomplete", e.Detail ?? e.Message));
        }
        return r;
    }

    private static void RunCore(ApfsContainer c, Func<VolumeLockInfo, string?>? passwords, IEnumerable<Crypto.VolumeKey>? keys, CheckReport r)
    {
        var sb = c.Superblock;
        var reader = c.Reader;
        foreach (var w in c.Warnings) r.Error(CoreText.T("Check.ContainerWarning", w));

        // ---- checkpoint ----
        var cp = Checkpoint.Read(c);
        foreach (var p in cp.Problems) r.Error($"checkpoint: {p}");
        if (!cp.IsValid) return;

        var block0 = reader.ReadBlock(0);
        if (!Fletcher64.Verify(block0)) r.Warnings.Add(CoreText.T("Check.Block0Corrupt"));
        else if (Bin.U64(block0, 16) != sb.Xid) r.Warnings.Add(CoreText.T("Check.Block0Stale", Bin.U64(block0, 16)));

        // ---- alan yöneticisi ----
        var sm = Spaceman.Read(c, cp);
        var chunks = sm.MainChunks();
        if (sm.Main.BlockCount != sb.BlockCount) r.Error(CoreText.T("Check.SpacemanBlockCount"));
        if ((ulong)chunks.Count != sm.Main.ChunkCount) r.Error(CoreText.T("Check.SpacemanChunkCount"));
        if (sm.Tier2.BlockCount != 0) r.Error(CoreText.T("Check.SpacemanTier2"));

        var used = new BlockSet(sb.BlockCount);
        var ipUsed = new BlockSet(sm.IpBlockCount);
        void Mark(ulong start, ulong count, string what)
        {
            if (count == 0) return;
            if (start >= sb.BlockCount || count > sb.BlockCount - start) { r.Error(CoreText.T("Check.RangeOutside", what, start, count)); return; }
            if (used.Mark(start, count) is ulong dup) r.Error(CoreText.T("Check.BlockUsedTwice", what, dup));
        }
        void MarkIp(ulong paddr, ulong count, string what)
        {
            if (paddr < sm.IpBase || paddr - sm.IpBase + count > sm.IpBlockCount) { r.Error(CoreText.T("Check.BlockOutsideIp", what, paddr)); return; }
            if (ipUsed.Mark(paddr - sm.IpBase, count) is ulong dup) r.Error(CoreText.T("Check.IpBlockUsedTwice", what, sm.IpBase + dup));
        }

        Mark(0, 1, CoreText.T("Check.LabelBlock0"));
        Mark(sb.XpDescBase, sb.XpDescBlocks, CoreText.T("Check.LabelXpDesc"));
        Mark(sb.XpDataBase, sb.XpDataBlocks, CoreText.T("Check.LabelXpData"));
        Mark(sm.IpBmBase, sm.IpBmBlockCount, CoreText.T("Check.LabelIpBitmapRing"));
        Mark(sm.IpBase, sm.IpBlockCount, CoreText.T("Check.LabelInternalPool"));
        Mark(sb.KeylockerStart, sb.KeylockerBlockCount, CoreText.T("Check.LabelContainerKeybag"));
        Mark(sb.MkbLockerStart, sb.MkbLockerBlockCount, CoreText.T("Check.LabelMediaKeybag"));
        if (sb.KeylockerStart != 0)
        {
            // Birim anahtar depoları ('recs') da kapsayıcının bloklarıdır; yerleri kapsayıcı anahtar deposundaki etiket-3 kayıtlarında
            foreach (var e in Crypto.VolumeKeys.LoadContainerKeybag(c))
                if (e.Tag == Crypto.VolumeKeys.TagVolumeUnlockRecords)
                {
                    if (e.Data.Length < 16) { r.Error(CoreText.T("Check.VolumeKeybagRecordShort")); continue; }
                    Mark(Bin.U64(e.Data, 0), Bin.U64(e.Data, 8), CoreText.T("Check.LabelVolumeKeybag"));
                }
        }
        if (sb.EfiJumpstart != 0) MarkEfi(reader, sb.EfiJumpstart, Mark, r);
        if (sb.BlockedOutCount != 0) r.Error(CoreText.T("Check.BlockedOut"));
        if (sb.EvictMappingTreeOid != 0) r.Error(CoreText.T("Check.EvictMapping"));

        foreach (var ch in chunks) if (ch.BitmapAddr != 0) MarkIp(ch.BitmapAddr, 1, CoreText.T("Check.LabelChunkBitmap"));
        foreach (var cib in sm.MainCibAddresses()) MarkIp(cib, 1, "CIB");
        for (uint i = 0; i < sm.Main.CabCount; i++) MarkIp(sm.ArrayU64(sm.Main.AddrOffset, i), 1, "CAB");

        CheckFreeQueues(sb, sm, Mark, MarkIp, r);

        if (cp.Find(sb.ReaperOid) is null) r.Error(CoreText.T("Check.ReaperMissing"));
        else
        {
            var reaper = ReaperState.Read(cp, sb);
            if (reaper.Problem is { } p) r.Error(p);
            foreach (var m in reaper.NotRewrittenByWriter(cp, sb))
                r.WriteBlockers.Add(CoreText.T("Check.UnknownEphemeral", m.Oid, m.Type, m.Subtype));
        }

        // ---- kapsayıcı nesne haritası ----
        var omapRecords = MarkObjectMap(c.Omap, sb.OmapOid, Mark, CoreText.T("Check.LabelContainerOmap"), out _);
        var fsLatest = new Dictionary<ulong, ObjectMap.Record>();
        foreach (var rec in omapRecords)
        {
            if (rec.Xid > sb.Xid) { r.Error(CoreText.T("Check.OmapFutureXid", rec.Oid, rec.Xid)); continue; }
            if (rec.Size != sb.BlockSize) r.Error(CoreText.T("Check.OmapRecordSize", rec.Oid, rec.Size));
            if (!fsLatest.TryAdd(rec.Oid, rec)) r.Error(CoreText.T("Check.OmapMultipleRecords", rec.Oid));
            Mark(rec.Paddr, 1, CoreText.T("Check.LabelVolumeSuperblock", rec.Oid));
        }
        foreach (ulong oid in sb.FsOids)
            if (!fsLatest.ContainsKey(oid)) r.Error(CoreText.T("Check.VolumeNotInOmap", oid));
        if (fsLatest.Count != sb.FsOids.Count) r.Error(CoreText.T("Check.OmapNonVolumeRecords"));

        // ---- birimler ----
        var opened = c.OpenVolumes(passwords, keys);
        for (int i = 0; i < sb.FsOids.Count; i++)
        {
            ulong oid = sb.FsOids[i];
            if (!fsLatest.TryGetValue(oid, out var loc)) continue;
            var apsb = reader.ReadObject(loc.Paddr, ApfsConstants.TypeFs, oid);
            var vol = opened.FirstOrDefault(x => x.Index == i)?.Volume;
            string label = CoreText.T("Check.LabelVolume", Bin.Utf8Z(Bin.Slice(apsb, 704, 256)));
            CheckVolume(c, apsb, vol, label, Mark, r);
        }

        // ---- bitmap karşılaştırması ----
        CompareMain(sm, chunks, used, r);
        CompareIp(sm, ipUsed, r);
    }

    // ------------------------------------------------------------------------------------------------------------------

    private static void MarkEfi(ObjectReader reader, ulong paddr, Action<ulong, ulong, string> mark, CheckReport r)
    {
        mark(paddr, 1, CoreText.T("Check.LabelEfiDriver"));
        var efi = reader.ReadObject(paddr, 0x14);
        uint n = Bin.U32(efi, 44);
        if (176 + (long)n * 16 > efi.Length) { r.Error(CoreText.T("Check.EfiExtentCount")); return; }
        for (uint i = 0; i < n; i++)
            mark(Bin.U64(efi, (int)(176 + i * 16)), Bin.U64(efi, (int)(184 + i * 16)), CoreText.T("Check.LabelEfiDriverData"));
    }

    private static void CheckFreeQueues(NxSuperblock sb, Spaceman sm, Action<ulong, ulong, string> mark,
        Action<ulong, ulong, string> markIp, CheckReport r)
    {
        string[] names = [CoreText.T("Check.LabelInternalPool"), CoreText.T("Check.LabelMainDevice"), CoreText.T("Check.LabelTier2")];
        for (int q = 0; q < 3; q++)
        {
            var info = sm.FreeQueues[q];
            if (q == Spaceman.SfqTier2)
            {
                if (info.TreeOid != 0 || info.Count != 0) r.Error(CoreText.T("Check.Tier2QueueNotEmpty"));
                continue;
            }
            ulong total = 0, oldest = 0;
            foreach (var e in sm.ReadFreeQueue(q))
            {
                if (e.Xid > sb.Xid) r.Error(CoreText.T("Check.FreeQueueFutureXid", names[q], e.Xid));
                if (e.Length == 0) r.Error(CoreText.T("Check.FreeQueueZeroLength", names[q]));
                if (oldest == 0 || e.Xid < oldest) oldest = e.Xid;
                total += e.Length;
                bool inIp = e.Paddr >= sm.IpBase && e.Paddr < sm.IpBase + sm.IpBlockCount;
                if (q == Spaceman.SfqIp)
                {
                    if (!inIp) r.Error(CoreText.T("Check.FreeQueueIpOutside", e.Paddr));
                    else markIp(e.Paddr, e.Length, CoreText.T("Check.LabelFreeQueueIp"));
                }
                else
                {
                    if (inIp) r.Error(CoreText.T("Check.FreeQueueMainInIp", e.Paddr));
                    else mark(e.Paddr, e.Length, CoreText.T("Check.LabelFreeQueueMain"));
                }
            }
            if (total != info.Count) r.Error(CoreText.T("Check.FreeQueueCount", names[q], info.Count, total));
            if (oldest != info.OldestXid) r.Error(CoreText.T("Check.FreeQueueOldest", names[q], info.OldestXid, oldest));
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
            mark(n.Paddr, 1, CoreText.T("Check.LabelNode", what));
            if ((n.ObjType & ApfsConstants.ObjEncrypted) != 0) flagged?.Add(n.Paddr);
            nodeCount++;
        }
        if (Bin.U32(omap.Raw, 36) != 0 || Bin.U64(omap.Raw, 56) != 0)
            throw new ApfsException(ApfsErrorCode.Unsupported, CoreText.T("Check.OmapHasSnapshots", what));
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
        if (Bin.U64(apsb, 216) != 0) { unsupported = true; r.Warnings.Add(CoreText.T("Check.VolumeHasSnapshots", label)); }
        foreach (var (off, name) in new[] { (976, CoreText.T("Check.LabelEncryptionInProgress")), (1024, CoreText.T("Check.LabelIntegrityMeta")), (1032, CoreText.T("Check.LabelFextTree")), (1000, CoreText.T("Check.LabelSnapshotExtension")) })
            if (Bin.U64(apsb, off) != 0) { unsupported = true; r.Warnings.Add(CoreText.T("Check.UnsupportedStructure", label, name)); }
        if (unsupported) r.OwnershipIncomplete = true;

        ulong counted = 0;
        void MarkVol(ulong start, ulong count, string what) { mark(start, count, $"{label}: {what}"); counted += count; }

        // Birim nesne haritası ve kayıtları (fs ağacı düğümleri)
        var omap = vol?.Omap ?? new ObjectMap(reader, volOmapPaddr);
        var flaggedOmap = new List<ulong>();
        var records = MarkObjectMap(omap, volOmapPaddr, MarkVol, "omap", out _, flaggedOmap);
        FlagErrors(r, label, CoreText.T("Check.FlagOmapNode"), flaggedOmap);
        var recordOids = new HashSet<ulong>();
        bool encrypted = (Bin.U64(apsb, 264) & 3) != ApfsConstants.ApfsFsUnencrypted;
        bool cryptoInProgress = Bin.U64(apsb, 976) != 0; // er_state: macOS düğümleri hâlâ şifreliyor/çözüyor olabilir
        int unflagged = 0;
        foreach (var rec in records)
        {
            if (rec.Xid > c.Xid) { r.Error(CoreText.T("Check.VolOmapFutureXid", label, rec.Oid)); continue; }
            if (!recordOids.Add(rec.Oid)) r.Error(CoreText.T("Check.VolOmapMultipleRecords", label, rec.Oid));
            if ((rec.Flags & ApfsConstants.OmapValDeleted) != 0) r.Error(CoreText.T("Check.VolOmapDeleted", label, rec.Oid));
            if (rec.Size != blockSize) r.Error(CoreText.T("Check.VolOmapRecordSize", label, rec.Oid, rec.Size));
            if (!encrypted && (rec.Flags & ApfsConstants.OmapValEncrypted) != 0)
                r.Error(CoreText.T("Check.EncryptedFlagOnPlain", label, rec.Oid));
            if (encrypted && !cryptoInProgress && (rec.Flags & ApfsConstants.OmapValEncrypted) == 0 && unflagged++ < 5)
                r.Error(CoreText.T("Check.MissingEncryptedFlag", label, rec.Oid));
            MarkVol(rec.Paddr, 1, CoreText.T("Check.LabelFsNode", rec.Oid));
        }

        // Fiziksel ağaçlar
        var phys = new List<(ulong Paddr, ulong Len, int Refcnt)>();
        if (extentRefOid != 0)
        {
            var tree = new BTree.BTree(reader, extentRefOid, ApfsConstants.TypeBlockRefTree, null);
            var flagged = new List<ulong>();
            foreach (var n in tree.EnumerateNodes())
            {
                MarkVol(n.Paddr, 1, CoreText.T("Check.LabelExtentrefNode"));
                if ((n.ObjType & ApfsConstants.ObjEncrypted) != 0) flagged.Add(n.Paddr);
            }
            FlagErrors(r, label, CoreText.T("Check.FlagExtentrefNode"), flagged);
            foreach (var e in tree.Scan(_ => 0))
            {
                if (e.Key.Length < 8 || e.Value.Length < 20) { r.Error(CoreText.T("Check.ExtentrefRecordShort", label)); continue; }
                ulong paddr = Bin.U64(e.Key, 0) & 0x0FFFFFFFFFFFFFFF;
                ulong len = Bin.U64(e.Value, 0) & 0x0FFFFFFFFFFFFFFF;
                int refcnt = Bin.I32(e.Value, 16);
                if (len == 0) r.Error(CoreText.T("Check.PhysExtentZeroLength", label, paddr));
                if (refcnt <= 0) r.Error(CoreText.T("Check.PhysExtentRefcnt", label, paddr, refcnt));
                phys.Add((paddr, len, refcnt));
                MarkVol(paddr, len, CoreText.T("Check.LabelFileData", paddr));
            }
        }
        if (snapMetaOid != 0)
        {
            var tree = new BTree.BTree(reader, snapMetaOid, ApfsConstants.TypeSnapMetaTree, null);
            var flagged = new List<ulong>();
            foreach (var n in tree.EnumerateNodes())
            {
                MarkVol(n.Paddr, 1, CoreText.T("Check.LabelSnapMetaNode"));
                if ((n.ObjType & ApfsConstants.ObjEncrypted) != 0) flagged.Add(n.Paddr);
            }
            FlagErrors(r, label, CoreText.T("Check.FlagSnapMetaNode"), flagged);
        }

        if (counted != allocCount)
        {
            string msg = CoreText.T("Check.AllocCount", label, allocCount, counted);
            if (unsupported) r.Warnings.Add(CoreText.T("Check.AllocCountMaybeUnchecked", msg)); else r.Error(msg);
        }

        if (vol is null)
        {
            r.Warnings.Add(CoreText.T("Check.VolumeNotOpened", label));
            r.Info.Add(CoreText.T("Check.FsFlagNotChecked", label));
            return;
        }

        // fs ağacı düğümleri ile omap kayıtları birebir; OBJ_ENCRYPTED ⇔ birim şifreli (er_state doluyken macOS düğümleri dönüştürüyor olabilir)
        var nodeOids = new HashSet<ulong>();
        var wrongFlag = new List<ulong>();
        int fsNodes = 0, fsFlagged = 0;
        foreach (var n in vol.FsTree.EnumerateNodes())
        {
            if (!nodeOids.Add(n.Oid)) r.Error(CoreText.T("Check.FsNodeUsedTwice", label, n.Oid));
            fsNodes++;
            bool flag = (n.ObjType & ApfsConstants.ObjEncrypted) != 0;
            if (flag) fsFlagged++;
            if (flag != encrypted) wrongFlag.Add(n.Oid);
        }
        r.Info.Add(CoreText.T("Check.FsNodeStats", label, fsNodes, fsFlagged));
        if (!cryptoInProgress)
            FlagErrors(r, label, encrypted ? CoreText.T("Check.FsNodeMissingFlag") : CoreText.T("Check.FsNodeWrongFlag"), wrongFlag);
        foreach (ulong oid in recordOids)
            if (!nodeOids.Contains(oid)) r.Error(CoreText.T("Check.OmapRecordUnused", label, oid));

        CheckFsTree(c, vol, label, phys, r);
    }

    /// <summary>OBJ_ENCRYPTED kuralının hataları: ilk 5 nesne adıyla, fazlası toplam sayıyla.</summary>
    private static void FlagErrors(CheckReport r, string label, string what, List<ulong> ids)
    {
        foreach (ulong id in ids.Take(5)) r.Error($"{label}: {what} {id}");
        if (ids.Count > 5) r.Error(CoreText.T("Check.FlagTotal", label, what, ids.Count));
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
                    if ((inodes[id].Inode.InternalFlags & 0x2) != 0) blockers.Add(CoreText.T("Check.LabelDirStats"));
                    break;
                case JType.DirStats:
                    blockers.Add(CoreText.T("Check.LabelDirStats"));
                    break;
                case JType.FileInfo:
                    blockers.Add(CoreText.T("Check.LabelFileInfo"));
                    break;
                case JType.DirRec:
                    var de = ApfsVolume.ParseDirRec(e.Key, e.Value);
                    if (de is null) { r.Error(CoreText.T("Check.UnnamedDirEntry", label, id)); break; }
                    drecs.Add((id, de));
                    int hashedLen = (int)(Bin.U32(e.Key, 8) & 0x3FF);
                    if (12 + hashedLen == e.Key.Length && ApfsNames.Hash(de.Name, v.IsCaseInsensitive) != Bin.U32(e.Key, 8) >> 10)
                        r.Error(CoreText.T("Check.NameHashWrong", label, de.Name));
                    break;
                case JType.DstreamId:
                    if (e.Value.Length < 4) { r.Error(CoreText.T("Check.DstreamRecordShort", label, id)); break; }
                    dstreamRefs[id] = Bin.U32(e.Value, 0);
                    break;
                case JType.CryptoState:
                    // CRYPTO_SW_ID'nin yer tutucusu: anahtarsız (key_len 0); dosyaların default_crypto_id'si bunu gösterir
                    if (id != CryptoSwId || e.Value.Length < 24 || Bin.U16(e.Value, 22) != 0) cryptoStates++;
                    break;
                case JType.FileExtent:
                    if (e.Key.Length < 16 || e.Value.Length < 24) { r.Error(CoreText.T("Check.ExtentRecordShort", label, id)); break; }
                    ulong len = Bin.U64(e.Value, 0) & 0x00FFFFFFFFFFFFFF;
                    if (tweakRules && Bin.U64(e.Value, 8) != 0)
                    {
                        if (Bin.U64(e.Value, 16) is 0 or CryptoSwId && badCrypto++ < 5)
                            r.Error(CoreText.T("Check.ExtentCryptoId", label, id, Bin.U64(e.Value, 16)));
                        if ((Bin.U64(e.Value, 0) >> 56 & FextCryptoIdIsTweak) == 0 && badCrypto++ < 5)
                            r.Error(CoreText.T("Check.ExtentNoTweakFlag", label, id));
                    }
                    if (len == 0 || len % blockSize != 0) r.Error(CoreText.T("Check.ExtentLength", label, id, len));
                    if (!extents.TryGetValue(id, out var list)) extents[id] = list = [];
                    list.Add((Bin.U64(e.Key, 8), len, Bin.U64(e.Value, 8)));
                    break;
                case JType.Xattr:
                    if (e.Value.Length >= 4 && (Bin.U16(e.Value, 0) & ApfsVolume.XattrDataStream) != 0)
                    {
                        if (e.Value.Length < 4 + 24) { r.Error(CoreText.T("Check.XattrStreamRecordShort", label, id)); break; }
                        xattrStreams.Add((id, Bin.U64(e.Value, 4), Bin.U64(e.Value, 12), Bin.U64(e.Value, 20)));
                    }
                    break;
            }
        }

        if (cryptoStates > 0 && (v.FsFlags & ApfsConstants.ApfsFsOneKey) != 0)
            r.Error(CoreText.T("Check.PerFileKeysOnOneKey", label, cryptoStates));
        foreach (var b in blockers) r.WriteBlockers.Add($"{label}: {b}");
        if (!inodes.ContainsKey(Inode.RootDirId)) r.Error(CoreText.T("Check.RootDirMissing", label));

        // Dizin girdileri → inode
        foreach (var (parent, de) in drecs)
        {
            // Kök klasör ve özel klasörün girdileri, inode'u olmayan sanal üstte (1) durur
            if (parent != Inode.RootDirParent)
            {
                if (!inodes.TryGetValue(parent, out var p)) { r.Error(CoreText.T("Check.EntryParentMissing", label, de.Name, parent)); continue; }
                if (!p.Inode.IsDirectory) r.Error(CoreText.T("Check.EntryParentNotDir", label, de.Name));
                p.Children++;
            }
            else if (de.FileId is not (Inode.RootDirId or PrivateDirId)) r.Error(CoreText.T("Check.EntryVirtualParentNotRoot", label, de.Name));
            if (!inodes.TryGetValue(de.FileId, out var t)) { r.Error(CoreText.T("Check.EntryInodeMissing", label, de.Name, de.FileId)); continue; }
            t.Drecs++;
            if (ExpectedType(t.Inode) != de.Type) r.Error(CoreText.T("Check.EntryTypeMismatch", label, de.Name));
        }

        long files = 0, dirs = 0, symlinks = 0, others = 0;
        var owners = new Dictionary<ulong, uint>();
        foreach (var (id, f) in inodes)
        {
            var ino = f.Inode;
            if (ino.IsDirectory)
            {
                if (id != Inode.RootDirId && id != PrivateDirId) dirs++;
                if (ino.ChildrenOrLinks != f.Children) r.Error(CoreText.T("Check.DirChildCount", label, id, ino.ChildrenOrLinks, f.Children));
                if (id != Inode.RootDirId && id != PrivateDirId && f.Drecs != 1) r.Error(CoreText.T("Check.DirMultiplyReferenced", label, id, f.Drecs));
            }
            else
            {
                if (ino.IsRegularFile) files++;
                else if (ino.IsSymlink) symlinks++;
                else others++;
                if (ino.ChildrenOrLinks != f.Drecs) r.Error(CoreText.T("Check.InodeLinkCount", label, id, ino.ChildrenOrLinks, f.Drecs));
            }
            if (ino.HasDataStream)
            {
                if (!ino.IsRegularFile) r.Error(CoreText.T("Check.InodeNotFileHasStream", label, id));
                owners[ino.PrivateId] = owners.GetValueOrDefault(ino.PrivateId) + 1;
                CheckStream(label, $"inode {id}", extents.GetValueOrDefault(ino.PrivateId), ino.DataSize, ino.AllocatedSize, r);
            }
        }
        // Başvuru sayıları: dosya veri akışlarının dstream_id kaydı vardır ve sayısı sahiplerle aynıdır (klonlarda 2+).
        foreach (var (sid, n) in owners)
        {
            if (dstreamRefs.TryGetValue(sid, out uint refcnt)) { if (refcnt != n) r.Error(CoreText.T("Check.DstreamRefcnt", label, sid, refcnt, n)); }
            else if (extents.ContainsKey(sid)) r.Error(CoreText.T("Check.DstreamNoRefRecord", label, sid));
        }
        // Öznitelik veri akışları paylaşılamaz ve dstream_id kaydı taşımaz (apfsck: "xattrs can't be cloned")
        var xattrOwners = new Dictionary<ulong, int>();
        foreach (var (owner, sid, size, alloc) in xattrStreams)
        {
            xattrOwners[sid] = xattrOwners.GetValueOrDefault(sid) + 1;
            if (owners.ContainsKey(sid)) r.Error(CoreText.T("Check.StreamFileAndXattr", label, sid));
            if (dstreamRefs.ContainsKey(sid)) r.Error(CoreText.T("Check.XattrStreamHasRefRecord", label, sid));
            CheckStream(label, CoreText.T("Check.LabelXattrStream", sid, owner), extents.GetValueOrDefault(sid), size, alloc, r);
        }
        foreach (var (sid, n) in xattrOwners)
            if (n != 1) r.Error(CoreText.T("Check.XattrStreamShared", label, sid, n));
        foreach (var sid in dstreamRefs.Keys)
            if (!owners.ContainsKey(sid)) r.Error(CoreText.T("Check.OrphanDstreamRecord", label, sid));
        foreach (var sid in extents.Keys)
            if (!owners.ContainsKey(sid) && !xattrOwners.ContainsKey(sid)) r.Error(CoreText.T("Check.OrphanExtents", label, sid));

        CheckPhysicalExtents(label, extents, phys, blockSize, r);

        if (files != (long)v.NumFiles || dirs != (long)v.NumDirectories || symlinks != (long)v.NumSymlinks || others != (long)v.NumOtherFsObjects)
            r.Error(CoreText.T("Check.Counters", label, v.NumFiles, v.NumDirectories, v.NumSymlinks, v.NumOtherFsObjects, files, dirs, symlinks, others));
        if (v.NextObjId <= maxId) r.Error(CoreText.T("Check.NextObjId", label, v.NextObjId, maxId));
        r.Info.Add(CoreText.T("Check.FsSummary", label, files, dirs, symlinks, extents.Count, phys.Count));
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
                if (x.Logical != next) { r.Error(CoreText.T("Check.StreamGapOrOverlap", label, what, x.Logical, next)); break; }
                next += x.Len;
            }
        }
        if (next != alloc) r.Error(CoreText.T("Check.StreamAllocSize", label, what, alloc, next));
        if (size > alloc) r.Error(CoreText.T("Check.StreamSizeTooLarge", label, what, size, alloc));
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
            if (phys[i].Paddr < phys[i - 1].Paddr + phys[i - 1].Len) r.Error(CoreText.T("Check.PhysExtentsOverlap", label, phys[i].Paddr));

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
                if (seg.Count != p.Refcnt && errors++ < 5) r.Error(CoreText.T("Check.PhysExtentCoverage", label, p.Paddr, p.Refcnt, seg.Count));
                covered += b - a;
            }
            matched += covered;
            if (covered != p.Len && errors++ < 5) r.Error(CoreText.T("Check.PhysExtentUncovered", label, p.Paddr, p.Len));
        }
        if (matched != segTotal && errors++ < 5) r.Error(CoreText.T("Check.FileDataNoPhysExtent", label, segTotal - matched));
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
            if (free != ch.FreeCount) r.Error(CoreText.T("Check.ChunkFreeCount", ch.Addr, ch.FreeCount, free));
            for (uint i = 0; i < ch.BlockCount; i++)
            {
                bool onDisk = (bm[i >> 3] & (1 << (int)(i & 7))) != 0;
                bool expected = used.Get(ch.Addr + i);
                if (onDisk == expected) continue;
                if (onDisk) leaked++; else freeButUsed++;
                if (examples.Count < 5) examples.Add(CoreText.T("Check.BitmapExample", ch.Addr + i, (onDisk ? CoreText.T("Check.MarkedUsedNotUsed") : CoreText.T("Check.UsedMarkedFree"))));
            }
        }
        if (sumFree != sm.Main.FreeCount) r.Error(CoreText.T("Check.DeviceFreeCount", sm.Main.FreeCount, sumFree));
        if (freeButUsed > 0) r.Error(CoreText.T("Check.UsedButFree", freeButUsed));
        if (leaked > 0)
        {
            // Denetlenmeyen yapılar varsa dolu ama sahipsiz görünen bloklar onlarındır; sızıntı denemez
            if (r.OwnershipIncomplete) r.Warnings.Add(CoreText.T("Check.LeakedUnchecked", leaked));
            else r.Error(CoreText.T("Check.Leaked", leaked));
        }
        if (freeButUsed > 0 || (leaked > 0 && !r.OwnershipIncomplete)) r.Error(CoreText.T("Check.BitmapExamples", string.Join("; ", examples)));
        string match = freeButUsed + leaked == 0 ? CoreText.T("Check.OwnershipIdentical") : freeButUsed == 0 && r.OwnershipIncomplete ? CoreText.T("Check.OwnershipIdenticalChecked", leaked) : CoreText.T("Check.OwnershipDifferent");
        r.Info.Add(CoreText.T("Check.SpacemanSummary", sm.Main.BlockCount, sm.Main.FreeCount, match));
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
        if (diff > 0) r.Error(CoreText.T("Check.IpBitmapDiff", diff));
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
