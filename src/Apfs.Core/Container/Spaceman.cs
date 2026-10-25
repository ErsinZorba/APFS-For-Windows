using Apfs.Util;

namespace Apfs.Container;

/// <summary>chunk_info_t: ana aygıtta bir parça (chunk) ve doluluk bitmap'inin yeri. BitmapAddr 0 ise parçanın tamamı boştur.</summary>
public readonly record struct ChunkInfo(ulong Xid, ulong Addr, uint BlockCount, uint FreeCount, ulong BitmapAddr);

/// <summary>spaceman_device_t (ana aygıt veya Fusion'ın ikinci katmanı).</summary>
public readonly record struct SpacemanDevice(ulong BlockCount, ulong ChunkCount, uint CibCount, uint CabCount, ulong FreeCount, uint AddrOffset);

/// <summary>spaceman_free_queue_t: serbest bırakılmış ama henüz bitmap'e geri verilmemiş blokların kuyruğu.</summary>
public readonly record struct FreeQueueInfo(ulong Count, ulong TreeOid, ulong OldestXid, ushort NodeLimit);

/// <summary>Boş kuyruktaki bir kayıt: <paramref name="Xid"/> işleminde bırakılan <paramref name="Length"/> blok.</summary>
public readonly record struct FreeQueueEntry(ulong Xid, ulong Paddr, ulong Length);

/// <summary>
/// Alan yöneticisi (spaceman_phys_t), salt okunur. Geçici bir nesnedir: yeri checkpoint haritasındadır ve birden çok blok sürebilir.
/// Yapı: ana aygıtın blokları parçalara (chunk) bölünür; her parçanın bitmap bloğu iç havuzdadır (internal pool). Parça bilgileri
/// chunk-info bloklarında (CIB), çok büyük diskte CIB adresleri de CIB adres bloklarında (CAB) durur. Bırakılan bloklar önce
/// boş kuyruklara (ana aygıt ve iç havuz için ayrı B-ağaçları) girer; macOS onları sonra bitmap'e geri verir.
/// </summary>
public sealed class Spaceman
{
    public const int SfqIp = 0, SfqMain = 1, SfqTier2 = 2;

    private readonly ObjectReader _reader;
    private readonly Checkpoint _checkpoint;
    private IReadOnlyList<ChunkInfo>? _chunks;

    private Spaceman(ObjectReader reader, Checkpoint checkpoint, CheckpointMapping mapping, byte[] raw)
    {
        _reader = reader;
        _checkpoint = checkpoint;
        Mapping = mapping;
        Raw = raw;
        Xid = Bin.U64(raw, 16);
        BlockSize = Bin.U32(raw, 32);
        BlocksPerChunk = Bin.U32(raw, 36);
        ChunksPerCib = Bin.U32(raw, 40);
        CibsPerCab = Bin.U32(raw, 44);
        Main = ReadDevice(raw, 48);
        Tier2 = ReadDevice(raw, 96);
        Flags = Bin.U32(raw, 144);
        IpBmTxMultiplier = Bin.U32(raw, 148);
        IpBlockCount = Bin.U64(raw, 152);
        IpBmSizeInBlocks = Bin.U32(raw, 160);
        IpBmBlockCount = Bin.U32(raw, 164);
        IpBmBase = Bin.U64(raw, 168);
        IpBase = Bin.U64(raw, 176);
        FsReserveBlockCount = Bin.U64(raw, 184);
        FsReserveAllocCount = Bin.U64(raw, 192);
        FreeQueues = [ReadFreeQueue(raw, 200), ReadFreeQueue(raw, 240), ReadFreeQueue(raw, 280)];
        IpBmFreeHead = Bin.U16(raw, 320);
        IpBmFreeTail = Bin.U16(raw, 322);
        IpBmXidOffset = Bin.U32(raw, 324);
        IpBitmapOffset = Bin.U32(raw, 328);
        IpBmFreeNextOffset = Bin.U32(raw, 332);
        Version = Bin.U32(raw, 336);
        StructSize = Bin.U32(raw, 340);
    }

