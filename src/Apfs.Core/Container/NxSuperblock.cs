using Apfs.Util;

namespace Apfs.Container;

/// <summary>nx_superblock_t: kapsayıcı süper bloğu (yalnızca okuyucunun kullandığı alanlar).</summary>
public sealed class NxSuperblock
{
    public ulong Paddr { get; private init; }
    public ulong Xid { get; private init; }
    public uint BlockSize { get; private init; }
    public ulong BlockCount { get; private init; }
    public ulong Features { get; private init; }
    public ulong IncompatibleFeatures { get; private init; }
    public Guid Uuid { get; private init; }
    public uint XpDescBlocks { get; private init; }
    public ulong XpDescBase { get; private init; }
    public ulong OmapOid { get; private init; }
    public uint XpDescIndex { get; private init; }
    public uint XpDescLen { get; private init; }
    public ulong SpacemanOid { get; private init; }
    public uint MaxFileSystems { get; private init; }
    public IReadOnlyList<ulong> FsOids { get; private init; } = [];
    public ulong KeylockerStart { get; private init; }
    public ulong KeylockerBlockCount { get; private init; }

    // Yazıcının ve tam denetleyicinin ihtiyaç duyduğu alanlar (okuma yolu bunları kullanmaz)
    public ulong ReadOnlyCompatibleFeatures { get; private init; }
    public ulong NextOid { get; private init; }
    public ulong NextXid { get; private init; }
    public uint XpDataBlocks { get; private init; }
    public ulong XpDataBase { get; private init; }
    public uint XpDescNext { get; private init; }
    public uint XpDataNext { get; private init; }
    public uint XpDataIndex { get; private init; }
    public uint XpDataLen { get; private init; }
    public ulong ReaperOid { get; private init; }
    public ulong BlockedOutStart { get; private init; }
    public ulong BlockedOutCount { get; private init; }
    public ulong EvictMappingTreeOid { get; private init; }
    public ulong Flags { get; private init; }
    public ulong EfiJumpstart { get; private init; }
    public ulong MkbLockerStart { get; private init; }
    public ulong MkbLockerBlockCount { get; private init; }

    public static NxSuperblock Parse(byte[] b, ulong paddr)
    {
        if (Bin.U32(b, 32) != ApfsConstants.NxMagic)
            throw new ApfsException(ApfsErrorCode.NotApfs, CoreText.T("Nx.NoSignature"));
        uint maxFs = Bin.U32(b, 180);
        var fsOids = new List<ulong>();
        for (int i = 0; i < Math.Min(maxFs, (uint)ApfsConstants.NxMaxFileSystems); i++)
        {
            ulong oid = Bin.U64(b, 184 + i * 8);
            if (oid != 0) fsOids.Add(oid);
        }
        return new NxSuperblock
        {
            Paddr = paddr,
            Xid = Bin.U64(b, 16),
            BlockSize = Bin.U32(b, 36),
            BlockCount = Bin.U64(b, 40),
            Features = Bin.U64(b, 48),
            IncompatibleFeatures = Bin.U64(b, 64),
            Uuid = Bin.Uuid(b, 72),
            XpDescBlocks = Bin.U32(b, 104),
            XpDescBase = Bin.U64(b, 112),
            OmapOid = Bin.U64(b, 160),
            XpDescIndex = Bin.U32(b, 136),
            XpDescLen = Bin.U32(b, 140),
            SpacemanOid = Bin.U64(b, 152),
            MaxFileSystems = maxFs,
            FsOids = fsOids,
            KeylockerStart = Bin.U64(b, 1296),
            KeylockerBlockCount = Bin.U64(b, 1304),
            ReadOnlyCompatibleFeatures = Bin.U64(b, 56),
            NextOid = Bin.U64(b, 88),
            NextXid = Bin.U64(b, 96),
            XpDataBlocks = Bin.U32(b, 108),
            XpDataBase = Bin.U64(b, 120),
            XpDescNext = Bin.U32(b, 128),
            XpDataNext = Bin.U32(b, 132),
            XpDataIndex = Bin.U32(b, 144),
            XpDataLen = Bin.U32(b, 148),
            ReaperOid = Bin.U64(b, 168),
            BlockedOutStart = Bin.U64(b, 1240),
            BlockedOutCount = Bin.U64(b, 1248),
            EvictMappingTreeOid = Bin.U64(b, 1256),
            Flags = Bin.U64(b, 1264),
            EfiJumpstart = Bin.U64(b, 1272),
            MkbLockerStart = Bin.U64(b, 1392),
            MkbLockerBlockCount = Bin.U64(b, 1400),
        };
    }
}
