using System.Security.Cryptography;
using Apfs.Container;
using Apfs.ImageBuilder;
using Apfs.Volume;

namespace Apfs.Tests;

/// <summary>M6: şifreli birimler (keybag, KEK/VEK, şifreli fs-tree düğümleri ve dosya verisi).</summary>
public class EncryptionTests(TestImages images) : IClassFixture<TestImages>
{
    private const string Password = Scenarios.EncryptedPassword;

    [Fact]
    public void BuilderCryptoMatchesIndependentVectors()
    {
        // Görüntü üreticisinin şifreleme kodu da bağımsız (OpenSSL) vektörlerle doğrulanır.
        var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "data", "crypto", "vectors.txt"));
        foreach (var p in lines.Where(l => l.StartsWith("xts ")).Select(l => l.Split(' ')))
        {
            var data = Convert.FromHexString(p[4]);
            BuilderCrypto.XtsEncrypt(Convert.FromHexString(p[1]), Convert.FromHexString(p[2]), data, ulong.Parse(p[3]));
            Assert.Equal(p[5].ToUpperInvariant(), Convert.ToHexString(data));
        }
        foreach (var p in lines.Where(l => l.StartsWith("wrap ")).Select(l => l.Split(' ')))
            Assert.Equal(p[3].ToUpperInvariant(), Convert.ToHexString(BuilderCrypto.Wrap(Convert.FromHexString(p[1]), Convert.FromHexString(p[2]))));
    }

    [Fact]
    public void LockedVolumeReportsHintAndOtherVolumeStillOpens()
    {
        var (path, _) = images.Get("encrypted");
        using var c = TestImages.Open(path);
        var r = c.OpenVolumes();
        Assert.Null(r[0].Volume);
        Assert.Equal(ApfsErrorCode.Encrypted, r[0].Error!.Code);
        Assert.Contains("her zamanki parola", r[0].Error!.Message);
        Assert.Equal("Açık", r[1].Volume!.Name);
    }

    [Fact]
    public void WrongPasswordIsRejectedWithHint()
    {
        var (path, _) = images.Get("encrypted");
        using var c = TestImages.Open(path);
        VolumeLockInfo? seen = null;
        var r = c.OpenVolumes(info => { seen = info; return "yanlış parola"; });
        Assert.Equal("Kasa", seen!.Name);
        Assert.Equal("her zamanki parola", seen.Hint);
        Assert.Equal(ApfsErrorCode.WrongPassword, r[0].Error!.Code);
        Assert.Contains("Parola yanlış", r[0].Error!.Message);
    }

    [Fact]
    public void CorrectPasswordDecryptsEverything()
    {
        var (path, builder) = images.Get("encrypted");
        using var c = TestImages.Open(path);
        var v = c.OpenVolumes(_ => Password)[0].Volume ?? throw new Exception("kilit açılmadı");
        Assert.NotNull(v.Crypto);
        foreach (var e in builder.Volumes[0].Enumerate().Where(e => e.Kind == ExpectedKind.File))
        {
            var ino = v.Lookup(e.Path);
            var truth = e.Content!.ToArray();
            Assert.Equal((ulong)truth.Length, v.GetFileSize(ino));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(truth)), Convert.ToHexString(SHA256.HashData(ReaderTests.ReadAll(v, ino))));
        }
        Assert.Equal(300, v.ListDirectory(v.Lookup("/Çok").Id).Count);
    }

    [Fact]
    public void EncryptedRandomReadsAcrossUnitAndExtentBoundaries()
    {
        var (path, builder) = images.Get("encrypted");
        using var c = TestImages.Open(path);
        var v = c.OpenVolumes(_ => Password)[0].Volume!;
        var truth = builder.Volumes[0].Enumerate().Single(e => e.Path == "Belgeler/büyük.bin").Content!.ToArray();
        var reader = v.OpenFile(v.Lookup("/Belgeler/büyük.bin"));
        var rnd = new Random(77);
        foreach (int off in new[] { 0, 511, 512, 4095, 4096, 1_000_001, truth.Length - 1 }.Concat(Enumerable.Range(0, 40).Select(_ => rnd.Next(truth.Length))))
        {
            var buf = new byte[rnd.Next(1, 70_000)];
            int n = reader.Read((ulong)off, buf);
            Assert.Equal(Math.Min(buf.Length, truth.Length - off), n);
            Assert.True(truth.AsSpan(off, n).SequenceEqual(buf.AsSpan(0, n)), $"ofset {off}");
        }
    }

    [Fact]
    public void RawImageDoesNotContainPlaintext()
    {
        // Şifrelemenin gerçekten uygulandığının kanıtı: düz metin ve Türkçe dosya adı görüntüde görünmemeli
        var (path, _) = images.Get("encrypted");
        var raw = File.ReadAllBytes(path);
        Assert.Equal(-1, raw.AsSpan().IndexOf("şifreli bir APFS"u8));
        Assert.Equal(-1, raw.AsSpan().IndexOf("gizli.txt"u8));
        Assert.NotEqual(-1, raw.AsSpan().IndexOf("hangi-birim.txt"u8)); // şifresiz birim düz
    }

    [Fact]
    public void EncryptedObjectFlagIsOnlyOnTheEncryptedVolumesFsNodes()
    {
        // apfsck object.c: OBJ_ENCRYPTED ⇔ birim şifreli ∧ alt tür FSTREE (üretici de bu kurala uymalı, yoksa testler hatayı gizler)
        var (path, _) = images.Get("encrypted");
        using var c = TestImages.Open(path);
        var r = c.OpenVolumes(_ => Password);
        var kasa = r[0].Volume!;
        var acik = r[1].Volume!;
        Assert.NotEmpty(kasa.FsTree.EnumerateNodes());
        Assert.All(kasa.FsTree.EnumerateNodes(), n => Assert.NotEqual(0u, n.ObjType & ApfsConstants.ObjEncrypted));
        Assert.All(acik.FsTree.EnumerateNodes(), n => Assert.Equal(0u, n.ObjType & ApfsConstants.ObjEncrypted));
        foreach (var v in new[] { kasa, acik })
            Assert.All(v.Omap.Tree.EnumerateNodes(), n => Assert.Equal(0u, n.ObjType & ApfsConstants.ObjEncrypted));
    }
}
