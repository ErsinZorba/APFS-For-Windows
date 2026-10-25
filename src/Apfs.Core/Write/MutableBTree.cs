using System.Buffers.Binary;
using Apfs.Util;

namespace Apfs.Write;

/// <summary>
/// Değiştirilebilir B-ağacı. Düğümler ihtiyaç oldukça yüklenir ve bellekte değiştirilir; diske yazma <see cref="FlushPhysical"/>,
/// <see cref="FlushVirtual"/> veya <see cref="FlushEphemeral"/> ile işlem sonunda yapılır (kopyala-yaz: hiçbir düğüm yerinde
/// değiştirilmez). Kurallar (apfsck ile aynı): yaprak olmayan düğüm boş kalmaz; iç düğümdeki anahtar çocuğunun ilk anahtarıdır;
/// kökteki özet (kayıt sayısı, düğüm sayısı, en uzun anahtar/değer) her zaman doğrudur.
/// Fiziksel ağaçta bir düğümün değişmesi köküne kadar bütün atalarını da değiştirir (kimlikleri blok adresidir); sanal ve geçici
/// ağaçlarda kimlikler sabittir, yalnızca değişen düğüm yeniden yazılır.
/// </summary>
internal sealed class MutableBTree
{
    private readonly TreeShape _shape;
    private readonly Func<ulong, bool, byte[]> _load;
    private readonly Func<ulong>? _newOid;
    private readonly bool _physical;
    private MNode _root;
    private readonly List<ulong> _deletedOids = [];

    /// <param name="load">(kimlik, kök mü) → doğrulanmış blok.</param>
    /// <param name="newOid">Sanal/geçici ağaçta yeni düğüme kimlik verir; fiziksel ağaçta null (kimlik yazarken verilir).</param>
    public MutableBTree(TreeShape shape, ulong rootOid, Func<ulong, bool, byte[]> load, Func<ulong>? newOid)
    {
        _shape = shape;
        _load = load;
        _newOid = newOid;
        _physical = newOid is null;
        _root = NodeCodec.Parse(load(rootOid, true), shape, isRoot: true);
        _root.Oid = rootOid;
    }

    public TreeShape Shape => _shape;
    public ulong RootOid => _root.Oid;
    public bool IsDirty => _root.Dirty;
    public ulong KeyCount => Bin.U64(_root.Footer!, 24);
    public ulong NodeCount => Bin.U64(_root.Footer!, 32);

    /// <summary>Silinen (diskte daha önce var olan) düğümlerin kimlikleri; yazılırken serbest bırakılır.</summary>
    public IReadOnlyList<ulong> DeletedOids => _deletedOids;

    // ---------------- Okuma ----------------

    private MNode Child(MNode n, int i)
    {
        if (n.Children[i] is { } c) return c;
        ulong oid = Bin.U64(n.Vals[i]!, 0);
        var child = NodeCodec.Parse(_load(oid, false), _shape, isRoot: false);
        child.Oid = oid;
        if (child.Level != n.Level - 1) throw ApfsException.Corrupt("B-ağacı düğüm seviyesi tutarsız");
        if (child.Count == 0) throw ApfsException.Corrupt("boş B-ağacı düğümü");
        n.Children[i] = child;
        return child;
    }

    /// <summary>İç düğümde anahtarın gireceği çocuk: ilk anahtarı anahtardan büyük olmayan son çocuk (yoksa 0).</summary>
    private int ChildIndex(MNode n, ReadOnlySpan<byte> key)
    {
        int lo = 0, hi = n.Count - 1, ans = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (_shape.Compare(n.Keys[mid], key) <= 0) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans;
    }

