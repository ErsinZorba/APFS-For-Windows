using System.Buffers.Binary;
using Apfs.Container;
using Apfs.IO;
using Apfs.Util;

namespace Apfs.Write;

/// <summary>
/// Bir kapsayıcı üzerinde yazma oturumu. Yalnızca yazma kapısının verdiği izinle açılır; disk izin verildiğinden beri değişmişse
/// (başka bir xid) açılmaz. Değişiklikler işlemler (transaction) hâlinde biriktirilir ve <see cref="Commit"/> ile kalıcı olur.
/// Commit sırası:
/// <list type="number">
/// <item>yeni fiziksel nesneler (ağaç düğümleri, iç havuz bitmap/CIB kopyaları) + geçici nesneler (veri halkasında, şu anki
/// checkpoint'in nesnelerinin ARDINA) + checkpoint haritası → <b>bariyer</b>;</item>
/// <item>yeni kapsayıcı süper bloğu (tanımlayıcı halkasında) → <b>bariyer</b>;</item>
/// <item>blok 0 kopyası yalnızca oturum kapanırken (<see cref="Close"/>) → <b>bariyer</b>.</item>
/// </list>
/// Hiçbir adımda şu anki (kalıcı) checkpoint'in gösterdiği bir blok değiştirilmez; yazma yarıda kesilirse disk eski checkpoint ile açılır.
/// </summary>
public sealed class WriteSession : IDisposable
{
    private readonly IWritableBlockDevice _device;
    private readonly ObjectReader _reader;
    private byte[] _nx;                   // son kalıcı kapsayıcı süper bloğu (ham)
    private NxSuperblock _sb;
    private Checkpoint _cp;
    private ulong _nextOid;
    private readonly SpacemanWriter _spaceman;
    private readonly byte[] _reaper;
    private readonly List<(CheckpointMapping Map, byte[] Raw)> _reapLists; // macOS'un silme sonrası bıraktığı boş liste; olduğu gibi taşınır
    private ulong _txXid;                 // açık işlemin xid'i
    private readonly List<(ulong Paddr, byte[] Block)> _pendingWrites = [];
    private bool _closed;

    // Kapsayıcı nesne haritası (birim süper bloklarının yerleri)
    private readonly MutableBTree _cOmap;
    private byte[] _cOmapObj;
    private ulong _cOmapObjPaddr;
    private readonly Dictionary<ulong, VolumeWriter> _volumes = [];

