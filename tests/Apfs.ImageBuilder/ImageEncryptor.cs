using System.Buffers.Binary;
using System.Text;

namespace Apfs.ImageBuilder;

/// <summary>
/// <c>apfs-mkimage encrypt</c>: şifresiz bir APFS kapsayıcı görüntüsünün (mkapfs / linux-apfs-rw yapımı; alan yöneticisi tam) İLK
/// birimini macOS'un yazılım şifrelemesiyle (APFS_FS_ONEKEY) şifrelenmiş birime dönüştürür. Okuyucudan (Apfs.Core) BAĞIMSIZ yazıldı:
/// B-ağacı gezgini, alan yöneticisi ve şifreleme bu dosyada ve <see cref="BuilderCrypto"/>'dadır.
/// Yapılanlar (gerçek USBTEST belleğinin dökümüyle aynı biçim, fixtures/usbtest-dump.txt ve fixtures/crypto-survey.txt):
/// kapsayıcı ve birim anahtar torbaları (iki yeni blok; ana bitmap, parça bilgisi ve aygıt boş sayacı güncellenir), kapsayıcı
/// bayrağı NX_CRYPTO_SW, birim bayrakları tam ONEKEY, birim omap kayıtlarında OMAP_VAL_ENCRYPTED (omap nesnesi bayrağı değişmez),
/// fs ağacı düğümleri düz metin sağlamasıyla mühürlenip tweak = fiziksel blok × (blok/512) ile şifrelenir, dosya extent'lerinde
/// crypto_id = extent'in fiziksel bloğu + FEXT_CRYPTO_ID_IS_TWEAK bayrağı, dosyalarda koruma sınıfı 3 ve default_crypto_id 4
/// (macOS kuralları; bkz. crypto-survey) ve veri blokları tweak = crypto_id × (blok/512) + birim ile
/// şifrelenir. Dönüştürme yerindedir (kopyala-yaz değil): yalnız test görüntüsü üretmek için.
/// </summary>
public static class ImageEncryptor
{
    public const string Password = "Şifreli Yazma ğüşİı 2026";
    public const string Hint = "deneme ipucu";
    private const int Iterations = 2000;
    private const uint NxMagic = 0x4253584E, ApsbMagic = 0x42535041;
    private const ulong NxCryptoSw = 0x4, FsOneKey = 0x8;
    private const uint OmapValEncrypted = 0x4;
    private const ushort BtRoot = 1, BtLeaf = 2, BtFixed = 4;

    /// <summary>Dönüştürmenin sonucu (testler için).</summary>
    public sealed record Result(Guid VolumeUuid, ulong ContainerKeybag, ulong VolumeKeybag, int FsNodes, int DataBlocks, int Extents);