    public CheckpointMapping Mapping { get; }
    /// <summary>Nesnenin ham baytları (sağlama toplamı doğrulanmış).</summary>
    public byte[] Raw { get; }
    public ulong Xid { get; }
    public uint BlockSize { get; }
    public uint BlocksPerChunk { get; }
    public uint ChunksPerCib { get; }
    public uint CibsPerCab { get; }
    public SpacemanDevice Main { get; }
    public SpacemanDevice Tier2 { get; }
    public uint Flags { get; }
    public uint IpBmTxMultiplier { get; }
    public ulong IpBlockCount { get; }
    public uint IpBmSizeInBlocks { get; }
    public uint IpBmBlockCount { get; }
    public ulong IpBmBase { get; }
    public ulong IpBase { get; }
    public ulong FsReserveBlockCount { get; }
    public ulong FsReserveAllocCount { get; }
    public IReadOnlyList<FreeQueueInfo> FreeQueues { get; }
    public ushort IpBmFreeHead { get; }
    public ushort IpBmFreeTail { get; }
    public uint IpBmXidOffset { get; }
    public uint IpBitmapOffset { get; }
    public uint IpBmFreeNextOffset { get; }
    public uint Version { get; }
    public uint StructSize { get; }

    public static Spaceman Read(ObjectReader reader, NxSuperblock sb, Checkpoint checkpoint)
    {
        var m = checkpoint.Find(sb.SpacemanOid) ?? throw ApfsException.Corrupt(CoreText.T("Sm.NotInCheckpoint"));
        var raw = checkpoint.ReadEphemeral(m);
        if ((Bin.U32(raw, 24) & ApfsConstants.ObjectTypeMask) != ApfsConstants.TypeSpaceman)
            throw ApfsException.Corrupt(CoreText.T("Sm.WrongType"));
        var sm = new Spaceman(reader, checkpoint, m, raw);
        if (sm.BlockSize != reader.BlockSize || sm.BlocksPerChunk != reader.BlockSize * 8)
            throw ApfsException.Corrupt(CoreText.T("Sm.SizeInconsistent"));
        return sm;
    }

    public static Spaceman Read(ApfsContainer c, Checkpoint checkpoint) => Read(c.Reader, c.Superblock, checkpoint);

    private static SpacemanDevice ReadDevice(byte[] r, int o) =>
        new(Bin.U64(r, o), Bin.U64(r, o + 8), Bin.U32(r, o + 16), Bin.U32(r, o + 20), Bin.U64(r, o + 24), Bin.U32(r, o + 32));

    private static FreeQueueInfo ReadFreeQueue(byte[] r, int o) =>
        new(Bin.U64(r, o), Bin.U64(r, o + 8), Bin.U64(r, o + 16), Bin.U16(r, o + 24));

    /// <summary>Alan yöneticisi nesnesinin içindeki bir u64 dizisi (ofset nesne başından).</summary>
    public ulong ArrayU64(uint offset, uint index) => Bin.U64(Raw, checked((int)(offset + index * 8)));

    /// <summary>Alan yöneticisi nesnesinin içindeki bir u16 dizisi.</summary>
    public ushort ArrayU16(uint offset, uint index) => Bin.U16(Raw, checked((int)(offset + index * 2)));

    /// <summary>Ana aygıtın CIB adresleri (CAB varsa onların içinden), sırayla.</summary>
    public IReadOnlyList<ulong> MainCibAddresses()
    {
        var dev = Main;
        var list = new List<ulong>((int)Math.Min(dev.CibCount, 1_000_000));
        if (dev.CabCount == 0)
        {
            for (uint i = 0; i < dev.CibCount; i++) list.Add(ArrayU64(dev.AddrOffset, i));
            return list;
        }
        for (uint c = 0; c < dev.CabCount; c++)
        {
            ulong cabAddr = ArrayU64(dev.AddrOffset, c);
            var cab = _reader.ReadObject(cabAddr, ApfsConstants.TypeSpacemanCab);
            if (Bin.U32(cab, 32) != c) throw ApfsException.Corrupt(CoreText.T("Sm.CabWrongIndex", cabAddr));
            uint n = Bin.U32(cab, 36);
            if (40 + (long)n * 8 > cab.Length) throw ApfsException.Corrupt(CoreText.T("Sm.CabCountTooBig", cabAddr));
            for (uint i = 0; i < n; i++) list.Add(Bin.U64(cab, (int)(40 + i * 8)));
        }
        return list;
    }

    /// <summary>Ana aygıtın tüm parça bilgileri, adres sırasıyla.</summary>
    public IReadOnlyList<ChunkInfo> MainChunks() => _chunks ??= ReadChunks();

