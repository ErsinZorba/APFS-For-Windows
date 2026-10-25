using System.Text;
using Apfs.Container;
using Apfs.Util;

namespace Apfs.Volume;

/// <summary>APFS birimi (apfs_superblock_t) ve dosya sistemi ağacı üzerinde salt okunur işlemler.</summary>
public sealed class ApfsVolume
{
    private readonly ApfsContainer _container;
    private readonly ObjectMap _omap;
    private readonly BTree.BTree _fsTree;

    // Önbellekler (disk salt okunur açıldığı için hiç bayatlamaz; sınırlar yalnızca bellek içindir).
    private readonly LruCache<ulong, Inode> _inodes = new(65_536);
    private readonly LruCache<ulong, DirectoryIndex> _dirs = new(500_000, d => d.Entries.Count + 1); // toplam girdi sayısı
    private readonly LruCache<ulong, FileExtent[]> _extents = new(1_000_000, e => e.Length + 1);    // toplam extent sayısı

    private ApfsVolume(ApfsContainer container, ulong oid, ulong paddr, byte[] sb, Func<VolumeLockInfo, string?>? passwordProvider,
        Apfs.Crypto.VolumeKey? key = null)
    {
        _container = container;
        RawSuperblock = sb;
        Oid = oid;
        Paddr = paddr;
        Index = Bin.U32(sb, 36);
        Features = Bin.U64(sb, 40);
        IncompatibleFeatures = Bin.U64(sb, 56);
        RootTreeType = Bin.U32(sb, 116);
        OmapOid = Bin.U64(sb, 128);
        RootTreeOid = Bin.U64(sb, 136);
        NumFiles = Bin.U64(sb, 184);
        NumDirectories = Bin.U64(sb, 192);
        NumSymlinks = Bin.U64(sb, 200);
        AllocatedBlocks = Bin.U64(sb, 88);
        Uuid = Bin.Uuid(sb, 240);
        FsFlags = Bin.U64(sb, 264);
        Name = Bin.Utf8Z(Bin.Slice(sb, 704, 256));
        Role = Bin.U16(sb, 964);
        LastModTimeNs = (long)Bin.U64(sb, 256);

        if (IsSealed)
            throw new ApfsException(ApfsErrorCode.Unsupported, CoreText.T("Volume.SealedSystemVolume", Name));
        if ((RootTreeType & ApfsConstants.ObjectTypeFlagsMask) != ApfsConstants.ObjVirtual)
            throw new ApfsException(ApfsErrorCode.Unsupported, CoreText.T("Volume.PhysicalFsTree"));

        if (IsEncrypted && key is not null)
        {
            if (key.VolumeUuid != Uuid) throw new ApfsException(ApfsErrorCode.InvalidArgument, CoreText.T("Volume.KeyNotForVolume", Name));
            Crypto = key.CreateCrypto(container.BlockSize);
        }
        else if (IsEncrypted)
        {
            var info = new VolumeLockInfo(oid, Name, Uuid, Role, Apfs.Crypto.VolumeKeys.GetPasswordHint(container, Uuid));
            string? password = passwordProvider?.Invoke(info);
            if (password is null)
                throw new ApfsException(ApfsErrorCode.Encrypted,
                    CoreText.T("Volume.LockedName", Name, (info.Hint is null ? "" : CoreText.T("Volume.PasswordHintSuffix", info.Hint))));
            Crypto = new Apfs.Crypto.VolumeCrypto(Apfs.Crypto.VolumeKeys.Unlock(container, Uuid, password), container.BlockSize);
        }

        _omap = new ObjectMap(container.Reader, OmapOid);
        var (rootPaddr, rootEncrypted) = ResolveVirtual(RootTreeOid);
        _fsTree = new BTree.BTree(container.Reader, rootPaddr, ApfsConstants.TypeFsTree, ResolveVirtual, rootEncrypted, Crypto);
    }

