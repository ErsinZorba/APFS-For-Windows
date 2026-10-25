using Apfs.Container;
using Apfs.Util;

namespace Apfs.Diagnostics;

/// <summary>
/// `apfs dump`: kapsayıcının iç yapısını metin olarak döker (kapsayıcı süper bloğu, checkpoint, alan yöneticisi, boş kuyruklar,
/// reaper, nesne haritası, birim süper blokları). Yazma desteği geliştirilirken macOS'un ve kendi yazıcımızın ürettiği yapıları
/// karşılaştırmak için kullanılır. Salt okunur; tutarsızlıklar "SORUN:" önekiyle yazılır.
/// </summary>
public static class ContainerDump
{
    public static IEnumerable<string> Lines(ApfsContainer c)
    {
        var sb = c.Superblock;
        yield return CoreText.T("Dump.ContainerHeader", sb.Xid, sb.Paddr, sb.NextXid, sb.NextOid);
        yield return CoreText.T("Dump.ContainerBlocks", sb.BlockSize, sb.BlockCount, sb.Uuid);
        yield return CoreText.T("Dump.ContainerFeatures", sb.Features, sb.ReadOnlyCompatibleFeatures, sb.IncompatibleFeatures, sb.Flags);
        yield return CoreText.T("Dump.DescRing", sb.XpDescBase, sb.XpDescBlocks, sb.XpDescIndex, sb.XpDescLen, sb.XpDescNext);
        yield return CoreText.T("Dump.DataRing", sb.XpDataBase, sb.XpDataBlocks, sb.XpDataIndex, sb.XpDataLen, sb.XpDataNext);
        yield return $"  spaceman oid {sb.SpacemanOid}, omap {sb.OmapOid}, reaper oid {sb.ReaperOid}, efi {sb.EfiJumpstart}, keylocker {sb.KeylockerStart}+{sb.KeylockerBlockCount}";
        yield return CoreText.T("Dump.ContainerVolumes", string.Join(", ", sb.FsOids), sb.MaxFileSystems, sb.BlockedOutStart, sb.BlockedOutCount);
        foreach (var w in c.Warnings) yield return CoreText.T("Dump.Warning", w);

        var block0 = c.Reader.ReadBlock(0);
        bool b0Valid = Fletcher64.Verify(block0);
        ulong b0Xid = Bin.U64(block0, 16);
        yield return CoreText.T("Dump.Block0Copy", b0Xid, (b0Valid ? CoreText.T("Dump.ChecksumOk") : CoreText.T("Dump.ChecksumBad"))) +
            (b0Xid == sb.Xid ? CoreText.T("Dump.Block0SameAsLatest") : CoreText.T("Dump.Block0Older"));

        // ---- Checkpoint ----
        var cp = Checkpoint.Read(c);
        yield return CoreText.T("Dump.CheckpointHeader", string.Join(", ", cp.MapBlocks), cp.Mappings.Count);
        foreach (var m in cp.Mappings)
            yield return CoreText.T("Dump.CheckpointMapping", m.Oid, m.Type, m.Subtype, m.Size, m.Paddr, (m.FsOid != 0 ? CoreText.T("Dump.CheckpointMappingVolume", m.FsOid) : ""));
        foreach (var p in cp.Problems) yield return CoreText.T("Dump.Problem", p);

        // ---- Alan yöneticisi ----
        Spaceman? sm = null;
        string? smError = null;
        try { sm = Spaceman.Read(c, cp); }
        catch (ApfsException e) when (e.Code is not ApfsErrorCode.IoError) { smError = e.Message; }
        if (sm is null)
        {
            yield return CoreText.T("Dump.SpacemanUnreadable", smError);
        }
        else
        {
            foreach (var l in SpacemanLines(c, sm)) yield return l;
        }

        // ---- Reaper ----
        if (cp.Find(sb.ReaperOid) is { } rm)
        {
            byte[]? r = null;
            string? rError = null;
            try { r = cp.ReadEphemeral(rm); } catch (ApfsException e) when (e.Code is not ApfsErrorCode.IoError) { rError = e.Message; }
            if (r is null) yield return CoreText.T("Dump.ReaperProblem", rError);
            else
            {
                yield return CoreText.T("Dump.ReaperHeader", Bin.U64(r, 32), Bin.U64(r, 40), Bin.U64(r, 48), Bin.U64(r, 56), Bin.U32(r, 64), Bin.U32(r, 68), Bin.U32(r, 72), Bin.U64(r, 80), Bin.U64(r, 88), Bin.U64(r, 96));
                ReaperState? st = null;
                try { st = ReaperState.Read(cp, sb); } catch (ApfsException e) when (e.Code is not ApfsErrorCode.IoError) { rError = e.Message; }
                foreach (var (m, l) in st?.Lists ?? [])
                    yield return CoreText.T("Dump.ReapList", m.Oid, m.Paddr, Bin.U64(l, 32), Bin.U32(l, 40), Bin.U32(l, 44), Bin.U32(l, 48), Bin.U32(l, 52), Bin.U32(l, 56), Bin.U32(l, 60));
                yield return st is null ? CoreText.T("Dump.ReaperStatusProblem", rError)
                    : st.IsIdle ? CoreText.T("Dump.ReaperStatusIdle") : CoreText.T("Dump.ReaperStatus", st.Problem);
            }
        }
        else yield return CoreText.T("Dump.ReaperNotInCheckpoint");

        // ---- Nesne haritası ----
        var omap = c.Reader.ReadObject(sb.OmapOid, ApfsConstants.TypeOmap);
        yield return OmapLine(CoreText.T("Dump.ContainerOmapTitle"), sb.OmapOid, omap);

        // ---- Birimler ----
        foreach (ulong oid in sb.FsOids)
        {
            byte[] v;
            ulong paddr;
            string? err = null;
            try
            {
                var loc = c.Omap.Resolve(oid, c.Xid);
                paddr = loc.Paddr;
                v = c.Reader.ReadObject(paddr, ApfsConstants.TypeFs, oid);
            }
            catch (ApfsException e) when (e.Code is not ApfsErrorCode.IoError) { err = e.Message; v = []; paddr = 0; }
            if (err is not null) { yield return CoreText.T("Dump.VolumeProblem", oid, err); continue; }
            foreach (var l in VolumeLines(c, oid, paddr, v)) yield return l;
        }
    }

