using System.Buffers.Binary;
using Apfs.Container;
using Apfs.Util;

namespace Apfs.Write;

/// <summary>
/// Alan yöneticisinin yazma tarafı (bir yazma oturumu boyunca bellekte tutulur). Kurallar (apfsck kurallarından ve
/// mkapfs/linux-apfs-rw görüntülerinden doğrulandı):
/// <list type="bullet">
/// <item>Bir işlemde (xid T) bırakılan blok T boyunca KARANTİNADADIR: bitmap'te dolu kalır, hiçbir ayırmaya verilmez; T'nin
/// commit'inde bitmap'te boş işaretlenir (boş kuyruğa girmez). Böylece T'den önceki (kalıcı) checkpoint'in gösterdiği bloklar T içinde
/// asla yeniden kullanılmaz; T kalıcı olduktan sonra kullanılabilir. (Boş kuyruk + sonraki işlemde boşaltma ile aynı güvenlik; kuyruk
/// düğüm sınırı sorunu yoktur.) macOS'tan kalan boş kuyruk kayıtları oturumun ilk işleminde bitmap'e geri verilir.
/// Aynı işlemde ayrılıp bırakılan blok hiç kalıcı olmadığından hemen boşa çıkar.</item>
/// <item>Parça bitmap'leri, CIB'ler ve CAB'lar yerinde değiştirilmez: commit'te iç havuzdan yeni bir kopyaya yazılır, eskisi iç
/// havuz boş kuyruğuna girer. İç havuzun kendi bitmap'i 16 kopyalık halkada bir sonraki boş yuvaya yazılır.</item>
/// </list>
/// </summary>
internal sealed class SpacemanWriter
{
    private readonly ObjectReader _reader;
    private readonly uint _bs;
    private readonly byte[] _raw;              // alan yöneticisi nesnesi (değiştirilecek kopya)
    private readonly ulong _blockCount;
    private readonly uint _blocksPerChunk;
    private readonly uint _chunksPerCib;

    private readonly List<ChunkInfo> _chunks;
    private readonly Dictionary<int, byte[]> _bitmaps = [];       // parça indeksi → bitmap (yüklenmiş)
    private readonly HashSet<int> _dirtyChunks = [];
    private readonly List<ulong> _cibAddrs;
    private readonly byte[] _ipBitmap;
    private readonly ulong _ipBase, _ipBlockCount;

    // Bu işlemde ayrılanlar (bırakılırsa hemen boşa çıkar) ve bırakılanlar (commit'te boş kuyruğa girer)
    private readonly HashSet<ulong> _allocatedThisTx = [];
    private readonly HashSet<ulong> _quarantineMain = [];   // bu işlemde bırakılan kalıcı bloklar (commit'te boşa çıkar)
    private readonly HashSet<ulong> _quarantineIp = [];
    private readonly HashSet<ulong> _allocatedIpThisTx = [];

    private readonly MutableBTree _fqIp, _fqMain;
    private int _searchHint;
    private bool _ipDirty; // iç havuz bitmap'i diskteki son kopyadan farklı (halkada yeni yuvaya yazılmalı)