    public static Result Encrypt(string input, string output, string password = Password)
    {
        File.Copy(input, output, overwrite: true);
        using var fs = new FileStream(output, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        return new Run(fs).Execute(password);
    }

    private sealed class Run(FileStream fs)
    {
        private uint _bs;

        private byte[] Read(ulong paddr)
        {
            var b = new byte[_bs];
            fs.Position = (long)(paddr * _bs);
            fs.ReadExactly(b);
            return b;
        }

        private void Write(ulong paddr, byte[] block)
        {
            fs.Position = (long)(paddr * _bs);
            fs.Write(block);
        }

        private static void Seal(byte[] obj) => new BlockWriter(obj).SealChecksum();

        private static bool ChecksumOk(byte[] obj)
        {
            var copy = (byte[])obj.Clone();
            Seal(copy);
            return copy.AsSpan(0, 8).SequenceEqual(obj.AsSpan(0, 8));
        }

        private static ulong U64(byte[] b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(o));
        private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
        private static ushort U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));
        private static void W64(byte[] b, int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o), v);
        private static void W32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);

        public Result Execute(string password)
        {
            var b0 = new byte[4096];
            fs.Position = 0;
            fs.ReadExactly(b0);
            if (U32(b0, 32) != NxMagic) throw new InvalidDataException("NXSB yok");
            _bs = U32(b0, 36);

            // ---- son checkpoint'in süper bloğu (tanımlayıcı halkasındaki en yüksek xid) ----
            uint descBlocks = U32(b0, 104) & 0x7FFFFFFF;
            ulong descBase = U64(b0, 112);
            ulong nxPaddr = 0, nxXid = 0;
            for (ulong i = 0; i < descBlocks; i++)
            {
                var b = Read(descBase + i);
                if (U32(b, 32) == NxMagic && ChecksumOk(b) && U64(b, 16) >= nxXid) { nxXid = U64(b, 16); nxPaddr = descBase + i; }
            }
            if (nxPaddr == 0) throw new InvalidDataException("geçerli checkpoint yok");
            var nx = Read(nxPaddr);
            Guid containerUuid = new(nx.AsSpan(72, 16), bigEndian: true);
            if (U64(nx, 1296) != 0) throw new InvalidDataException("kapsayıcıda zaten anahtar torbası var");

            // ---- checkpoint haritası → alan yöneticisi ----
            uint dataBlocks = U32(nx, 108);
            ulong dataBase = U64(nx, 120);
            uint descIndex = U32(nx, 136), descLen = U32(nx, 140);
            ulong smOid = U64(nx, 152);
            ulong smPaddr = 0;
            uint smSize = 0;
            for (uint m = 0; m + 1 < descLen; m++)
            {
                var map = Read(descBase + (descIndex + m) % descBlocks);
                for (int k = 0; k < U32(map, 36); k++)
                {
                    int e = 40 + k * 40;
                    if (U64(map, e + 24) == smOid) { smPaddr = U64(map, e + 32); smSize = U32(map, e + 8); }
                }
            }
            if (smPaddr == 0) throw new InvalidDataException("alan yöneticisi checkpoint haritasında yok");
            uint smBlocks = smSize / _bs;
            if (smPaddr - dataBase + smBlocks > dataBlocks) throw new InvalidDataException("alan yöneticisi halkada sarıyor (desteklenmiyor)");
            var sm = new byte[smSize];
            for (uint k = 0; k < smBlocks; k++) Read(smPaddr + k).CopyTo(sm, (int)(k * _bs));
            if (!ChecksumOk(sm)) throw new InvalidDataException("alan yöneticisinin sağlaması tutmuyor");

            // ---- iki boş blok: kapsayıcı anahtar torbası + birim anahtar torbası ----
            var (keysPaddr, recsPaddr) = AllocateTwo(sm);
            for (uint k = 0; k < smBlocks; k++) Write(smPaddr + k, sm.AsSpan((int)(k * _bs), (int)_bs).ToArray());

            // ---- birim 0 ----
            ulong volOid = U64(nx, 184);
            ulong apsbPaddr = LookupPhysOmap(U64(nx, 160), volOid);
            var apsb = Read(apsbPaddr);
            if (U32(apsb, 32) != ApsbMagic || !ChecksumOk(apsb)) throw new InvalidDataException("birim süper bloğu okunamadı");
            if ((U64(apsb, 264) & 3) != 1) throw new InvalidDataException("birim zaten şifreli");
            Guid volUuid = new(apsb.AsSpan(240, 16), bigEndian: true);

            // Birim omap'i: bütün kayıtlara OMAP_VAL_ENCRYPTED; fs düğümlerinin yerleri toplanır
            var omapObj = Read(U64(apsb, 128));
            var nodes = new Dictionary<ulong, ulong>(); // oid → paddr (en yeni xid)
            var nodeXid = new Dictionary<ulong, ulong>();
            MarkOmapEncrypted(U64(omapObj, 48), nodes, nodeXid);

            // fs ağacı: extent'lere crypto_id, düğümleri mühürle + şifrele
            var vek = Derive("vek", volUuid, 32);
            var dataTweaks = new Dictionary<ulong, ulong>(); // veri bloğu → ilk 512'lik birimin tweak'i
            int extents = 0, fsNodes = 0;
            void WalkFs(ulong oid)
            {
                ulong paddr = nodes.TryGetValue(oid, out var p) ? p : throw new InvalidDataException($"fs düğümü {oid} omap'te yok");
                var node = Read(paddr);
                if (!ChecksumOk(node)) throw new InvalidDataException($"fs düğümü {paddr} sağlaması tutmuyor");
                foreach (var (k, kLen, v, vLen) in Entries(node))
                {
                    if (U16(node, 34) > 0) { WalkFs(U64(node, v)); continue; }
                    if (U64(node, k) >> 60 == 3 && vLen >= 92 && (U16(node, v + 80) & 0xF000) == 0x8000)
                    {
                        // Dosya inode'u: PROTECTION_CLASS_C ve veri akışının default_crypto_id'si CRYPTO_SW_ID (macOS gibi)
                        W32(node, v + 60, 3);
                        int num = U16(node, v + 92), pos = v + 96 + num * 4;
                        for (int i = 0; i < num; i++)
                        {
                            int type = node[v + 96 + i * 4], size = U16(node, v + 96 + i * 4 + 2);
                            if (type == 8 && size >= 24) W64(node, pos + 16, 4);
                            pos += (size + 7) & ~7;
                        }
                        continue;
                    }
                    if (U64(node, k) >> 60 != 8 || vLen < 24) continue; // yalnız dosya extent'leri
                    ulong phys = U64(node, v + 8);
                    if (phys == 0) continue;                             // delik
                    ulong len = U64(node, v) & 0x00FFFFFFFFFFFFFF;
                    ulong cryptoId = phys;                               // macOS kuralı (crypto-survey)
                    W64(node, v + 16, cryptoId);
                    node[v + 7] |= 0x1; // FEXT_CRYPTO_ID_IS_TWEAK (len_and_flags'in üst baytı)
                    extents++;
                    for (ulong blk = 0; blk < (len + _bs - 1) / _bs; blk++)
                    {
                        ulong tweak = (cryptoId + blk) * (_bs / 512);
                        if (dataTweaks.TryGetValue(phys + blk, out var t) && t != tweak)
                            throw new InvalidDataException($"blok {phys + blk} iki farklı tweak ile kullanılıyor");
                        dataTweaks[phys + blk] = tweak;
                    }
                }
                W32(node, 24, U32(node, 24) | 0x10000000); // OBJ_ENCRYPTED: şifreli birimin fs ağacı düğümü (apfsck object.c)
                Seal(node);
                BuilderCrypto.XtsEncrypt(vek[..16], vek[16..], node, paddr * (_bs / 512));
                Write(paddr, node);
                fsNodes++;
            }
            WalkFs(U64(apsb, 136));
            if (fsNodes != nodes.Count) throw new InvalidDataException($"omap'te fs ağacına bağlı olmayan {nodes.Count - fsNodes} düğüm var");

            foreach (var (blk, tweak) in dataTweaks)
            {
                var d = Read(blk);
                BuilderCrypto.XtsEncrypt(vek[..16], vek[16..], d, tweak);
                Write(blk, d);
            }

            // ---- anahtar torbaları ----
            var userUuid = DeterministicGuid("kullanıcı", volUuid);
            var kek = Derive("kek", volUuid, 32);
            var recs = new List<(Guid, ushort, byte[])>
            {
                (userUuid, 3, BuilderCrypto.KekBlob(userUuid, kek, password, Derive("salt", volUuid, 16), Iterations, Derive("hs1", volUuid, 8))),
                (volUuid, 4, Encoding.UTF8.GetBytes(Hint)),
            };
            var range = new byte[16];
            W64(range, 0, recsPaddr);
            W64(range, 8, 1);
            var keys = new List<(Guid, ushort, byte[])>
            {
                (volUuid, 2, BuilderCrypto.VekBlob(volUuid, vek, kek, Derive("hs2", volUuid, 8))),
                (volUuid, 3, range),
            };
            Write(recsPaddr, Keybag(recsPaddr, nxXid, 0x72656373, recs, volUuid));
            Write(keysPaddr, Keybag(keysPaddr, nxXid, 0x6B657973, keys, containerUuid));

            // ---- süper bloklar ----
            W64(apsb, 264, (U64(apsb, 264) & ~3UL) | FsOneKey);
            Seal(apsb);
            Write(apsbPaddr, apsb);

            foreach (ulong p in new[] { nxPaddr, 0UL })
            {
                var b = Read(p);
                if (p == 0 && U64(b, 16) != nxXid) continue; // blok 0 eski checkpoint'in kopyasıysa dokunulmaz
                W64(b, 1264, U64(b, 1264) | NxCryptoSw);
                W64(b, 1296, keysPaddr);
                W64(b, 1304, 1);
                Seal(b);
                Write(p, b);
            }
            fs.Flush();
            return new Result(volUuid, keysPaddr, recsPaddr, fsNodes, dataTweaks.Count, extents);
        }

        /// <summary>Ana aygıtta bitmap'i olan ilk parçadan iki boş blok ayırır: bitmap, parça bilgisi (CIB) ve aygıt sayacı güncellenir.</summary>
        private (ulong, ulong) AllocateTwo(byte[] sm)
        {
            if (U32(sm, 68) != 0) throw new InvalidDataException("CAB'lı kapsayıcı desteklenmiyor");
            uint cibCount = U32(sm, 64), addrOffset = U32(sm, 80);
            for (uint c = 0; c < cibCount; c++)
            {
                ulong cibPaddr = U64(sm, (int)(addrOffset + c * 8));
                var cib = Read(cibPaddr);
                for (int i = 0; i < U32(cib, 36); i++)
                {
                    int o = 40 + i * 32;
                    ulong addr = U64(cib, o + 8), bmAddr = U64(cib, o + 24);
                    uint count = U32(cib, o + 16), free = U32(cib, o + 20);
                    if (bmAddr == 0 || free < 2) continue;
                    var bm = Read(bmAddr);
                    var got = new List<ulong>();
                    for (uint bit = 1; bit < count && got.Count < 2; bit++)
                        if ((bm[bit >> 3] & (1 << (int)(bit & 7))) == 0)
                        {
                            bm[bit >> 3] |= (byte)(1 << (int)(bit & 7));
                            got.Add(addr + bit);
                        }
                    if (got.Count < 2) continue;
                    Write(bmAddr, bm);
                    W32(cib, o + 20, free - 2);
                    Seal(cib);
                    Write(cibPaddr, cib);
                    W64(sm, 72, U64(sm, 72) - 2);
                    Seal(sm);
                    return (got[0], got[1]);
                }
            }
            throw new InvalidDataException("anahtar torbaları için boş blok yok");
        }

        /// <summary>Düğümün kayıtları: (anahtar ofseti, anahtar uzunluğu, değer ofseti, değer uzunluğu); ofsetler blok başından.</summary>
        private List<(int K, int KLen, int V, int VLen)> Entries(byte[] node)
        {
            ushort flags = U16(node, 32);
            int nkeys = (int)U32(node, 36);
            int tocOff = 56 + U16(node, 40), tocLen = U16(node, 42);
            int keyStart = 56 + U16(node, 40) + tocLen;
            int valEnd = (int)_bs - ((flags & BtRoot) != 0 ? 40 : 0);
            bool leaf = (flags & BtLeaf) != 0;
            var list = new List<(int, int, int, int)>(nkeys);
            for (int i = 0; i < nkeys; i++)
            {
                if ((flags & BtFixed) != 0)
                {
                    int t = tocOff + i * 4;
                    list.Add((keyStart + U16(node, t), 16, valEnd - U16(node, t + 2), leaf ? 16 : 8));
                }
                else
                {
                    int t = tocOff + i * 8;
                    list.Add((keyStart + U16(node, t), U16(node, t + 2), valEnd - U16(node, t + 4), U16(node, t + 6)));
                }
            }
            return list;
        }

        /// <summary>Fiziksel omap ağacında (sabit 16/16) oid'in en yeni eşlemesi.</summary>
        private ulong LookupPhysOmap(ulong omapObjPaddr, ulong oid)
        {
            var obj = Read(omapObjPaddr);
            ulong best = 0, bestXid = 0;
            void Walk(ulong paddr)
            {
                var node = Read(paddr);
                foreach (var (k, _, v, _) in Entries(node))
                {
                    if (U16(node, 34) > 0) { Walk(U64(node, v)); continue; }
                    if (U64(node, k) == oid && U64(node, k + 8) >= bestXid) { bestXid = U64(node, k + 8); best = U64(node, v + 8); }
                }
            }
            Walk(U64(obj, 48));
            return best != 0 ? best : throw new InvalidDataException($"nesne {oid} kapsayıcı omap'inde yok");
        }

        /// <summary>Birim omap'inin bütün yaprak kayıtlarına OMAP_VAL_ENCRYPTED koyar ve düğümleri yeniden mühürler.</summary>
        private void MarkOmapEncrypted(ulong paddr, Dictionary<ulong, ulong> nodes, Dictionary<ulong, ulong> nodeXid)
        {
            var node = Read(paddr);
            bool changed = false;
            foreach (var (k, _, v, _) in Entries(node))
            {
                if (U16(node, 34) > 0) { MarkOmapEncrypted(U64(node, v), nodes, nodeXid); continue; }
                ulong oid = U64(node, k), xid = U64(node, k + 8);
                W32(node, v, U32(node, v) | OmapValEncrypted);
                changed = true;
                if (!nodeXid.TryGetValue(oid, out var x) || xid > x) { nodeXid[oid] = xid; nodes[oid] = U64(node, v + 8); }
            }
            if (!changed) return;
            Seal(node);
            Write(paddr, node);
        }

        private byte[] Keybag(ulong paddr, ulong xid, uint type, List<(Guid Uuid, ushort Tag, byte[] Data)> entries, Guid xtsUuid)
        {
            var w = new BlockWriter(new byte[_bs]);
            w.ObjHeader(0, xid, type, 0); // apfsck: keybag nesnesinin kimliği yoktur (crypto.c)
            int pos = 48;
            foreach (var (uuid, tag, data) in entries)
            {
                w.Uuid(pos, uuid);
                w.U16(pos + 16, tag);
                w.U16(pos + 18, (ushort)data.Length);
                w.Bytes(pos + 24, data);
                pos += (24 + data.Length + 15) & ~15;
            }
            w.U16(32, 2);
            w.U16(34, (ushort)entries.Count);
            w.U32(36, (uint)(pos - 48));
            w.SealChecksum();
            var key = BuilderCrypto.Raw(xtsUuid);
            BuilderCrypto.XtsEncrypt(key, key, w.Buffer, paddr * (_bs / 512));
            return w.Buffer;
        }

        private static byte[] Derive(string label, Guid seed, int length) =>
            System.Security.Cryptography.SHA256.HashData([.. Encoding.UTF8.GetBytes(label), .. seed.ToByteArray()]).AsSpan(0, length).ToArray();

        private static Guid DeterministicGuid(string label, Guid seed)
        {
            var h = Derive(label, seed, 16);
            h[6] = (byte)((h[6] & 0x0F) | 0x40);
            h[8] = (byte)((h[8] & 0x3F) | 0x80);
            return new Guid(h, bigEndian: true);
        }
    }
}
