using System.Security.Cryptography;
using Apfs.Diagnostics;
using Apfs.IO;
using Apfs.Volume;
using Apfs.Write;

namespace Apfs.Tests;

/// <summary>
/// Faz 2.2: dosya verisi. Rastgele yazma/kısaltma/uzatma işlemleri hem görüntüye hem bellekteki modele uygulanır; her commit'ten
/// sonra (1) oturumun okuduğu, (2) görüntüyü sıfırdan açan okuyucunun okuduğu içerik modelle birebir olmalı ve tam denetim 0 hata
/// vermelidir. Linux'un yazdığı dosyaları değiştirme (kalıcı blokların kopyala-yaz'ı, fiziksel extent bölünmesi) ayrıca sınanır.
/// </summary>
public class DataWriteTests(LinuxFixtures fixtures) : IClassFixture<LinuxFixtures>
{
    private static byte[] ReadAllSession(VolumeWriter v, ulong id)
    {
        var buf = new byte[v.GetSize(id)];
        Assert.Equal(buf.Length, v.Read(id, 0, buf));
        return buf;
    }

    private static void AssertImage(IBlockDevice dev, Dictionary<string, (ulong Id, List<byte> Data)> model)
    {
        using var c = Apfs.Container.ApfsContainer.Open(dev, ownsDevice: false);
        var r = FullCheck.Run(c, keys: TestKeys.Of(c));
        Assert.True(r.IsClean, string.Join("\n", r.Errors));
        var vol = c.OpenVolumes(keys: TestKeys.Of(c))[0].Volume!;
        foreach (var (name, (_, data)) in model)
        {
            var ino = vol.Lookup("veri/" + name);
            Assert.Equal((ulong)data.Count, vol.GetFileSize(ino));
            Assert.True(data.ToArray().AsSpan().SequenceEqual(ReaderTests.ReadAll(vol, ino)), $"{name} içeriği farklı");
        }
    }

    [Theory]
    [InlineData("mk-ci-64m", 1)]
    [InlineData("mk-ci-64m", 2)]
    [InlineData("pop-ci-64m", 3)]
    [InlineData("enc-mk-ci-64m", 4)]
    [InlineData("enc-pop-ci-64m", 5)]
    public void RandomWritesMatchModel(string fixture, int seed)
    {
        var (path, grant) = FsOpsTests.Prepare(fixtures, fixture);
        var rnd = new Random(seed);
        var model = new Dictionary<string, (ulong Id, List<byte> Data)>();
        using (var dev = new FileImageWriter(path, grant))
        using (var s = WriteSession.Open(dev, grant))
        {
            var v = s.Volume(0, TestKeys.First(s.Container));
            ulong dir = v.CreateDirectory(Inode.RootDirId, "veri");
            for (int f = 0; f < 6; f++) model[$"f{f}.bin"] = (v.CreateFile(dir, $"f{f}.bin"), []);

            for (int op = 1; op <= 400; op++)
            {
                var name = $"f{rnd.Next(6)}.bin";
                var (id, data) = model[name];
                int kind = rnd.Next(10);
                if (kind < 6)
                {
                    // yaz: çoğu zaman dosyanın içine/sonuna, bazen ötesine (delik oluşur)
                    int maxOff = data.Count + (rnd.Next(4) == 0 ? 300_000 : 10);
                    int off = rnd.Next(0, Math.Max(1, maxOff));
                    var chunk = new byte[rnd.Next(1, rnd.Next(2) == 0 ? 5000 : 200_000)];
                    rnd.NextBytes(chunk);
                    v.Write(id, (ulong)off, chunk);
                    while (data.Count < off) data.Add(0);
                    for (int i = 0; i < chunk.Length; i++)
                    {
                        if (off + i < data.Count) data[off + i] = chunk[i]; else data.Add(chunk[i]);
                    }
                }
                else if (kind < 8)
                {
                    int newSize = rnd.Next(0, data.Count + 1); // kısalt
                    v.SetSize(id, (ulong)newSize);
                    data.RemoveRange(newSize, data.Count - newSize);
                }
                else
                {
                    int newSize = data.Count + rnd.Next(0, 400_000); // uzat (delik)
                    v.SetSize(id, (ulong)newSize);
                    while (data.Count < newSize) data.Add(0);
                }
                Assert.True(data.ToArray().AsSpan().SequenceEqual(ReadAllSession(v, id)), $"işlem {op}: {name} oturumda farklı");

                if (op % 40 == 0)
                {
                    s.Commit();
                    AssertImage(dev, model);
                }
            }
            s.Close();
        }
        using (var c = TestImages.Open(path))
            Assert.True(FullCheck.Run(c, keys: TestKeys.Of(c)).IsClean);
        DevOutput.Save(path, $"veri-{fixture}-{seed}");
    }