    public SpacemanWriter(ObjectReader reader, NxSuperblock sb, Checkpoint cp, Func<ulong> newEphemeralOid)
    {
        _reader = reader;
        _bs = reader.BlockSize;
        var sm = Spaceman.Read(reader, sb, cp);
        if (sm.Tier2.BlockCount != 0) throw new ApfsException(ApfsErrorCode.Unsupported, "Fusion alan yöneticisi");
        if (sm.IpBmSizeInBlocks != 1) throw new ApfsException(ApfsErrorCode.Unsupported, "çok bloklu iç havuz bitmap'i");
        if (sm.Main.CabCount != 0) throw new ApfsException(ApfsErrorCode.Unsupported, "CIB adres blokları (çok büyük disk) henüz desteklenmiyor");
        Mapping = sm.Mapping;
        _raw = (byte[])sm.Raw.Clone();
        _blockCount = sm.Main.BlockCount;
        _blocksPerChunk = sm.BlocksPerChunk;
        _chunksPerCib = sm.ChunksPerCib;
        _chunks = [.. sm.MainChunks()];
        _cibAddrs = [.. sm.MainCibAddresses()];
        _ipBitmap = sm.ReadIpBitmap();
        _ipBase = sm.IpBase;
        _ipBlockCount = sm.IpBlockCount;

        var shape = new TreeShape(ApfsConstants.TypeSpacemanFreeQueue, ApfsConstants.ObjEphemeral, 16, 8, KeyOrder.FreeQueue, (int)_bs);
        Func<ulong, bool, byte[]> load = (oid, _) => cp.ReadEphemeral(oid);
        _fqIp = new MutableBTree(shape, sm.FreeQueues[Spaceman.SfqIp].TreeOid, load, newEphemeralOid);
        _fqMain = new MutableBTree(shape, sm.FreeQueues[Spaceman.SfqMain].TreeOid, load, newEphemeralOid);
    }

    public CheckpointMapping Mapping { get; }
    public MutableBTree FreeQueueIp => _fqIp;
    public MutableBTree FreeQueueMain => _fqMain;

    /// <summary>Ana aygıttaki boş blok sayısı (boş kuyruktakiler hariç: onlar henüz kullanılamaz).</summary>
    public ulong FreeBlocks => Bin.U64(_raw, 72);

    // ---------------------------------------------------------------------------------------------------------------
    // İşlem başı: önceki işlemlerin bıraktığı blokları bitmap'e geri ver

    /// <summary>
    /// Yeni işlemin (xid) başında: boş kuyruklardaki bütün kayıtlar (hepsi daha eski işlemlerden) bitmap'lere geri verilir ve
    /// kuyruklar boşaltılır.
    /// </summary>
    public void BeginTransaction(ulong xid)
    {
        foreach (var e in _fqMain.Scan(_ => 0, includeGhosts: true).ToList())
        {
            var (fx, paddr, len) = Entry(e);
            if (fx >= xid) throw ApfsException.Corrupt("boş kuyrukta gelecekteki bir işlemin kaydı");
            for (ulong b = paddr; b < paddr + len; b++) SetMain(b, used: false);
            _fqMain.Delete(e.Key);
        }
        foreach (var e in _fqIp.Scan(_ => 0, includeGhosts: true).ToList())
        {
            var (fx, paddr, len) = Entry(e);
            if (fx >= xid) throw ApfsException.Corrupt("iç havuz boş kuyruğunda gelecekteki bir işlemin kaydı");
            for (ulong b = paddr; b < paddr + len; b++) SetIp(b, used: false);
            _fqIp.Delete(e.Key);
        }
    }

    private static (ulong Xid, ulong Paddr, ulong Len) Entry(Apfs.BTree.BTreeEntry e) =>
        (Bin.U64(e.Key, 0), Bin.U64(e.Key, 8), e.Value.Length >= 8 ? Bin.U64(e.Value, 0) : 1);

    // ---------------------------------------------------------------------------------------------------------------
    // Ana aygıt

    private byte[] Bitmap(int chunk)
    {
        if (_bitmaps.TryGetValue(chunk, out var bm)) return bm;
        var ch = _chunks[chunk];
        bm = ch.BitmapAddr == 0 ? new byte[_bs] : _reader.ReadBlock(ch.BitmapAddr);
        _bitmaps[chunk] = bm;
        return bm;
    }

    public bool IsAllocated(ulong paddr)
    {
        int c = (int)(paddr / _blocksPerChunk);
        ulong bit = paddr - _chunks[c].Addr;
        return (Bitmap(c)[bit >> 3] & (1 << (int)(bit & 7))) != 0;
    }

