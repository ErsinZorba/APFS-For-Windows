using System.Buffers.Binary;
using Apfs.Container;
using Apfs.Util;

namespace Apfs.Write;

internal delegate int KeyComparer(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b);

/// <summary>
/// Bir B-ağacının biçimi: alt tür, saklama türü (fiziksel/sanal/geçici), sabit anahtar/değer boyutları ve sıralama.
/// Sabit boyutlu ağaçlarda (omap, boş kuyruk) içindekiler tablosu düğümün alabileceği en fazla kayıt kadar önceden ayrılır ve
/// kayıtlar 8 bayta hizalıdır; değişken boyutlu ağaçlarda tablo 16 kayıtlık adımlarla büyür ve kayıtlar sıkışık durur.
/// </summary>
internal sealed record TreeShape(uint Subtype, uint StorageFlag, int FixedKeySize, int FixedValSize, KeyComparer Compare, int BlockSize)
{
    public bool IsFixed => FixedKeySize > 0;
    public const int HeaderSize = ApfsConstants.BTreeNodeHeaderSize; // 56
    public const int FooterSize = ApfsConstants.BTreeInfoSize;       // 40
    public const int TocIncrement = 16;
    public const ushort NoOffset = 0xFFFF;

    /// <summary>Sabit boyutlu ağaçta düğümün içindekiler tablosundaki kayıt yeri sayısı (apfsck node_min_table_size ile aynı).</summary>
    public int FixedCapacity(bool leaf) =>
        (BlockSize - HeaderSize) / (FixedKeySize + (leaf ? FixedValSize : 8) + 4);
}

/// <summary>Bellekteki değiştirilebilir B-ağacı düğümü.</summary>
internal sealed class MNode
{
    /// <summary>Diskteki kimlik: fiziksel ağaçta blok adresi (henüz yazılmamışsa 0), sanal/geçici ağaçta nesne kimliği.</summary>
    public ulong Oid;
    public int Level;
    public bool Dirty;
    public readonly List<byte[]> Keys = [];
    /// <summary>Değerler; yaprakta null = hayalet kayıt. İç düğümde 8 baytlık çocuk kimliği.</summary>
    public readonly List<byte[]?> Vals = [];
    /// <summary>İç düğümde yüklenmiş çocuklar (Keys ile aynı sırada; yüklenmemişse null).</summary>
    public readonly List<MNode?> Children = [];
    /// <summary>Kökte btree_info_t (40 bayt); diğerlerinde null.</summary>
    public byte[]? Footer;

    public bool IsLeaf => Level == 0;
    public int Count => Keys.Count;
}

/// <summary>B-ağacı düğümünü diskteki biçimden okur ve diskteki biçime yazar (btree_node_phys_t).</summary>
internal static class NodeCodec
{
    public static MNode Parse(byte[] b, TreeShape shape, bool isRoot)
    {
        ushort flags = Bin.U16(b, 32);
        int level = Bin.U16(b, 34);
        int n = (int)Bin.U32(b, 36);
        int tocOff = Bin.U16(b, 40), tocLen = Bin.U16(b, 42);
        bool root = (flags & ApfsConstants.BtNodeRoot) != 0;
        bool leaf = (flags & ApfsConstants.BtNodeLeaf) != 0;
        bool fixedKv = (flags & ApfsConstants.BtNodeFixedKvSize) != 0;
        if (root != isRoot) throw ApfsException.Corrupt(CoreText.T("Codec.RootFlagMismatch"));
        if (leaf != (level == 0)) throw ApfsException.Corrupt(CoreText.T("Codec.LeafFlagLevelMismatch"));
        if (fixedKv != shape.IsFixed) throw ApfsException.Corrupt(CoreText.T("Codec.FixedFlagMismatch"));
        if (level >= BTree.BTree.MaxDepth) throw ApfsException.Corrupt(CoreText.T("Codec.InvalidLevel"));
        int tocStart = TreeShape.HeaderSize + tocOff;
        int keyStart = tocStart + tocLen;
        int valEnd = b.Length - (root ? TreeShape.FooterSize : 0);
        int entry = fixedKv ? 4 : 8;
        if (keyStart > valEnd || (long)n * entry > tocLen) throw ApfsException.Corrupt(CoreText.T("Codec.InvalidToc"));

        var node = new MNode { Oid = Bin.U64(b, 8), Level = level };
        for (int i = 0; i < n; i++)
        {
            int t = tocStart + i * entry;
            int kOff, kLen, vOff, vLen;
            if (fixedKv)
            {
                kOff = Bin.U16(b, t); vOff = Bin.U16(b, t + 2);
                kLen = shape.FixedKeySize; vLen = leaf ? shape.FixedValSize : 8;
            }
            else
            {
                kOff = Bin.U16(b, t); kLen = Bin.U16(b, t + 2); vOff = Bin.U16(b, t + 4); vLen = Bin.U16(b, t + 6);
            }
            if (keyStart + kOff + kLen > valEnd) throw ApfsException.Corrupt(CoreText.T("Codec.KeyOutOfBounds"));
            node.Keys.Add(b.AsSpan(keyStart + kOff, kLen).ToArray());
            if (vOff == TreeShape.NoOffset)
            {
                if (!leaf) throw ApfsException.Corrupt(CoreText.T("Codec.GhostInIndex"));
                node.Vals.Add(null);
            }
            else
            {
                int vStart = valEnd - vOff;
                if (vStart < keyStart || vStart + vLen > valEnd) throw ApfsException.Corrupt(CoreText.T("Codec.ValueOutOfBounds"));
                if (!leaf && vLen != 8) throw ApfsException.Corrupt(CoreText.T("Codec.IndexValueNot8"));
                node.Vals.Add(b.AsSpan(vStart, vLen).ToArray());
            }
            node.Children.Add(null);
        }
        if (root) node.Footer = b.AsSpan(b.Length - TreeShape.FooterSize).ToArray();
        return node;
    }

