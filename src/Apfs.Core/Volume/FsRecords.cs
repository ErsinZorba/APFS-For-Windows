using Apfs.Util;

namespace Apfs.Volume;

/// <summary>Dosya sistemi kayıt türleri (j_obj_types).</summary>
public enum JType : byte
{
    Any = 0,
    SnapMetadata = 1,
    Extent = 2,
    Inode = 3,
    Xattr = 4,
    SiblingLink = 5,
    DstreamId = 6,
    CryptoState = 7,
    FileExtent = 8,
    DirRec = 9,
    DirStats = 10,
    SnapName = 11,
    SiblingMap = 12,
    FileInfo = 13,
}

/// <summary>Dizin girdisi türü (DT_*).</summary>
public enum DirEntryType : byte
{
    Unknown = 0,
    Fifo = 1,
    CharDevice = 2,
    Directory = 4,
    BlockDevice = 6,
    Regular = 8,
    Symlink = 10,
    Socket = 12,
    Whiteout = 14,
}

/// <summary>j_key_t yardımcıları: üst 4 bit tür, alt 60 bit nesne kimliği.</summary>
internal static class JKey
{
    public const ulong ObjIdMask = 0x0FFFFFFFFFFFFFFF;
    public const int TypeShift = 60;

    public static ulong ObjId(ReadOnlySpan<byte> key) => Bin.U64(key, 0) & ObjIdMask;
    public static JType Type(ReadOnlySpan<byte> key) => (JType)(Bin.U64(key, 0) >> TypeShift);

    /// <summary>(oid, tür) önekine göre karşılaştırma; B-ağacı taramasında kullanılır.</summary>
    public static int ComparePrefix(ReadOnlySpan<byte> key, ulong oid, JType type)
    {
        ulong kOid = ObjId(key);
        if (kOid != oid) return kOid < oid ? -1 : 1;
        var kType = Type(key);
        return kType == type ? 0 : (kType < type ? -1 : 1);
    }
}

/// <summary>Inode genişletilmiş alan türleri (INO_EXT_TYPE_*).</summary>
internal static class InodeXField
{
    public const byte SnapXid = 1;
    public const byte DeltaTreeOid = 2;
    public const byte DocumentId = 3;
    public const byte Name = 4;
    public const byte PrevFsize = 5;
    public const byte FinderInfo = 7;
    public const byte Dstream = 8;
    public const byte DirStatsKey = 10;
    public const byte FsUuid = 11;
    public const byte SparseBytes = 13;
    public const byte Rdev = 14;
}

/// <summary>xf_blob_t ayrıştırıcısı: (tür, veri) çiftleri.</summary>
internal static class XFields
{
    public static IEnumerable<(byte Type, byte[] Data)> Parse(byte[] blob, int offset)
    {
        if (offset >= blob.Length) yield break;
        int num = Bin.U16(blob, offset);
        int dataPos = offset + 4 + num * 4;
        for (int i = 0; i < num; i++)
        {
            int h = offset + 4 + i * 4;
            byte type = Bin.Slice(blob, h, 1)[0];
            int size = Bin.U16(blob, h + 2);
            var data = Bin.Slice(blob, dataPos, size).ToArray();
            yield return (type, data);
            dataPos += (size + 7) & ~7; // her alan 8 bayta hizalanır
        }
    }
}