    /// <summary>Kilidi açılmış şifreli birimin şifre çözücüsü; şifresiz birimde null.</summary>
    public Apfs.Crypto.VolumeCrypto? Crypto { get; }

    public ulong Oid { get; }
    public ulong Paddr { get; }
    public uint Index { get; }
    public string Name { get; }
    public Guid Uuid { get; }
    public ushort Role { get; }
    public ulong Features { get; }
    public ulong IncompatibleFeatures { get; }
    public ulong FsFlags { get; }
    public uint RootTreeType { get; }
    public ulong OmapOid { get; }
    public ulong RootTreeOid { get; }
    public ulong NumFiles { get; }
    public ulong NumDirectories { get; }
    public ulong NumSymlinks { get; }
    public ulong AllocatedBlocks { get; }
    public long LastModTimeNs { get; }

    public ApfsContainer Container => _container;
    public bool IsCaseInsensitive => (IncompatibleFeatures & ApfsConstants.ApfsIncompatCaseInsensitive) != 0;
    public bool IsNormalizationInsensitive => (IncompatibleFeatures & ApfsConstants.ApfsIncompatNormalizationInsensitive) != 0;
    public bool IsSealed => (IncompatibleFeatures & ApfsConstants.ApfsIncompatSealedVolume) != 0;
    /// <summary>Yazılımla şifrelenmiş birim (apfs_fs_flags'in alt iki biti APFS_FS_UNENCRYPTED değilse; apfs-fuse ile aynı kural).</summary>
    public bool IsEncrypted => (FsFlags & 3) != ApfsConstants.ApfsFsUnencrypted;

    /// <summary>
    /// Kullanıcının dosyalarını taşıyan birim mi? Rolsüz, Kullanıcı, Veri ve Yedek birimleri varsayılan olarak bağlanır;
    /// Sistem, Önyükleme, Kurtarma, VM gibi macOS'un iç birimleri gizlenir (CLI'da --all ile bağlanabilir).
    /// </summary>
    public bool IsUserVisible => IsUserVisibleRole(Role);

    public static bool IsUserVisibleRole(ushort role) =>
        role is ApfsConstants.RoleNone or ApfsConstants.RoleUser or ApfsConstants.RoleData or ApfsConstants.RoleBackup;

    public string RoleName => Role switch
    {
        ApfsConstants.RoleNone => "",
        ApfsConstants.RoleSystem => CoreText.U("Volume.RoleSystem"),
        ApfsConstants.RoleUser => CoreText.U("Volume.RoleUser"),
        ApfsConstants.RoleRecovery => CoreText.U("Volume.RoleRecovery"),
        ApfsConstants.RoleVm => CoreText.U("Volume.RoleVm"),
        ApfsConstants.RolePreboot => CoreText.U("Volume.RolePreboot"),
        ApfsConstants.RoleInstaller => CoreText.U("Volume.RoleInstaller"),
        ApfsConstants.RoleData => CoreText.U("Volume.RoleData"),
        ApfsConstants.RoleUpdate => CoreText.U("Volume.RoleUpdate"),
        ApfsConstants.RoleBackup => CoreText.U("Volume.RoleBackup"),
        _ => $"0x{Role:x}",
    };

    /// <summary>
    /// Birimi açar. Şifreli birimde <paramref name="passwordProvider"/> çağrılır (null dönerse veya verilmemişse Encrypted hatası,
    /// parola yanlışsa WrongPassword hatası; ikisi de ipucunu içerir).
    /// </summary>
    public static ApfsVolume Open(ApfsContainer container, ulong oid, Func<VolumeLockInfo, string?>? passwordProvider = null)
    {
        var loc = container.Omap.Resolve(oid, container.Xid);
        var sb = container.Reader.ReadObject(loc.Paddr, ApfsConstants.TypeFs, oid);
        if (Bin.U32(sb, 32) != ApfsConstants.ApfsMagic)
            throw ApfsException.Corrupt(CoreText.T("Volume.NoApsbMagic", oid));
        return new ApfsVolume(container, oid, loc.Paddr, sb, passwordProvider);
    }

