using System.Buffers.Binary;
using System.Text;
using Apfs.Container;
using Apfs.Util;
using Apfs.Volume;

namespace Apfs.Write;

/// <summary>
/// Bir birimin yazma tarafı. Dosya sistemi ağacı (sanal), birim nesne haritası (fiziksel) ve extentref ağacı (fiziksel) bellekte
/// değiştirilir; <see cref="Flush"/> commit sırasında kopyala-yaz zincirini yürütür:
/// extentref → fs ağacı düğümleri (yeni bloklara; omap kayıtları güncellenir) → omap ağacı → omap nesnesi → birim süper bloğu
/// (yeni bloğa; kapsayıcı omap'i güncellenir). Birim blok sayacı (apfs_fs_alloc_count) her ayırma/bırakmada güncellenir; birim
/// süper bloğunun kendisi sayılmaz (apfsck ile aynı kural).
/// </summary>
public sealed partial class VolumeWriter
{
    private const ulong ModeDir = 0x4000, ModeFile = 0x8000;
    public const uint DefaultOwner = 99, DefaultGroup = 99; // "bilinmeyen": Mac'te her kullanıcıyla eşleşir
    private const string SoftwareId = "apfs-for-windows 1.2.0";

    private readonly WriteSession _s;
    private byte[] _apsb;
    private ulong _apsbPaddr;
    private byte[] _omapObj;
    private ulong _omapObjPaddr;
    private readonly MutableBTree _omap, _fs, _extRef;
    private bool _dirty, _modifiedByStamped;

    internal VolumeWriter(WriteSession s, ulong oid, ulong apsbPaddr, byte[] apsb, Crypto.VolumeKey? key = null)
    {
        _s = s;
        Oid = oid;
        _apsb = (byte[])apsb.Clone();
        _apsbPaddr = apsbPaddr;
        int bs = (int)s.Reader.BlockSize;
        if ((Bin.U64(_apsb, 264) & 3) != ApfsConstants.ApfsFsUnencrypted)
        {
            // Şifreli birim: yalnız yazılım şifrelemesi, tek anahtar (ONEKEY); kilidi açılmış anahtar şart
            if (key is null) throw new ApfsException(ApfsErrorCode.Encrypted, "şifreli birime anahtarsız yazılmaz; önce kilidi açın");
            if ((Bin.U64(_apsb, 264) & ApfsConstants.ApfsFsOneKey) == 0) throw new ApfsException(ApfsErrorCode.Unsupported, "dosya başına anahtarlı şifreli birime yazılmaz");
            if (key.VolumeUuid != Bin.Uuid(_apsb, 240)) throw new ApfsException(ApfsErrorCode.InvalidArgument, "anahtar bu birime ait değil");
            _key = key.Clone();
            Crypto = _key.CreateCrypto((uint)bs);
        }

        ulong incompat = Bin.U64(_apsb, 56);
        CaseInsensitive = (incompat & ApfsConstants.ApfsIncompatCaseInsensitive) != 0;
        Hashed = CaseInsensitive || (incompat & ApfsConstants.ApfsIncompatNormalizationInsensitive) != 0;

        _omapObjPaddr = Bin.U64(_apsb, 128);
        _omapObj = (byte[])s.LoadPhysical(_omapObjPaddr, ApfsConstants.TypeOmap).Clone();
        _omap = new MutableBTree(new TreeShape(ApfsConstants.TypeOmap, ApfsConstants.ObjPhysical, 16, 16, KeyOrder.Omap, bs),
            Bin.U64(_omapObj, 48), (paddr, _) => s.LoadPhysical(paddr, 0), null);
        // Şifreli birimin fs ağacı düğümleri OBJ_ENCRYPTED taşır (apfsck object.c; Mac İlk Yardım işaretsiz kökü "bozuk" sayar)
        _fs = new MutableBTree(new TreeShape(ApfsConstants.TypeFsTree, ApfsConstants.ObjVirtual | (Crypto is null ? 0 : ApfsConstants.ObjEncrypted), 0, 0, KeyOrder.Fs, bs),
            Bin.U64(_apsb, 136), (oid2, _) => s.LoadVirtual(oid2, ResolveOmap(oid2), Crypto), s.NewOid);
        _extRef = new MutableBTree(new TreeShape(ApfsConstants.TypeBlockRefTree, ApfsConstants.ObjPhysical, 0, 0, KeyOrder.ExtentRef, bs),
            Bin.U64(_apsb, 144), (paddr, _) => s.LoadPhysical(paddr, 0), null);
    }

