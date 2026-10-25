using System.Buffers.Binary;
using Apfs.Container;
using Apfs.IO;
using Apfs.Util;
using Apfs.Write;

namespace Apfs.Tests;

/// <summary>
/// Faz 1.3: değiştirilebilir B-ağacı. Rastgele işlemler bellekteki modelle (SortedDictionary) karşılaştırılır; belli aralıklarla
/// ağaç "diske" yazılır (serbest kalan bloklar hemen yeniden kullanılır), sıfırdan açılır ve hem kendisiyle hem de okuma yolundaki
/// salt okunur BTree ile okunup modelle karşılaştırılır. Düğüm kuralları (boş düğüm yok, ayraç = çocuğun ilk anahtarı, seviyeler,
/// kökteki sayaçlar) her yazımdan sonra denetlenir.
/// </summary>
public class MutableBTreeTests
{
    private const int Bs = 4096;

    // ---- Bellekteki "disk" ----

    private sealed class MemDisk : IBlockDevice
    {
        public readonly byte[] Data;
        public MemDisk(int blocks) => Data = new byte[blocks * Bs];
        public long Length => Data.Length;
        public int SectorSize => 512;
        public void Read(long offset, Span<byte> buffer) => Data.AsSpan((int)offset, buffer.Length).CopyTo(buffer);
        public void Write(ulong block, byte[] b) => b.CopyTo(Data, (int)block * Bs);
        public byte[] Block(ulong block) => Data.AsSpan((int)block * Bs, Bs).ToArray();
        public void Dispose() { }
    }

    /// <summary>Blok ayırıcı: bir yazımda serbest bırakılanlar ancak yazım bittikten sonra yeniden kullanılır (gerçekteki gibi).</summary>
    private sealed class Allocator(ulong start, ulong end)
    {
        private ulong _next = start;
        private readonly Queue<ulong> _reusable = new();
        private readonly List<ulong> _pending = [];
        public readonly HashSet<ulong> Live = [];
        public ulong Allocate()
        {
            ulong b = _reusable.Count > 0 ? _reusable.Dequeue() : _next++;
            if (b >= end) throw new InvalidOperationException("disk doldu");
            Assert.True(Live.Add(b), $"blok {b} iki kez verildi");
            return b;
        }
        public void Free(ulong b)
        {
            Assert.True(Live.Remove(b), $"kullanılmayan blok {b} serbest bırakıldı");
            _pending.Add(b);
        }
        public void EndFlush()
        {
            foreach (var b in _pending) _reusable.Enqueue(b);
            _pending.Clear();
        }
    }