    private void SetMain(ulong paddr, bool used)
    {
        if (paddr >= _blockCount) throw ApfsException.Corrupt($"blok {paddr} kapsayıcının dışında");
        int c = (int)(paddr / _blocksPerChunk);
        var bm = Bitmap(c);
        ulong bit = paddr - _chunks[c].Addr;
        byte mask = (byte)(1 << (int)(bit & 7));
        bool was = (bm[bit >> 3] & mask) != 0;
        if (was == used) throw ApfsException.Corrupt($"blok {paddr} zaten {(used ? "dolu" : "boş")} (alan yöneticisi tutarsız)");
        bm[bit >> 3] = (byte)(used ? bm[bit >> 3] | mask : bm[bit >> 3] & ~mask);
        var ch = _chunks[c];
        _chunks[c] = ch with { FreeCount = used ? ch.FreeCount - 1 : ch.FreeCount + 1 };
        _dirtyChunks.Add(c);
        long dev = (long)Bin.U64(_raw, 72) + (used ? -1 : 1);
        BinaryPrimitives.WriteUInt64LittleEndian(_raw.AsSpan(72), (ulong)dev);
    }

    /// <summary>
    /// En fazla <paramref name="maxBlocks"/> ardışık boş blok ayırır (en az 1); bulunamazsa ApfsException (disk dolu).
    /// <paramref name="reserve"/> kadar blok her zaman boş bırakılır.
    /// </summary>
    public (ulong Start, ulong Count) Allocate(ulong maxBlocks, ulong reserve = 0)
    {
        if (maxBlocks == 0) throw new ArgumentOutOfRangeException(nameof(maxBlocks));
        if (FreeBlocks <= reserve) throw new ApfsException(ApfsErrorCode.DiskFull);
        maxBlocks = Math.Min(maxBlocks, FreeBlocks - reserve);
        for (int pass = 0; pass < _chunks.Count; pass++)
        {
            int c = (_searchHint + pass) % _chunks.Count;
            var ch = _chunks[c];
            if (ch.FreeCount == 0) continue;
            var bm = Bitmap(c);
            for (uint i = 0; i < ch.BlockCount; i++)
            {
                if ((bm[i >> 3] & (1 << (int)(i & 7))) != 0) continue;
                ulong start = ch.Addr + i;
                ulong n = 0;
                while (n < maxBlocks && i + n < ch.BlockCount && (bm[(i + n) >> 3] & (1 << (int)((i + n) & 7))) == 0) n++;
                for (ulong b = start; b < start + n; b++)
                {
                    SetMain(b, used: true);
                    _allocatedThisTx.Add(b);
                }
                _searchHint = c;
                return (start, n);
            }
        }
        throw new ApfsException(ApfsErrorCode.DiskFull);
    }

    /// <summary>Blok bu işlemde mi ayrıldı (henüz hiçbir kalıcı checkpoint göstermiyor; yerinde değiştirilebilir).</summary>
    public bool IsAllocatedThisTx(ulong paddr) => _allocatedThisTx.Contains(paddr);

    /// <summary>Tek blok ayırır (metadata düğümleri için).</summary>
    public ulong AllocateBlock() => Allocate(1).Start;

