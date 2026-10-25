using System.Buffers.Binary;
using System.Text;
using Apfs.Util;
using Apfs.Volume;

namespace Apfs.Write;

/// <summary>
/// Silme, ad değiştirme/taşıma ve nitelikler (Faz 2.3–2.5). apfsck'nin kuralları:
/// <list type="bullet">
/// <item>Her inode adını (NAME alanı) ve üst klasörünü taşır; tek bağlantılı dosyada bunlar dizin girdisiyle aynı olmalı.</item>
/// <item>Sert bağlantılı dosyada her bağlantının kardeş kaydı (SiblingLink: inode+kardeş → üst+ad) ve kardeş haritası
/// (SiblingMap: kardeş → inode) vardır; dizin girdisi SIBLING_ID alanı taşır. Birincil bağlantı en küçük kardeş kimliğidir; inode'un
/// adı/üstü onunkidir. Kardeş kaydı sayısı bağlantı sayısına eşittir (tek bağlantıda kayıt olabilir de olmayabilir de).</item>
/// <item>Paylaşılan veri akışı (klon; dstream refcnt &gt; 1): silmede yalnızca sayı düşer.</item>
/// </list>
/// </summary>
public sealed partial class VolumeWriter
{
    private const byte DrecExtSiblingId = 1;
    public const uint UfHidden = 0x8000;

    /// <summary>Dizin girdisinin diskteki anahtarı ve değeri (girdi gerçek adıyla).</summary>
    private (byte[] Key, byte[] Val) DrecRecord(ulong parentId, DirEntry de)
    {
        var key = Records.DrecKey(parentId, de.Name, Hashed, CaseInsensitive);
        if (!_fs.TryGet(key, out var val)) throw ApfsException.Corrupt(CoreText.T("VolWriter.EntryRecordMissing", de.Name));
        return (key, val);
    }

    private static ulong? SiblingIdOf(byte[] drecVal)
    {
        if (drecVal.Length <= 18) return null;
        foreach (var x in Records.ParseXFields(drecVal.AsSpan(18)))
            if (x.Type == DrecExtSiblingId && x.Data.Length >= 8) return Bin.U64(x.Data, 0);
        return null;
    }

    private static byte[] SiblingLinkKey(ulong inodeId, ulong siblingId)
    {
        var k = Records.Key(inodeId, JType.SiblingLink, 8);
        BinaryPrimitives.WriteUInt64LittleEndian(k.AsSpan(8), siblingId);
        return k;
    }