    private WriteSession(IWritableBlockDevice device, ApfsContainer container, byte[] nx, NxSuperblock sb, Checkpoint cp)
    {
        var reader = container.Reader;
        Container = container;
        _device = device;
        _reader = reader;
        _nx = nx;
        _sb = sb;
        _cp = cp;
        _nextOid = sb.NextOid;
        _spaceman = new SpacemanWriter(reader, sb, cp, () => _nextOid++);
        // Commit yalnız tanıdığı geçici nesneleri yeniden yazar; başka bir şey varsa (ya da silme sürüyorsa) hiçbir şey yazmadan dur
        var reaper = ReaperState.Read(cp, sb);
        if (reaper.Problem is { } busy) throw new ApfsException(ApfsErrorCode.Unsupported, busy);
        if (reaper.NotRewrittenByWriter(cp, sb).ToList() is [var unknown, ..])
            throw new ApfsException(ApfsErrorCode.Unsupported, CoreText.T("Write.UnknownEphemeral", unknown.Oid, unknown.Type, unknown.Subtype));
        _reaper = (byte[])reaper.Raw.Clone();
        _reapLists = reaper.Lists.Select(l => (l.Map, (byte[])l.Raw.Clone())).ToList();
        _cOmapObjPaddr = sb.OmapOid;
        _cOmapObj = (byte[])LoadPhysical(sb.OmapOid, ApfsConstants.TypeOmap).Clone();
        _cOmap = new MutableBTree(new TreeShape(ApfsConstants.TypeOmap, ApfsConstants.ObjPhysical, 16, 16, KeyOrder.Omap, (int)reader.BlockSize),
            Bin.U64(_cOmapObj, 48), (paddr, _) => LoadPhysical(paddr, 0), null);
        BeginTransaction();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Nesne okuma/mühürleme (önbelleksiz: yazma oturumunda bloklar yeniden kullanılır, ObjectReader önbelleğine güvenilmez)

    /// <summary>Fiziksel nesneyi okur ve doğrular: sağlama toplamı, kimlik = adres, (verildiyse) tür.</summary>
    internal byte[] LoadPhysical(ulong paddr, uint type)
    {
        var b = _reader.ReadBlock(paddr);
        if (!Fletcher64.Verify(b)) throw new ApfsException(ApfsErrorCode.ChecksumMismatch, CoreText.T("Write.Block", paddr));
        if (Bin.U64(b, 8) != paddr) throw ApfsException.Corrupt(CoreText.T("Write.PhysOidNotAddress", paddr));
        if (type != 0 && (Bin.U32(b, 24) & ApfsConstants.ObjectTypeMask) != type) throw ApfsException.Corrupt(CoreText.T("Write.WrongObjectType", paddr));
        return b;
    }

    /// <summary>
    /// Sanal nesneyi (nesne haritasının gösterdiği bloktan) okur ve doğrular. Şifreli düğüm (<paramref name="crypto"/> verilmiş)
    /// ÖNCE çözülür, SONRA doğrulanır: sağlama toplamı düz metin üzerindendir.
    /// </summary>
    internal byte[] LoadVirtual(ulong oid, ulong paddr, Crypto.VolumeCrypto? crypto = null)
    {
        var b = _reader.ReadBlock(paddr);
        crypto?.DecryptBlock(b, paddr);
        if (!Fletcher64.Verify(b)) throw new ApfsException(ApfsErrorCode.ChecksumMismatch, CoreText.T("Write.Block", paddr));
        if (Bin.U64(b, 8) != oid) throw ApfsException.Corrupt(CoreText.T("Write.OidMismatch", paddr, oid));
        return b;
    }

    /// <summary>Fiziksel nesnenin kopyasına kimlik (= adres), xid ve sağlama toplamı yazar.</summary>
    internal byte[] SealPhysical(byte[] obj, ulong paddr, ulong xid) => Seal(obj, paddr, xid);

    internal byte[] SealVirtual(byte[] obj, ulong oid, ulong xid, int _) => Seal(obj, oid, xid);

    private static byte[] Seal(byte[] obj, ulong oid, ulong xid)
    {
        var b = (byte[])obj.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), oid);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), xid);
        BinaryPrimitives.WriteUInt64LittleEndian(b, Fletcher64.Compute(b));
        return b;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Birimler

    private ulong ResolveContainerOmap(ulong oid)
    {
        ulong? found = null;
        foreach (var e in _cOmap.Scan(k => Bin.U64(k, 0).CompareTo(oid)))
        {
            if (found is not null) throw ApfsException.Corrupt(CoreText.T("Write.MultipleOmapRecords", oid));
            found = Bin.U64(e.Value, 8);
        }
        return found ?? throw ApfsException.Corrupt(CoreText.T("Write.NotInContainerOmap", oid));
    }

    /// <summary>
    /// Kapsayıcıdaki <paramref name="index"/>. birimin yazıcısı. Şifreli birimde kilidi açılmış <paramref name="key"/> şarttır
    /// (yazıcı kendi kopyasını alır); şifresiz birimde yok sayılır.
    /// </summary>
    public VolumeWriter Volume(int index, Crypto.VolumeKey? key = null)
    {
        ulong oid = _sb.FsOids[index];
        if (_volumes.TryGetValue(oid, out var vw)) return vw;
        ulong paddr = ResolveContainerOmap(oid);
        var apsb = LoadVirtual(oid, paddr);
        if (Bin.U32(apsb, 32) != ApfsConstants.ApfsMagic) throw ApfsException.Corrupt(CoreText.T("Write.NoApsbMagic"));
        vw = new VolumeWriter(this, oid, paddr, apsb, key);
        if (vw.Crypto is not null) _encryptedVolumes++;
        // Görüntü HEMEN kurulur: sonraki commit'ler kapsayıcının eski omap bloklarını yeniden kullanabilir
        if (_sb.Xid == Container.Xid) vw.View(Container);
        return _volumes[oid] = vw;
    }

    private void RelocateVolume(ulong oid, ulong paddr, ulong xid)
    {
        foreach (var e in _cOmap.Scan(k => Bin.U64(k, 0).CompareTo(oid)).ToList()) _cOmap.Delete(e.Key);
        var key = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(key, oid);
        BinaryPrimitives.WriteUInt64LittleEndian(key.AsSpan(8), xid);
        var val = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(val.AsSpan(4), _reader.BlockSize);
        BinaryPrimitives.WriteUInt64LittleEndian(val.AsSpan(8), paddr);
        _cOmap.Put(key, val);
    }

    /// <summary>Kapsayıcının (yazılabilir) aygıtı üzerinde oturum açar.</summary>
    public static WriteSession Open(IWritableBlockDevice device, WriteGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var container = ApfsContainer.Open(device);
        var sb = container.Superblock;
        if (sb.Uuid != grant.ContainerUuid || sb.Xid != grant.Xid)
            throw new ApfsException(ApfsErrorCode.InvalidArgument, CoreText.T("Write.ChangedSinceGrant"));
        if (container.Warnings.Count > 0)
            throw new ApfsException(ApfsErrorCode.Unsupported, CoreText.T("Write.OpenWarnings"));
        var cp = Checkpoint.Read(container);
        if (!cp.IsValid) throw new ApfsException(ApfsErrorCode.Corrupt, CoreText.T("Write.CheckpointInvalid", string.Join("; ", cp.Problems)));
        var nx = container.Reader.ReadBlock(sb.Paddr);
        return new WriteSession(device, container, nx, sb, cp);
    }

    /// <summary>Oturum açılırken diskten açılan kapsayıcı (birim görüntüleri bunun üzerinde kurulur; kayıtları oturumdan okunur).</summary>
    public ApfsContainer Container { get; }

    public ulong CommittedXid => _sb.Xid;
    internal ulong TotalBlocks => _sb.BlockCount;
    /// <summary>Kullanılabilir boş blok sayısı (bu işlemde bırakılanlar hariç).</summary>
    public ulong FreeBlocks => _spaceman.FreeBlocks;
    public ulong TransactionXid => _txXid;
    internal SpacemanWriter Spaceman => _spaceman;
    internal ObjectReader Reader => _reader;

    /// <summary>Yeni geçici/sanal nesne kimliği (nx_next_oid).</summary>
    internal ulong NewOid() => _nextOid++;

    private void BeginTransaction()
    {
        _txXid = _sb.NextXid;
        _spaceman.BeginTransaction(_txXid);
    }

    private int _encryptedVolumes;

    /// <summary>
    /// Dosya verisini hemen yazar. Yalnızca bu işlemde ayrılmış bloklara yazılabilir (hiçbir kalıcı checkpoint göstermez; commit
    /// öncesi kesilirse zararsızdır); başka bloğa yazma girişimi programlama hatasıdır.
    /// Şifreli birimin yazıldığı oturumda veri YALNIZ şifrelenerek yazılır: <paramref name="crypto"/> ve <paramref name="cryptoId"/>
    /// (ilk bloğun crypto_id'si; tweak = crypto_id × (blok/512) + birim) verilmezse istisna — düz metin diske asla gitmez.
    /// </summary>
    internal void WriteData(ulong paddr, ReadOnlySpan<byte> data, Crypto.VolumeCrypto? crypto = null, ulong cryptoId = 0)
    {
        uint bs = _reader.BlockSize;
        if (data.Length % bs != 0) throw new ArgumentException(CoreText.T("Write.DataNotBlockMultiple"));
        if (_encryptedVolumes > 0 && (crypto is null || cryptoId == 0))
            throw new InvalidOperationException(CoreText.T("Write.PlainWriteRefused"));
        if (crypto is not null && cryptoId == 0) throw new InvalidOperationException(CoreText.T("Write.CryptoIdZero"));
        for (ulong b = paddr; b < paddr + (ulong)(data.Length / bs); b++)
            if (!_spaceman.IsAllocatedThisTx(b)) throw new InvalidOperationException(CoreText.T("Write.BlockNotAllocatedThisTx", b));
        if (crypto is null)
        {
            _device.Write((long)(paddr * bs), data);
            return;
        }
        var buf = data.ToArray(); // düz metnin kopyası yerinde şifrelenir
        crypto.EncryptData(buf, cryptoId, 0);
        _device.Write((long)(paddr * bs), buf);
    }

    /// <summary>İşlem içinde yazılacak fiziksel blok (ilk bariyerden önce yazılır).</summary>
    internal void QueueWrite(ulong paddr, byte[] block) => _pendingWrites.Add((paddr, block));

    /// <summary>Açık işlemi kalıcı yapar ve yenisini başlatır.</summary>
    public void Commit()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        ulong xid = _txXid;
        uint bs = _reader.BlockSize;

        // ---- 1) fiziksel yazımlar ----
        // Birimler: kopyala-yaz zinciri; birim süper blokları yeni bloklara, kapsayıcı omap'i güncellenir
        foreach (var vw in _volumes.Values.Where(v => v.IsDirty))
        {
            var (oid, newPaddr, oldPaddr) = vw.Flush(xid);
            _spaceman.Free(oldPaddr);
            RelocateVolume(oid, newPaddr, xid);
        }
        if (_cOmap.IsDirty)
        {
            ulong root = _cOmap.FlushPhysical(xid, _spaceman.AllocateBlock, p => _spaceman.Free(p), QueueWrite);
            ulong newObj = _spaceman.AllocateBlock();
            _spaceman.Free(_cOmapObjPaddr);
            BinaryPrimitives.WriteUInt64LittleEndian(_cOmapObj.AsSpan(48), root);
            _cOmapObjPaddr = newObj;
            QueueWrite(newObj, SealPhysical(_cOmapObj, newObj, xid));
        }
        // Alan yöneticisi en sonda: yukarıdaki bütün ayırma/bırakmalar bitmiş olmalı
        _pendingWrites.AddRange(_spaceman.PrepareCommit(xid));

        // ---- geçici nesneler (eşleme sırası: reaper, reap list, alan yöneticisi, boş kuyruk düğümleri) ----
        var ephemeral = new List<(CheckpointMapping Map, byte[] Data)>();
        void AddEphemeral(byte[] obj, uint type, uint subtype, ulong oid)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(obj.AsSpan(16), xid);
            BinaryPrimitives.WriteUInt64LittleEndian(obj, Fletcher64.Compute(obj));
            ephemeral.Add((new CheckpointMapping(type, subtype, (uint)obj.Length, 0, oid, 0), obj));
        }
        AddEphemeral(_reaper, Bin.U32(_reaper, 24), Bin.U32(_reaper, 28), _sb.ReaperOid);
        foreach (var (map, raw) in _reapLists) AddEphemeral(raw, map.Type, map.Subtype, map.Oid);
        var sm = (byte[])_spaceman.RawForCheckpoint().Clone();
        AddEphemeral(sm, Bin.U32(sm, 24), Bin.U32(sm, 28), _sb.SpacemanOid);
        foreach (var tree in new[] { _spaceman.FreeQueueIp, _spaceman.FreeQueueMain })
            tree.FlushEphemeral(xid, (oid, block) =>
                ephemeral.Add((new CheckpointMapping(Bin.U32(block, 24), Bin.U32(block, 28), (uint)block.Length, 0, oid, 0), block)));

        // Veri halkasında yer: şu anki checkpoint'in nesnelerinin ardından
        uint dataBlocks = _sb.XpDataBlocks, descBlocks = _sb.XpDescBlocks;
        uint need = (uint)ephemeral.Sum(e => e.Data.Length / bs);
        if (need > dataBlocks - _sb.XpDataLen)
            throw new ApfsException(ApfsErrorCode.Unsupported, CoreText.T("Write.CheckpointDataAreaFull"));
        uint dataIndex = _sb.XpDataNext, idx = dataIndex;
        var placed = new List<CheckpointMapping>();
        foreach (var (map, data) in ephemeral)
        {
            ulong paddr = Checkpoint.DataBlock(_sb, idx);
            for (uint k = 0; k < data.Length / bs; k++)
                _pendingWrites.Add((Checkpoint.DataBlock(_sb, idx + k), data.AsSpan((int)(k * bs), (int)bs).ToArray()));
            placed.Add(map with { Paddr = paddr });
            idx = (idx + (uint)(data.Length / bs)) % dataBlocks;
        }
        uint dataLen = need;

        // Checkpoint haritası blokları ve süper blok: tanımlayıcı halkasında şu anki checkpoint'in ardından
        int perBlock = (int)((bs - 40) / 40);
        int mapBlocks = Math.Max(1, (placed.Count + perBlock - 1) / perBlock);
        if (mapBlocks + 1 > descBlocks - _sb.XpDescLen)
            throw new ApfsException(ApfsErrorCode.Unsupported, CoreText.T("Write.CheckpointDescAreaFull"));
        uint descIndex = _sb.XpDescNext;
        for (int m = 0; m < mapBlocks; m++)
        {
            ulong paddr = Checkpoint.DescBlock(_sb, descIndex + (uint)m);
            var b = new byte[bs];
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), paddr);
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), xid);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24), ApfsConstants.ObjPhysical | ApfsConstants.TypeCheckpointMap);
            var slice = placed.Skip(m * perBlock).Take(perBlock).ToList();
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(32), m == mapBlocks - 1 ? Apfs.Container.Checkpoint.MapLast : 0);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(36), (uint)slice.Count);
            for (int i = 0; i < slice.Count; i++)
            {
                var e = slice[i];
                int o = 40 + i * 40;
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), e.Type);
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 4), e.Subtype);
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 8), e.Size);
                BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o + 16), e.FsOid);
                BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o + 24), e.Oid);
                BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o + 32), e.Paddr);
            }
            BinaryPrimitives.WriteUInt64LittleEndian(b, Fletcher64.Compute(b));
            _pendingWrites.Add((paddr, b));
        }

        // ---- bariyer 1 ----
        foreach (var (paddr, block) in _pendingWrites) _device.Write((long)(paddr * bs), block);
        _pendingWrites.Clear();
        _device.Flush();

        // ---- 2) kapsayıcı süper bloğu ----
        var nx = (byte[])_nx.Clone();
        uint descLen = (uint)mapBlocks + 1;
        ulong sbPaddr = Checkpoint.DescBlock(_sb, descIndex + (uint)mapBlocks);
        BinaryPrimitives.WriteUInt64LittleEndian(nx.AsSpan(16), xid);
        BinaryPrimitives.WriteUInt64LittleEndian(nx.AsSpan(88), _nextOid);
        BinaryPrimitives.WriteUInt64LittleEndian(nx.AsSpan(160), _cOmapObjPaddr);
        BinaryPrimitives.WriteUInt64LittleEndian(nx.AsSpan(96), xid + 1);
        BinaryPrimitives.WriteUInt32LittleEndian(nx.AsSpan(128), (descIndex + descLen) % descBlocks); // desc_next
        BinaryPrimitives.WriteUInt32LittleEndian(nx.AsSpan(132), (dataIndex + dataLen) % dataBlocks); // data_next
        BinaryPrimitives.WriteUInt32LittleEndian(nx.AsSpan(136), descIndex % descBlocks);
        BinaryPrimitives.WriteUInt32LittleEndian(nx.AsSpan(140), descLen);
        BinaryPrimitives.WriteUInt32LittleEndian(nx.AsSpan(144), dataIndex % dataBlocks);
        BinaryPrimitives.WriteUInt32LittleEndian(nx.AsSpan(148), dataLen);
        BinaryPrimitives.WriteUInt64LittleEndian(nx, Fletcher64.Compute(nx));
        _device.Write((long)(sbPaddr * bs), nx);
        _device.Flush();

        // ---- yeni durumu benimse ----
        _nx = nx;
        _sb = NxSuperblock.Parse(nx, sbPaddr);
        _cp = Checkpoint.Read(_reader, _sb);
        if (!_cp.IsValid) throw new InvalidOperationException(CoreText.T("Write.WrittenCheckpointInvalid", string.Join("; ", _cp.Problems)));
        BeginTransaction();
    }

    /// <summary>Oturumu kapatır: açık işlem kalıcı yapılır, blok 0'a süper blok kopyası yazılır (diskin "düzgün kapatıldı" işareti).</summary>
    public void Close()
    {
        if (_closed) return;
        Commit();
        var copy = (byte[])_nx.Clone();
        _device.Write(0, copy);
        _device.Flush();
        _closed = true;
    }

    public void Dispose()
    {
        // Kapatılmadan bırakılan oturum: açık işlem YAZILMAZ (disk son commit'te kalır; blok 0 eski = düzgün kapatılmamış).
        _closed = true;
        foreach (var vw in _volumes.Values) vw.ReleaseKey();
    }
}
