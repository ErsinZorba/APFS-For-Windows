using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Apfs.Container;
using Apfs.Crypto;
using Apfs.Diagnostics;
using Apfs.ImageBuilder;
using Apfs.IO;
using Apfs.Util;
using Apfs.Volume;
using Apfs.Write;

namespace Apfs.Tests;

/// <summary>
/// Şifreli APFS birimine yazma: bağımsız şifreleyicinin (apfs-mkimage encrypt)
/// ürettiği görüntünün biçimi, anahtarla açma, şifreli birimde tam denetim kuralları, yazma kapısı, düz metin sızıntısı ve
/// OpenSSL ile bağımsız çözme. Şifreli görüntüde yazma işlemlerinin kendisi DataWrite/NamespaceOps/Model/Crash testlerinin
/// "enc-" varyantlarında sınanır.
/// </summary>
public class EncryptedWriteTests(LinuxFixtures fixtures) : IClassFixture<LinuxFixtures>
{
    private const int Bs = 4096;

    private static VolumeKey Key(ApfsContainer c) => TestKeys.First(c)!;

    // ---------------------------------------------------------------------------------------------------------------
    // 2.2 şifreleyicinin çıktısı

    [Theory]
    [InlineData("mk-ci-64m")]
    [InlineData("pop-ci-64m")]
    public void EncryptorOutputHasUsbtestShapeAndSameContent(string name)
    {
        using var c = TestImages.Open(fixtures.Get("enc-" + name));
        // Gerçek USBTEST belleğinin dökümüyle aynı bayraklar (fixtures/usbtest-dump.txt)
        Assert.Equal(ApfsConstants.NxCryptoSw, c.Superblock.Flags & ApfsConstants.NxCryptoSw);
        var vol = c.OpenVolumes(_ => ImageEncryptor.Password)[0].Volume!;
        Assert.Equal(ApfsConstants.ApfsFsOneKey, vol.FsFlags);
        Assert.Equal(0UL, Bin.U64(vol.RawSuperblock, 976)); // er_state
        Assert.Equal(0u, Bin.U32(vol.Omap.Raw, 32));        // omap nesnesi bayrağı
        Assert.True(vol.IsCaseInsensitive);
        Assert.All(vol.Omap.AllRecords(), r => Assert.Equal(ApfsConstants.OmapValEncrypted, r.Flags & ApfsConstants.OmapValEncrypted));
        // Veri extent'lerinde FEXT_CRYPTO_ID_IS_TWEAK ve crypto_id = fiziksel blok; dosyalarda koruma sınıfı 3 (USBTEST ile aynı)
        foreach (var e in vol.AllRecords())
        {
            if (JKey.Type(e.Key) == JType.FileExtent && Bin.U64(e.Value, 8) != 0)
            {
                Assert.Equal(1UL, Bin.U64(e.Value, 0) >> 56);
                Assert.Equal(Bin.U64(e.Value, 8), Bin.U64(e.Value, 16));
            }
            if (JKey.Type(e.Key) == JType.Inode && (Bin.U16(e.Value, 80) & 0xF000) == 0x8000) Assert.Equal(3u, Bin.U32(e.Value, 60));
        }

        var withKey = FullCheck.Run(c, keys: [Key(c)]);
        Assert.True(withKey.IsClean, string.Join("\n", withKey.Errors));
        var locked = FullCheck.Run(c);
        Assert.True(locked.IsClean, string.Join("\n", locked.Errors));

        if (name == "pop-ci-64m")
        {
            var manifest = LinuxFixtures.Manifest(name);
            Assert.True(manifest.Count > 1000);
            foreach (var (path, sha) in manifest)
                Assert.True(sha == Convert.ToHexString(SHA256.HashData(ReaderTests.ReadAll(vol, vol.Lookup(path)))), $"{path} içeriği farklı");
        }
    }