    public ulong Oid { get; }

    private Crypto.VolumeKey? _key;

    /// <summary>Şifreli birimde şifreleyici (fs ağacı düğümleri ve dosya verisi); şifresiz birimde null.</summary>
    internal Crypto.VolumeCrypto? Crypto { get; }

    /// <summary>Oturum biterken anahtar kopyası sıfırlanır.</summary>
    internal void ReleaseKey()
    {
        _key?.Dispose();
        _key = null;
        Crypto?.Dispose();
    }

    public bool CaseInsensitive { get; }
    /// <summary>Dizin girdisi anahtarları karmalı mı (normalleştirmeye duyarsız birim).</summary>
    public bool Hashed { get; }
    public string Name => Bin.Utf8Z(Bin.Slice(_apsb, 704, 256));
    public bool IsDirty => _dirty;

    // ---------------------------------------------------------------------------------------------------------------
    // Nesne haritası

    private ulong ResolveOmap(ulong oid)
    {
        ulong? found = null;
        foreach (var e in _omap.Scan(k => Bin.U64(k, 0).CompareTo(oid)))
        {
            if (found is not null) throw ApfsException.Corrupt($"nesne {oid} için birden çok omap kaydı");
            if ((Bin.U32(e.Value, 0) & ApfsConstants.OmapValDeleted) != 0) throw ApfsException.Corrupt($"nesne {oid} silinmiş işaretli");
            found = Bin.U64(e.Value, 8);
        }
        return found ?? throw ApfsException.Corrupt($"nesne {oid} birim omap'inde yok");
    }

    /// <summary>Şifreli birimde fs ağacı düğümlerinin omap kaydı OMAP_VAL_ENCRYPTED taşır (apfsck btree.c: iki yönde denetler).</summary>
    private uint NodeFlags => Crypto is null ? 0 : ApfsConstants.OmapValEncrypted;