    /// <summary>
    /// Blokları bırakır. Bu işlemde ayrılmışsa hemen boşa çıkar; değilse boş kuyruğa girer (bitmap'te dolu kalır).
    /// </summary>
    public void Free(ulong paddr, ulong count = 1)
    {
        for (ulong b = paddr; b < paddr + count; b++)
        {
            if (!IsAllocated(b)) throw ApfsException.Corrupt($"boş blok {b} bırakılmak istendi");
            if (_allocatedThisTx.Remove(b)) SetMain(b, used: false);
            else if (!_quarantineMain.Add(b)) throw ApfsException.Corrupt($"blok {b} aynı işlemde iki kez bırakıldı");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // İç havuz

    private void SetIp(ulong paddr, bool used)
    {
        if (paddr < _ipBase || paddr >= _ipBase + _ipBlockCount) throw ApfsException.Corrupt($"blok {paddr} iç havuzun dışında");
        ulong bit = paddr - _ipBase;
        byte mask = (byte)(1 << (int)(bit & 7));
        bool was = (_ipBitmap[bit >> 3] & mask) != 0;
        if (was == used) throw ApfsException.Corrupt($"iç havuz bloğu {paddr} zaten {(used ? "dolu" : "boş")}");
        _ipBitmap[bit >> 3] = (byte)(used ? _ipBitmap[bit >> 3] | mask : _ipBitmap[bit >> 3] & ~mask);
        _ipDirty = true;
    }

    private ulong AllocateIp()
    {
        for (ulong i = 0; i < _ipBlockCount; i++)
            if ((_ipBitmap[i >> 3] & (1 << (int)(i & 7))) == 0)
            {
                SetIp(_ipBase + i, used: true);
                _allocatedIpThisTx.Add(_ipBase + i);
                return _ipBase + i;
            }
        throw ApfsException.Corrupt("iç havuzda yer kalmadı (alan yöneticisi tutarsız)");
    }

    private void FreeIp(ulong paddr)
    {
        if (_allocatedIpThisTx.Remove(paddr)) SetIp(paddr, used: false);
        else if (!_quarantineIp.Add(paddr)) throw ApfsException.Corrupt($"iç havuz bloğu {paddr} aynı işlemde iki kez bırakıldı");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Commit

    /// <summary>
    /// İşlemi kapatır: değişen parçaların bitmap'leri ve CIB'leri iç havuzda yeni kopyalara, iç havuz bitmap'i halkadaki yeni
    /// yuvaya yazılır; bırakılan bloklar boş kuyruklara eklenir; alan yöneticisi nesnesi (geçici, checkpoint'te yazılacak)
    /// güncellenir. Döndürülen (adres, blok) çiftleri birinci bariyerden önce yazılmalıdır.
    /// </summary>
    public List<(ulong Paddr, byte[] Block)> PrepareCommit(ulong xid)
    {
        var writes = new List<(ulong, byte[])>();
        var dirtyCibs = new SortedSet<int>();

        // 0) Karantinadaki ana aygıt blokları boşa çıkar (bu checkpoint'te boş; ancak sonraki işlemde kullanılabilir)
        foreach (ulong b in _quarantineMain) SetMain(b, used: false);
        _quarantineMain.Clear();

        // 1) Parça bitmap'leri: yeni iç havuz bloğuna
        foreach (int c in _dirtyChunks)
        {
            var ch = _chunks[c];
            ulong newBm = AllocateIp();
            if (ch.BitmapAddr != 0) FreeIp(ch.BitmapAddr);
            writes.Add((newBm, (byte[])_bitmaps[c].Clone()));
            _chunks[c] = ch with { Xid = xid, BitmapAddr = newBm };
            dirtyCibs.Add((int)(c / _chunksPerCib));
        }
        _dirtyChunks.Clear();

        // 2) CIB'ler: yeni iç havuz bloğuna
        uint addrOffset = Bin.U32(_raw, 48 + 32);
        foreach (int ci in dirtyCibs)
        {
            ulong newCib = AllocateIp();
            FreeIp(_cibAddrs[ci]);
            _cibAddrs[ci] = newCib;
            writes.Add((newCib, BuildCib(ci, newCib, xid)));
            BinaryPrimitives.WriteUInt64LittleEndian(_raw.AsSpan((int)(addrOffset + ci * 8)), newCib);
        }

        // 3) İç havuz karantinası (eski bitmap/CIB kopyaları) boşa çıkar; ayırmalardan SONRA (aynı commit'te yeniden verilmesin)
        foreach (ulong b in _quarantineIp) SetIp(b, used: false);
        _quarantineIp.Clear();
        // Boş kuyruklar her zaman boş (ilk işlemde boşaltıldı): sayaç 0, en eski xid 0
        WriteFreeQueueInfo(Spaceman.SfqMain, CountQueue(_fqMain), xid);
        WriteFreeQueueInfo(Spaceman.SfqIp, CountQueue(_fqIp), xid);

        // 4) İç havuz bitmap'i: halkadaki boş listenin başından yeni yuva; eskisi listenin sonuna
        if (_ipDirty) { writes.Add(RotateIpBitmap(xid)); _ipDirty = false; }

        _allocatedThisTx.Clear();
        _allocatedIpThisTx.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(_raw.AsSpan(16), xid);
        return writes;
    }

    private static ulong CountQueue(MutableBTree tree)
    {
        ulong total = 0;
        foreach (var e in tree.Scan(_ => 0, includeGhosts: true)) total += Entry(e).Len;
        return total;
    }

    private void WriteFreeQueueInfo(int which, ulong count, ulong xid)
    {
        int o = 200 + which * 40;
        BinaryPrimitives.WriteUInt64LittleEndian(_raw.AsSpan(o), count);
        BinaryPrimitives.WriteUInt64LittleEndian(_raw.AsSpan(o + 16), count == 0 ? 0 : xid); // kuyrukta yalnızca bu işlemin kayıtları kalır
        ushort limit = Bin.U16(_raw, o + 24);
        var tree = which == Spaceman.SfqMain ? _fqMain : _fqIp;
        if (tree.NodeCount > limit)
            throw new ApfsException(ApfsErrorCode.Unsupported, "tek işlemde çok fazla blok bırakıldı (boş kuyruk sınırı)");
    }

    private byte[] BuildCib(int cibIndex, ulong paddr, ulong xid)
    {
        int first = (int)(cibIndex * _chunksPerCib);
        int count = Math.Min((int)_chunksPerCib, _chunks.Count - first);
        var b = new byte[_bs];
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), paddr);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), xid);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24), ApfsConstants.ObjPhysical | ApfsConstants.TypeSpacemanCib);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(32), (uint)cibIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(36), (uint)count);
        for (int i = 0; i < count; i++)
        {
            var ch = _chunks[first + i];
            int o = 40 + i * 32;
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o), ch.Xid);
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o + 8), ch.Addr);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 16), ch.BlockCount);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 20), ch.FreeCount);
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o + 24), ch.BitmapAddr);
        }
        BinaryPrimitives.WriteUInt64LittleEndian(b, Fletcher64.Compute(b));
        return b;
    }

    private (ulong, byte[]) RotateIpBitmap(ulong xid)
    {
        uint ringLen = Bin.U32(_raw, 164);
        ulong ringBase = Bin.U64(_raw, 168);
        uint bitmapOff = Bin.U32(_raw, 328), xidOff = Bin.U32(_raw, 324), nextOff = Bin.U32(_raw, 332);
        ushort head = Bin.U16(_raw, 320), tail = Bin.U16(_raw, 322);
        ushort old = Bin.U16(_raw, (int)bitmapOff);
        ushort slot = head;
        ushort NextOf(ushort i) => Bin.U16(_raw, (int)(nextOff + i * 2));
        void SetNext(ushort i, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(_raw.AsSpan((int)(nextOff + i * 2)), v);
        if (slot >= ringLen || slot == tail) throw ApfsException.Corrupt("iç havuz bitmap halkasında boş yuva yok");

        head = NextOf(slot);          // yeni yuva listeden çıkar
        SetNext(slot, 0xFFFF);
        SetNext(tail, old);           // eski yuva listenin sonuna
        SetNext(old, 0xFFFF);
        tail = old;
        BinaryPrimitives.WriteUInt16LittleEndian(_raw.AsSpan(320), head);
        BinaryPrimitives.WriteUInt16LittleEndian(_raw.AsSpan(322), tail);
        BinaryPrimitives.WriteUInt16LittleEndian(_raw.AsSpan((int)bitmapOff), slot);
        BinaryPrimitives.WriteUInt64LittleEndian(_raw.AsSpan((int)xidOff), xid);
        return (ringBase + slot, (byte[])_ipBitmap.Clone());
    }

    /// <summary>Alan yöneticisi nesnesinin (geçici) son hâli; checkpoint yazıcısı sağlama toplamını hesaplar.</summary>
    public byte[] RawForCheckpoint() => _raw;
}