    private static int TocLength(MNode node, TreeShape shape) =>
        shape.IsFixed
            ? shape.FixedCapacity(node.IsLeaf) * 4
            : (Math.Max(node.Count, 1) + TreeShape.TocIncrement - 1) / TreeShape.TocIncrement * TreeShape.TocIncrement * 8;

    private static int DataLength(MNode node)
    {
        int sum = 0;
        for (int i = 0; i < node.Count; i++) sum += node.Keys[i].Length + (node.Vals[i]?.Length ?? 0);
        return sum;
    }

    /// <summary>Düğüm bir bloğa sığıyor mu (kökte 40 baytlık özet de hesaba katılır).</summary>
    public static bool Fits(MNode node, TreeShape shape, bool isRoot)
    {
        if (shape.IsFixed && node.Count > shape.FixedCapacity(node.IsLeaf)) return false;
        int avail = shape.BlockSize - TreeShape.HeaderSize - (isRoot ? TreeShape.FooterSize : 0);
        return TocLength(node, shape) + DataLength(node) <= avail;
    }

    /// <summary>Düğümü diskteki biçime yazar ve sağlama toplamını hesaplar.</summary>
    public static byte[] Serialize(MNode node, TreeShape shape, bool isRoot, ulong oid, ulong xid)
    {
        if (!Fits(node, shape, isRoot)) throw new InvalidOperationException(CoreText.T("Codec.NodeTooBig"));
        var b = new byte[shape.BlockSize];
        var s = b.AsSpan();
        BinaryPrimitives.WriteUInt64LittleEndian(s[8..], oid);
        BinaryPrimitives.WriteUInt64LittleEndian(s[16..], xid);
        uint objType = (isRoot ? ApfsConstants.TypeBTree : ApfsConstants.TypeBTreeNode) | shape.StorageFlag;
        BinaryPrimitives.WriteUInt32LittleEndian(s[24..], objType);
        BinaryPrimitives.WriteUInt32LittleEndian(s[28..], shape.Subtype);

        ushort flags = (ushort)((isRoot ? ApfsConstants.BtNodeRoot : 0) | (node.IsLeaf ? ApfsConstants.BtNodeLeaf : 0)
            | (shape.IsFixed ? ApfsConstants.BtNodeFixedKvSize : 0));
        BinaryPrimitives.WriteUInt16LittleEndian(s[32..], flags);
        BinaryPrimitives.WriteUInt16LittleEndian(s[34..], (ushort)node.Level);
        BinaryPrimitives.WriteUInt32LittleEndian(s[36..], (uint)node.Count);
        int tocLen = TocLength(node, shape);
        BinaryPrimitives.WriteUInt16LittleEndian(s[40..], 0);               // table_space.off
        BinaryPrimitives.WriteUInt16LittleEndian(s[42..], (ushort)tocLen);  // table_space.len

        int keyStart = TreeShape.HeaderSize + tocLen;
        int valEnd = shape.BlockSize - (isRoot ? TreeShape.FooterSize : 0);
        int kPos = 0, vPos = 0;
        for (int i = 0; i < node.Count; i++)
        {
            var key = node.Keys[i];
            var val = node.Vals[i];
            if (shape.IsFixed && key.Length != shape.FixedKeySize) throw new InvalidOperationException(CoreText.T("Codec.FixedKeySizeMismatch"));
            key.CopyTo(s[(keyStart + kPos)..]);
            ushort vOffField = TreeShape.NoOffset;
            if (val is not null)
            {
                vPos += val.Length;
                val.CopyTo(s[(valEnd - vPos)..]);
                vOffField = (ushort)vPos;
            }
            int t = TreeShape.HeaderSize + i * (shape.IsFixed ? 4 : 8);
            if (shape.IsFixed)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(s[t..], (ushort)kPos);
                BinaryPrimitives.WriteUInt16LittleEndian(s[(t + 2)..], vOffField);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(s[t..], (ushort)kPos);
                BinaryPrimitives.WriteUInt16LittleEndian(s[(t + 2)..], (ushort)key.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(s[(t + 4)..], vOffField);
                BinaryPrimitives.WriteUInt16LittleEndian(s[(t + 6)..], (ushort)(val?.Length ?? 0));
            }
            kPos += key.Length;
        }
        int freeLen = valEnd - vPos - (keyStart + kPos);
        BinaryPrimitives.WriteUInt16LittleEndian(s[44..], (ushort)kPos);     // free_space.off (anahtar alanının başından)
        BinaryPrimitives.WriteUInt16LittleEndian(s[46..], (ushort)freeLen);  // free_space.len
        BinaryPrimitives.WriteUInt16LittleEndian(s[48..], TreeShape.NoOffset); // key_free_list: boş
        BinaryPrimitives.WriteUInt16LittleEndian(s[50..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(s[52..], TreeShape.NoOffset); // val_free_list: boş
        BinaryPrimitives.WriteUInt16LittleEndian(s[54..], 0);

        if (isRoot)
        {
            var footer = node.Footer ?? throw new InvalidOperationException(CoreText.T("Codec.RootNoFooter"));
            footer.CopyTo(s[(shape.BlockSize - TreeShape.FooterSize)..]);
        }
        BinaryPrimitives.WriteUInt64LittleEndian(s, Fletcher64.Compute(b));
        return b;
    }
}