    private void Relocate(ulong oid, ulong paddr)
    {
        foreach (var e in _omap.Scan(k => Bin.U64(k, 0).CompareTo(oid)).ToList())
        {
            _omap.Delete(e.Key);
            FreeMeta(Bin.U64(e.Value, 8));
        }
        var key = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(key, oid);
        BinaryPrimitives.WriteUInt64LittleEndian(key.AsSpan(8), _s.TransactionXid);
        var val = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(val, NodeFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(val.AsSpan(4), _s.Reader.BlockSize);
        BinaryPrimitives.WriteUInt64LittleEndian(val.AsSpan(8), paddr);
        _omap.Put(key, val);
    }

    /// <summary>
    /// fs ağacı düğümünün yazımı: düğüm düz metin üzerinden mühürlenmiş gelir; şifreli birimde tweak = fiziksel blok ile şifrelenir
    /// (yalnız fs ağacı; omap, extentref ve süper bloklar şifresizdir).
    /// </summary>
    private void WriteFsNode(ulong paddr, byte[] sealedNode)
    {
        if (Crypto is not null)
        {
            sealedNode = (byte[])sealedNode.Clone();
            Crypto.EncryptBlock(sealedNode, paddr);
        }
        _s.QueueWrite(paddr, sealedNode);
    }

    private void RemoveVirtual(ulong oid)
    {
        foreach (var e in _omap.Scan(k => Bin.U64(k, 0).CompareTo(oid)).ToList())
        {
            _omap.Delete(e.Key);
            FreeMeta(Bin.U64(e.Value, 8));
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Blok sayacı

    private void AddAlloc(long delta)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(_apsb.AsSpan(88), (ulong)((long)Bin.U64(_apsb, 88) + delta));
        int counter = delta > 0 ? 224 : 232; // toplam ayrılmış / toplam bırakılmış
        BinaryPrimitives.WriteUInt64LittleEndian(_apsb.AsSpan(counter), Bin.U64(_apsb, counter) + (ulong)Math.Abs(delta));
    }

    internal ulong AllocMeta()
    {
        ulong b = _s.Spaceman.AllocateBlock();
        AddAlloc(+1);
        return b;
    }

    internal void FreeMeta(ulong paddr)
    {
        _s.Spaceman.Free(paddr);
        AddAlloc(-1);
    }

    private void Counter(int offset, long delta) =>
        BinaryPrimitives.WriteUInt64LittleEndian(_apsb.AsSpan(offset), (ulong)((long)Bin.U64(_apsb, offset) + delta));

    private ulong NextObjId()
    {
        ulong id = Bin.U64(_apsb, 176);
        BinaryPrimitives.WriteUInt64LittleEndian(_apsb.AsSpan(176), id + 1);
        return id;
    }

    private bool _dirtyEver;
    private void Touch() { _dirty = true; _dirtyEver = true; }

    // ---- Dosya sistemi ağacındaki her değişikliğin tek geçtiği yer: görüntünün önbelleği burada düşürülür ----

    private ApfsVolume? _view;

    private void FsPut(byte[] key, byte[]? value)
    {
        _fs.Put(key, value);
        if (_view is null) return;
        if (JKey.Type(key) == JType.DirRec && value is not null && ApfsVolume.ParseDirRec(key, value) is { } de)
            _view.SessionDirAdd(JKey.ObjId(key), de);
        else
            _view.InvalidateRecord(JKey.Type(key), JKey.ObjId(key));
    }

    private bool FsDelete(byte[] key)
    {
        bool removed = _fs.Delete(key);
        if (_view is null) return removed;
        if (JKey.Type(key) == JType.DirRec) _view.SessionDirRemove(JKey.ObjId(key), ApfsVolume.DrecKeyName(key));
        else _view.InvalidateRecord(JKey.Type(key), JKey.ObjId(key));
        return removed;
    }

    /// <summary>Klasör dizini: görüntü varsa onun (yerinde güncellenen) önbelleğinden, yoksa ağaçtan kurulur.</summary>
    private DirectoryIndex IndexOf(ulong dirId) =>
        _view?.GetDirectory(dirId) ?? new DirectoryIndex([.. ListDirectory(dirId)], CaseInsensitive);

    /// <summary>
    /// Oturumun güncel görüntüsü: okuyucunun bütün yetenekleri (ad arama, sıkıştırılmış dosya, sembolik bağ, öznitelikler) oturumun
    /// bellekteki ağacı üzerinde çalışır. Veri blokları aygıttan okunur (bu işlemde yazılanlar zaten aygıtta).
    /// </summary>
    public ApfsVolume View(Container.ApfsContainer container)
    {
        if (_view is not null) return _view;
        if (container.Xid != _s.CommittedXid && _dirtyEver)
            throw new InvalidOperationException("görüntü, oturumun ilk değişikliğinden önce kurulmalı");
        var v = _key is null ? ApfsVolume.Open(container, Oid) : ApfsVolume.Open(container, Oid, _key);
        v.UseSessionSource(compare => _fs.Scan(compare));
        return _view = v;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Okuma (oturumun güncel görüntüsü)

    internal InodeRecord? GetInode(ulong id) =>
        _fs.TryGet(Records.Key(id, JType.Inode), out var v) ? InodeRecord.Parse(v) : null;

    private void PutInode(ulong id, InodeRecord ino) => FsPut(Records.Key(id, JType.Inode), ino.Encode());

    /// <summary>Klasördeki girdiler (anahtar sırasıyla).</summary>
    public IReadOnlyList<DirEntry> ListDirectory(ulong dirId)
    {
        var list = new List<DirEntry>();
        foreach (var e in _fs.Scan(k => JKey.ComparePrefix(k, dirId, JType.DirRec)))
            if (ApfsVolume.ParseDirRec(e.Key, e.Value) is { } de) list.Add(de);
        return list;
    }

    /// <summary>Ada göre girdi (okuyucudaki <see cref="DirectoryIndex"/> ile aynı; duyarlı birimde tek adaylı harf farkını da bulur).</summary>
    public DirEntry? FindEntry(ulong dirId, string name) => IndexOf(dirId).Find(name);

    /// <summary>
    /// Bu ad klasörde birimin KATI kuralına göre zaten var mı: harf duyarsız birimde katlanmış ad, normalleştirmeye duyarsız
    /// birimde NFD, tam duyarlı birimde bayt bayt. (Arama kolaylığı burada uygulanmaz; yoksa duyarlı birimde "a" ile "A" birlikte
    /// oluşturulamazdı.)
    /// </summary>
    public DirEntry? FindCollision(ulong dirId, string name) => IndexOf(dirId).FindCollision(name, Hashed);

    // ---------------------------------------------------------------------------------------------------------------
    // Dosya sistemi işlemleri

    public static long NowNs() => (DateTime.UtcNow - DateTime.UnixEpoch).Ticks * 100;

    /// <summary>APFS ad kuralları: boş değil, "." / ".." değil, "/" ve NUL içermez, UTF-8 en fazla 255 bayt.</summary>
    public static void ValidateName(string name)
    {
        if (name.Length == 0 || name is "." or "..") throw new ApfsException(ApfsErrorCode.InvalidArgument, "geçersiz ad");
        if (name.Contains('/') || name.Contains('\0')) throw new ApfsException(ApfsErrorCode.InvalidArgument, "adda geçersiz karakter");
        if (Encoding.UTF8.GetByteCount(name) > 255) throw new ApfsException(ApfsErrorCode.InvalidArgument, "ad çok uzun (en fazla 255 bayt)");
    }

    private InodeRecord RequireDirectory(ulong dirId)
    {
        var dir = GetInode(dirId) ?? throw new ApfsException(ApfsErrorCode.NotFound, $"klasör {dirId}");
        if (!dir.IsDirectory) throw new ApfsException(ApfsErrorCode.InvalidArgument, "klasör değil");
        return dir;
    }

    /// <summary>Yeni klasör oluşturur; kimliğini döndürür.</summary>
    public ulong CreateDirectory(ulong parentId, string name) => CreateNode(parentId, name, isDirectory: true);

    /// <summary>Yeni boş dosya oluşturur; kimliğini döndürür.</summary>
    public ulong CreateFile(ulong parentId, string name) => CreateNode(parentId, name, isDirectory: false);

    private ulong CreateNode(ulong parentId, string name, bool isDirectory)
    {
        ValidateName(name);
        var parent = RequireDirectory(parentId);
        if (FindCollision(parentId, name) is not null) throw new ApfsException(ApfsErrorCode.AlreadyExists, name);

        long now = NowNs();
        ulong id = NextObjId();
        var ino = new InodeRecord
        {
            ParentId = parentId,
            PrivateId = id,
            CreateNs = now, ModNs = now, ChangeNs = now, AccessNs = now,
            InternalFlags = Records.InodeNoRsrcFork,
            ChildrenOrLinks = isDirectory ? 0 : 1,
            Owner = DefaultOwner,
            Group = DefaultGroup,
            Mode = (ushort)(isDirectory ? ModeDir | 0x1ED : ModeFile | 0x1A4), // 0755 / 0644
            // Şifreli birimde dosyalar PROTECTION_CLASS_C (3), klasörler 0: macOS'un USBTEST'te yazdığı gibi (98/98 dosya, 18/18 klasör)
            DefaultProtectionClass = Crypto is not null && !isDirectory ? 3u : 0u,
        };
        ino.Set(Records.XtName, Records.XfDoNotCopy, Records.NameXField(name));
        PutInode(id, ino);
        FsPut(Records.DrecKey(parentId, name, Hashed, CaseInsensitive),
            Records.DrecVal(id, now, isDirectory ? DirEntryType.Directory : DirEntryType.Regular));

        parent.ChildrenOrLinks++;
        parent.ModNs = parent.ChangeNs = now;
        PutInode(parentId, parent);
        Counter(isDirectory ? 192 : 184, +1);
        Touch();
        return id;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Commit

    /// <summary>Kopyala-yaz zinciri; birim süper bloğunun yeni yerini kapsayıcı omap'ine işlemek için (oid, yeni blok) döndürür.</summary>
    internal (ulong Oid, ulong NewPaddr, ulong OldPaddr) Flush(ulong xid)
    {
        int bs = (int)_s.Reader.BlockSize;

        // 1) extentref ağacı (fiziksel)
        if (_extRef.IsDirty)
            BinaryPrimitives.WriteUInt64LittleEndian(_apsb.AsSpan(144), _extRef.FlushPhysical(xid, AllocMeta, FreeMeta, _s.QueueWrite));

        // 2) fs ağacı (sanal): değişen düğümler yeni bloklara, omap kayıtları güncellenir
        _fs.FlushVirtual(xid, AllocMeta, Relocate, RemoveVirtual, WriteFsNode);

        // 3) omap ağacı (fiziksel) ve omap nesnesi (kopyala-yaz)
        if (_omap.IsDirty)
        {
            ulong root = _omap.FlushPhysical(xid, AllocMeta, FreeMeta, _s.QueueWrite);
            ulong newObj = AllocMeta();
            FreeMeta(_omapObjPaddr);
            BinaryPrimitives.WriteUInt64LittleEndian(_omapObj.AsSpan(48), root);
            _omapObjPaddr = newObj;
            _s.QueueWrite(newObj, _s.SealPhysical(_omapObj, newObj, xid));
            BinaryPrimitives.WriteUInt64LittleEndian(_apsb.AsSpan(128), newObj);
        }

        // 4) birim süper bloğu: sayaçlar, değiştiren kaydı, son değişiklik zamanı; yeni bloğa (kapsayıcı omap'ini oturum günceller)
        StampModifiedBy(xid);
        BinaryPrimitives.WriteInt64LittleEndian(_apsb.AsSpan(256), NowNs());
        ulong newApsb = _s.Spaceman.AllocateBlock();
        ulong old = _apsbPaddr;
        _apsbPaddr = newApsb;
        _s.QueueWrite(newApsb, _s.SealVirtual(_apsb, Oid, xid, bs));
        _dirty = false;
        return (Oid, newApsb, old);
    }

    /// <summary>apfs_modified_by: oturumun ilk commit'inde listeyi bir kaydırıp başa kendi kaydımızı ekler; sonra son xid'i günceller.</summary>
    private void StampModifiedBy(ulong xid)
    {
        const int Base = 320, Entry = 48, Count = 8;
        if (!_modifiedByStamped)
        {
            Array.Copy(_apsb, Base, _apsb, Base + Entry, Entry * (Count - 1));
            Array.Clear(_apsb, Base, Entry);
            Encoding.ASCII.GetBytes(SoftwareId).CopyTo(_apsb, Base);
            BinaryPrimitives.WriteInt64LittleEndian(_apsb.AsSpan(Base + 32), NowNs());
            _modifiedByStamped = true;
        }
        BinaryPrimitives.WriteUInt64LittleEndian(_apsb.AsSpan(Base + 40), xid);
    }
}
