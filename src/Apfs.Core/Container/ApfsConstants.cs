namespace Apfs.Container;

/// <summary>Apple File System Reference'taki sabitler (yalnızca okuyucunun ihtiyaç duyduğu kadarı).</summary>
public static class ApfsConstants
{
    public const uint NxMagic = 0x4253584E;   // 'NXSB'
    public const uint ApfsMagic = 0x42535041; // 'APSB'

    public const int ObjHeaderSize = 32;
    public const int MinBlockSize = 4096;
    public const int MaxBlockSize = 65536;
    public const int NxMaxFileSystems = 100;

    // obj_phys_t.o_type
    public const uint ObjectTypeMask = 0x0000FFFF;
    public const uint ObjectTypeFlagsMask = 0xFFFF0000;
    public const uint ObjVirtual = 0x00000000;
    public const uint ObjEphemeral = 0x80000000;
    public const uint ObjPhysical = 0x40000000;
    public const uint ObjNoHeader = 0x20000000;
    public const uint ObjEncrypted = 0x10000000;

    public const uint TypeNxSuperblock = 0x01;
    public const uint TypeBTree = 0x02;
    public const uint TypeBTreeNode = 0x03;
    public const uint TypeSpaceman = 0x05;
    public const uint TypeSpacemanCab = 0x06;
    public const uint TypeSpacemanCib = 0x07;
    public const uint TypeSpacemanBitmap = 0x08;
    public const uint TypeSpacemanFreeQueue = 0x09;
    public const uint TypeOmap = 0x0B;
    public const uint TypeCheckpointMap = 0x0C;
    public const uint TypeFs = 0x0D;
    public const uint TypeFsTree = 0x0E;
    public const uint TypeBlockRefTree = 0x0F;
    public const uint TypeSnapMetaTree = 0x10;
    public const uint TypeNxReaper = 0x11;
    public const uint TypeNxReapList = 0x12;
    public const uint TypeOmapSnapshot = 0x13;
    public const uint TypeFextTree = 0x1F;

    // nx_incompatible_features
    public const ulong NxIncompatVersion1 = 0x1;
    public const ulong NxIncompatVersion2 = 0x2;
    public const ulong NxIncompatFusion = 0x100;

    // checkpoint descriptor alanı bitişik değilse bu bit set edilir
    public const uint XpDescBlocksNonContiguous = 0x80000000;

    // omap_val_t.ov_flags
    public const uint OmapValDeleted = 0x1;
    public const uint OmapValSaved = 0x2;
    public const uint OmapValEncrypted = 0x4;
    public const uint OmapValNoHeader = 0x8;

    // omap_phys_t.om_flags: şifreleme/anahtar değiştirme işlemi sürüyor
    public const uint OmapEncrypting = 0x10;
    public const uint OmapDecrypting = 0x20;
    public const uint OmapKeyrolling = 0x40;
    public const uint OmapCryptoGeneration = 0x80;

    // nx_flags
    public const ulong NxCryptoSw = 0x4;

    // btree_node_phys_t.btn_flags
    public const ushort BtNodeRoot = 0x1;
    public const ushort BtNodeLeaf = 0x2;
    public const ushort BtNodeFixedKvSize = 0x4;
    public const ushort BtNodeHashed = 0x8;
    public const ushort BtNodeNoHeader = 0x10;

    // btree_info_fixed_t.bt_flags
    public const uint BTreePhysical = 0x10;
    public const uint BTreeHashed = 0x80;
    public const uint BTreeNoHeader = 0x100;

    public const int BTreeInfoSize = 40;
    public const int BTreeNodeHeaderSize = 56;

    // apfs_superblock_t.apfs_incompatible_features
    public const ulong ApfsIncompatCaseInsensitive = 0x1;
    public const ulong ApfsIncompatDatalessSnaps = 0x2;
    public const ulong ApfsIncompatEncRolled = 0x4;
    public const ulong ApfsIncompatNormalizationInsensitive = 0x8;
    public const ulong ApfsIncompatIncompleteRestore = 0x10;
    public const ulong ApfsIncompatSealedVolume = 0x20;

    // apfs_superblock_t.apfs_fs_flags
    public const ulong ApfsFsUnencrypted = 0x1;
    public const ulong ApfsFsOneKey = 0x8;

    // apfs_superblock_t.apfs_role
    public const ushort RoleNone = 0x0000;
    public const ushort RoleSystem = 0x0001;
    public const ushort RoleUser = 0x0002;
    public const ushort RoleRecovery = 0x0004;
    public const ushort RoleVm = 0x0008;
    public const ushort RolePreboot = 0x0010;
    public const ushort RoleInstaller = 0x0020;
    public const ushort RoleData = 0x0040;
    public const ushort RoleBaseband = 0x0080;
    public const ushort RoleUpdate = 0x00C0;
    public const ushort RoleXart = 0x0100;
    public const ushort RoleHardware = 0x0140;
    public const ushort RoleBackup = 0x0180;
}
