using Apfs.Util;
using Apfs.Volume;

namespace Apfs.Diagnostics;

/// <summary>
/// Geliştirme: bir yolun dosya sistemi ağacındaki ham kayıtlarını (dizin girdisi, inode, veri akışı, extent, öznitelik…) onaltılık
/// döker. Yazıcının ürettiği kayıtları macOS'un/Linux'un ürettikleriyle karşılaştırmak için. Salt okunur.
/// </summary>
public static class RecordDump
{
    public static IEnumerable<string> Lines(ApfsVolume v, string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var ino = v.LookupComponents(parts, out _);
        var ids = new HashSet<ulong> { ino.Id, ino.PrivateId };
        foreach (var (n, _, data) in v.ListXattrs(ino.Id))
            if (data.Length >= 8 && (v.ListXattrs(ino.Id).First(x => x.Name == n).Flags & ApfsVolume.XattrDataStream) != 0)
                ids.Add(Bin.U64(data, 0));
        var physBlocks = new List<ulong>();
        foreach (var e in v.AllRecords())
        {
            if (JKey.Type(e.Key) == JType.FileExtent && ids.Contains(JKey.ObjId(e.Key)) && e.Value.Length >= 16 && Bin.U64(e.Value, 8) != 0)
                physBlocks.Add(Bin.U64(e.Value, 8));
            ulong id = JKey.ObjId(e.Key);
            var type = JKey.Type(e.Key);
            bool drecHere = type == JType.DirRec && id == ino.ParentId && ApfsVolume.ParseDirRec(e.Key, e.Value)?.FileId == ino.Id;
            if (!ids.Contains(id) && !drecHere) continue;
            yield return $"{type,-11} id {id,-6} anahtar {Convert.ToHexString(e.Key.AsSpan(8))}";
            yield return $"            değer({e.Value.Length}) {Convert.ToHexString(e.Value)}";
        }
        // Dosyanın fiziksel extent kayıtları (extentref ağacı)
        var tree = new BTree.BTree(v.Reader, v.ExtentRefTreeOid, Container.ApfsConstants.TypeBlockRefTree, null);
        foreach (var e in tree.Scan(_ => 0))
        {
            ulong paddr = JKey.ObjId(e.Key);
            ulong len = Bin.U64(e.Value, 0) & 0x0FFFFFFFFFFFFFFF;
            if (!physBlocks.Any(b => b >= paddr && b < paddr + len)) continue;
            yield return $"PhysExt     blok {paddr} uzunluk {len} tür {Bin.U64(e.Value, 0) >> 60} sahip {Bin.U64(e.Value, 8)} başvuru {Bin.I32(e.Value, 16)}";
        }
    }
}