    /// <summary>Yaprakta ikili arama: bulunduysa indeks, bulunmadıysa ~(ekleme yeri).</summary>
    private int LeafSearch(MNode n, ReadOnlySpan<byte> key)
    {
        int lo = 0, hi = n.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int c = _shape.Compare(n.Keys[mid], key);
            if (c == 0) return mid;
            if (c < 0) lo = mid + 1; else hi = mid - 1;
        }
        return ~lo;
    }

    private MNode FindLeaf(ReadOnlySpan<byte> key, List<(MNode Node, int Index)>? path)
    {
        var n = _root;
        while (!n.IsLeaf)
        {
            int i = ChildIndex(n, key);
            path?.Add((n, i));
            n = Child(n, i);
        }
        return n;
    }

    /// <summary>Anahtarın değeri; yoksa false. Hayalet kayıtta değer boş dizidir.</summary>
    public bool TryGet(ReadOnlySpan<byte> key, out byte[] value)
    {
        var leaf = FindLeaf(key, null);
        int i = LeafSearch(leaf, key);
        value = i >= 0 ? leaf.Vals[i] ?? [] : [];
        return i >= 0;
    }

    /// <summary>Anahtardan küçük ya da ona eşit en büyük kayıt (yoksa null). İç düğüm ayraçları çocuğun ilk anahtarı olduğu için
    /// seçilen yaprakta her zaman bulunur.</summary>
    public BTree.BTreeEntry? Floor(ReadOnlySpan<byte> key)
    {
        var leaf = FindLeaf(key, null);
        int i = LeafSearch(leaf, key);
        if (i < 0) i = ~i - 1;
        if (i < 0) return null;
        return new BTree.BTreeEntry(leaf.Keys[i], leaf.Vals[i] ?? []);
    }

    /// <summary>Sıralı tarama; <paramref name="compare"/> okuyucudaki BTree.Scan ile aynı sözleşmededir.</summary>
    public IEnumerable<BTree.BTreeEntry> Scan(Func<ReadOnlySpan<byte>, int> compare, bool includeGhosts = false)
    {
        var results = new List<BTree.BTreeEntry>();
        ScanNode(_root, compare, includeGhosts, results);
        return results;
    }

    private bool ScanNode(MNode n, Func<ReadOnlySpan<byte>, int> compare, bool ghosts, List<BTree.BTreeEntry> results)
    {
        if (n.IsLeaf)
        {
            for (int i = 0; i < n.Count; i++)
            {
                int c = compare(n.Keys[i]);
                if (c < 0) continue;
                if (c > 0) return false;
                if (n.Vals[i] is { } v) results.Add(new BTree.BTreeEntry(n.Keys[i], v));
                else if (ghosts) results.Add(new BTree.BTreeEntry(n.Keys[i], []));
            }
            return true;
        }
        for (int i = 0; i < n.Count; i++)
        {
            if (compare(n.Keys[i]) > 0) return false;
            if (i + 1 < n.Count && compare(n.Keys[i + 1]) < 0) continue;
            if (!ScanNode(Child(n, i), compare, ghosts, results)) return false;
        }
        return true;
    }

    // ---------------- Değiştirme ----------------

    /// <summary>Kaydı ekler; anahtar varsa değerini değiştirir. <paramref name="value"/> null ise hayalet kayıt.</summary>
    public void Put(byte[] key, byte[]? value)
    {
        if (_shape.IsFixed && key.Length != _shape.FixedKeySize) throw new ArgumentException("sabit anahtar boyutu uyuşmuyor");
        var path = new List<(MNode Node, int Index)>();
        var leaf = FindLeaf(key, path);
        int i = LeafSearch(leaf, key);
        if (i >= 0)
        {
            if (Same(leaf.Vals[i], value)) return;
            leaf.Vals[i] = value;
        }
        else
        {
            i = ~i;
            leaf.Keys.Insert(i, key);
            leaf.Vals.Insert(i, value);
            leaf.Children.Insert(i, null);
            AdjustFooter(keys: +1, nodes: 0);
        }
        UpdateLongest(key.Length, value?.Length ?? 0);
        Touch(leaf, path);
        FixSeparators(leaf, path);
        EnsureFits(leaf, path);
    }

    /// <summary>
    /// Yoldaki her düğüm (aşağıdan yukarıya) bloğa sığmalı. Yaprağa eklenen kayıt dışında, değişken boyutlu ağaçta bir ayracın
    /// uzaması da üst düğümü taşırabilir.
    /// </summary>
    private void EnsureFits(MNode leaf, List<(MNode Node, int Index)> path)
    {
        var nodes = new List<MNode>(path.Count + 1);
        foreach (var (n, _) in path) nodes.Add(n);
        nodes.Add(leaf);
        for (int level = nodes.Count - 1; level >= 0; level--)
        {
            var n = nodes[level];
            if (NodeCodec.Fits(n, _shape, ReferenceEquals(n, _root))) continue;
            SplitIfNeeded(n, path.GetRange(0, level));
        }
    }

    /// <summary>Kaydı siler; yoksa false.</summary>
    public bool Delete(ReadOnlySpan<byte> key)
    {
        var path = new List<(MNode Node, int Index)>();
        var leaf = FindLeaf(key, path);
        int i = LeafSearch(leaf, key);
        if (i < 0) return false;
        leaf.Keys.RemoveAt(i);
        leaf.Vals.RemoveAt(i);
        leaf.Children.RemoveAt(i);
        AdjustFooter(keys: -1, nodes: 0);
        Touch(leaf, path);
        RemoveEmpty(leaf, path);
        return true;
    }

    private static bool Same(byte[]? a, byte[]? b) =>
        a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);

    /// <summary>Düğümü değişmiş işaretler; fiziksel ağaçta bütün ataları da (kimlikleri değişecek). Kök her zaman değişir (özet).</summary>
    private void Touch(MNode node, List<(MNode Node, int Index)> path)
    {
        node.Dirty = true;
        if (_physical) foreach (var (p, _) in path) p.Dirty = true;
        _root.Dirty = true;
    }

    /// <summary>İç düğüm anahtarları çocuklarının ilk anahtarıyla aynı kalsın (aşağıdan yukarı).</summary>
    private static void FixSeparators(MNode node, List<(MNode Node, int Index)> path)
    {
        var child = node;
        for (int level = path.Count - 1; level >= 0; level--)
        {
            var (p, idx) = path[level];
            if (child.Count == 0) return;
            if (p.Keys[idx].AsSpan().SequenceEqual(child.Keys[0])) return;
            p.Keys[idx] = child.Keys[0];
            p.Dirty = true;
            child = p;
            if (idx != 0) return; // üstteki ayraç yalnızca ilk çocuk değişince etkilenir
        }
    }

    private void SplitIfNeeded(MNode node, List<(MNode Node, int Index)> path)
    {
        while (true)
        {
            bool isRoot = ReferenceEquals(node, _root);
            if (NodeCodec.Fits(node, _shape, isRoot)) return;
            if (node.Count < 2) throw new InvalidOperationException("tek kayıt düğüme sığmıyor");

            if (isRoot)
            {
                // Kök kimliğini korur: içeriği yeni bir sol çocuğa taşınır, kök bir seviye yükselir.
                var left = NewNode(node.Level);
                Move(node, left, 0, node.Count);
                node.Level = left.Level + 1;
                node.Keys.Add(left.Keys[0]);
                node.Vals.Add(new byte[8]);
                node.Children.Add(left);
                node.Dirty = true;
                path.Insert(0, (node, 0));
                node = left;
                continue;
            }

            var (parent, idx) = path[^1];
            path.RemoveAt(path.Count - 1);
            int half = SplitPoint(node);
            var right = NewNode(node.Level);
            Move(node, right, half, node.Count - half);
            parent.Keys.Insert(idx + 1, right.Keys[0]);
            parent.Vals.Insert(idx + 1, new byte[8]);
            parent.Children.Insert(idx + 1, right);
            parent.Dirty = true;
            node = parent;
        }
    }

    /// <summary>Bayt olarak yaklaşık ortadan böler (iki yarı da en az bir kayıt).</summary>
    private static int SplitPoint(MNode n)
    {
        int total = 0;
        for (int i = 0; i < n.Count; i++) total += n.Keys[i].Length + (n.Vals[i]?.Length ?? 0) + 8;
        int acc = 0;
        for (int i = 0; i < n.Count - 1; i++)
        {
            acc += n.Keys[i].Length + (n.Vals[i]?.Length ?? 0) + 8;
            if (acc * 2 >= total) return Math.Max(1, i + 1);
        }
        return n.Count - 1;
    }

    private static void Move(MNode from, MNode to, int start, int count)
    {
        to.Keys.AddRange(from.Keys.GetRange(start, count));
        to.Vals.AddRange(from.Vals.GetRange(start, count));
        to.Children.AddRange(from.Children.GetRange(start, count));
        from.Keys.RemoveRange(start, count);
        from.Vals.RemoveRange(start, count);
        from.Children.RemoveRange(start, count);
        from.Dirty = true;
        to.Dirty = true;
    }

    private MNode NewNode(int level)
    {
        AdjustFooter(keys: 0, nodes: +1);
        var node = new MNode { Oid = _newOid?.Invoke() ?? 0, Level = level, Dirty = true };
        RegisterCreated(node);
        return node;
    }

    /// <summary>Boşalan kök olmayan düğümü atasından çıkarır; tek çocuklu iç kökü bir seviye indirir.</summary>
    private void RemoveEmpty(MNode node, List<(MNode Node, int Index)> path)
    {
        while (node.Count == 0 && path.Count > 0)
        {
            var (parent, idx) = path[^1];
            path.RemoveAt(path.Count - 1);
            parent.Keys.RemoveAt(idx);
            parent.Vals.RemoveAt(idx);
            parent.Children.RemoveAt(idx);
            parent.Dirty = true;
            Forget(node);
            node = parent;
        }
        if (node.Count > 0)
        {
            FixSeparators(node, path);
            EnsureFits(node, path);
        }

        // Tek çocuklu iç kök: çocuğun içeriğini al (kök kimliği korunur). Kökte 40 baytlık özet olduğu için içerik ancak
        // sığıyorsa birleştirilir; sığmıyorsa kök tek çocuklu kalır (geçerli bir ağaçtır).
        while (!_root.IsLeaf && _root.Count == 1)
        {
            var only = Child(_root, 0);
            var probe = new MNode { Level = only.Level };
            probe.Keys.AddRange(only.Keys);
            probe.Vals.AddRange(only.Vals);
            if (!NodeCodec.Fits(probe, _shape, isRoot: true)) break;
            _root.Keys.Clear(); _root.Vals.Clear(); _root.Children.Clear();
            _root.Keys.AddRange(only.Keys);
            _root.Vals.AddRange(only.Vals);
            _root.Children.AddRange(only.Children);
            _root.Level = only.Level;
            _root.Dirty = true;
            Forget(only);
        }
    }

    private void Forget(MNode node)
    {
        AdjustFooter(keys: 0, nodes: -1);
        if (node.Oid != 0 && !IsNewUnwritten(node)) _deletedOids.Add(node.Oid);
    }

    // Fiziksel ağaçta yeni düğümün kimliği 0'dır; sanal/geçici ağaçta yeni düğümün kimliği vardır ama diskte karşılığı yoktur.
    private readonly HashSet<ulong> _createdOids = [];
    private bool IsNewUnwritten(MNode node) => !_physical && _createdOids.Contains(node.Oid);

    private void AdjustFooter(long keys, long nodes)
    {
        var f = _root.Footer!;
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(24), (ulong)((long)Bin.U64(f, 24) + keys));
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(32), (ulong)((long)Bin.U64(f, 32) + nodes));
        _root.Dirty = true;
    }

    private void UpdateLongest(int keyLen, int valLen)
    {
        var f = _root.Footer!;
        if (keyLen > Bin.U32(f, 16)) BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(16), (uint)keyLen);
        if (valLen > Bin.U32(f, 20)) BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(20), (uint)valLen);
    }

    // ---------------- Yazma ----------------

    /// <summary>
    /// Fiziksel ağacı yazar: değişen her düğüme yeni blok verilir (eski bloğu serbest bırakılır), çocuk adresleri atalara işlenir.
    /// Yeni kök adresini döndürür.
    /// </summary>
    public ulong FlushPhysical(ulong xid, Func<ulong> allocate, Action<ulong> free, Action<ulong, byte[]> write)
    {
        if (!_physical) throw new InvalidOperationException("fiziksel olmayan ağaç");
        foreach (ulong oid in _deletedOids) free(oid);
        _deletedOids.Clear();
        if (_root.Dirty) FlushPhysicalNode(_root, isRoot: true, xid, allocate, free, write);
        return _root.Oid;
    }

    private void FlushPhysicalNode(MNode n, bool isRoot, ulong xid, Func<ulong> allocate, Action<ulong> free, Action<ulong, byte[]> write)
    {
        if (!n.IsLeaf)
            for (int i = 0; i < n.Count; i++)
                if (n.Children[i] is { Dirty: true } c)
                {
                    FlushPhysicalNode(c, isRoot: false, xid, allocate, free, write);
                    BinaryPrimitives.WriteUInt64LittleEndian(n.Vals[i], c.Oid);
                }
                else if (n.Children[i] is { } clean)
                    BinaryPrimitives.WriteUInt64LittleEndian(n.Vals[i], clean.Oid);
        ulong paddr = allocate();
        if (n.Oid != 0) free(n.Oid);
        n.Oid = paddr;
        write(paddr, NodeCodec.Serialize(n, _shape, isRoot, paddr, xid));
        n.Dirty = false;
    }

    /// <summary>
    /// Sanal ağacı yazar: değişen her düğüm yeni bir bloğa yazılır ve <paramref name="relocate"/>(oid, yeni blok) ile nesne haritası
    /// güncellenir (eski blok orada serbest bırakılır). Silinen düğümler <paramref name="remove"/> ile haritadan çıkarılır.
    /// </summary>
    public void FlushVirtual(ulong xid, Func<ulong> allocate, Action<ulong, ulong> relocate, Action<ulong> remove, Action<ulong, byte[]> write)
    {
        if (_physical) throw new InvalidOperationException("sanal olmayan ağaç");
        foreach (ulong oid in _deletedOids) remove(oid);
        _deletedOids.Clear();
        FlushVirtualNode(_root, isRoot: true, xid, allocate, relocate, write);
        _createdOids.Clear();
    }

    private void FlushVirtualNode(MNode n, bool isRoot, ulong xid, Func<ulong> allocate, Action<ulong, ulong> relocate, Action<ulong, byte[]> write)
    {
        if (!n.IsLeaf)
            for (int i = 0; i < n.Count; i++)
            {
                if (n.Children[i] is not { } c) continue;
                BinaryPrimitives.WriteUInt64LittleEndian(n.Vals[i], c.Oid);
                FlushVirtualNode(c, isRoot: false, xid, allocate, relocate, write);
            }
        if (!n.Dirty) return;
        ulong paddr = allocate();
        write(paddr, NodeCodec.Serialize(n, _shape, isRoot, n.Oid, xid));
        relocate(n.Oid, paddr);
        n.Dirty = false;
    }

    /// <summary>
    /// Geçici ağacı (boş kuyruk) yazar: her checkpoint'te bütün düğümler yeniden yazılır. <paramref name="place"/>(oid, blok).
    /// </summary>
    public void FlushEphemeral(ulong xid, Action<ulong, byte[]> place)
    {
        if (_physical) throw new InvalidOperationException("geçici olmayan ağaç");
        _deletedOids.Clear();
        _createdOids.Clear();
        FlushEphemeralNode(_root, isRoot: true, xid, place);
    }

    private void FlushEphemeralNode(MNode n, bool isRoot, ulong xid, Action<ulong, byte[]> place)
    {
        if (!n.IsLeaf)
            for (int i = 0; i < n.Count; i++)
            {
                var c = Child(n, i);
                BinaryPrimitives.WriteUInt64LittleEndian(n.Vals[i], c.Oid);
                FlushEphemeralNode(c, isRoot: false, xid, place);
            }
        place(n.Oid, NodeCodec.Serialize(n, _shape, isRoot, n.Oid, xid));
        n.Dirty = false;
    }

    /// <summary>Bütün düğümleri (yükleyerek) sayar; testler ve denetim için.</summary>
    internal IEnumerable<MNode> AllNodes()
    {
        var stack = new Stack<MNode>();
        stack.Push(_root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            if (!n.IsLeaf) for (int i = n.Count - 1; i >= 0; i--) stack.Push(Child(n, i));
        }
    }

    /// <summary>Yeni bir sanal/geçici düğüm kimliği alındığında (NewNode) diskte karşılığı olmadığını kaydeder.</summary>
    private void RegisterCreated(MNode node)
    {
        if (!_physical) _createdOids.Add(node.Oid);
    }
}