    [Theory]
    [InlineData("pop-ci-64m")]
    [InlineData("enc-pop-ci-64m")]
    public void ModifyingLinuxWrittenFilesCopiesOnWriteAndKeepsOthersIntact(string fixture)
    {
        var (path, grant) = FsOpsTests.Prepare(fixtures, fixture);
        var manifest = LinuxFixtures.Manifest("pop-ci-64m");
        byte[] expectedOrta;
        using (var c = TestImages.Open(path))
            expectedOrta = ReaderTests.ReadAll(c.OpenVolumes(keys: TestKeys.Of(c))[0].Volume!, c.OpenVolumes(keys: TestKeys.Of(c))[0].Volume!.Lookup("orta.bin"));
        var patch = new byte[10_000];
        new Random(5).NextBytes(patch);
        patch.CopyTo(expectedOrta, 123_456);

        using (var dev = new FileImageWriter(path, grant))
        using (var s = WriteSession.Open(dev, grant))
        {
            var v = s.Volume(0, TestKeys.First(s.Container));
            ulong orta = v.FindEntry(Inode.RootDirId, "orta.bin")!.FileId;
            v.Write(orta, 123_456, patch);                                         // kalıcı blokların ortası: kopyala-yaz
            ulong buyuk = v.FindEntry(Inode.RootDirId, "buyuk.bin")!.FileId;
            v.SetSize(buyuk, 5000);                                                 // kısalt (son blok sıfırlanır)
            ulong seyrek = v.FindEntry(Inode.RootDirId, "seyrek.bin")!.FileId;
            v.Write(seyrek, 10 * 4096 + 7, "deliğe yazı"u8);                       // deliğin içine
            v.SetSize(seyrek, 60L * 1024 * 1024 is var n ? (ulong)n : 0);           // uzat
            s.Close();
        }

        using var c2 = TestImages.Open(path);
        var r = FullCheck.Run(c2, keys: TestKeys.Of(c2));
        Assert.True(r.IsClean, string.Join("\n", r.Errors));
        var vol = c2.OpenVolumes(keys: TestKeys.Of(c2))[0].Volume!;
        Assert.Equal(expectedOrta, ReaderTests.ReadAll(vol, vol.Lookup("orta.bin")));
        Assert.Equal(5000UL, vol.GetFileSize(vol.Lookup("buyuk.bin")));
        var sey = ReaderTests.ReadAll(vol, vol.Lookup("seyrek.bin"));
        Assert.Equal(60L * 1024 * 1024, sey.Length);
        Assert.Equal("deliğe yazı"u8.ToArray(), sey.AsSpan(10 * 4096 + 7, "deliğe yazı"u8.Length).ToArray());
        Assert.Equal("ORTA"u8.ToArray(), sey.AsSpan(26214400, 4).ToArray());
        // değiştirilmeyen dosyalar aynen
        foreach (var (p, hash) in manifest)
        {
            if (p is "orta.bin" or "buyuk.bin" or "seyrek.bin") continue;
            Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(ReaderTests.ReadAll(vol, vol.Lookup(p)))));
        }
        DevOutput.Save(path, fixture.StartsWith("enc-") ? "veri-linux-dosyalari-enc" : "veri-linux-dosyalari");
    }
}