    /// <summary>
    /// Birimi kilidi önceden açılmış anahtarla açar (parola sorulmaz). Şifresiz birimde anahtar yok sayılır; anahtar başka birimin
    /// ise InvalidArgument, yanlışsa ilk düğümde sağlama hatası.
    /// </summary>
    public static ApfsVolume Open(ApfsContainer container, ulong oid, Apfs.Crypto.VolumeKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var loc = container.Omap.Resolve(oid, container.Xid);
        var sb = container.Reader.ReadObject(loc.Paddr, ApfsConstants.TypeFs, oid);
        if (Bin.U32(sb, 32) != ApfsConstants.ApfsMagic)
            throw ApfsException.Corrupt(CoreText.T("Volume.NoApsbMagic", oid));
        return new ApfsVolume(container, oid, loc.Paddr, sb, null, key);
    }

    private (ulong Paddr, bool Encrypted) ResolveVirtual(ulong oid)
    {
        var v = _omap.Resolve(oid, _container.Xid);
        return (v.Paddr, v.IsEncrypted);
    }

    // ---- Kayıt erişimi ----

    private IEnumerable<BTree.BTreeEntry> Records(ulong oid, JType type) =>
        _sessionScan is { } scan ? scan(k => JKey.ComparePrefix(k, oid, type)) : _fsTree.Scan(k => JKey.ComparePrefix(k, oid, type));

    // ---- Yazma oturumunun görüntüsü (okuma yolu bunları kullanmaz) ----

    private Func<Func<ReadOnlySpan<byte>, int>, IEnumerable<BTree.BTreeEntry>>? _sessionScan;

    /// <summary>
    /// Bu birim nesnesini bir yazma oturumunun güncel görüntüsü yapar: kayıtlar diskteki ağaçtan değil oturumun bellekteki
    /// ağacından okunur. Değişen kayıtların önbellekleri <see cref="InvalidateRecord"/> ile düşürülür.
    /// </summary>
    internal void UseSessionSource(Func<Func<ReadOnlySpan<byte>, int>, IEnumerable<BTree.BTreeEntry>> scan)
    {
        _sessionScan = scan;
        _inodes.Clear();
        _dirs.Clear();
        _extents.Clear();
    }

    /// <summary>Yazıcı bir dizin girdisi ekledi/değiştirdi: önbellekteki klasör dizini yerinde güncellenir (yeniden kurulmaz).</summary>
    internal void SessionDirAdd(ulong dirId, DirEntry e)
    {
        if (_dirs.TryGet(dirId, out var idx)) idx.SessionAdd(e);
    }

    /// <summary>Yazıcı bir dizin girdisini sildi.</summary>
    internal void SessionDirRemove(ulong dirId, string name)
    {
        if (_dirs.TryGet(dirId, out var idx)) idx.SessionRemove(name);
    }

    /// <summary>Dizin girdisi anahtarındaki ad (karmalı ya da karmasız).</summary>
    internal static string DrecKeyName(byte[] key)
    {
        int hashedLen = (int)(Bin.U32(key, 8) & 0x3FF);
        if (12 + hashedLen == key.Length) return Bin.Utf8Z(Bin.Slice(key, 12, hashedLen));
        return Bin.Utf8Z(Bin.Slice(key, 10, Bin.U16(key, 8)));
    }

    /// <summary>Yazıcı bir kaydı değiştirdiğinde: ilgili önbellek girdisini düşürür.</summary>
    internal void InvalidateRecord(JType type, ulong id)
    {
        switch (type)
        {
            case JType.Inode: _inodes.Remove(id); break;
            case JType.DirRec: _dirs.Remove(id); break;
            case JType.FileExtent: _extents.Remove(id); break;
        }
    }

    public Inode GetInode(ulong id) =>
        TryGetInode(id) ?? throw new ApfsException(ApfsErrorCode.NotFound, $"inode {id}");