    private static byte[] SiblingLinkVal(ulong parentId, string name)
    {
        var nb = Encoding.UTF8.GetBytes(name + "\0");
        var v = new byte[10 + nb.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(v, parentId);
        BinaryPrimitives.WriteUInt16LittleEndian(v.AsSpan(8), (ushort)nb.Length);
        nb.CopyTo(v, 10);
        return v;
    }

    /// <summary>Inode'un kardeş kayıtları: (kardeş kimliği, üst, ad), kimlik sırasıyla.</summary>
    private List<(ulong Sibling, ulong Parent, string Name)> Siblings(ulong inodeId)
    {
        var list = new List<(ulong, ulong, string)>();
        foreach (var e in _fs.Scan(k => JKey.ComparePrefix(k, inodeId, JType.SiblingLink)))
            list.Add((Bin.U64(e.Key, 8), Bin.U64(e.Value, 0), Bin.Utf8Z(Bin.Slice(e.Value, 10, Bin.U16(e.Value, 8)))));
        list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return list;
    }

    /// <summary>Inode'un birincil adı/üstü: kardeş varsa en küçük kimlikli kardeşinki.</summary>
    private void SyncPrimaryName(InodeRecord ino, ulong inodeId)
    {
        var sibs = Siblings(inodeId);
        if (sibs.Count == 0) return;
        ino.ParentId = sibs[0].Parent;
        ino.Set(Records.XtName, Records.XfDoNotCopy, Records.NameXField(sibs[0].Name));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Silme

    /// <summary>
    /// <paramref name="parentId"/> içindeki <paramref name="name"/> girdisini siler. Klasör boş olmalıdır. Dosyanın son bağlantısı
    /// silinince inode, öznitelikleri ve (paylaşılmıyorsa) verisi bırakılır.
    /// </summary>
    public void Delete(ulong parentId, string name)
    {
        var parent = RequireDirectory(parentId);
        var de = FindEntry(parentId, name) ?? throw new ApfsException(ApfsErrorCode.NotFound, name);
        var target = GetInode(de.FileId) ?? throw ApfsException.Corrupt(CoreText.T("VolWriter.EntryInodeMissing", de.Name));
        if (target.IsDirectory && target.ChildrenOrLinks != 0) throw new ApfsException(ApfsErrorCode.DirectoryNotEmpty, de.Name);

        var (key, val) = DrecRecord(parentId, de);
        FsDelete(key);
        long now = NowNs();
        parent.ChildrenOrLinks--;
        parent.ModNs = parent.ChangeNs = now;
        PutInode(parentId, parent);

        if (target.IsDirectory)
        {
            DeleteInodeAndAttributes(de.FileId, target);
            Counter(192, -1);
        }
        else if (target.ChildrenOrLinks > 1)
        {
            // Sert bağlantılardan biri: kardeş kaydını ve haritasını sil, birincil adı yenile
            if (SiblingIdOf(val) is ulong sib)
            {
                FsDelete(SiblingLinkKey(de.FileId, sib));
                FsDelete(Records.Key(sib, JType.SiblingMap));
            }
            target.ChildrenOrLinks--;
            target.ChangeNs = now;
            SyncPrimaryName(target, de.FileId);
            PutInode(de.FileId, target);
        }
        else
        {
            if (SiblingIdOf(val) is ulong sib)
            {
                FsDelete(SiblingLinkKey(de.FileId, sib));
                FsDelete(Records.Key(sib, JType.SiblingMap));
            }
            DeleteInodeAndAttributes(de.FileId, target);
            Counter((target.Mode & Inode.ModeTypeMask) switch
            {
                Inode.ModeRegular => 184,
                Inode.ModeSymlink => 200,
                _ => 208,
            }, -1);
        }
        Touch();
    }

    /// <summary>Inode'u, özniteliklerini (akışlarıyla), kalan kardeş kayıtlarını ve kendi veri akışını siler.</summary>
    private void DeleteInodeAndAttributes(ulong id, InodeRecord ino)
    {
        foreach (var e in _fs.Scan(k => JKey.ComparePrefix(k, id, JType.Xattr)).ToList())
        {
            if (e.Value.Length >= 4 + 8 && (Bin.U16(e.Value, 0) & Records.XattrDataStream) != 0)
                DeleteStream(Bin.U64(e.Value, 4));
            FsDelete(e.Key);
        }
        foreach (var (sib, _, _) in Siblings(id))
        {
            FsDelete(SiblingLinkKey(id, sib));
            FsDelete(Records.Key(sib, JType.SiblingMap));
        }
        // Dosya veri akışı: paylaşılıyorsa (klon) yalnızca sayı düşer
        var dsKey = Records.Key(ino.PrivateId, JType.DstreamId);
        if (_fs.TryGet(dsKey, out var ds))
        {
            uint refcnt = Bin.U32(ds, 0);
            if (refcnt > 1) FsPut(dsKey, Records.DstreamIdVal(refcnt - 1));
            else { DeleteStream(ino.PrivateId); FsDelete(dsKey); }
        }
        else DeleteStream(ino.PrivateId); // dstream_id kaydı olmayan (eski/boş) akış
        FsDelete(Records.Key(id, JType.Inode));
    }

    /// <summary>Bir veri akışının bütün extent'lerini siler ve fiziksel bloklarının başvurusunu düşürür.</summary>
    private void DeleteStream(ulong streamId)
    {
        foreach (var x in LoadExtents(streamId))
        {
            DeleteExtent(streamId, x);
            if (x.Phys != 0) DecRef(x.Phys, x.Blocks);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Ad değiştirme / taşıma

    /// <summary>
    /// Girdiyi yeni klasöre/ada taşır. Hedef varsa: <paramref name="replace"/> ise ve türler uyumluysa (dosya→dosya, boş klasör→klasör)
    /// önce silinir; değilse AlreadyExists. Aynı öğeye harf büyüklüğü değişikliği (duyarsız birimde) serbesttir. Klasör kendi altına taşınamaz.
    /// </summary>
    public void Rename(ulong srcParentId, string srcName, ulong dstParentId, string dstName, bool replace)
    {
        ValidateName(dstName);
        var srcParent = RequireDirectory(srcParentId);
        RequireDirectory(dstParentId);
        var de = FindEntry(srcParentId, srcName) ?? throw new ApfsException(ApfsErrorCode.NotFound, srcName);
        var ino = GetInode(de.FileId) ?? throw ApfsException.Corrupt(CoreText.T("VolWriter.EntryInodeMissing", de.Name));

        if (ino.IsDirectory)
            for (ulong p = dstParentId; p != Inode.RootDirParent && p != 0; p = GetInode(p)?.ParentId ?? 0)
                if (p == de.FileId) throw new ApfsException(ApfsErrorCode.InvalidArgument, CoreText.T("VolWriter.MoveIntoSelf"));

        var existing = FindCollision(dstParentId, dstName);
        bool sameItem = existing is not null && existing.FileId == de.FileId && dstParentId == srcParentId;
        if (existing is not null && !sameItem)
        {
            if (!replace) throw new ApfsException(ApfsErrorCode.AlreadyExists, dstName);
            var other = GetInode(existing.FileId)!;
            if (other.IsDirectory != ino.IsDirectory) throw new ApfsException(ApfsErrorCode.AlreadyExists, dstName);
            Delete(dstParentId, existing.Name);
            if (dstParentId == srcParentId) srcParent = GetInode(srcParentId)!;
        }
        if (sameItem && existing!.Name == dstName) return; // değişiklik yok

        var (oldKey, val) = DrecRecord(srcParentId, de);
        FsDelete(oldKey);
        FsPut(Records.DrecKey(dstParentId, dstName, Hashed, CaseInsensitive), val);

        long now = NowNs();
        if (dstParentId != srcParentId)
        {
            srcParent.ChildrenOrLinks--;
            srcParent.ModNs = srcParent.ChangeNs = now;
            PutInode(srcParentId, srcParent);
            var dstParent = GetInode(dstParentId)!;
            dstParent.ChildrenOrLinks++;
            dstParent.ModNs = dstParent.ChangeNs = now;
            PutInode(dstParentId, dstParent);
        }
        else
        {
            srcParent.ModNs = srcParent.ChangeNs = now;
            PutInode(srcParentId, srcParent);
        }

        // Inode'un adı/üstü; sert bağlantıda bu bağlantının kardeş kaydı
        ino = GetInode(de.FileId)!;
        if (SiblingIdOf(val) is ulong sib)
        {
            FsPut(SiblingLinkKey(de.FileId, sib), SiblingLinkVal(dstParentId, dstName));
            SyncPrimaryName(ino, de.FileId);
        }
        else
        {
            ino.ParentId = dstParentId;
            ino.Set(Records.XtName, Records.XfDoNotCopy, Records.NameXField(dstName));
        }
        ino.ChangeNs = now;
        PutInode(de.FileId, ino);
        Touch();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Nitelikler

    /// <summary>Zamanları değiştirir (null olanlar değişmez). APFS zamanları 1970'ten beri nanosaniyedir.</summary>
    public void SetTimes(ulong id, long? createNs, long? modNs, long? changeNs, long? accessNs)
    {
        var ino = GetInode(id) ?? throw new ApfsException(ApfsErrorCode.NotFound, $"inode {id}");
        if (createNs is long c) ino.CreateNs = c;
        if (modNs is long m) ino.ModNs = m;
        if (accessNs is long a) ino.AccessNs = a;
        ino.ChangeNs = changeNs ?? NowNs();
        PutInode(id, ino);
        Touch();
    }

    /// <summary>Gizli (UF_HIDDEN) bayrağını açar/kapatır.</summary>
    public void SetHidden(ulong id, bool hidden)
    {
        var ino = GetInode(id) ?? throw new ApfsException(ApfsErrorCode.NotFound, $"inode {id}");
        uint flags = hidden ? ino.BsdFlags | UfHidden : ino.BsdFlags & ~UfHidden;
        if (flags == ino.BsdFlags) return;
        ino.BsdFlags = flags;
        ino.ChangeNs = NowNs();
        PutInode(id, ino);
        Touch();
    }

    /// <summary>Dosyanın veri akışı paylaşılıyor mu (Mac'te çoğaltılmış/klon dosya).</summary>
    internal bool IsCloned(InodeRecord ino) =>
        _fs.TryGet(Records.Key(ino.PrivateId, JType.DstreamId), out var ds) && Bin.U32(ds, 0) > 1;
}