    private static IEnumerable<string> SpacemanLines(ApfsContainer c, Spaceman sm)
    {
        var d = sm.Main;
        yield return CoreText.T("Dump.SpacemanHeader", sm.Xid, sm.Mapping.Size, sm.Version, sm.StructSize, sm.Flags);
        yield return CoreText.T("Dump.SpacemanGeometry", sm.BlocksPerChunk, sm.ChunksPerCib, sm.CibsPerCab);
        yield return CoreText.T("Dump.SpacemanMainDevice", d.BlockCount, d.ChunkCount, d.CibCount, d.CabCount, d.FreeCount, d.AddrOffset);
        if (sm.Tier2.BlockCount != 0) yield return CoreText.T("Dump.SpacemanTier2", sm.Tier2.BlockCount);
        yield return CoreText.T("Dump.SpacemanInternalPool", sm.IpBase, sm.IpBlockCount, sm.IpBmBase, sm.IpBmBlockCount, sm.IpBmSizeInBlocks, sm.IpBmTxMultiplier, sm.IpBmFreeHead, sm.IpBmFreeTail);
        yield return CoreText.T("Dump.SpacemanReserve", sm.FsReserveBlockCount, sm.FsReserveAllocCount);

        // Parçalar: bitmap'ten sayılan boş = parça bilgisindeki boş = aygıt sayacı
        IReadOnlyList<ChunkInfo>? chunks = null;
        string? err = null;
        try { chunks = sm.MainChunks(); } catch (ApfsException e) when (e.Code is not ApfsErrorCode.IoError) { err = e.Message; }
        if (chunks is null) { yield return CoreText.T("Dump.ChunkInfoUnreadable", err); yield break; }
        ulong sumInfo = 0, sumBitmap = 0, blocks = 0, expect = 0;
        int mismatched = 0, notConsecutive = 0;
        foreach (var ch in chunks)
        {
            uint fromBitmap = Spaceman.CountFree(sm.ReadChunkBitmap(ch), ch.BlockCount);
            sumInfo += ch.FreeCount;
            sumBitmap += fromBitmap;
            blocks += ch.BlockCount;
            if (fromBitmap != ch.FreeCount) mismatched++;
            if (ch.Addr != expect) notConsecutive++;
            expect = ch.Addr + ch.BlockCount;
        }
        yield return CoreText.T("Dump.ChunksSummary", chunks.Count, blocks, d.FreeCount, sumInfo, sumBitmap);
        if (sumInfo != d.FreeCount || sumBitmap != d.FreeCount) yield return CoreText.T("Dump.FreeCountersDisagree");
        if (mismatched > 0) yield return CoreText.T("Dump.ChunkBitmapMismatch", mismatched);
        if (notConsecutive > 0) yield return CoreText.T("Dump.ChunksNotConsecutive", notConsecutive);
        if (blocks != d.BlockCount || (ulong)chunks.Count != d.ChunkCount) yield return CoreText.T("Dump.ChunkCountsMismatch");
        if (d.BlockCount != c.Superblock.BlockCount) yield return CoreText.T("Dump.SpacemanBlockCountMismatch");

        // İç havuz bitmap'i
        string ipLine;
        try
        {
            var ip = sm.ReadIpBitmap();
            ulong used = 0;
            for (ulong i = 0; i < sm.IpBlockCount; i++) if ((ip[i >> 3] & (1 << (int)(i & 7))) != 0) used++;
            ipLine = CoreText.T("Dump.IpBitmapUsage", used, sm.IpBlockCount);
        }
        catch (ApfsException e) when (e.Code is not ApfsErrorCode.IoError) { ipLine = CoreText.T("Dump.IpBitmapUnreadable", e.Message); }
        yield return ipLine;

        string[] names = [CoreText.T("Dump.QueueInternalPool"), CoreText.T("Dump.QueueMainDevice"), CoreText.T("Dump.QueueTier2")];
        for (int q = 0; q < 3; q++)
        {
            var fq = sm.FreeQueues[q];
            if (fq.TreeOid == 0 && fq.Count == 0) continue;
            string line;
            try
            {
                ulong entries = 0, total = 0, oldest = ulong.MaxValue;
                foreach (var e in sm.ReadFreeQueue(q)) { entries++; total += e.Length; oldest = Math.Min(oldest, e.Xid); }
                line = CoreText.T("Dump.FreeQueue", names[q], fq.Count, fq.TreeOid, fq.OldestXid, fq.NodeLimit, entries, total) + (total != fq.Count ? CoreText.T("Dump.FreeQueueCountMismatch") : "") +
                    (entries > 0 && oldest != fq.OldestXid ? CoreText.T("Dump.FreeQueueOldestMismatch") : "");
            }
            catch (ApfsException e) when (e.Code is not ApfsErrorCode.IoError) { line = CoreText.T("Dump.FreeQueueUnreadable", names[q], e.Message); }
            yield return line;
        }
    }

