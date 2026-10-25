using Apfs.Util;

namespace Apfs.Container;

/// <summary>omap_val_t: sanal nesnenin fiziksel konumu.</summary>
public readonly record struct OmapValue(ulong Paddr, uint Flags, uint Size, ulong Xid)
{
    public bool IsEncrypted => (Flags & ApfsConstants.OmapValEncrypted) != 0;
}

/// <summary>Nesne haritası (omap_phys_t): sanal oid + xid → fiziksel adres.</summary>
public sealed class ObjectMap
{
    private readonly BTree.BTree _tree;
    // Her fs-tree düğümü erişiminde omap çözümlemesi gerekir; disk değişmediği için sonuçlar güvenle önbelleğe alınır.
    private readonly LruCache<(ulong Oid, ulong Xid), OmapValue?> _cache = new(65_536);

    public ObjectMap(ObjectReader reader, ulong omapPaddr)
    {
        var omap = reader.ReadObject(omapPaddr, ApfsConstants.TypeOmap);
        Raw = omap;
        TreeType = Bin.U32(omap, 40);
        ulong treeOid = Bin.U64(omap, 48);
        if ((TreeType & ApfsConstants.ObjectTypeFlagsMask) != ApfsConstants.ObjPhysical)
            throw new ApfsException(ApfsErrorCode.Unsupported, "fiziksel olmayan nesne haritası ağacı");
        _tree = new BTree.BTree(reader, treeOid, ApfsConstants.TypeOmap, resolveVirtual: null);
    }

    public uint TreeType { get; }

    /// <summary>omap_phys_t nesnesinin ham baytları.</summary>
    internal byte[] Raw { get; }

    /// <summary>Nesne haritası ağacı (tam denetleyici düğümlerini gezer).</summary>
    internal BTree.BTree Tree => _tree;

    /// <summary>Bir nesne haritası kaydı (omap_key_t + omap_val_t).</summary>
    internal readonly record struct Record(ulong Oid, ulong Xid, uint Flags, uint Size, ulong Paddr);

    /// <summary>Haritadaki bütün kayıtlar, anahtar sırasıyla (önbelleğe alınmaz).</summary>
    internal IEnumerable<Record> AllRecords()
    {
        foreach (var e in _tree.Scan(_ => 0))
        {
            if (e.Key.Length < 16 || e.Value.Length < 16) throw ApfsException.Corrupt("nesne haritası kaydı çok kısa");
            yield return new Record(Bin.U64(e.Key, 0), Bin.U64(e.Key, 8), Bin.U32(e.Value, 0), Bin.U32(e.Value, 4), Bin.U64(e.Value, 8));
        }
    }

    /// <summary>
    /// <paramref name="oid"/> için xid'i <paramref name="maxXid"/>'den büyük olmayan en yeni eşlemeyi döndürür.
    /// </summary>
    public OmapValue? Lookup(ulong oid, ulong maxXid) =>
        _cache.GetOrAdd((oid, maxXid), k => LookupUncached(k.Oid, k.Xid));

    private OmapValue? LookupUncached(ulong oid, ulong maxXid)
    {
        OmapValue? best = null;
        foreach (var e in _tree.Scan(k => Bin.U64(k, 0).CompareTo(oid)))
        {
            ulong xid = Bin.U64(e.Key, 8);
            if (xid > maxXid) continue;
            if (best is null || xid >= best.Value.Xid)
                best = new OmapValue(Bin.U64(e.Value, 8), Bin.U32(e.Value, 0), Bin.U32(e.Value, 4), xid);
        }
        if (best is { } b && (b.Flags & ApfsConstants.OmapValDeleted) != 0) return null;
        return best;
    }

    /// <summary>Bulunamazsa bozuk veri hatası veren sürüm.</summary>
    public OmapValue Resolve(ulong oid, ulong maxXid) =>
        Lookup(oid, maxXid) ?? throw ApfsException.Corrupt($"nesne haritasında bulunamayan nesne {oid}");
}
