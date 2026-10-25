using Apfs.Util;

namespace Apfs.Container;

/// <summary>checkpoint_mapping_t: geçici (ephemeral) bir nesnenin checkpoint veri alanındaki yeri.</summary>
public readonly record struct CheckpointMapping(uint Type, uint Subtype, uint Size, ulong FsOid, ulong Oid, ulong Paddr)
{
    public uint ObjectType => Type & ApfsConstants.ObjectTypeMask;
}

/// <summary>
/// Seçilen kapsayıcı süper bloğunun checkpoint'i: tanımlayıcı halkasındaki eşleme blokları ve veri halkasındaki geçici nesneler.
/// Okurken macOS'un (ve apfsck'nin) geçerlilik kurallarını denetler; kurala uymayan her şey <see cref="Problems"/>'a yazılır.
/// Kurallar: eşleme blokları süper bloktan hemen önce gelir, hepsi checkpoint'in xid'ini taşır, yalnızca sonuncusu LAST işaretlidir;
/// geçici nesneler veri halkasında data_index'ten başlayıp boşluksuz, eşleme sırasıyla dizilir ve tam data_next'te biter
/// (halkanın sonundan başa sarabilir); her geçici nesne checkpoint'in xid'ini taşır.
/// </summary>
public sealed class Checkpoint
{
    public const uint MapLast = 0x1;
    private const int MapHeader = 40, MapEntry = 40;

    private readonly ObjectReader _reader;
    private readonly NxSuperblock _sb;
    private readonly Dictionary<ulong, CheckpointMapping> _byOid = [];

    private Checkpoint(ObjectReader reader, NxSuperblock sb)
    {
        _reader = reader;
        _sb = sb;
    }

    public IReadOnlyList<ulong> MapBlocks { get; private set; } = [];
    public IReadOnlyList<CheckpointMapping> Mappings { get; private set; } = [];
    public IReadOnlyList<string> Problems { get; private set; } = [];
    public bool IsValid => Problems.Count == 0;

    /// <summary>Tanımlayıcı halkasındaki i. bloğun fiziksel adresi.</summary>
    public static ulong DescBlock(NxSuperblock sb, uint i) => sb.XpDescBase + i % (sb.XpDescBlocks & ~ApfsConstants.XpDescBlocksNonContiguous);

    /// <summary>Veri halkasındaki i. bloğun fiziksel adresi.</summary>
    public static ulong DataBlock(NxSuperblock sb, uint i) => sb.XpDataBase + i % sb.XpDataBlocks;

    public static Checkpoint Read(ObjectReader reader, NxSuperblock sb)
    {
        var cp = new Checkpoint(reader, sb);
        cp.Parse();
        return cp;
    }

    public static Checkpoint Read(ApfsContainer c) => Read(c.Reader, c.Superblock);

    public CheckpointMapping? Find(ulong oid) => _byOid.TryGetValue(oid, out var m) ? m : null;

