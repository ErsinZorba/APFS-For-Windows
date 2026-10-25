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
        yield return $"[kapsayıcı] xid {sb.Xid} (blok {sb.Paddr}), sonraki xid {sb.NextXid}, sonraki oid {sb.NextOid}";
        yield return $"  blok {sb.BlockSize} × {sb.BlockCount}, uuid {sb.Uuid}";
        yield return $"  özellikler 0x{sb.Features:x}, ro-uyumlu 0x{sb.ReadOnlyCompatibleFeatures:x}, uyumsuz 0x{sb.IncompatibleFeatures:x}, bayraklar 0x{sb.Flags:x}";
        yield return $"  tanımlayıcı halkası: taban {sb.XpDescBase}, {sb.XpDescBlocks} blok, index {sb.XpDescIndex}, len {sb.XpDescLen}, next {sb.XpDescNext}";
        yield return $"  veri halkası:        taban {sb.XpDataBase}, {sb.XpDataBlocks} blok, index {sb.XpDataIndex}, len {sb.XpDataLen}, next {sb.XpDataNext}";
        yield return $"  spaceman oid {sb.SpacemanOid}, omap {sb.OmapOid}, reaper oid {sb.ReaperOid}, efi {sb.EfiJumpstart}, keylocker {sb.KeylockerStart}+{sb.KeylockerBlockCount}";
        yield return $"  birimler: {string.Join(", ", sb.FsOids)} (en fazla {sb.MaxFileSystems}); engelli aralık {sb.BlockedOutStart}+{sb.BlockedOutCount}";
        foreach (var w in c.Warnings) yield return $"  UYARI: {w}";

        var block0 = c.Reader.ReadBlock(0);
        bool b0Valid = Fletcher64.Verify(block0);
        ulong b0Xid = Bin.U64(block0, 16);
        yield return $"  blok 0 kopyası: xid {b0Xid}, sağlama {(b0Valid ? "doğru" : "BOZUK")}" +
            (b0Xid == sb.Xid ? " (son checkpoint ile aynı)" : " (son checkpoint'ten eski: düzgün kapatılmamış)");

        // ---- Checkpoint ----
        var cp = Checkpoint.Read(c);
        yield return $"[checkpoint] eşleme blokları: {string.Join(", ", cp.MapBlocks)}; {cp.Mappings.Count} geçici nesne";
        foreach (var m in cp.Mappings)
            yield return $"  oid {m.Oid}: tür 0x{m.Type:x8}/{m.Subtype:x}, {m.Size} bayt, blok {m.Paddr}{(m.FsOid != 0 ? $", birim {m.FsOid}" : "")}";
        foreach (var p in cp.Problems) yield return $"  SORUN: {p}";

        // ---- Alan yöneticisi ----
        Spaceman? sm = null;
        string? smError = null;
        try { sm = Spaceman.Read(c, cp); }
        catch (ApfsException e) when (e.Code is not ApfsErrorCode.IoError) { smError = e.Message; }
        if (sm is null)
        {
            yield return $"[alan yöneticisi] SORUN: okunamadı: {smError}";
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
            if (r is null) yield return $"[reaper] SORUN: {rError}";
            else
            {
                yield return $"[reaper] sonraki {Bin.U64(r, 32)}, tamamlanan {Bin.U64(r, 40)}, baş {Bin.U64(r, 48)}, kuyruk {Bin.U64(r, 56)}, " +
                    $"bayrak 0x{Bin.U32(r, 64):x}, liste {Bin.U32(r, 68)}, tür 0x{Bin.U32(r, 72):x}, birim {Bin.U64(r, 80)}, nesne {Bin.U64(r, 88)}, xid {Bin.U64(r, 96)}";
                ReaperState? st = null;
                try { st = ReaperState.Read(cp, sb); } catch (ApfsException e) when (e.Code is not ApfsErrorCode.IoError) { rError = e.Message; }
                foreach (var (m, l) in st?.Lists ?? [])
                    yield return $"  liste {m.Oid} (blok {m.Paddr}): next {Bin.U64(l, 32)}, bayrak 0x{Bin.U32(l, 40):x}, en fazla {Bin.U32(l, 44)}, " +
                        $"sayı {Bin.U32(l, 48)}, ilk 0x{Bin.U32(l, 52):x8}, son 0x{Bin.U32(l, 56):x8}, boş 0x{Bin.U32(l, 60):x8}";
                yield return st is null ? $"  durum: SORUN: {rError}"
                    : st.IsIdle ? "  durum: boşta" : $"  durum: {st.Problem}";
            }
        }
        else yield return "[reaper] SORUN: checkpoint haritasında yok";

        // ---- Nesne haritası ----
        var omap = c.Reader.ReadObject(sb.OmapOid, ApfsConstants.TypeOmap);
        yield return OmapLine("[kapsayıcı omap]", sb.OmapOid, omap);

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
            if (err is not null) { yield return $"[birim {oid}] SORUN: {err}"; continue; }
            foreach (var l in VolumeLines(c, oid, paddr, v)) yield return l;
        }
    }

    private static IEnumerable<string> SpacemanLines(ApfsContainer c, Spaceman sm)
    {
        var d = sm.Main;
        yield return $"[alan yöneticisi] xid {sm.Xid}, {sm.Mapping.Size} bayt, sürüm {sm.Version}, yapı {sm.StructSize}, bayrak 0x{sm.Flags:x}";
        yield return $"  parça başına blok {sm.BlocksPerChunk}, CIB başına parça {sm.ChunksPerCib}, CAB başına CIB {sm.CibsPerCab}";
        yield return $"  ana aygıt: {d.BlockCount} blok, {d.ChunkCount} parça, {d.CibCount} CIB, {d.CabCount} CAB, boş {d.FreeCount}, adres ofseti {d.AddrOffset}";
        if (sm.Tier2.BlockCount != 0) yield return $"  ikinci katman: {sm.Tier2.BlockCount} blok (Fusion)";
        yield return $"  iç havuz: taban {sm.IpBase}, {sm.IpBlockCount} blok; bitmap halkası taban {sm.IpBmBase}, {sm.IpBmBlockCount} blok, " +
            $"bitmap {sm.IpBmSizeInBlocks} blok, çarpan {sm.IpBmTxMultiplier}, boş liste {sm.IpBmFreeHead}→{sm.IpBmFreeTail}";
        yield return $"  birim rezervi: {sm.FsReserveBlockCount} blok, ayrılan {sm.FsReserveAllocCount}";

        // Parçalar: bitmap'ten sayılan boş = parça bilgisindeki boş = aygıt sayacı
        IReadOnlyList<ChunkInfo>? chunks = null;
        string? err = null;
        try { chunks = sm.MainChunks(); } catch (ApfsException e) when (e.Code is not ApfsErrorCode.IoError) { err = e.Message; }
        if (chunks is null) { yield return $"  SORUN: parça bilgileri okunamadı: {err}"; yield break; }
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
        yield return $"  parçalar: {chunks.Count}, toplam {blocks} blok; boş: aygıt sayacı {d.FreeCount}, parça bilgileri {sumInfo}, bitmap'ler {sumBitmap}";
        if (sumInfo != d.FreeCount || sumBitmap != d.FreeCount) yield return "  SORUN: boş blok sayaçları birbirini tutmuyor";
        if (mismatched > 0) yield return $"  SORUN: {mismatched} parçada bitmap ile parça bilgisi farklı";
        if (notConsecutive > 0) yield return $"  SORUN: {notConsecutive} parça ardışık değil";
        if (blocks != d.BlockCount || (ulong)chunks.Count != d.ChunkCount) yield return "  SORUN: parça/blok sayıları aygıtla tutmuyor";
        if (d.BlockCount != c.Superblock.BlockCount) yield return "  SORUN: alan yöneticisinin blok sayısı kapsayıcıdan farklı";

        // İç havuz bitmap'i
        string ipLine;
        try
        {
            var ip = sm.ReadIpBitmap();
            ulong used = 0;
            for (ulong i = 0; i < sm.IpBlockCount; i++) if ((ip[i >> 3] & (1 << (int)(i & 7))) != 0) used++;
            ipLine = $"  iç havuz bitmap'i: {used} / {sm.IpBlockCount} blok kullanımda";
        }
        catch (ApfsException e) when (e.Code is not ApfsErrorCode.IoError) { ipLine = $"  SORUN: iç havuz bitmap'i okunamadı: {e.Message}"; }
        yield return ipLine;

        string[] names = ["iç havuz", "ana aygıt", "ikinci katman"];
        for (int q = 0; q < 3; q++)
        {
            var fq = sm.FreeQueues[q];
            if (fq.TreeOid == 0 && fq.Count == 0) continue;
            string line;
            try
            {
                ulong entries = 0, total = 0, oldest = ulong.MaxValue;
                foreach (var e in sm.ReadFreeQueue(q)) { entries++; total += e.Length; oldest = Math.Min(oldest, e.Xid); }
                line = $"  boş kuyruk ({names[q]}): sayaç {fq.Count}, ağaç {fq.TreeOid}, en eski xid {fq.OldestXid}, düğüm sınırı {fq.NodeLimit}; " +
                    $"{entries} kayıt, {total} blok" + (total != fq.Count ? "  SORUN: sayaç tutmuyor" : "") +
                    (entries > 0 && oldest != fq.OldestXid ? "  SORUN: en eski xid tutmuyor" : "");
            }
            catch (ApfsException e) when (e.Code is not ApfsErrorCode.IoError) { line = $"  SORUN: boş kuyruk ({names[q]}) okunamadı: {e.Message}"; }
            yield return line;
        }
    }

    private static string OmapLine(string title, ulong paddr, byte[] om) =>
        $"{title} blok {paddr}: bayrak 0x{Bin.U32(om, 32):x}, anlık görüntü {Bin.U32(om, 36)}, ağaç türü 0x{Bin.U32(om, 40):x8}, ağaç {Bin.U64(om, 48)}, " +
        $"anlık görüntü ağacı {Bin.U64(om, 56)}, en yeni anlık {Bin.U64(om, 64)}";

    private static IEnumerable<string> VolumeLines(ApfsContainer c, ulong oid, ulong paddr, byte[] v)
    {
        string name = Bin.Utf8Z(Bin.Slice(v, 704, 256));
        yield return $"[birim {oid}] \"{name}\" blok {paddr}, xid {Bin.U64(v, 16)}, sıra {Bin.U32(v, 36)}, rol 0x{Bin.U16(v, 964):x}";
        yield return $"  özellikler 0x{Bin.U64(v, 40):x}, ro-uyumlu 0x{Bin.U64(v, 48):x}, uyumsuz 0x{Bin.U64(v, 56):x}, fs bayrakları 0x{Bin.U64(v, 264):x}";
        yield return $"  ağaçlar: omap {Bin.U64(v, 128)}, kök {Bin.U64(v, 136)} (tür 0x{Bin.U32(v, 116):x8}), extentref {Bin.U64(v, 144)} (tür 0x{Bin.U32(v, 120):x8}), " +
            $"snap_meta {Bin.U64(v, 152)} (tür 0x{Bin.U32(v, 124):x8})";
        yield return $"  sayaçlar: dosya {Bin.U64(v, 184)}, klasör {Bin.U64(v, 192)}, sembolik {Bin.U64(v, 200)}, diğer {Bin.U64(v, 208)}, anlık görüntü {Bin.U64(v, 216)}";
        yield return $"  bloklar: ayrılan {Bin.U64(v, 88)}, toplam ayrılmış {Bin.U64(v, 224)}, toplam bırakılmış {Bin.U64(v, 232)}; rezerv {Bin.U64(v, 72)}, kota {Bin.U64(v, 80)}";
        yield return $"  sonraki nesne kimliği {Bin.U64(v, 176)}, sonraki belge kimliği {Bin.U32(v, 960)}, son değişiklik {Bin.U64(v, 256)}, ayırma zamanı {Bin.U64(v, 64)}";
        yield return $"  meta_crypto: {Bin.U16(v, 96)}.{Bin.U16(v, 98)} bayrak 0x{Bin.U32(v, 100):x} sınıf {Bin.U32(v, 104)}";
        yield return $"  biçimlendiren: {ModifiedBy(v, 272)}";
        for (int i = 0; i < 8; i++)
        {
            int o = 320 + i * 48;
            if (v[o] == 0) continue;
            yield return $"  değiştiren[{i}]: {ModifiedBy(v, o)}";
        }
        yield return $"  geri dönüş xid {Bin.U64(v, 160)}, er_state {Bin.U64(v, 976)}, cloneinfo {Bin.U64(v, 984)}/{Bin.U64(v, 992)}, " +
            $"integrity {Bin.U64(v, 1024)}, fext {Bin.U64(v, 1032)}";

        // Birimin nesne haritası
        var om = c.Reader.ReadObject(Bin.U64(v, 128), ApfsConstants.TypeOmap);
        yield return OmapLine("  [birim omap]", Bin.U64(v, 128), om);
    }

    private static string ModifiedBy(byte[] v, int o) =>
        $"\"{Bin.Utf8Z(Bin.Slice(v, o, 32))}\" zaman {Bin.U64(v, o + 32)} son xid {Bin.U64(v, o + 40)}";
}
