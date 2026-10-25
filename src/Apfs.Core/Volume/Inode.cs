using Apfs.Util;

namespace Apfs.Volume;

/// <summary>j_inode_val_t: dosya veya klasörün özellikleri.</summary>
public sealed class Inode
{
    public const ulong RootDirParent = 1;
    public const ulong RootDirId = 2;

    public const ushort ModeTypeMask = 0xF000;
    public const ushort ModeDir = 0x4000;
    public const ushort ModeRegular = 0x8000;
    public const ushort ModeSymlink = 0xA000;

    public const uint BsdFlagHidden = 0x8000;
    public const uint BsdFlagCompressed = 0x20;

    public const ulong InternalHasRsrcFork = 0x4000;
    public const ulong InternalIsSparse = 0x200;
    public const ulong InternalHasUncompressedSize = 0x40000;

    public ulong Id { get; private init; }
    public ulong ParentId { get; private init; }
    /// <summary>Dosya verisinin extent'lerinin bağlı olduğu kimlik (private_id).</summary>
    public ulong PrivateId { get; private init; }
    public long CreateTimeNs { get; private init; }
    public long ModTimeNs { get; private init; }
    public long ChangeTimeNs { get; private init; }
    public long AccessTimeNs { get; private init; }
    public ulong InternalFlags { get; private init; }
    /// <summary>Klasörde çocuk sayısı, dosyada bağlantı sayısı.</summary>
    public int ChildrenOrLinks { get; private init; }
    public uint BsdFlags { get; private init; }
    public uint Owner { get; private init; }
    public uint Group { get; private init; }
    public ushort Mode { get; private init; }
    public string? Name { get; private init; }
    /// <summary>Veri akışının mantıksal boyutu (j_dstream_t.size); veri akışı yoksa 0.</summary>
    public ulong DataSize { get; private init; }
    public ulong AllocatedSize { get; private init; }
    public bool HasDataStream { get; private init; }
    /// <summary>j_inode_val_t.uncompressed_size; yalnızca <see cref="HasUncompressedSize"/> ise geçerlidir.</summary>
    public ulong UncompressedSize { get; private init; }
    public bool HasUncompressedSize => (InternalFlags & InternalHasUncompressedSize) != 0;

    public bool IsDirectory => (Mode & ModeTypeMask) == ModeDir;
    public bool IsRegularFile => (Mode & ModeTypeMask) == ModeRegular;
    public bool IsSymlink => (Mode & ModeTypeMask) == ModeSymlink;
    public bool IsCompressed => (BsdFlags & BsdFlagCompressed) != 0;
    public bool IsHidden => (BsdFlags & BsdFlagHidden) != 0;

    internal static Inode Parse(ulong id, byte[] v)
    {
        if (v.Length < 92) throw ApfsException.Corrupt(CoreText.T("Volume.InodeRecordShort", id));
        string? name = null;
        ulong size = 0, alloc = 0;
        bool hasDs = false;
        if (v.Length > 92)
        {
            foreach (var (type, data) in XFields.Parse(v, 92))
            {
                switch (type)
                {
                    case InodeXField.Name:
                        name = Bin.Utf8Z(data);
                        break;
                    case InodeXField.Dstream when data.Length >= 16:
                        size = Bin.U64(data, 0);
                        alloc = Bin.U64(data, 8);
                        hasDs = true;
                        break;
                }
            }
        }
        return new Inode
        {
            Id = id,
            ParentId = Bin.U64(v, 0),
            PrivateId = Bin.U64(v, 8),
            CreateTimeNs = (long)Bin.U64(v, 16),
            ModTimeNs = (long)Bin.U64(v, 24),
            ChangeTimeNs = (long)Bin.U64(v, 32),
            AccessTimeNs = (long)Bin.U64(v, 40),
            InternalFlags = Bin.U64(v, 48),
            ChildrenOrLinks = Bin.I32(v, 56),
            BsdFlags = Bin.U32(v, 68),
            Owner = Bin.U32(v, 72),
            Group = Bin.U32(v, 76),
            Mode = Bin.U16(v, 80),
            UncompressedSize = Bin.U64(v, 84),
            Name = name,
            DataSize = size,
            AllocatedSize = alloc,
            HasDataStream = hasDs,
        };
    }

    /// <summary>APFS zaman damgası (1970'ten beri nanosaniye, UTC) → DateTime.</summary>
    public static DateTime ToDateTime(long ns) =>
        ns <= 0 ? DateTime.UnixEpoch : DateTime.UnixEpoch.AddTicks(Math.Min(ns / 100, DateTime.MaxValue.Ticks - DateTime.UnixEpoch.Ticks));
}

/// <summary>Bir klasördeki girdi (j_drec).</summary>
public sealed record DirEntry(string Name, ulong FileId, DirEntryType Type, long DateAddedNs);

/// <summary>Dosya verisinin bir parçası (j_file_extent). PhysBlock 0 ise seyrek (boş) alandır.</summary>
public readonly record struct FileExtent(ulong LogicalOffset, ulong Length, ulong PhysBlock, ulong CryptoId);