    private void Parse()
    {
        var problems = new List<string>();
        var maps = new List<ulong>();
        var mappings = new List<CheckpointMapping>();
        uint descBlocks = _sb.XpDescBlocks;
        if ((descBlocks & ApfsConstants.XpDescBlocksNonContiguous) != 0 || (_sb.XpDataBlocks & ApfsConstants.XpDescBlocksNonContiguous) != 0)
        {
            Problems = [CoreText.T("Cp.NonContiguous")];
            return;
        }
        if (descBlocks == 0 || _sb.XpDataBlocks == 0 || _sb.XpDescIndex >= descBlocks || _sb.XpDataIndex >= _sb.XpDataBlocks
            || _sb.XpDescLen < 1 || _sb.XpDescLen > descBlocks || _sb.XpDataLen > _sb.XpDataBlocks
            || (_sb.XpDescIndex + _sb.XpDescLen) % descBlocks != _sb.XpDescNext
            || (_sb.XpDataIndex + _sb.XpDataLen) % _sb.XpDataBlocks != _sb.XpDataNext)
        {
            Problems = [CoreText.T("Cp.RingInconsistent")];
            return;
        }

        // Süper blok, tanımlayıcı aralığının son bloğu olmalı
        ulong sbAt = DescBlock(_sb, _sb.XpDescIndex + _sb.XpDescLen - 1);
        if (_sb.Paddr != 0 && _sb.Paddr != sbAt)
            problems.Add(CoreText.T("Cp.SuperblockNotAtEnd", _sb.Paddr, sbAt));

        uint dataIdx = _sb.XpDataIndex;
        bool sawLast = false;
        for (uint i = 0; i + 1 < _sb.XpDescLen; i++)
        {
            ulong paddr = DescBlock(_sb, _sb.XpDescIndex + i);
            maps.Add(paddr);
            byte[] b;
            try { b = _reader.ReadBlock(paddr); }
            catch (ApfsException e) when (e.Code is not ApfsErrorCode.IoError) { problems.Add(CoreText.T("Cp.MapBlockUnreadable", paddr)); continue; }
            if (!Fletcher64.Verify(b)) { problems.Add(CoreText.T("Cp.MapBlockChecksum", paddr)); continue; }
            if (Bin.U64(b, 8) != paddr) problems.Add(CoreText.T("Cp.MapBlockWrongOid", paddr));
            if (Bin.U64(b, 16) != _sb.Xid) problems.Add(CoreText.T("Cp.MapBlockXid", paddr, Bin.U64(b, 16), _sb.Xid));
            if (Bin.U32(b, 24) != (ApfsConstants.ObjPhysical | ApfsConstants.TypeCheckpointMap) || Bin.U32(b, 28) != 0)
                problems.Add(CoreText.T("Cp.MapBlockWrongType", paddr));
            uint flags = Bin.U32(b, 32), count = Bin.U32(b, 36);
            if ((flags & ~MapLast) != 0) problems.Add(CoreText.T("Cp.MapBlockUnknownFlag", paddr));
            bool last = (flags & MapLast) != 0;
            if (last != (i + 2 == _sb.XpDescLen)) problems.Add(CoreText.T("Cp.MapBlockLastMisplaced", paddr));
            sawLast |= last;
            if (MapHeader + (long)count * MapEntry > b.Length) { problems.Add(CoreText.T("Cp.MapBlockCountTooBig", paddr)); continue; }
            for (int k = 0; k < count; k++)
            {
                int e = MapHeader + k * MapEntry;
                var m = new CheckpointMapping(Bin.U32(b, e), Bin.U32(b, e + 4), Bin.U32(b, e + 8),
                    Bin.U64(b, e + 16), Bin.U64(b, e + 24), Bin.U64(b, e + 32));
                if (Bin.U32(b, e + 12) != 0) problems.Add(CoreText.T("Cp.MappingPadding", m.Oid));
                if (m.Size == 0 || m.Size % _reader.BlockSize != 0) { problems.Add(CoreText.T("Cp.MappingSize", m.Oid)); continue; }
                if (m.Paddr != DataBlock(_sb, dataIdx)) problems.Add(CoreText.T("Cp.MappingOrder", m.Oid));
                if (!_byOid.TryAdd(m.Oid, m)) problems.Add(CoreText.T("Cp.MappingDuplicate", m.Oid));
                mappings.Add(m);
                dataIdx = (dataIdx + m.Size / _reader.BlockSize) % _sb.XpDataBlocks;
            }
        }
        if (_sb.XpDescLen > 1 && !sawLast) problems.Add(CoreText.T("Cp.NoLastMapBlock"));
        if (dataIdx != _sb.XpDataNext) problems.Add(CoreText.T("Cp.EphemeralOverlap"));

        // Her geçici nesne okunabilir, doğru türde ve checkpoint'in xid'inde olmalı
        foreach (var m in mappings)
        {
            try
            {
                var o = ReadEphemeral(m);
                if (Bin.U32(o, 24) != m.Type || Bin.U32(o, 28) != m.Subtype) problems.Add(CoreText.T("Cp.EphemeralTypeMismatch", m.Oid));
                if (Bin.U64(o, 8) != m.Oid) problems.Add(CoreText.T("Cp.EphemeralWrongOid", m.Oid));
                if (Bin.U64(o, 16) != _sb.Xid) problems.Add(CoreText.T("Cp.EphemeralNotLatest", m.Oid, Bin.U64(o, 16)));
            }
            catch (ApfsException e) when (e.Code is not ApfsErrorCode.IoError)
            {
                problems.Add(CoreText.T("Cp.EphemeralError", m.Oid, e.Detail ?? e.Code.ToString()));
            }
        }

        MapBlocks = maps;
        Mappings = mappings;
        Problems = problems;
    }

    /// <summary>Geçici nesneyi (birden çok blok sürebilir, halkanın sonundan başa sarabilir) okur ve sağlama toplamını doğrular.</summary>
    public byte[] ReadEphemeral(CheckpointMapping m)
    {
        if (m.Size == 0 || m.Size % _reader.BlockSize != 0 || m.Size > 64u * _reader.BlockSize)
            throw ApfsException.Corrupt(CoreText.T("Cp.EphemeralInvalidSize", m.Oid, m.Size));
        if (m.Paddr < _sb.XpDataBase || m.Paddr >= _sb.XpDataBase + _sb.XpDataBlocks)
            throw ApfsException.Corrupt(CoreText.T("Cp.EphemeralOutsideRing", m.Oid));
        var buf = new byte[m.Size];
        uint start = (uint)(m.Paddr - _sb.XpDataBase);
        for (uint k = 0; k < m.Size / _reader.BlockSize; k++)
            _reader.ReadBlock(DataBlock(_sb, start + k)).CopyTo(buf, (int)(k * _reader.BlockSize));
        if (!Fletcher64.Verify(buf))
            throw new ApfsException(ApfsErrorCode.ChecksumMismatch, CoreText.T("Cp.EphemeralChecksum", m.Oid, m.Paddr));
        return buf;
    }

    /// <summary>Geçici nesneyi kimliğiyle okur; eşleme yoksa bozuk veri hatası.</summary>
    public byte[] ReadEphemeral(ulong oid) =>
        ReadEphemeral(Find(oid) ?? throw ApfsException.Corrupt(CoreText.T("Cp.EphemeralNotMapped", oid)));
}