    private static string OmapLine(string title, ulong paddr, byte[] om) =>
        CoreText.T("Dump.OmapLine", title, paddr, Bin.U32(om, 32), Bin.U32(om, 36), Bin.U32(om, 40), Bin.U64(om, 48), Bin.U64(om, 56), Bin.U64(om, 64));

    private static IEnumerable<string> VolumeLines(ApfsContainer c, ulong oid, ulong paddr, byte[] v)
    {
        string name = Bin.Utf8Z(Bin.Slice(v, 704, 256));
        yield return CoreText.T("Dump.VolumeHeader", oid, name, paddr, Bin.U64(v, 16), Bin.U32(v, 36), Bin.U16(v, 964));
        yield return CoreText.T("Dump.VolumeFeatures", Bin.U64(v, 40), Bin.U64(v, 48), Bin.U64(v, 56), Bin.U64(v, 264));
        yield return CoreText.T("Dump.VolumeTrees", Bin.U64(v, 128), Bin.U64(v, 136), Bin.U32(v, 116), Bin.U64(v, 144), Bin.U32(v, 120), Bin.U64(v, 152), Bin.U32(v, 124));
        yield return CoreText.T("Dump.VolumeCounts", Bin.U64(v, 184), Bin.U64(v, 192), Bin.U64(v, 200), Bin.U64(v, 208), Bin.U64(v, 216));
        yield return CoreText.T("Dump.VolumeBlocks", Bin.U64(v, 88), Bin.U64(v, 224), Bin.U64(v, 232), Bin.U64(v, 72), Bin.U64(v, 80));
        yield return CoreText.T("Dump.VolumeIds", Bin.U64(v, 176), Bin.U32(v, 960), Bin.U64(v, 256), Bin.U64(v, 64));
        yield return CoreText.T("Dump.VolumeMetaCrypto", Bin.U16(v, 96), Bin.U16(v, 98), Bin.U32(v, 100), Bin.U32(v, 104));
        yield return CoreText.T("Dump.VolumeFormattedBy", ModifiedBy(v, 272));
        for (int i = 0; i < 8; i++)
        {
            int o = 320 + i * 48;
            if (v[o] == 0) continue;
            yield return CoreText.T("Dump.VolumeModifiedBy", i, ModifiedBy(v, o));
        }
        yield return CoreText.T("Dump.VolumeMisc", Bin.U64(v, 160), Bin.U64(v, 976), Bin.U64(v, 984), Bin.U64(v, 992), Bin.U64(v, 1024), Bin.U64(v, 1032));

        // Birimin nesne haritası
        var om = c.Reader.ReadObject(Bin.U64(v, 128), ApfsConstants.TypeOmap);
        yield return OmapLine(CoreText.T("Dump.VolumeOmapTitle"), Bin.U64(v, 128), om);
    }

    private static string ModifiedBy(byte[] v, int o) =>
        CoreText.T("Dump.ModifiedByEntry", Bin.Utf8Z(Bin.Slice(v, o, 32)), Bin.U64(v, o + 32), Bin.U64(v, o + 40));
}