    private static byte[] Footer(uint flags, int keySize, int valSize)
    {
        var f = new byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(f, flags);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(4), Bs);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(8), (uint)keySize);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(12), (uint)valSize);
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(32), 1); // düğüm sayısı
        return f;
    }

    private static readonly TreeShape OmapShape =
        new(ApfsConstants.TypeOmap, ApfsConstants.ObjPhysical, 16, 16, KeyOrder.Omap, Bs);
    private static readonly TreeShape FsShape =
        new(ApfsConstants.TypeFsTree, ApfsConstants.ObjVirtual, 0, 0, KeyOrder.Fs, Bs);
    private static readonly TreeShape FqShape =
        new(ApfsConstants.TypeSpacemanFreeQueue, ApfsConstants.ObjEphemeral, 16, 8, KeyOrder.FreeQueue, Bs);

    private sealed class ByteKeyComparer(KeyComparer c) : IComparer<byte[]>
    {
        public int Compare(byte[]? x, byte[]? y) => c(x, y);
    }

    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void PhysicalFixedTreeMatchesModelAndReadOnlyReader()
    {
        var disk = new MemDisk(60_000);
        var alloc = new Allocator(1, 60_000);
        ulong root = alloc.Allocate();
        disk.Write(root, NodeCodec.Serialize(new MNode { Footer = Footer(0x10, 16, 16) }, OmapShape, true, root, 1));

        var model = new SortedDictionary<byte[], byte[]?>(new ByteKeyComparer(KeyOrder.Omap));
        var rnd = new Random(1234);
        var tree = OpenPhysical(disk, root, OmapShape);
        ulong xid = 1;
        for (int op = 1; op <= 100_000; op++)
        {
            var key = new byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(key, (ulong)rnd.Next(0, 20_000));
            BinaryPrimitives.WriteUInt64LittleEndian(key.AsSpan(8), (ulong)rnd.Next(1, 4));
            if (rnd.Next(3) == 0)
            {
                Assert.Equal(model.Remove(key), tree.Delete(key));
            }
            else
            {
                var val = new byte[16];
                rnd.NextBytes(val);
                tree.Put(key, val);
                model[key] = val;
            }
            if (op % 5_000 == 0)
            {
                xid++;
                root = tree.FlushPhysical(xid, alloc.Allocate, alloc.Free, disk.Write);
                alloc.EndFlush();
                tree = OpenPhysical(disk, root, OmapShape);
                AssertMatches(model, tree.Scan(_ => 0));
                AssertMatches(model, ReadOnlyScan(disk, root, ApfsConstants.TypeOmap, null));
                CheckInvariants(tree, OmapShape, (ulong)model.Count);
                // Yalnızca canlı bloklar ağaçta (serbest bırakılan blok hâlâ kullanılmıyor)
                Assert.Equal(alloc.Live.Count, (int)tree.NodeCount);
            }
        }
    }

    [Fact]
    public void VirtualVariableTreeMatchesModelAndReadOnlyReader()
    {
        var disk = new MemDisk(60_000);
        var alloc = new Allocator(1, 60_000);
        var omap = new Dictionary<ulong, ulong>(); // sanal kimlik → blok
        ulong nextOid = 1000;
        ulong rootOid = nextOid++;
        ulong rootPaddr = alloc.Allocate();
        omap[rootOid] = rootPaddr;
        disk.Write(rootPaddr, NodeCodec.Serialize(new MNode { Footer = Footer(0x40, 0, 0) }, FsShape, true, rootOid, 1));

        var model = new SortedDictionary<byte[], byte[]?>(new ByteKeyComparer(KeyOrder.Fs));
        var rnd = new Random(99);
        Func<ulong> newOid = () => nextOid++;
        MutableBTree Open() => new(FsShape, rootOid, (oid, _) => disk.Block(omap[oid]), newOid);
        var tree = Open();
        ulong xid = 1;
        for (int op = 1; op <= 30_000; op++)
        {
            byte[] key = RandomFsKey(rnd);
            if (rnd.Next(4) == 0 && model.Count > 0)
            {
                // var olan bir kaydı sil (dağılım: rastgele bir anahtar)
                var victim = model.Keys.ElementAt(rnd.Next(model.Count));
                Assert.True(tree.Delete(victim));
                model.Remove(victim);
            }
            else
            {
                var val = new byte[rnd.Next(0, 400)];
                rnd.NextBytes(val);
                tree.Put(key, val);
                model[key] = val;
            }
            if (op % 2_000 == 0)
            {
                xid++;
                tree.FlushVirtual(xid, alloc.Allocate,
                    (oid, paddr) => { if (omap.TryGetValue(oid, out var old)) alloc.Free(old); omap[oid] = paddr; },
                    oid => { alloc.Free(omap[oid]); omap.Remove(oid); },
                    disk.Write);
                alloc.EndFlush();
                Assert.Equal(rootOid, tree.RootOid); // sanal kökün kimliği değişmez
                tree = Open();
                AssertMatches(model, tree.Scan(_ => 0));
                AssertMatches(model, ReadOnlyScan(disk, omap[rootOid], ApfsConstants.TypeFsTree, oid => (omap[oid], false)));
                CheckInvariants(tree, FsShape, (ulong)model.Count);
                Assert.Equal(omap.Count, (int)tree.NodeCount);
            }
        }
    }

    [Fact]
    public void VariableKeysWithHeavyDeletesNeverOverflow()
    {
        // Uzun model testinin yakaladığı hata: ayraç uzaması ve kök birleştirmede taşma. Uzun/kısa anahtarlar karışık, yoğun silme.
        var disk = new MemDisk(60_000);
        var alloc = new Allocator(1, 60_000);
        var omap = new Dictionary<ulong, ulong>();
        ulong nextOid = 1000, rootOid = nextOid++;
        omap[rootOid] = alloc.Allocate();
        disk.Write(omap[rootOid], NodeCodec.Serialize(new MNode { Footer = Footer(0x40, 0, 0) }, FsShape, true, rootOid, 1));
        Func<ulong> newOid = () => nextOid++;
        var tree = new MutableBTree(FsShape, rootOid, (oid, _) => disk.Block(omap[oid]), newOid);
        var model = new SortedDictionary<byte[], byte[]?>(new ByteKeyComparer(KeyOrder.Fs));
        var rnd = new Random(2024);
        ulong xid = 1;
        for (int op = 1; op <= 40_000; op++)
        {
            if (rnd.Next(100) < 45 && model.Count > 0)
            {
                var victim = model.Keys.ElementAt(rnd.Next(model.Count));
                tree.Delete(victim);
                model.Remove(victim);
            }
            else
            {
                // ad uzunluğu 1–250 bayt arası: ayraçlar çok farklı uzunlukta
                ulong id = (ulong)rnd.Next(16, 60);
                string name = new string((char)('a' + rnd.Next(26)), rnd.Next(1, 250)) + rnd.Next(1000);
                var nb = System.Text.Encoding.UTF8.GetBytes(name + " ");
                var k = new byte[12 + nb.Length];
                BinaryPrimitives.WriteUInt64LittleEndian(k, id | (9UL << 60));
                BinaryPrimitives.WriteUInt32LittleEndian(k.AsSpan(8), (ApfsNames.Hash(name, true) << 10) | (uint)nb.Length);
                nb.CopyTo(k, 12);
                var val = new byte[rnd.Next(18, 300)];
                tree.Put(k, val);
                model[k] = val;
            }
            if (op % 2_000 == 0)
            {
                xid++;
                tree.FlushVirtual(xid, alloc.Allocate,
                    (oid, paddr) => { if (omap.TryGetValue(oid, out var old)) alloc.Free(old); omap[oid] = paddr; },
                    oid => { alloc.Free(omap[oid]); omap.Remove(oid); },
                    disk.Write);
                alloc.EndFlush();
                tree = new MutableBTree(FsShape, rootOid, (oid, _) => disk.Block(omap[oid]), newOid);
                AssertMatches(model, tree.Scan(_ => 0));
                CheckInvariants(tree, FsShape, (ulong)model.Count);
            }
        }
    }

    [Fact]
    public void EphemeralTreeWithGhostsRoundTrips()
    {
        var blocks = new Dictionary<ulong, byte[]>();
        ulong nextOid = 5000, rootOid = nextOid++;
        blocks[rootOid] = NodeCodec.Serialize(new MNode { Footer = Footer(0x0C, 16, 8) }, FqShape, true, rootOid, 1);
        var tree = new MutableBTree(FqShape, rootOid, (oid, _) => blocks[oid], () => nextOid++);
        var model = new SortedDictionary<byte[], byte[]?>(new ByteKeyComparer(KeyOrder.FreeQueue));
        var rnd = new Random(7);
        for (int i = 0; i < 5_000; i++)
        {
            var key = new byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(key, (ulong)rnd.Next(1, 50));
            BinaryPrimitives.WriteUInt64LittleEndian(key.AsSpan(8), (ulong)rnd.Next(0, 1_000_000));
            byte[]? val = null;
            if (rnd.Next(2) == 0) { val = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(val, (ulong)rnd.Next(2, 100)); }
            tree.Put(key, val);
            model[key] = val;
        }
        var written = new Dictionary<ulong, byte[]>();
        tree.FlushEphemeral(2, (oid, b) => written[oid] = b);
        var reopened = new MutableBTree(FqShape, rootOid, (oid, _) => written[oid], () => nextOid++);
        var all = reopened.Scan(_ => 0, includeGhosts: true).ToList();
        Assert.Equal(model.Count, all.Count);
        int k = 0;
        foreach (var (key, val) in model)
        {
            Assert.Equal(key, all[k].Key);
            Assert.Equal(val ?? [], all[k].Value);
            k++;
        }
        Assert.Equal(written.Count, (int)reopened.NodeCount);
        CheckInvariants(reopened, FqShape, (ulong)model.Count);
    }

    // ---------------------------------------------------------------------------------------------------------------

    private static MutableBTree OpenPhysical(MemDisk disk, ulong root, TreeShape shape) =>
        new(shape, root, (oid, _) => disk.Block(oid), null);

    private static IEnumerable<Apfs.BTree.BTreeEntry> ReadOnlyScan(MemDisk disk, ulong rootPaddr, uint subtype, Func<ulong, (ulong, bool)>? resolve)
    {
        var reader = new ObjectReader(disk, Bs);
        return new Apfs.BTree.BTree(reader, rootPaddr, subtype, resolve).Scan(_ => 0).ToList();
    }

    private static void AssertMatches(SortedDictionary<byte[], byte[]?> model, IEnumerable<Apfs.BTree.BTreeEntry> actual)
    {
        var list = actual.ToList();
        Assert.Equal(model.Count, list.Count);
        int i = 0;
        foreach (var (k, v) in model)
        {
            Assert.Equal(k, list[i].Key);
            Assert.Equal(v ?? [], list[i].Value);
            i++;
        }
    }

    private static byte[] RandomFsKey(Random rnd)
    {
        ulong id = (ulong)rnd.Next(16, 400);
        int kind = rnd.Next(3);
        if (kind == 0) // inode
        {
            var k = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(k, id | (3UL << 60));
            return k;
        }
        if (kind == 1) // karmalı dizin girdisi
        {
            string name = $"dosya-{rnd.Next(0, 5000)}-{new string('x', rnd.Next(0, 60))}";
            var nb = System.Text.Encoding.UTF8.GetBytes(name + "\0");
            var k = new byte[12 + nb.Length];
            BinaryPrimitives.WriteUInt64LittleEndian(k, id | (9UL << 60));
            BinaryPrimitives.WriteUInt32LittleEndian(k.AsSpan(8), (ApfsNames.Hash(name, true) << 10) | (uint)nb.Length);
            nb.CopyTo(k, 12);
            return k;
        }
        var e = new byte[16]; // extent
        BinaryPrimitives.WriteUInt64LittleEndian(e, id | (8UL << 60));
        BinaryPrimitives.WriteUInt64LittleEndian(e.AsSpan(8), (ulong)rnd.Next(0, 1000) * 4096);
        return e;
    }

    /// <summary>Düğüm kuralları: kök dışında boş düğüm yok, seviyeler tutarlı, ayraç = çocuğun ilk anahtarı, kayıtlar sıralı, sayaçlar doğru.</summary>
    private static void CheckInvariants(MutableBTree tree, TreeShape shape, ulong expectedKeys)
    {
        Assert.Equal(expectedKeys, tree.KeyCount);
        ulong nodes = 0, keys = 0;
        void Walk(MNode n, bool isRoot)
        {
            nodes++;
            if (!isRoot) Assert.True(n.Count > 0, "boş düğüm");
            Assert.True(NodeCodec.Fits(n, shape, isRoot));
            for (int i = 1; i < n.Count; i++) Assert.True(shape.Compare(n.Keys[i - 1], n.Keys[i]) < 0, "sırasız kayıt");
            if (n.IsLeaf) { keys += (ulong)n.Count; return; }
            for (int i = 0; i < n.Count; i++)
            {
                var c = n.Children[i]!;
                Assert.Equal(n.Level - 1, c.Level);
                Assert.Equal(n.Keys[i], c.Keys[0]);
                Walk(c, false);
            }
        }
        _ = tree.AllNodes().Count(); // bütün düğümleri yükle
        Walk(tree.AllNodes().First(), true);
        Assert.Equal(expectedKeys, keys);
        Assert.Equal(tree.NodeCount, nodes);
    }
}