    public Inode? TryGetInode(ulong id)
    {
        if (_inodes.TryGet(id, out var cached)) return cached;
        foreach (var e in Records(id, JType.Inode))
        {
            var ino = Inode.Parse(id, e.Value);
            _inodes.Add(id, ino);
            return ino;
        }
        return null;
    }

    public Inode Root => GetInode(Inode.RootDirId);

    /// <summary>Klasördeki girdiler (B-ağacı sırasıyla; önbellekten).</summary>
    public IReadOnlyList<DirEntry> ListDirectory(ulong dirId) => GetDirectory(dirId).Entries;

    /// <summary>Klasörün tamamı bir kez taranır ve ada göre dizinlenir; sonraki aramalar O(1).</summary>
    public DirectoryIndex GetDirectory(ulong dirId) =>
        _dirs.GetOrAdd(dirId, id => new DirectoryIndex(ScanDirectory(id).ToList(), IsCaseInsensitive));

    /// <summary>Teşhis: klasördeki karmalı anahtarların (ad, diskteki karma) çiftleri. Karmasız anahtarlı birimde boş.</summary>
    public IEnumerable<(string Name, uint StoredHash)> ListDirectoryHashes(ulong dirId)
    {
        foreach (var e in Records(dirId, JType.DirRec))
        {
            int hashedLen = (int)(Bin.U32(e.Key, 8) & 0x3FF);
            if (12 + hashedLen != e.Key.Length) continue;
            yield return (Bin.Utf8Z(Bin.Slice(e.Key, 12, hashedLen)), Bin.U32(e.Key, 8) >> 10);
        }
    }

    private IEnumerable<DirEntry> ScanDirectory(ulong dirId)
    {
        foreach (var e in Records(dirId, JType.DirRec))
        {
            var entry = ParseDirRec(e.Key, e.Value);
            if (entry is not null) yield return entry;
        }
    }

    /// <summary>
    /// j_drec_hashed_key_t (8 + 4 + ad) veya j_drec_key_t (8 + 2 + ad). Hangisi olduğu anahtar uzunluğundan çıkarılır;
    /// böylece birim bayrakları tuhaf olsa bile doğru okunur.
    /// </summary>
    internal static DirEntry? ParseDirRec(byte[] key, byte[] val)
    {
        string name;
        int hashedLen = (int)(Bin.U32(key, 8) & 0x3FF);
        int plainLen = Bin.U16(key, 8);
        if (12 + hashedLen == key.Length)
            name = Bin.Utf8Z(Bin.Slice(key, 12, hashedLen));
        else if (10 + plainLen == key.Length)
            name = Bin.Utf8Z(Bin.Slice(key, 10, plainLen));
        else
            throw ApfsException.Corrupt(CoreText.T("Volume.DirEntryKeyLength"));
        if (val.Length < 18) throw ApfsException.Corrupt(CoreText.T("Volume.DirEntryValueShort"));
        ulong fileId = Bin.U64(val, 0);
        long added = (long)Bin.U64(val, 8);
        var type = (DirEntryType)(Bin.U16(val, 16) & 0xF);
        if (name.Length == 0) return null;
        return new DirEntry(name, fileId, type, added);
    }

    /// <summary>Bir veri akışının extent'leri, mantıksal ofsete göre sıralı (önbellekten).</summary>
    public FileExtent[] GetExtents(ulong privateId) =>
        _extents.GetOrAdd(privateId, id => ScanExtents(id).ToArray());

    private List<FileExtent> ScanExtents(ulong privateId)
    {
        var list = new List<FileExtent>();
        foreach (var e in Records(privateId, JType.FileExtent))
        {
            if (e.Key.Length < 16 || e.Value.Length < 24) throw ApfsException.Corrupt(CoreText.T("Volume.FileExtentShort"));
            ulong logical = Bin.U64(e.Key, 8);
            ulong lenAndFlags = Bin.U64(e.Value, 0);
            ulong len = lenAndFlags & 0x00FFFFFFFFFFFFFF;
            list.Add(new FileExtent(logical, len, Bin.U64(e.Value, 8), Bin.U64(e.Value, 16)));
        }
        list.Sort((a, b) => a.LogicalOffset.CompareTo(b.LogicalOffset));
        return list;
    }