    [Fact]
    public void LockedAndWrongPasswordAreReportedWithHint()
    {
        using var c = TestImages.Open(fixtures.Get("enc-mk-ci-64m"));
        var locked = c.OpenVolumes()[0];
        Assert.Equal(ApfsErrorCode.Encrypted, locked.Error!.Code);
        var wrong = c.OpenVolumes(_ => "yanlış")[0];
        Assert.Equal(ApfsErrorCode.WrongPassword, wrong.Error!.Code);
        Assert.Contains(ImageEncryptor.Hint, wrong.Error.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 2.3 anahtar

    [Fact]
    public void VolumeKeyOpensWithoutPasswordAndIsBoundToItsVolume()
    {
        using var c = TestImages.Open(fixtures.Get("enc-pop-ci-64m"));
        var key = Key(c);
        var vol = ApfsVolume.Open(c, c.Superblock.FsOids[0], key);
        Assert.Equal("merhaba dünya\n", Encoding.UTF8.GetString(ReaderTests.ReadAll(vol, vol.Lookup("Belgeler/merhaba.txt"))));

        // Başka birimin anahtarı reddedilir
        using var other = VolumeKey.FromBytes(Guid.NewGuid(), new byte[32]);
        Assert.Equal(ApfsErrorCode.InvalidArgument, Assert.Throws<ApfsException>(() => ApfsVolume.Open(c, c.Superblock.FsOids[0], other)).Code);
        // Yanlış VEK ilk düğümde sağlama hatası verir (yeni açılmış kapsayıcıda: blok önbelleği çözülmüş düğümü tutar)
        using var wrong = VolumeKey.FromBytes(key.VolumeUuid, new byte[32]);
        using (var fresh = TestImages.Open(fixtures.Get("enc-pop-ci-64m")))
            Assert.Equal(ApfsErrorCode.ChecksumMismatch, Assert.Throws<ApfsException>(() => ApfsVolume.Open(fresh, fresh.Superblock.FsOids[0], wrong)).Code);

        // Bırakılan anahtar kullanılamaz; kopyası bağımsızdır
        var copy = key.Clone();
        key.Dispose();
        Assert.Throws<ObjectDisposedException>(() => key.Clone());
        Assert.NotNull(ApfsVolume.Open(c, c.Superblock.FsOids[0], copy).Root);
        copy.Dispose();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 2.3 tam denetimin şifreleme kuralları (bozuk kopyalarda kırmızı)

    private static readonly TreeShape OmapShape = new(ApfsConstants.TypeOmap, ApfsConstants.ObjPhysical, 16, 16, KeyOrder.Omap, Bs);
    // Şifreli birimin fs ağacı düğümü: OBJ_ENCRYPTED taşır (bozmalar yalnız hedeflenen kuralı kırsın)
    private static readonly TreeShape FsShape = new(ApfsConstants.TypeFsTree, ApfsConstants.ObjVirtual | ApfsConstants.ObjEncrypted, 0, 0, KeyOrder.Fs, Bs);

    /// <summary>Düğümü (şifreliyse çözüp) ayrıştırır, değiştirir, yeniden mühürler (ve şifreler).</summary>
    private static void PatchNode(string path, ulong paddr, bool isRoot, TreeShape shape, VolumeCrypto? crypto, Action<MNode> change)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        var b = new byte[Bs];
        fs.Position = (long)paddr * Bs;
        fs.ReadExactly(b);
        crypto?.DecryptBlock(b, paddr);
        Assert.True(Fletcher64.Verify(b));
        var n = NodeCodec.Parse(b, shape, isRoot);
        change(n);
        var o = NodeCodec.Serialize(n, shape, isRoot, Bin.U64(b, 8), Bin.U64(b, 16));
        crypto?.EncryptBlock(o, paddr);
        fs.Position = (long)paddr * Bs;
        fs.Write(o);
    }

    private static string Errors(string path)
    {
        using var c = TestImages.Open(path);
        var r = FullCheck.Run(c, keys: TestKeys.Of(c));
        Assert.False(r.IsClean, "bozulma yakalanmadı");
        return string.Join("\n", r.Errors);
    }

    [Fact]
    public void FullCheckRequiresEncryptedFlagOnEveryNodeOfEncryptedVolume()
    {
        string path = fixtures.Copy("enc-mk-ci-64m");
        using (var c = TestImages.Open(path))
        {
            var vol = c.OpenVolumes(keys: [Key(c)])[0].Volume!;
            ulong root = Bin.U64(vol.Omap.Raw, 48);
            PatchNode(path, root, true, OmapShape, null, n => n.Vals[0]![0] &= unchecked((byte)~ApfsConstants.OmapValEncrypted));
        }
        // Düğüm hâlâ şifreli ama işaretsiz: okuyucu çözmeden doğrular → sağlama hatası; kural ayrıca adıyla raporlanır
        Assert.Contains("şifreli işaretlenmemiş düğüm", Errors(path), StringComparison.Ordinal);

        // Ters yön: şifresiz birimde işaretli düğüm
        string plain = fixtures.Copy("mk-ci-64m");
        using (var c = TestImages.Open(plain))
            PatchNode(plain, Bin.U64(c.OpenVolumes()[0].Volume!.Omap.Raw, 48), true, OmapShape, null,
                n => n.Vals[0]![0] |= (byte)ApfsConstants.OmapValEncrypted);
        Assert.Contains("şifresiz birimde şifreli işaretli düğüm", Errors(plain), StringComparison.Ordinal);
    }

    [Fact]
    public void FullCheckRequiresEncryptedObjectFlagOnEveryFsNodeOfEncryptedVolume()
    {
        // Mac İlk Yardım (B-Sifreli, 2026-10-25): "apfs_root: bt: invalid o_type (0x2, expected 0x10000002)"
        string path = fixtures.Copy("enc-pop-ci-64m");
        using (var c = TestImages.Open(path))
        {
            var key = Key(c);
            var vol = c.OpenVolumes(keys: [key])[0].Volume!;
            var nodes = vol.FsTree.EnumerateNodes().ToList();
            Assert.All(nodes, n => Assert.NotEqual(0u, n.ObjType & ApfsConstants.ObjEncrypted));
            Assert.Contains(FullCheck.Run(c, keys: [key]).Info, l => l.Contains($"fs düğümleri {nodes.Count}, OBJ_ENCRYPTED işaretli {nodes.Count}", StringComparison.Ordinal));
            ulong rootPaddr = vol.Omap.Resolve(vol.RootTreeOid, c.Xid).Paddr;
            using var crypto = key.CreateCrypto(Bs);
            var plainShape = FsShape with { StorageFlag = ApfsConstants.ObjVirtual };
            PatchNode(path, rootPaddr, true, plainShape, crypto, _ => { });
        }
        Assert.Contains("şifreli birimde OBJ_ENCRYPTED işaretsiz fs düğümü", Errors(path), StringComparison.Ordinal);

        // Kilitli denetim fs ağacını okuyamaz: kural "denetlenmedi" der, hata saymaz
        using var c2 = TestImages.Open(fixtures.Get("enc-pop-ci-64m"));
        var locked = FullCheck.Run(c2);
        Assert.True(locked.IsClean, string.Join("\n", locked.Errors));
        Assert.Contains(locked.Info, l => l.Contains("OBJ_ENCRYPTED işareti denetlenmedi", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(4UL)]
    public void FullCheckRejectsDataExtentWithReservedCryptoId(ulong cryptoId)
    {
        string path = fixtures.Copy("enc-pop-ci-64m");
        using (var c = TestImages.Open(path))
        {
            var key = Key(c);
            var vol = c.OpenVolumes(keys: [key])[0].Volume!;
            ulong rootPaddr = vol.Omap.Resolve(vol.RootTreeOid, c.Xid).Paddr;
            using var crypto = key.CreateCrypto(Bs);
            bool done = false;
            foreach (var visit in vol.FsTree.EnumerateNodes().Where(v => v.Level == 0).ToList())
            {
                if (done) break;
                ulong paddr = vol.Omap.Resolve(visit.Oid, c.Xid).Paddr;
                PatchNode(path, paddr, paddr == rootPaddr, FsShape, crypto, n =>
                {
                    for (int i = 0; i < n.Count && !done; i++)
                        if (JKey.Type(n.Keys[i]) == JType.FileExtent && Bin.U64(n.Vals[i]!, 8) != 0)
                        {
                            BitConverter.TryWriteBytes(n.Vals[i].AsSpan(16), cryptoId);
                            done = true;
                        }
                });
            }
            Assert.True(done);
        }
        Assert.Contains($"crypto_id'si {cryptoId}", Errors(path), StringComparison.Ordinal);
    }

    [Fact]
    public void FullCheckRequiresTweakFlagOnEncryptedDataExtents()
    {
        string path = fixtures.Copy("enc-pop-ci-64m");
        using (var c = TestImages.Open(path))
        {
            var key = Key(c);
            var vol = c.OpenVolumes(keys: [key])[0].Volume!;
            ulong rootPaddr = vol.Omap.Resolve(vol.RootTreeOid, c.Xid).Paddr;
            using var crypto = key.CreateCrypto(Bs);
            bool done = false;
            foreach (var visit in vol.FsTree.EnumerateNodes().Where(v => v.Level == 0).ToList())
            {
                if (done) break;
                ulong paddr = vol.Omap.Resolve(visit.Oid, c.Xid).Paddr;
                PatchNode(path, paddr, paddr == rootPaddr, FsShape, crypto, n =>
                {
                    for (int i = 0; i < n.Count && !done; i++)
                        if (JKey.Type(n.Keys[i]) == JType.FileExtent && Bin.U64(n.Vals[i]!, 8) != 0) { n.Vals[i]![7] = 0; done = true; }
                });
            }
            Assert.True(done);
        }
        Assert.Contains("FEXT_CRYPTO_ID_IS_TWEAK", Errors(path), StringComparison.Ordinal);
    }

    [Fact]
    public void WriterGivesNewFilesTheMacOsShape()
    {
        var (path, grant) = FsOpsTests.Prepare(fixtures, "enc-pop-ci-64m");
        var data = new byte[5 * Bs];
        new Random(3).NextBytes(data);
        FsOpsTests.Run(path, grant, (_, v) =>
        {
            ulong f = v.CreateFile(Inode.RootDirId, "yeni.bin");
            v.Write(f, 0, data);
            v.CreateDirectory(Inode.RootDirId, "yeni-klasör");
        });
        // Sonraki oturumda ortadan yazma: kalıcı blok kopyala-yaz ile değişir, extent üçe bölünür (bayrak korunmalı)
        using (var c0 = TestImages.Open(path)) grant = WriteGate.Evaluate(c0, TestKeys.Of(c0)).Grant!;
        FsOpsTests.Run(path, grant, (_, v) => v.Write(v.FindEntry(Inode.RootDirId, "yeni.bin")!.FileId, 2 * Bs + 7, new byte[100]));
        Array.Clear(data, 2 * Bs + 7, 100);
        using var c = TestImages.Open(path);
        var vol = c.OpenVolumes(keys: TestKeys.Of(c))[0].Volume!;
        var ino = vol.Lookup("yeni.bin");
        var raw = vol.AllRecords().First(e => JKey.ObjId(e.Key) == ino.Id && JKey.Type(e.Key) == JType.Inode).Value;
        Assert.Equal(3u, Bin.U32(raw, 60));                                  // PROTECTION_CLASS_C
        var dirRaw = vol.AllRecords().First(e => JKey.ObjId(e.Key) == vol.Lookup("yeni-klasör").Id && JKey.Type(e.Key) == JType.Inode).Value;
        Assert.Equal(0u, Bin.U32(dirRaw, 60));
        var ds = XFields.Parse(raw, 92).First(x => x.Type == InodeXField.Dstream).Data;
        Assert.Equal(4UL, Bin.U64(ds, 16));                                  // default_crypto_id = CRYPTO_SW_ID
        var exts = vol.AllRecords().Where(e => JKey.ObjId(e.Key) == ino.PrivateId && JKey.Type(e.Key) == JType.FileExtent).ToList();
        Assert.True(exts.Count >= 3, $"{exts.Count} extent");
        Assert.Equal(data, ReaderTests.ReadAll(vol, ino));
        Assert.All(exts, e => Assert.Equal(1UL, Bin.U64(e.Value, 0) >> 56)); // FEXT_CRYPTO_ID_IS_TWEAK
        Assert.All(exts, e => Assert.Equal(Bin.U64(e.Value, 8), Bin.U64(e.Value, 16)));
    }

    [Fact]
    public void EmptyCryptoStateOfSoftwareIdIsAccepted()
    {
        // Gerçek USBTEST: tek crypto_state kaydı, kimlik 4 (CRYPTO_SW_ID), anahtar uzunluğu 0 → hata değil
        string path = fixtures.Copy("enc-mk-ci-64m");
        using (var c = TestImages.Open(path))
        {
            var key = Key(c);
            var vol = c.OpenVolumes(keys: [key])[0].Volume!;
            using var crypto = key.CreateCrypto(Bs);
            PatchNode(path, vol.Omap.Resolve(vol.RootTreeOid, c.Xid).Paddr, true, FsShape, crypto, n => InsertCryptoState(n, 4, keyLen: 0));
        }
        using var c2 = TestImages.Open(path);
        var r = FullCheck.Run(c2, keys: TestKeys.Of(c2));
        Assert.True(r.IsClean, string.Join("\n", r.Errors));
        Assert.True(WriteGate.Evaluate(c2, TestKeys.Of(c2)).Allowed);
    }

    private static void InsertCryptoState(MNode n, ulong oid, ushort keyLen)
    {
        var k = Records.Key(oid, JType.CryptoState);
        var v = new byte[24 + keyLen];
        v[0] = 1;                                // refcnt
        BitConverter.TryWriteBytes(v.AsSpan(22), keyLen);
        int at = 0;
        while (at < n.Count && KeyOrder.Fs(n.Keys[at], k) < 0) at++;
        n.Keys.Insert(at, k);
        n.Vals.Insert(at, v);
        BitConverter.TryWriteBytes(n.Footer!.AsSpan(24), Bin.U64(n.Footer!, 24) + 1);
        BitConverter.TryWriteBytes(n.Footer!.AsSpan(20), Math.Max(Bin.U32(n.Footer!, 20), (uint)v.Length));
    }

    [Fact]
    public void FullCheckRejectsCryptoStateOnOneKeyVolumeAndGateRefuses()
    {
        string path = fixtures.Copy("enc-mk-ci-64m");
        using (var c = TestImages.Open(path))
        {
            var key = Key(c);
            var vol = c.OpenVolumes(keys: [key])[0].Volume!;
            ulong rootPaddr = vol.Omap.Resolve(vol.RootTreeOid, c.Xid).Paddr;
            Assert.Equal(0, vol.FsTree.Height); // tek yapraklı kök: özet de güncellenir
            using var crypto = key.CreateCrypto(Bs);
            PatchNode(path, rootPaddr, true, FsShape, crypto, n =>
            {
                var k = Records.Key(5, JType.CryptoState);
                var v = new byte[24];
                v[0] = 1;  // refcnt
                v[4] = 5;  // major_version
                int at = 0;
                while (at < n.Count && KeyOrder.Fs(n.Keys[at], k) < 0) at++;
                n.Keys.Insert(at, k);
                n.Vals.Insert(at, v);
                BitConverter.TryWriteBytes(n.Footer!.AsSpan(24), Bin.U64(n.Footer!, 24) + 1);
                BitConverter.TryWriteBytes(n.Footer!.AsSpan(20), Math.Max(Bin.U32(n.Footer!, 20), 24u));
            });
        }
        Assert.Contains("crypto_state", Errors(path), StringComparison.Ordinal);
        using var c2 = TestImages.Open(path);
        Assert.False(WriteGate.Evaluate(c2, TestKeys.Of(c2)).Allowed);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 2.5 yazma kapısı

    private static void PatchApsb(string path, Action<byte[]> change)
    {
        ulong paddr;
        using (var c = TestImages.Open(path)) paddr = c.Omap.Resolve(c.Superblock.FsOids[0], c.Xid).Paddr;
        FullCheckTests.PatchObject(path, paddr, change);
    }

    private GateResult Gate(string path, bool withKey = true)
    {
        using var c = TestImages.Open(path);
        return WriteGate.Evaluate(c, withKey ? TestKeys.Of(c) : null);
    }

    [Fact]
    public void GateOpensEncryptedVolumeOnlyWithKeyAndRejectsEachUnsupportedState()
    {
        Assert.True(Gate(fixtures.Copy("enc-pop-ci-64m")).Allowed);

        var locked = Gate(fixtures.Copy("enc-pop-ci-64m"), withKey: false);
        Assert.False(locked.Allowed);
        Assert.Contains(locked.Reasons, r => r.Contains("önce kilidini açın", StringComparison.Ordinal));

        void Rejected(Action<string> corrupt, string expected)
        {
            string p = fixtures.Copy("enc-mk-ci-64m");
            corrupt(p);
            var g = Gate(p);
            Assert.False(g.Allowed, expected);
            Assert.Contains(g.Reasons, r => r.Contains(expected, StringComparison.Ordinal));
        }
        // Dosya başına anahtar (ONEKEY yok)
        Rejected(p => PatchApsb(p, b => BitConverter.TryWriteBytes(b.AsSpan(264), 0UL)), "türde şifreli");
        // Kapsayıcıda NX_CRYPTO_SW yok
        Rejected(p =>
        {
            ulong sbPaddr;
            using (var c = TestImages.Open(p)) sbPaddr = c.Superblock.Paddr;
            FullCheckTests.PatchObject(p, sbPaddr, b => BitConverter.TryWriteBytes(b.AsSpan(1264), Bin.U64(b, 1264) & ~ApfsConstants.NxCryptoSw));
        }, "türde şifreli");
        // macOS şifrelemeyi sürdürüyor (er_state)
        Rejected(p => PatchApsb(p, b => BitConverter.TryWriteBytes(b.AsSpan(976), 12345UL)), "henüz bitmemiş");
        // Şifrelemesi sonradan değiştirilmiş (Seagate diskinin durumu)
        Rejected(p => PatchApsb(p, b => BitConverter.TryWriteBytes(b.AsSpan(56), Bin.U64(b, 56) | ApfsConstants.ApfsIncompatEncRolled)), "ENC_ROLLED");
        // omap'te şifreleme işlemi bayrağı
        Rejected(p =>
        {
            ulong omap;
            using (var c = TestImages.Open(p)) omap = Bin.U64(c.OpenVolumes(keys: TestKeys.Of(c))[0].Volume!.RawSuperblock, 128);
            FullCheckTests.PatchObject(p, omap, b => BitConverter.TryWriteBytes(b.AsSpan(32), Bin.U32(b, 32) | ApfsConstants.OmapEncrypting));
        }, "henüz bitmemiş");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 2.4 düz metin sızıntısı ve anahtarsız yazma

    private const string NameMarker = "SIZINTI-AD-7F3A";
    private const string DataMarker = "SIZINTI-ICERIK-7F3A|";

    /// <summary>İşaretli adlar ve içerikle yazar; ham görüntüde işaretlerin kaç kez geçtiğini döndürür.</summary>
    private (int Names, int Data) WriteMarkersAndScan(string fixture)
    {
        var (path, grant) = FsOpsTests.Prepare(fixtures, fixture);
        var content = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(DataMarker, 12_000)));
        FsOpsTests.Run(path, grant, (_, v) =>
        {
            ulong dir = v.CreateDirectory(Inode.RootDirId, NameMarker + "-klasor");
            ulong f = v.CreateFile(dir, NameMarker + "-dosya.txt");
            v.Write(f, 0, content);
            ulong small = v.CreateFile(Inode.RootDirId, NameMarker + "-kucuk.txt");
            v.Write(small, 0, Encoding.ASCII.GetBytes(DataMarker));
            v.Write(f, 5000, Encoding.ASCII.GetBytes(DataMarker)); // kısmi blok yeniden yazımı
        });
        using (var c = TestImages.Open(path))
        {
            var r = FullCheck.Run(c, keys: TestKeys.Of(c));
            Assert.True(r.IsClean, string.Join("\n", r.Errors));
            var vol = c.OpenVolumes(keys: TestKeys.Of(c))[0].Volume!;
            Assert.Equal(content, ReaderTests.ReadAll(vol, vol.Lookup($"{NameMarker}-klasor/{NameMarker}-dosya.txt")));
        }
        var raw = File.ReadAllBytes(path);
        return (Count(raw, Encoding.ASCII.GetBytes(NameMarker)), Count(raw, Encoding.ASCII.GetBytes(DataMarker)));
    }

    private static int Count(byte[] hay, byte[] needle)
    {
        int n = 0;
        for (int i = hay.AsSpan().IndexOf(needle); i >= 0; )
        {
            n++;
            int next = hay.AsSpan(i + 1).IndexOf(needle);
            i = next < 0 ? -1 : i + 1 + next;
        }
        return n;
    }

    [Fact]
    public void PlaintextNamesAndDataNeverReachTheEncryptedDisk()
    {
        // Denetimin kendisi: şifresiz görüntüde işaretler bulunur
        var plain = WriteMarkersAndScan("pop-ci-64m");
        // (blok sınırında bölünen işaretler sayılmaz; dosya blokları fiziksel olarak bitişik olmayabilir)
        Assert.True(plain.Names >= 3 && plain.Data >= 11_000, $"tarayıcı işaret bulamadı ({plain})");
        var enc = WriteMarkersAndScan("enc-pop-ci-64m");
        Assert.Equal((0, 0), enc);
    }

    [Fact]
    public void EncryptedSessionRefusesKeylessWriterAndKeylessData()
    {
        var (path, grant) = FsOpsTests.Prepare(fixtures, "enc-mk-ci-64m");
        using var dev = new FileImageWriter(path, grant);
        using var s = WriteSession.Open(dev, grant);
        Assert.Equal(ApfsErrorCode.Encrypted, Assert.Throws<ApfsException>(() => s.Volume(0)).Code);
        s.Volume(0, TestKeys.First(s.Container));
        var (p, _) = s.Spaceman.Allocate(1, 0);
        Assert.Throws<InvalidOperationException>(() => s.WriteData(p, new byte[Bs]));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // OpenSSL ile bağımsız çözme (Python + cryptography yoksa atlanır)

    private static string? FindScript()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "tools", "openssl_check.py");
            if (File.Exists(f)) return f;
        }
        return null;
    }

    private static string? Python(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("python") { RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 ? o.Trim() : null;
        }
        catch (Win32Exception) { return null; }
    }

    [Fact]
    public void OpenSslDecryptsNodesAndExtentsWrittenByEncryptorAndWriter()
    {
        string? script = FindScript();
        if (script is null || Python("-c", "import cryptography") is null) return; // bağımsız doğrulayıcı kurulu değil

        var (path, grant) = FsOpsTests.Prepare(fixtures, "enc-pop-ci-64m");
        var content = new byte[3 * Bs + 1234];
        new Random(7).NextBytes(content);
        FsOpsTests.Run(path, grant, (_, v) => v.Write(v.CreateFile(Inode.RootDirId, "openssl.bin"), 0, content));

        using var c = TestImages.Open(path);
        var vek = VolumeKeys.Unlock(c, c.VolumeUuid(c.Superblock.FsOids[0]), ImageEncryptor.Password);
        string vekHex = Convert.ToHexString(vek);
        var vol = c.OpenVolumes(keys: TestKeys.Of(c))[0].Volume!;

        // Yazıcının yazdığı fs kök düğümü ve bütün düğümler: çözülen düz metnin sağlaması tutar
        foreach (var n in vol.FsTree.EnumerateNodes())
        {
            ulong paddr = vol.Omap.Resolve(n.Oid, c.Xid).Paddr;
            Assert.StartsWith("fletcher=ok", Python(script, path, $"{Bs}", $"{paddr}", "1", vekHex, $"{paddr * Bs / 512}"));
        }

        // Dosya verisi: yazıcının (openssl.bin) ve şifreleyicinin (Linux'un yazdığı merhaba.txt) extent'leri
        void CheckFile(string file, byte[] expected)
        {
            var ino = vol.Lookup(file);
            var padded = new byte[(expected.Length + Bs - 1) / Bs * Bs];
            expected.CopyTo(padded, 0);
            var got = new List<byte>();
            foreach (var x in vol.GetExtents(ino.PrivateId))
            {
                Assert.Equal(x.PhysBlock, x.CryptoId); // macOS kuralı: crypto_id = ilk yazıldığı fiziksel blok
                string o = Python(script, path, $"{Bs}", $"{x.PhysBlock}", $"{x.Length / Bs}", vekHex, $"{x.CryptoId * Bs / 512}")!;
                Assert.Equal(o[(o.IndexOf("sha256=", StringComparison.Ordinal) + 7)..],
                    Convert.ToHexString(SHA256.HashData(padded.AsSpan((int)x.LogicalOffset, (int)x.Length))), ignoreCase: true);
            }
        }
        CheckFile("openssl.bin", content);
        CheckFile("Belgeler/merhaba.txt", Encoding.UTF8.GetBytes("merhaba dünya\n"));
    }
}
