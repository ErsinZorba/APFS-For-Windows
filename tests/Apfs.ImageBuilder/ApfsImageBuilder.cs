using System.Text;

namespace Apfs.ImageBuilder;

/// <summary>
/// Test amaçlı APFS kapsayıcı görüntüsü üretir (Apple File System Reference'a göre).
/// Yalnızca okuyucunun ve 7-Zip'in ihtiyaç duyduğu yapılar yazılır: süper bloklar, checkpoint alanı,
/// nesne haritaları, dosya sistemi B-ağaçları ve dosya verisi. Alan yöneticisi (spaceman) yazılmaz.
/// </summary>
public sealed class ApfsImageBuilder
{
    public const uint BlockSize = 4096;
    private const ulong Xid = 1;
    private const ulong DescBase = 1, DescBlocks = 8, DataBase = 9, DataBlocks = 8, FirstFree = 17;
    private static readonly long FixedTimeNs = new DateTimeOffset(2024, 5, 17, 10, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds() * 1_000_000;

    private readonly List<VolumeSpec> _volumes = [];

    public IReadOnlyList<VolumeSpec> Volumes => _volumes;

    public Guid ContainerUuid { get; set; } = Guid.Parse("0D5E1A2B-3C4D-4E5F-8091-A2B3C4D5E6F7");
    /// <summary>Görüntüyü GPT bölüm tablosuyla sar (gerçek disklerdeki gibi).</summary>
    public bool WithGpt { get; set; }
    /// <summary>GPT'li görüntünün sonuna VHD altbilgisi ekle (Windows'a sanal disk olarak takılabilsin). GPT'yi gerektirir.</summary>
    public bool AsVhd { get; set; }
    /// <summary>Kapsayıcının en az bu kadar blok olması (VHD'nin Windows'a takılabilmesi için asgari boyut).</summary>
    public ulong MinBlockCount { get; set; }

    public VolumeSpec AddVolume(string name, bool caseInsensitive = true, ushort role = 0)
    {
        var v = new VolumeSpec(name, caseInsensitive, role);
        _volumes.Add(v);
        return v;
    }

    // ---------------- Yazım ----------------

    private ulong _nextBlock;
    private ulong _nextOid;
    private ulong _nextXattrStreamId = 0x100000; // öznitelik veri akışı kimlikleri (inode numaralarıyla çakışmasın)
    private readonly Dictionary<ulong, byte[]> _meta = [];

    /// <summary>Dosya verisi parçası. Şifreli birimde tüm extent (blok katı) VEK ile, tweak = crypto_id × 8 + birim olarak şifrelenir.</summary>
    private readonly record struct DataPiece(ulong Paddr, FileContent Content, long Offset, long Length, long ExtentBytes, byte[]? Vek, ulong CryptoId);
    private readonly List<DataPiece> _data = [];

    /// <summary>Şifreli birimlerde extent crypto_id = fiziksel blok + bu fark (okuyucunun fiziksel adresi değil crypto_id'yi kullandığını sınamak için).</summary>
    private const ulong CryptoIdOffset = 1_000_000;

    private sealed record EncryptedVolume(VolumeSpec Spec, Guid Uuid, Guid UserUuid, byte[] Vek, byte[] Kek);
    private readonly List<EncryptedVolume> _encrypted = [];
    private byte[]? _currentVek;

    public void Write(string path)
    {
        _nextBlock = FirstFree;
        _nextOid = 1024;
        _nextXattrStreamId = 0x100000;
        _meta.Clear();
        _data.Clear();
        _encrypted.Clear();

        ulong containerOmapPaddr = Alloc();
        var volOids = new List<(ulong Oid, ulong Paddr)>();
        foreach (var vol in _volumes)
        {
            ulong volOid = _nextOid++;
            ulong apsb = WriteVolume(vol, volOid, _volumes.IndexOf(vol));
            volOids.Add((volOid, apsb));
        }

        // Kapsayıcı nesne haritası: birim oid → APSB
        var cRecs = volOids.Select(v => OmapRec(v.Oid, v.Paddr)).ToList();
        ulong cTree = WriteTree(cRecs, fixedKv: true, physical: true, subtype: 0x0B, btFlags: 0x12, out _);
        WriteOmapObject(containerOmapPaddr, cTree);

        (ulong Start, ulong Count) keylocker = _encrypted.Count > 0 ? WriteKeybags() : (0, 0);

        ulong blockCount = Math.Max(_nextBlock + 16, MinBlockCount);
        WriteNxSuperblocks(containerOmapPaddr, blockCount, volOids.Select(v => v.Oid).ToList(), keylocker);
        Flush(path, blockCount);
    }

    /// <summary>
    /// Kapsayıcı keybag'i ('keys': her şifreli birim için VEK kaydı ve birim keybag'inin yeri) ile birim keybag'lerini
    /// ('recs': kullanıcının KEK kaydı ve parola ipucu) yazar. Keybag blokları sağlaması alınıp kapsayıcı/birim UUID'siyle şifrelenir.
    /// </summary>
    private (ulong, ulong) WriteKeybags()
    {
        var containerEntries = new List<(Guid Uuid, ushort Tag, byte[] Data)>();
        foreach (var ev in _encrypted)
        {
            var seed = Encoding.UTF8.GetBytes(ev.Spec.Name);
            var recs = new List<(Guid, ushort, byte[])>
            {
                (ev.UserUuid, 3, BuilderCrypto.KekBlob(ev.UserUuid, ev.Kek, ev.Spec.Password!, Derive("salt", seed, 16), ev.Spec.Iterations, Derive("hs1", seed, 8))),
            };
            if (ev.Spec.PasswordHint is { } hint) recs.Add((ev.Uuid, 4, Encoding.UTF8.GetBytes(hint)));
            ulong recsPaddr = Alloc();
            _meta[recsPaddr] = Keybag(recsPaddr, 0x72656373, recs, BuilderCrypto.Raw(ev.Uuid));

            var range = new byte[16];
            BitConverter.TryWriteBytes(range.AsSpan(0, 8), recsPaddr);
            BitConverter.TryWriteBytes(range.AsSpan(8, 8), 1UL);
            containerEntries.Add((ev.Uuid, 2, BuilderCrypto.VekBlob(ev.Uuid, ev.Vek, ev.Kek, Derive("hs2", seed, 8))));
            containerEntries.Add((ev.Uuid, 3, range));
        }
        ulong keysPaddr = Alloc();
        _meta[keysPaddr] = Keybag(keysPaddr, 0x6B657973, containerEntries, BuilderCrypto.Raw(ContainerUuid));
        return (keysPaddr, 1);
    }

    private static byte[] Keybag(ulong paddr, uint type, List<(Guid Uuid, ushort Tag, byte[] Data)> entries, byte[] xtsKey)
    {
        var w = new BlockWriter(new byte[BlockSize]);
        w.ObjHeader(paddr, Xid, type, 0);
        int pos = 48;
        foreach (var (uuid, tag, data) in entries)
        {
            w.Uuid(pos, uuid);
            w.U16(pos + 16, tag);
            w.U16(pos + 18, (ushort)data.Length);
            w.Bytes(pos + 24, data);
            pos += (24 + data.Length + 15) & ~15;
        }
        w.U16(32, 2);                       // kl_version
        w.U16(34, (ushort)entries.Count);   // kl_nkeys
        w.U32(36, (uint)(pos - 48));        // kl_nbytes
        w.SealChecksum();
        BuilderCrypto.XtsEncrypt(xtsKey, xtsKey, w.Buffer, paddr * (BlockSize / 512));
        return w.Buffer;
    }

    private static byte[] Derive(string label, byte[] seed, int length) =>
        System.Security.Cryptography.SHA256.HashData([.. Encoding.UTF8.GetBytes(label), .. seed]).AsSpan(0, length).ToArray();

    private ulong Alloc() => _nextBlock++;

    private ulong WriteVolume(VolumeSpec vol, ulong volOid, int index)
    {
        ulong apsbPaddr = Alloc();
        ulong omapPaddr = Alloc();
        var volUuid = DeterministicGuid(vol.Name, index);
        _currentVek = null;
        if (vol.Password is not null)
        {
            var seed = Encoding.UTF8.GetBytes(vol.Name);
            var ev = new EncryptedVolume(vol, volUuid, DeterministicGuid("kullanıcı:" + vol.Name, index), Derive("vek", seed, 32), Derive("kek", seed, 32));
            _encrypted.Add(ev);
            _currentVek = ev.Vek;
        }

        var recs = new List<(byte[] Key, byte[] Val)>();
        ulong numFiles = 0, numDirs = 0, numSymlinks = 0;
        ulong nextObjId = 16;

        void Walk(DirSpec dir, ulong parentIno)
        {
            numDirs += dir.Ino == 2 ? 0UL : 1UL;
            recs.Add((JKey(dir.Ino, 3), InodeVal(parentIno, dir.Ino, 0x41ED, (uint)dir.Children.Count, dir.Name.Length == 0 ? "root" : dir.Name, null, dir.Hidden)));
            AddXattrs(recs, dir);
            nextObjId = Math.Max(nextObjId, dir.Ino + 1);
            foreach (var child in dir.Children)
            {
                var target = child is HardLinkSpec h ? h.Target : child;
                byte dtype = target switch { DirSpec => 4, SymlinkSpec => 10, _ => 8 };
                recs.Add((DrecKey(dir.Ino, child.Name, vol.CaseInsensitive), DrecVal(target.Ino, dtype)));
                if (child is HardLinkSpec) continue; // inode kaydı asıl dosyada yazılır
                switch (child)
                {
                    case DirSpec d:
                        Walk(d, dir.Ino);
                        break;
                    case FileSpec f when f.Compression is { } comp:
                    {
                        // decmpfs: veri fork'u boş; içerik öznitelikte veya kaynak çatalında
                        numFiles++;
                        nextObjId = Math.Max(nextObjId, f.Ino + 1);
                        var raw = f.Content.ToArray();
                        recs.Add((JKey(f.Ino, 3), InodeVal(dir.Ino, f.Ino, 0x81A4, (uint)f.LinkCount, f.Name, null, f.Hidden,
                            bsdExtra: 0x20 /* UF_COMPRESSED */, internalExtra: 0x40000 /* HAS_UNCOMPRESSED_SIZE */, uncompressedSize: (ulong)raw.Length)));
                        var decmpfs = DecmpfsWriter.Header(comp.Type, raw.Length, comp.IsResourceFork ? [] : DecmpfsWriter.Inline(comp, raw));
                        if (decmpfs.Length > 3804) throw new InvalidOperationException($"decmpfs özniteliği gömülemeyecek kadar büyük: {f.Name}");
                        f.Xattrs["com.apple.decmpfs"] = decmpfs;
                        AddXattrs(recs, f);
                        if (comp.IsResourceFork)
                        {
                            var fork = DecmpfsWriter.ResourceFork(comp, raw);
                            ulong streamId = _nextXattrStreamId++;
                            nextObjId = Math.Max(nextObjId, streamId + 1);
                            // Not: öznitelik akışı için DSTREAM_ID kaydı yazılmaz; 7-Zip bu kaydı yalnızca bir inode'un hemen
                            // ardında kabul ediyor (gerçek APFS görüntüleriyle uyumlu davranış).
                            foreach (var (logical, length, phys, cryptoId) in AllocateExtents(new BytesContent(fork), 1, explicitHoles: true))
                                recs.Add((ExtentKey(streamId, logical), ExtentVal(length, phys, cryptoId)));
                            recs.Add(XattrStreamRecord(f.Ino, "com.apple.ResourceFork", streamId, (ulong)fork.Length));
                        }
                        break;
                    }
                    case FileSpec f:
                        numFiles++;
                        nextObjId = Math.Max(nextObjId, f.Ino + 1);
                        WriteFileData(f);
                        ulong mapped = f.Extents.Where(x => x.Phys != 0).Aggregate(0UL, (s, x) => s + x.Length);
                        ulong allocated = ((ulong)f.Content.Length + BlockSize - 1) / BlockSize * BlockSize;
                        ulong sparseBytes = allocated > mapped ? allocated - mapped : 0;
                        recs.Add((JKey(f.Ino, 3), InodeVal(dir.Ino, f.Ino, 0x81A4, (uint)f.LinkCount, f.Name, (ulong)f.Content.Length, f.Hidden, sparseBytes)));
                        if (f.Content.Length > 0)
                        {
                            recs.Add((JKey(f.Ino, 6), BitConverter.GetBytes(1u))); // j_dstream_id_val_t.refcnt
                            foreach (var (logical, length, phys, cryptoId) in f.Extents)
                                recs.Add((ExtentKey(f.Ino, logical), ExtentVal(length, phys, cryptoId)));
                        }
                        AddXattrs(recs, f);
                        break;
                    case SymlinkSpec s:
                        numSymlinks++;
                        nextObjId = Math.Max(nextObjId, s.Ino + 1);
                        recs.Add((JKey(s.Ino, 3), InodeVal(dir.Ino, s.Ino, 0xA1ED, 1, s.Name, null, s.Hidden)));
                        s.Xattrs["com.apple.fs.symlink"] = [.. Encoding.UTF8.GetBytes(s.Target), 0];
                        AddXattrs(recs, s, fsOwnedSymlink: true);
                        break;
                }
            }
        }
        Walk(vol.Root, 1);
        recs.Sort((a, b) => CompareFsKeys(a.Key, b.Key));

        // Dosya sistemi ağacı (sanal) ve onun nesne haritası
        var omapRecs = new List<(byte[] Key, byte[] Val)>();
        ulong fsRootOid = WriteTree(recs, fixedKv: false, physical: false, subtype: 0x0E, btFlags: 0, out var virtualNodes, _currentVek);
        foreach (var (oid, paddr) in virtualNodes.OrderBy(n => n.Oid))
            omapRecs.Add(OmapRec(oid, paddr, _currentVek is null ? 0u : 0x4u)); // OMAP_VAL_ENCRYPTED
        ulong omapTree = WriteTree(omapRecs, fixedKv: true, physical: true, subtype: 0x0B, btFlags: 0x12, out _);
        WriteOmapObject(omapPaddr, omapTree);

        // apfs_superblock_t
        var w = new BlockWriter(new byte[BlockSize]);
        w.ObjHeader(volOid, Xid, 0x0000000D, 0);
        w.U32(32, 0x42535041); // 'APSB'
        w.U32(36, (uint)index);
        w.U64(40, 0x2 | 0x4); // özellikler: HARDLINK_MAP_RECORDS | STRICTATIME (bilgi amaçlı)
        w.U64(56, vol.CaseInsensitive ? 0x1UL : 0x8UL); // büyük/küçük harf duyarsız veya normalizasyon duyarsız
        w.U64(64, (ulong)FixedTimeNs); // unmount_time
        w.U64(88, (ulong)_data.Count); // fs_alloc_count (yaklaşık)
        w.U16(96, 0); // meta_crypto: şifresiz
        w.U32(116, 0x00000002); // root_tree_type: BTREE | VIRTUAL
        w.U32(120, 0x40000002); // extentref_tree_type
        w.U32(124, 0x40000002); // snap_meta_tree_type
        w.U64(128, omapPaddr);
        w.U64(136, fsRootOid);
        w.U64(176, nextObjId);
        w.U64(184, numFiles);
        w.U64(192, numDirs);
        w.U64(200, numSymlinks);
        w.Uuid(240, volUuid);
        w.U64(256, (ulong)FixedTimeNs);
        w.U64(264, _currentVek is null ? 0x1UL : 0x8UL); // APFS_FS_UNENCRYPTED; şifreli birimde APFS_FS_ONEKEY
        w.Bytes(272, Encoding.UTF8.GetBytes("apfs-mkimage (test)"));
        var nameBytes = Encoding.UTF8.GetBytes(vol.Name);
        w.Bytes(704, nameBytes.AsSpan(0, Math.Min(nameBytes.Length, 255)));
        w.U16(964, vol.Role);
        w.SealChecksum();
        _meta[apsbPaddr] = w.Buffer;
        return apsbPaddr;
    }

    private static void AddXattrs(List<(byte[] Key, byte[] Val)> recs, EntrySpec e, bool fsOwnedSymlink = false)
    {
        foreach (var (name, data) in e.Xattrs)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var key = new byte[8 + 2 + nameBytes.Length + 1];
            var kw = new BlockWriter(key);
            kw.U64(0, JKeyValue(e.Ino, 4));
            kw.U16(8, (ushort)(nameBytes.Length + 1));
            kw.Bytes(10, nameBytes);
            var val = new byte[4 + data.Length];
            var vw = new BlockWriter(val);
            ushort flags = (ushort)(0x2 | (fsOwnedSymlink && name == "com.apple.fs.symlink" ? 0x4 : 0)); // EMBEDDED (+ FILE_SYSTEM_OWNED)
            vw.U16(0, flags);
            vw.U16(2, (ushort)data.Length);
            vw.Bytes(4, data);
            recs.Add((key, val));
        }
    }