    /// <summary>Genişletilmiş öznitelikler: ad → (bayraklar, veri). Veri akışında tutulanlar için veri, j_xattr_dstream_t'dir.</summary>
    public IEnumerable<(string Name, ushort Flags, byte[] Data)> ListXattrs(ulong id)
    {
        foreach (var e in Records(id, JType.Xattr))
        {
            int nameLen = Bin.U16(e.Key, 8);
            string name = Bin.Utf8Z(Bin.Slice(e.Key, 10, nameLen));
            ushort flags = Bin.U16(e.Value, 0);
            int dataLen = Bin.U16(e.Value, 2);
            yield return (name, flags, Bin.Slice(e.Value, 4, dataLen).ToArray());
        }
    }

    public const ushort XattrDataStream = 0x1;
    public const ushort XattrDataEmbedded = 0x2;

    /// <summary>Bir özniteliğin içeriğini okur (gömülü veya veri akışında). Yoksa null.</summary>
    public byte[]? ReadXattr(ulong id, string name, int maxSize = 64 * 1024 * 1024)
    {
        foreach (var (n, flags, data) in ListXattrs(id))
        {
            if (!string.Equals(n, name, StringComparison.Ordinal)) continue;
            if ((flags & XattrDataEmbedded) != 0) return data;
            if ((flags & XattrDataStream) != 0)
            {
                if (data.Length < 16) throw ApfsException.Corrupt(CoreText.T("Volume.XattrDstreamShort"));
                ulong streamId = Bin.U64(data, 0);
                ulong size = Bin.U64(data, 8);
                if (size > (ulong)maxSize) throw new ApfsException(ApfsErrorCode.Unsupported, CoreText.T("Volume.XattrTooLarge", size));
                var buf = new byte[size];
                var reader = new FileDataReader(this, GetExtents(streamId), size);
                reader.Read(0, buf);
                return buf;
            }
            throw ApfsException.Corrupt(CoreText.T("Volume.XattrUnknownStorage", name));
        }
        return null;
    }

    /// <summary>Sembolik bağın hedefi.</summary>
    public string? ReadSymlinkTarget(ulong id)
    {
        var data = ReadXattr(id, "com.apple.fs.symlink");
        return data is null ? null : Bin.Utf8Z(data);
    }

    /// <summary>
    /// "/" ayraçlı APFS yolunu çözümler. APFS adlarında "\" geçerli bir karakter olduğundan ayraç sayılmaz.
    /// Ad karşılaştırması birimin kurallarına uyar (bkz. <see cref="DirectoryIndex"/>).
    /// </summary>
    public Inode Lookup(string path) =>
        LookupComponents(path.Split('/', StringSplitOptions.RemoveEmptyEntries), out _);

    /// <summary>Ad bileşenlerinden yol çözümler; <paramref name="actualNames"/> diskteki gerçek adları döndürür.</summary>
    public Inode LookupComponents(IReadOnlyList<string> components, out List<string> actualNames)
    {
        actualNames = new List<string>(components.Count);
        var current = Root;
        foreach (var part in components)
        {
            if (!current.IsDirectory)
                throw new ApfsException(ApfsErrorCode.NotFound, string.Join('/', components));
            var entry = FindEntry(current.Id, part) ?? throw new ApfsException(ApfsErrorCode.NotFound, string.Join('/', components));
            actualNames.Add(entry.Name);
            current = GetInode(entry.FileId);
        }
        return current;
    }

    public DirEntry? FindEntry(ulong dirId, string name) => GetDirectory(dirId).Find(name);