    private List<ChunkInfo> ReadChunks()
    {
        var list = new List<ChunkInfo>();
        var cibs = MainCibAddresses();
        for (int c = 0; c < cibs.Count; c++)
        {
            var cib = _reader.ReadObject(cibs[c], ApfsConstants.TypeSpacemanCib);
            if (Bin.U32(cib, 32) != (uint)c) throw ApfsException.Corrupt(CoreText.T("Sm.CibWrongIndex", cibs[c]));
            uint n = Bin.U32(cib, 36);
            if (40 + (long)n * 32 > cib.Length) throw ApfsException.Corrupt(CoreText.T("Sm.CibCountTooBig", cibs[c]));
            for (uint i = 0; i < n; i++)
            {
                int o = (int)(40 + i * 32);
                list.Add(new ChunkInfo(Bin.U64(cib, o), Bin.U64(cib, o + 8), Bin.U32(cib, o + 16), Bin.U32(cib, o + 20), Bin.U64(cib, o + 24)));
            }
        }
        return list;
    }

    /// <summary>Parçanın bitmap'i (1 bit = 1 blok, 1 = kullanımda). BitmapAddr 0 ise tamamı sıfır.</summary>
    public byte[] ReadChunkBitmap(ChunkInfo chunk) =>
        chunk.BitmapAddr == 0 ? new byte[_reader.BlockSize] : _reader.ReadBlock(chunk.BitmapAddr);

    /// <summary>Bitmap'te boş görünen blok sayısı (her parçanın blok sayısı kadar bit sayılır).</summary>
    public static uint CountFree(byte[] bitmap, uint blockCount)
    {
        uint used = 0;
        for (uint i = 0; i < blockCount; i++)
            if ((bitmap[i >> 3] & (1 << (int)(i & 7))) != 0) used++;
        return blockCount - used;
    }

    /// <summary>Bitmap'lerin tamamından sayılan boş blok sayısı.</summary>
    public ulong CountFreeFromBitmaps()
    {
        ulong free = 0;
        foreach (var ch in MainChunks()) free += CountFree(ReadChunkBitmap(ch), ch.BlockCount);
        return free;
    }

    /// <summary>İç havuzun bitmap'i (1 bit = iç havuzdaki 1 blok). sm_ip_bitmap_offset'teki indeksler bitmap halkasını gösterir.</summary>
    public byte[] ReadIpBitmap()
    {
        var bm = new byte[checked((int)(IpBmSizeInBlocks * BlockSize))];
        for (uint i = 0; i < IpBmSizeInBlocks; i++)
        {
            ushort idx = ArrayU16(IpBitmapOffset, i);
            if (idx >= IpBmBlockCount) throw ApfsException.Corrupt(CoreText.T("Sm.IpBitmapIndexOutside"));
            _reader.ReadBlock(IpBmBase + idx).CopyTo(bm, (int)(i * BlockSize));
        }
        return bm;
    }

    /// <summary>Boş kuyruğun kayıtları (sfqk_xid, sfqk_paddr) sırasıyla. Değeri olmayan kayıt tek bloktur.</summary>
    public IEnumerable<FreeQueueEntry> ReadFreeQueue(int which)
    {
        var q = FreeQueues[which];
        if (q.TreeOid == 0) yield break;
        var root = _checkpoint.Find(q.TreeOid) ?? throw ApfsException.Corrupt(CoreText.T("Sm.FreeQueueTreeNotMapped", q.TreeOid));
        var tree = new BTree.BTree(_reader, root.Paddr, ApfsConstants.TypeSpacemanFreeQueue,
            oid => (_checkpoint.Find(oid)?.Paddr ?? throw ApfsException.Corrupt(CoreText.T("Sm.FreeQueueNodeNotMapped", oid)), false));
        foreach (var e in tree.Scan(_ => 0, includeGhosts: true))
        {
            if (e.Key.Length < 16) throw ApfsException.Corrupt(CoreText.T("Sm.FreeQueueKeyTooShort"));
            ulong len = e.Value.Length >= 8 ? Bin.U64(e.Value, 0) : 1;
            yield return new FreeQueueEntry(Bin.U64(e.Key, 0), Bin.U64(e.Key, 8), len);
        }
    }

    /// <summary>Bir blok ana aygıt bitmap'inde kullanımda mı?</summary>
    public bool IsAllocated(ulong paddr)
    {
        ulong chunkIndex = paddr / BlocksPerChunk;
        var chunks = MainChunks();
        if (chunkIndex >= (ulong)chunks.Count) throw ApfsException.Corrupt(CoreText.T("Sm.BlockOutOfRange", paddr));
        var ch = chunks[(int)chunkIndex];
        if (ch.BitmapAddr == 0) return false;
        ulong bit = paddr - ch.Addr;
        return (ReadChunkBitmap(ch)[bit >> 3] & (1 << (int)(bit & 7))) != 0;
    }
}