    /// <summary>Veri akışında saklanan öznitelik kaydı (j_xattr_val_t + j_xattr_dstream_t).</summary>
    private static (byte[] Key, byte[] Val) XattrStreamRecord(ulong ino, string name, ulong streamId, ulong size)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var key = new byte[8 + 2 + nameBytes.Length + 1];
        var kw = new BlockWriter(key);
        kw.U64(0, JKeyValue(ino, 4));
        kw.U16(8, (ushort)(nameBytes.Length + 1));
        kw.Bytes(10, nameBytes);
        var val = new byte[4 + 8 + 40];
        var vw = new BlockWriter(val);
        vw.U16(0, 0x1); // XATTR_DATA_STREAM
        vw.U16(2, 48);
        vw.U64(4, streamId);
        vw.U64(12, size);
        vw.U64(20, (size + BlockSize - 1) / BlockSize * BlockSize);
        return (key, val);
    }

    private void WriteFileData(FileSpec f)
    {
        f.Extents.Clear();
        f.Extents.AddRange(AllocateExtents(f.Content, f.Fragments, f.ExplicitHoles));
    }

    /// <summary>Veri için blok ayırır. Parçalı verilerde parçaların arasına boş bir blok bırakılır.</summary>
    private List<(ulong Logical, ulong Length, ulong Phys, ulong CryptoId)> AllocateExtents(FileContent content, int fragments, bool explicitHoles)
    {
        var extents = new List<(ulong Logical, ulong Length, ulong Phys, ulong CryptoId)>();
        long fileBlocks = (content.Length + BlockSize - 1) / BlockSize;
        var ranges = content.DataRanges.ToList();
        long cursor = 0;
        foreach (var (rOff, rLen) in ranges)
        {
            // Aralığı blok sınırlarına genişlet
            long start = rOff / BlockSize * BlockSize;
            long end = Math.Min((rOff + rLen + BlockSize - 1) / BlockSize * BlockSize, fileBlocks * BlockSize);
            if (start < cursor) start = cursor;
            if (start >= end) continue;
            if (start > cursor && explicitHoles)
                extents.Add(((ulong)cursor, (ulong)(start - cursor), 0, 0));
            long blocks = (end - start) / BlockSize;
            int pieces = (int)Math.Clamp(fragments, 1, Math.Max(1, blocks));
            long perPiece = (blocks + pieces - 1) / pieces;
            for (long b = 0; b < blocks; b += perPiece)
            {
                long n = Math.Min(perPiece, blocks - b);
                ulong phys = _nextBlock;
                _nextBlock += (ulong)n;
                long pieceOff = start + b * BlockSize;
                long pieceLen = Math.Min(n * BlockSize, content.Length - pieceOff);
                ulong cryptoId = _currentVek is null ? 0 : phys + CryptoIdOffset;
                _data.Add(new DataPiece(phys, content, pieceOff, pieceLen, n * BlockSize, _currentVek, cryptoId));
                // Extent uzunluğu blok katıdır (APFS'te her zaman); son bloğun fazlası dosya boyutuyla kırpılır.
                extents.Add(((ulong)pieceOff, (ulong)(n * BlockSize), phys, cryptoId));
                if (pieces > 1) _nextBlock++; // parçalar bitişik olmasın
            }
            cursor = end;
        }
        if (explicitHoles && cursor < fileBlocks * BlockSize)
            extents.Add(((ulong)cursor, (ulong)(fileBlocks * BlockSize - cursor), 0, 0));
        return extents;
    }

    // ---------------- B-ağacı yazımı ----------------

    private sealed class NodeSpec
    {
        public int Level;
        public List<(byte[] Key, byte[] Val)> Recs = [];
        public List<NodeSpec> Children = [];
        public ulong Oid, Paddr;
    }

    /// <summary>
    /// Sıralı kayıtlardan çok seviyeli B-ağacı yazar ve kök düğümün oid'ini döndürür (fiziksel ağaçta oid = adres).
    /// </summary>
    private ulong WriteTree(List<(byte[] Key, byte[] Val)> recs, bool fixedKv, bool physical, uint subtype, uint btFlags,
        out List<(ulong Oid, ulong Paddr)> virtualNodes, byte[]? encryptVek = null)
    {
        int keySize = fixedKv ? 16 : 0, valSize = fixedKv ? 16 : 0;
        int nonRootCap = (int)BlockSize - 56;
        int rootCap = nonRootCap - 40;

        int Cost((byte[] Key, byte[] Val) r, bool leaf) => fixedKv
            ? 4 + keySize + (leaf ? valSize : 8)
            : 8 + Align8(r.Key.Length) + Align8(leaf ? r.Val.Length : 8);

        List<NodeSpec> PackLevel(List<(byte[] Key, byte[] Val)> items, int level, List<NodeSpec>? children)
        {
            bool leaf = level == 0;
            int total = items.Sum(r => Cost(r, leaf));
            if (total <= rootCap)
                return [new NodeSpec { Level = level, Recs = items, Children = children ?? [] }];
            int cap = nonRootCap;
            var nodes = new List<NodeSpec>();
            var cur = new NodeSpec { Level = level };
            int used = 0;
            for (int i = 0; i < items.Count; i++)
            {
                int c = Cost(items[i], leaf);
                if (used + c > cap && cur.Recs.Count > 0)
                {
                    nodes.Add(cur);
                    cur = new NodeSpec { Level = level };
                    used = 0;
                }
                cur.Recs.Add(items[i]);
                if (children is not null) cur.Children.Add(children[i]);
                used += c;
            }
            nodes.Add(cur);
            if (nodes.Count == 1)
            {
                // Kök olamayacak kadar dolu tek düğüm: ikiye böl
                var only = nodes[0];
                int half = only.Recs.Count / 2;
                nodes =
                [
                    new NodeSpec { Level = level, Recs = only.Recs[..half], Children = children is null ? [] : only.Children[..half] },
                    new NodeSpec { Level = level, Recs = only.Recs[half..], Children = children is null ? [] : only.Children[half..] },
                ];
            }
            return nodes;
        }

        var levelNodes = PackLevel(recs, 0, null);
        var all = new List<NodeSpec>(levelNodes);
        while (levelNodes.Count > 1)
        {
            var indexRecs = levelNodes.Select(n => (n.Recs[0].Key, Array.Empty<byte>())).ToList();
            levelNodes = PackLevel(indexRecs, levelNodes[0].Level + 1, levelNodes);
            all.AddRange(levelNodes);
        }
        var root = levelNodes[0];

        foreach (var n in all)
        {
            n.Paddr = Alloc();
            n.Oid = physical ? n.Paddr : _nextOid++;
        }

        // Şifreli birimin fs ağacı düğümleri OBJ_ENCRYPTED taşır (apfsck object.c); başka hiçbir nesne taşımaz
        uint storage = (physical ? 0x40000000u : 0u) | (encryptVek is not null ? 0x10000000u : 0u);
        foreach (var n in all)
        {
            bool isRoot = n == root;
            bool leaf = n.Level == 0;
            var w = new BlockWriter(new byte[BlockSize]);
            w.ObjHeader(n.Oid, Xid, (isRoot ? 0x2u : 0x3u) | storage, subtype);
            ushort flags = (ushort)((isRoot ? 0x1 : 0) | (leaf ? 0x2 : 0) | (fixedKv ? 0x4 : 0));
            w.U16(32, flags);
            w.U16(34, (ushort)n.Level);
            w.U32(36, (uint)n.Recs.Count);
            int tocLen = n.Recs.Count * (fixedKv ? 4 : 8);
            w.U16(40, 0);
            w.U16(42, (ushort)tocLen);
            int keyStart = 56 + tocLen;
            int valEnd = (int)BlockSize - (isRoot ? 40 : 0);
            int kpos = 0, vpos = 0;
            for (int i = 0; i < n.Recs.Count; i++)
            {
                var key = n.Recs[i].Key;
                var val = leaf ? n.Recs[i].Val : BitConverter.GetBytes(n.Children[i].Oid);
                w.Bytes(keyStart + kpos, key);
                vpos += fixedKv ? val.Length : Align8(val.Length);
                w.Bytes(valEnd - vpos, val);
                int toc = 56 + i * (fixedKv ? 4 : 8);
                if (fixedKv)
                {
                    w.U16(toc, (ushort)kpos);
                    w.U16(toc + 2, (ushort)vpos);
                }
                else
                {
                    w.U16(toc, (ushort)kpos);
                    w.U16(toc + 2, (ushort)key.Length);
                    w.U16(toc + 4, (ushort)vpos);
                    w.U16(toc + 6, (ushort)val.Length);
                }
                kpos += fixedKv ? key.Length : Align8(key.Length);
            }
            if (keyStart + kpos > valEnd - vpos) throw new InvalidOperationException("B-ağacı düğümü taştı");
            w.U16(44, (ushort)kpos);
            w.U16(46, (ushort)(valEnd - vpos - keyStart - kpos));
            w.U16(48, 0xFFFF); w.U16(50, 0);
            w.U16(52, 0xFFFF); w.U16(54, 0);
            if (isRoot)
            {
                int info = (int)BlockSize - 40;
                w.U32(info, btFlags);
                w.U32(info + 4, BlockSize);
                w.U32(info + 8, (uint)keySize);
                w.U32(info + 12, (uint)valSize);
                w.U32(info + 16, (uint)recs.Select(r => r.Key.Length).DefaultIfEmpty(0).Max());
                w.U32(info + 20, (uint)recs.Select(r => r.Val.Length).DefaultIfEmpty(0).Max());
                w.U64(info + 24, (ulong)recs.Count);
                w.U64(info + 32, (ulong)all.Count);
            }
            w.SealChecksum();
            // Şifreli birimde fs-tree düğümü: sağlama düz metin üzerinden alınır, sonra blok fiziksel adresiyle şifrelenir
            if (encryptVek is not null)
                BuilderCrypto.XtsEncrypt(encryptVek[..16], encryptVek[16..], w.Buffer, n.Paddr * (BlockSize / 512));
            _meta[n.Paddr] = w.Buffer;
        }
        virtualNodes = physical ? [] : all.Select(n => (n.Oid, n.Paddr)).ToList();
        return root.Oid;
    }

    private void WriteOmapObject(ulong paddr, ulong treePaddr)
    {
        var w = new BlockWriter(new byte[BlockSize]);
        w.ObjHeader(paddr, Xid, 0x4000000B, 0);
        w.U32(32, 0);           // om_flags
        w.U32(36, 0);           // om_snap_count
        w.U32(40, 0x40000002);  // om_tree_type: BTREE | PHYSICAL
        w.U32(44, 0x40000002);  // om_snapshot_tree_type
        w.U64(48, treePaddr);
        w.SealChecksum();
        _meta[paddr] = w.Buffer;
    }

    private void WriteNxSuperblocks(ulong omapPaddr, ulong blockCount, List<ulong> fsOids, (ulong Start, ulong Count) keylocker)
    {
        byte[] Make(ulong oid)
        {
            var w = new BlockWriter(new byte[BlockSize]);
            w.ObjHeader(1, Xid, 0x80000001, 0); // NX_SUPERBLOCK | EPHEMERAL, oid = OID_NX_SUPERBLOCK
            w.U32(32, 0x4253584E); // 'NXSB'
            w.U32(36, BlockSize);
            w.U64(40, blockCount);
            w.U64(64, 0x2); // NX_INCOMPAT_VERSION2
            w.Uuid(72, ContainerUuid);
            w.U64(88, _nextOid);
            w.U64(96, Xid + 1);
            w.U32(104, (uint)DescBlocks);
            w.U32(108, (uint)DataBlocks);
            w.U64(112, DescBase);
            w.U64(120, DataBase);
            w.U32(128, 2); // xp_desc_next
            w.U32(132, 0); // xp_data_next
            w.U32(136, 0); // xp_desc_index
            w.U32(140, 2); // xp_desc_len: harita + süper blok
            w.U32(144, 0);
            w.U32(148, 0);
            w.U64(152, SpacemanOid); // nx_spaceman_oid (geçici nesne; yeri checkpoint haritasında)
            w.U64(160, omapPaddr);
            w.U32(180, 100); // nx_max_file_systems
            for (int i = 0; i < fsOids.Count; i++) w.U64(184 + i * 8, fsOids[i]);
            w.U64(1296, keylocker.Start); // nx_keylocker
            w.U64(1304, keylocker.Count);
            w.SealChecksum();
            return w.Buffer;
        }

        _meta[0] = Make(1);
        // Checkpoint alanı: alan yöneticisini eşleyen checkpoint haritası ve ardından süper blok kopyası
        var map = new BlockWriter(new byte[BlockSize]);
        map.ObjHeader(DescBase, Xid, 0x4000000C, 0);
        map.U32(32, 0x1); // CHECKPOINT_MAP_LAST
        map.U32(36, 1);   // cpm_count
        map.U32(40, 0x80000005); // cpm_type: SPACEMAN | EPHEMERAL
        map.U32(44, 0);
        map.U32(48, BlockSize);  // cpm_size
        map.U64(56, 0);          // cpm_fs_oid
        map.U64(64, SpacemanOid);
        map.U64(72, DataBase);   // cpm_paddr: checkpoint veri alanının ilk bloğu
        map.SealChecksum();
        _meta[DescBase] = map.Buffer;
        _meta[DescBase + 1] = Make(1);

        // Alan yöneticisi (yalnızca başlık ve ana aygıt sayaçları; bitmapler yazılmaz): sm_dev[SD_MAIN]
        var sm = new BlockWriter(new byte[BlockSize]);
        sm.ObjHeader(SpacemanOid, Xid, 0x80000005, 0);
        sm.U32(32, BlockSize);           // sm_block_size
        sm.U64(48, blockCount);          // sm_dev[0].sm_block_count
        sm.U64(72, blockCount - Math.Min(blockCount, _nextBlock)); // sm_dev[0].sm_free_count
        sm.SealChecksum();
        _meta[DataBase] = sm.Buffer;
    }

    /// <summary>Alan yöneticisinin geçici (ephemeral) nesne kimliği.</summary>
    private const ulong SpacemanOid = 1022;

    /// <summary>Testler için: üretilen görüntüde alan yöneticisinin bildirdiği boş blok sayısı.</summary>
    public ulong LastFreeBlocks { get; private set; }

    private void Flush(string path, ulong blockCount)
    {
        long containerBytes = (long)blockCount * BlockSize;
        long baseOffset = WithGpt ? Gpt.PartitionStartBytes : 0;
        long total = WithGpt ? Gpt.TotalSize(containerBytes) : containerBytes;

        if (AsVhd && !WithGpt) throw new InvalidOperationException("VHD için GPT gerekli");
        using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        // Görüntü dosyasını NTFS'te seyrek yap: tamamı sıfır olan bölgeler diske hiç yazılmaz (6 GB'lık test dosyası birkaç MB kaplar).
        // VHD hariç: Windows seyrek VHD dosyalarını takmıyor ("sıkıştırılmamış, şifrelenmemiş ve seyrek olmamalı").
        if (OperatingSystem.IsWindows() && !AsVhd) NtfsSparse.Mark(fs);
        LastFreeBlocks = blockCount - Math.Min(blockCount, _nextBlock);
        fs.SetLength(total + (AsVhd ? 512 : 0));
        foreach (var (paddr, block) in _meta)
        {
            fs.Position = baseOffset + (long)paddr * BlockSize;
            fs.Write(block);
        }
        var buf = new byte[1 << 20];
        foreach (var (paddr, content, offset, length, extentBytes, vek, cryptoId) in _data)
        {
            if (vek is not null)
            {
                // Şifreli: extent'in tamamı (blok katı, sondaki dolgu dahil) şifrelenip yazılır; tweak = crypto_id × 8 + birim
                for (long pos = 0; pos < extentBytes; pos += buf.Length)
                {
                    int n = (int)Math.Min(buf.Length, extentBytes - pos);
                    Array.Clear(buf, 0, n);
                    int avail = (int)Math.Clamp(length - pos, 0, n);
                    if (avail > 0) content.Read(offset + pos, buf.AsSpan(0, avail));
                    BuilderCrypto.XtsEncrypt(vek[..16], vek[16..], buf.AsSpan(0, n), cryptoId * (BlockSize / 512) + (ulong)pos / 512);
                    fs.Position = baseOffset + (long)paddr * BlockSize + pos;
                    fs.Write(buf, 0, n);
                }
                continue;
            }
            long done = 0;
            while (done < length)
            {
                int n = (int)Math.Min(buf.Length, length - done);
                content.Read(offset + done, buf.AsSpan(0, n));
                if (buf.AsSpan(0, n).ContainsAnyExcept((byte)0)) // sıfır bölgeler seyrek dosyada zaten sıfır okunur
                {
                    fs.Position = baseOffset + (long)paddr * BlockSize + done;
                    fs.Write(buf, 0, n);
                }
                done += n;
            }
        }
        if (WithGpt) Gpt.Write(fs, containerBytes, ContainerUuid);
        if (AsVhd) Vhd.AppendFooter(fs, total);
    }

    // ---------------- Kayıt kodlama ----------------

    private static int Align8(int n) => (n + 7) & ~7;

    private static ulong JKeyValue(ulong oid, byte type) => (oid & 0x0FFFFFFFFFFFFFFF) | ((ulong)type << 60);
    private static byte[] JKey(ulong oid, byte type) => BitConverter.GetBytes(JKeyValue(oid, type));

    private static (byte[] Key, byte[] Val) OmapRec(ulong oid, ulong paddr, uint flags = 0)
    {
        var k = new byte[16];
        var kw = new BlockWriter(k);
        kw.U64(0, oid);
        kw.U64(8, Xid);
        var v = new byte[16];
        var vw = new BlockWriter(v);
        vw.U32(0, flags);
        vw.U32(4, BlockSize);
        vw.U64(8, paddr);
        return (k, v);
    }

    private static byte[] InodeVal(ulong parent, ulong ino, ushort mode, uint childrenOrLinks, string name, ulong? size, bool hidden,
        ulong sparseBytes = 0, uint bsdExtra = 0, ulong internalExtra = 0, ulong uncompressedSize = 0)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var xf = new List<(byte Type, byte Flags, byte[] Data)> { (4, 0x2, [.. nameBytes, 0]) };
        if (size is > 0)
        {
            var ds = new byte[40];
            var dw = new BlockWriter(ds);
            dw.U64(0, size.Value);
            dw.U64(8, (size.Value + BlockSize - 1) / BlockSize * BlockSize - sparseBytes);
            xf.Add((8, 0x20, ds));
        }
        if (sparseBytes > 0)
            xf.Add((13, 0x20, BitConverter.GetBytes(sparseBytes))); // INO_EXT_TYPE_SPARSE_BYTES
        int dataLen = xf.Sum(x => Align8(x.Data.Length));
        var v = new byte[92 + 4 + xf.Count * 4 + dataLen];
        var w = new BlockWriter(v);
        w.U64(0, parent);
        w.U64(8, ino); // private_id: veri akışı kimliği = inode numarası
        w.U64(16, (ulong)FixedTimeNs);
        w.U64(24, (ulong)FixedTimeNs + 1_000_000_000);
        w.U64(32, (ulong)FixedTimeNs + 1_000_000_000);
        w.U64(40, (ulong)FixedTimeNs + 2_000_000_000);
        w.U64(48, (sparseBytes > 0 ? 0x200UL : 0UL) | internalExtra); // INODE_IS_SPARSE, ...
        w.U32(56, childrenOrLinks);
        w.U32(68, (hidden ? 0x8000u : 0u) | bsdExtra); // bsd_flags: UF_HIDDEN, UF_COMPRESSED
        w.U64(84, uncompressedSize);
        w.U32(72, 501);
        w.U32(76, 20);
        w.U16(80, mode);
        w.U16(92, (ushort)xf.Count);
        w.U16(94, (ushort)dataLen);
        int dpos = 92 + 4 + xf.Count * 4;
        for (int i = 0; i < xf.Count; i++)
        {
            w.U8(96 + i * 4, xf[i].Type);
            w.U8(96 + i * 4 + 1, xf[i].Flags);
            w.U16(96 + i * 4 + 2, (ushort)xf[i].Data.Length);
            w.Bytes(dpos, xf[i].Data);
            dpos += Align8(xf[i].Data.Length);
        }
        return v;
    }

    private static byte[] DrecKey(ulong parent, string name, bool caseInsensitive)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var k = new byte[8 + 4 + nameBytes.Length + 1];
        var w = new BlockWriter(k);
        w.U64(0, JKeyValue(parent, 9));
        uint hash = NameHash(name, caseInsensitive);
        w.U32(8, (hash << 10) | (uint)((nameBytes.Length + 1) & 0x3FF));
        w.Bytes(12, nameBytes);
        return k;
    }

    private static byte[] DrecVal(ulong fileId, byte dtype)
    {
        var v = new byte[18];
        var w = new BlockWriter(v);
        w.U64(0, fileId);
        w.U64(8, (ulong)FixedTimeNs);
        w.U16(16, dtype);
        return v;
    }

    private static byte[] ExtentKey(ulong privateId, ulong logical)
    {
        var k = new byte[16];
        var w = new BlockWriter(k);
        w.U64(0, JKeyValue(privateId, 8));
        w.U64(8, logical);
        return k;
    }

    private static byte[] ExtentVal(ulong length, ulong phys, ulong cryptoId = 0)
    {
        var v = new byte[24];
        var w = new BlockWriter(v);
        w.U64(0, length & 0x00FFFFFFFFFFFFFF);
        w.U64(8, phys);
        w.U64(16, cryptoId); // crypto_id
        return v;
    }

    /// <summary>
    /// Dizin girdisi ad karması: (duyarsızsa büyük/küçük harf katlanmış) NFD ad → UTF-32 → CRC32C, alt 22 bit.
    /// Not: büyük/küçük harf katlaması burada yalnızca ASCII için yapılır; tam Unicode katlaması M3'te eklenecek.
    /// </summary>
    private static uint NameHash(string name, bool caseInsensitive)
    {
        string n = name.Normalize(NormalizationForm.FormD);
        uint crc = 0xFFFFFFFF;
        foreach (var rune in n.EnumerateRunes())
        {
            int cp = rune.Value;
            if (caseInsensitive && cp is >= 'A' and <= 'Z') cp += 32;
            crc = Crc32C.Update(crc, BitConverter.GetBytes(cp));
        }
        return crc & 0x3FFFFF;
    }

    private static int CompareFsKeys(byte[] a, byte[] b)
    {
        ulong ha = BitConverter.ToUInt64(a, 0), hb = BitConverter.ToUInt64(b, 0);
        ulong oa = ha & 0x0FFFFFFFFFFFFFFF, ob = hb & 0x0FFFFFFFFFFFFFFF;
        if (oa != ob) return oa.CompareTo(ob);
        ulong ta = ha >> 60, tb = hb >> 60;
        if (ta != tb) return ta.CompareTo(tb);
        switch (ta)
        {
            case 9: // DIR_REC: karma, sonra ad
                uint hA = BitConverter.ToUInt32(a, 8) >> 10, hB = BitConverter.ToUInt32(b, 8) >> 10;
                if (hA != hB) return hA.CompareTo(hB);
                return a.AsSpan(12).SequenceCompareTo(b.AsSpan(12));
            case 8: // FILE_EXTENT: mantıksal ofset
                return BitConverter.ToUInt64(a, 8).CompareTo(BitConverter.ToUInt64(b, 8));
            case 4: // XATTR: ad
                return a.AsSpan(10).SequenceCompareTo(b.AsSpan(10));
            default:
                return 0;
        }
    }

    private static Guid DeterministicGuid(string name, int index)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"{name}#{index}"));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes.AsSpan(0, 16), bigEndian: true);
    }
}

/// <summary>CRC-32C (Castagnoli), son tersleme olmadan (Linux crc32c gibi).</summary>
internal static class Crc32C
{
    private static readonly uint[] Table = Build();

    private static uint[] Build()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0x82F63B78 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
