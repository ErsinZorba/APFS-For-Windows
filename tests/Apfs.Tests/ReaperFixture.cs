using System.Buffers.Binary;
using Apfs.Container;
using Apfs.Util;

namespace Apfs.Tests;

/// <summary>
/// macOS'un bir birimi sildikten sonra kapsayıcıda bıraktığı yapıyı (boş reap list) çıplak bir kapsayıcı görüntüsüne ekler.
/// Gerçek örnek (2026-10-25, kullanıcının HDD'si, A-Duz ve B-Sifreli): reaper sonraki 4 / tamamlanan 3, baş = kuyruk = liste
/// nesnesi, liste sayısı 1, tür 0xd, nesne 0; liste tür 0x80000012/0, next 0, flags 0, max 100, count 0, first/last 0xFFFFFFFF,
/// free 1 ve gövdede eski girdiler. Son checkpoint yerinde değiştirilir: liste veri halkasında data_next'e konur, eşlemesi son
/// eşleme bloğuna eklenir, next_oid ilerler, blok 0 kopyası yenilenir. Çıktı apfsck'den temiz geçer (adım 1 doğrulaması).
/// Yazıcıdan BAĞIMSIZ: Core'un yazma kodunu kullanmaz.
/// </summary>
internal static class ReaperFixture
{
    public const uint FlagBhm = 0x1, FlagContinue = 0x2;

    /// <summary>
    /// <paramref name="changeReaper"/> / <paramref name="changeList"/>: mühürlemeden önce nesneleri bozmak için (ret testleri).
    /// Dönen değer listenin oid'i.
    /// </summary>
    public static ulong InjectIdleReapList(string path, Action<byte[]>? changeReaper = null, Action<byte[]>? changeList = null) =>
        Inject(path, ApfsConstants.TypeNxReapList, linkToReaper: true, changeReaper, changeList);

    /// <summary>Reaper'a bağlı olmayan, verilen türde bir geçici nesne ekler (yazıcının tanımadığı tür denemesi).</summary>
    public static ulong InjectOrphanEphemeral(string path, uint type) => Inject(path, type, linkToReaper: false, null, null);

    private static ulong Inject(string path, uint type, bool linkToReaper, Action<byte[]>? changeReaper, Action<byte[]>? changeList)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        var nx = Read(fs, 0);
        int bs = (int)U32(nx, 36);
        if (bs != 4096) throw new InvalidOperationException("yalnız 4096 baytlık blok");
        ulong xid = U64(nx, 16), nextOid = U64(nx, 88);
        uint descBlocks = U32(nx, 104), dataBlocks = U32(nx, 108);
        ulong descBase = U64(nx, 112), dataBase = U64(nx, 120);
        uint descIndex = U32(nx, 136), descLen = U32(nx, 140), dataLen = U32(nx, 148), dataNext = U32(nx, 132);
        ulong reaperOid = U64(nx, 168);

        // Son checkpoint'in süper bloğu tanımlayıcı aralığının sonunda; blok 0 onun kopyası olmalı
        ulong sbAt = descBase + (descIndex + descLen - 1) % descBlocks;
        var sb = Read(fs, sbAt);
        if (U64(sb, 16) != xid) throw new InvalidOperationException("blok 0 son checkpoint'in kopyası değil");
        if (dataLen + 1 > dataBlocks) throw new InvalidOperationException("veri halkasında yer yok");

        // Son eşleme bloğu (LAST) ve reaper'ın yeri
        ulong mapAt = descBase + (descIndex + descLen - 2) % descBlocks;
        var map = Read(fs, mapAt);
        uint count = U32(map, 36);
        if ((U32(map, 32) & 1) == 0 || 40 + (count + 1) * 40 > bs) throw new InvalidOperationException("eşleme bloğunda yer yok");
        ulong reaperAt = 0;
        for (uint k = 0; k < count; k++)
            if (U64(map, 40 + (int)k * 40 + 24) == reaperOid) reaperAt = U64(map, 40 + (int)k * 40 + 32);
        if (reaperAt == 0) throw new InvalidOperationException("reaper son eşleme bloğunda değil");

        ulong listOid = nextOid;
        ulong listAt = dataBase + dataNext;

        // Liste: başlık + iki eski girdi (macOS gövdeyi sıfırlamıyor; free = 1 eski girdilerin boş zinciri)
        var list = new byte[bs];
        Head(list, listOid, xid, ApfsConstants.ObjEphemeral | type);
        W32(list, 44, (uint)((bs - 64) / 40));          // nrl_max
        W32(list, 52, 0xFFFFFFFF);                       // nrl_first
        W32(list, 56, 0xFFFFFFFF);                       // nrl_last
        W32(list, 60, 1);                                // nrl_free
        W32(list, 64, 2); W32(list, 72, 0x40000002); W32(list, 76, 0x1000); W64(list, 80, 0x425); W64(list, 88, 0x4868b); W64(list, 96, 0xda);
        W32(list, 112, 0x0d); W32(list, 116, 0x1000);
        changeList?.Invoke(list);
        Seal(list);

        // Reaper: silme bitmiş, liste baş ve kuyrukta
        var reaper = Read(fs, reaperAt);
        if (linkToReaper)
        {
            W64(reaper, 32, 4); W64(reaper, 40, 3);      // nr_next_reap_id, nr_completed_id
            W64(reaper, 48, listOid); W64(reaper, 56, listOid);
            W32(reaper, 64, U32(reaper, 64) | FlagBhm);
            W32(reaper, 68, 1); W32(reaper, 72, 0x0d);  // nr_rlcount, nr_type
            W64(reaper, 96, 3);                          // nr_xid
        }
        changeReaper?.Invoke(reaper);
        Seal(reaper);

        // Eşleme: (tür, alt tür, boyut, dolgu, fs_oid, oid, paddr)
        int e = 40 + (int)count * 40;
        W32(map, e, ApfsConstants.ObjEphemeral | type);
        W32(map, e + 8, (uint)bs);
        W64(map, e + 24, listOid);
        W64(map, e + 32, listAt);
        W32(map, 36, count + 1);
        Seal(map);

        W64(sb, 88, nextOid + 1);
        W32(sb, 132, (dataNext + 1) % dataBlocks);
        W32(sb, 148, dataLen + 1);
        Seal(sb);

        Write(fs, listAt, list);
        Write(fs, reaperAt, reaper);
        Write(fs, mapAt, map);
        Write(fs, sbAt, sb);
        Write(fs, 0, sb);
        return listOid;
    }

    private static void Head(byte[] b, ulong oid, ulong xid, uint type)
    {
        W64(b, 8, oid); W64(b, 16, xid); W32(b, 24, type); W32(b, 28, 0);
    }

    private static void Seal(byte[] b) => BinaryPrimitives.WriteUInt64LittleEndian(b, Fletcher64.Compute(b));
    private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
    private static ulong U64(byte[] b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(o));
    internal static void W32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    internal static void W64(byte[] b, int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o), v);

    private static byte[] Read(FileStream fs, ulong block)
    {
        var b = new byte[4096];
        fs.Position = (long)block * 4096;
        fs.ReadExactly(b);
        return b;
    }

    private static void Write(FileStream fs, ulong block, byte[] b)
    {
        fs.Position = (long)block * 4096;
        fs.Write(b);
    }
}