    /// <summary>Dosyanın veri okuyucusu; sıkıştırılmış dosyalarda içerik okundukça açılır.</summary>
    public IDataReader OpenFile(Inode inode)
    {
        if (inode.IsDirectory)
            throw new ApfsException(ApfsErrorCode.InvalidArgument, CoreText.T("Volume.DirectoryAsFile"));
        if (inode.IsCompressed)
            return Compression.Decmpfs.Open(this, inode);
        var extents = inode.HasDataStream ? GetExtents(inode.PrivateId) : [];
        return new FileDataReader(this, extents, inode.DataSize);
    }

    /// <summary>
    /// Dosyanın kullanıcıya görünen boyutu. Sıkıştırılmış dosyada açılmış boyut: inode'daki uncompressed_size
    /// (INODE_HAS_UNCOMPRESSED_SIZE varsa) veya decmpfs başlığı. Okunamazsa 0 (dosya listede yine görünür).
    /// </summary>
    public ulong GetFileSize(Inode inode)
    {
        if (!inode.IsCompressed) return inode.DataSize;
        if (inode.HasUncompressedSize) return inode.UncompressedSize;
        try
        {
            var attr = ReadXattr(inode.Id, Compression.Decmpfs.AttrName, maxSize: 64 * 1024);
            return attr is null ? 0 : Compression.Decmpfs.ParseHeader(attr).UncompressedSize;
        }
        catch (ApfsException e) when (e.Code is not ApfsErrorCode.IoError)
        {
            return 0;
        }
    }

    /// <summary>Veri akışında saklanan bir özniteliğin (ör. kaynak çatalı) okuyucusu; gömülü öznitelikler için bellek okuyucusu.</summary>
    public IDataReader? OpenXattrStream(ulong id, string name)
    {
        foreach (var (n, flags, data) in ListXattrs(id))
        {
            if (!string.Equals(n, name, StringComparison.Ordinal)) continue;
            if ((flags & XattrDataStream) != 0)
            {
                if (data.Length < 16) throw ApfsException.Corrupt(CoreText.T("Volume.XattrDstreamShort"));
                return new FileDataReader(this, GetExtents(Bin.U64(data, 0)), Bin.U64(data, 8));
            }
            if ((flags & XattrDataEmbedded) != 0) return new EmbeddedReader(data);
            throw ApfsException.Corrupt(CoreText.T("Volume.XattrUnknownStorage", name));
        }
        return null;
    }

    private sealed class EmbeddedReader(byte[] data) : IDataReader
    {
        public ulong Length => (ulong)data.Length;
        public int Read(ulong offset, Span<byte> buffer)
        {
            if (offset >= (ulong)data.Length) return 0;
            int n = (int)Math.Min((ulong)buffer.Length, (ulong)data.Length - offset);
            data.AsSpan((int)offset, n).CopyTo(buffer);
            return n;
        }
    }

    internal ObjectReader Reader => _container.Reader;

    // ---- Tam denetleyicinin erişimi (okuma yolu kullanmaz) ----

    /// <summary>apfs_superblock_t ham baytları.</summary>
    internal byte[] RawSuperblock { get; }
    internal ObjectMap Omap => _omap;
    internal BTree.BTree FsTree => _fsTree;
    internal ulong ExtentRefTreeOid => Bin.U64(RawSuperblock, 144);
    internal uint ExtentRefTreeType => Bin.U32(RawSuperblock, 120);
    internal ulong SnapMetaTreeOid => Bin.U64(RawSuperblock, 152);
    internal uint SnapMetaTreeType => Bin.U32(RawSuperblock, 124);
    internal ulong NextObjId => Bin.U64(RawSuperblock, 176);
    internal ulong NumOtherFsObjects => Bin.U64(RawSuperblock, 208);
    internal ulong NumSnapshots => Bin.U64(RawSuperblock, 216);

    /// <summary>Dosya sistemi ağacının bütün kayıtları, anahtar sırasıyla.</summary>
    internal IEnumerable<BTree.BTreeEntry> AllRecords() => _fsTree.Scan(_ => 0);
}
