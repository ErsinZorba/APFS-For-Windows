using System.IO.Compression;
using Apfs.Container;
using Apfs.Diagnostics;
using Apfs.IO;
using Apfs.Write;

namespace Apfs.Tests;

/// <summary>
/// Faz 1.7: kesinti benzetimi. Kayıt eden aygıt her yazmayı ve her bariyeri (Flush) günlüğe alır. Disk önbelleği modeli:
/// bariyerden önceki yazmalar kalıcıdır; iki bariyer arasındaki yazmaların HERHANGİ bir alt kümesi kalıcı olmuş olabilir
/// (sıra bozulması dahil); 4 KiB'lık blok yazımı bölünmez (blok 0 hariç: süper blok yarım yazılırsa sağlama toplamı tutmaz).
/// Her kesim noktasında görüntü yeniden kurulur: disk ya önceki ya yeni checkpoint ile açılmalı ve tam denetimden 0 hatayla geçmelidir.
/// </summary>
public class CrashTests
{
    private sealed class MemoryDevice(byte[] data) : IBlockDevice
    {
        public long Length => data.Length;
        public int SectorSize => 512;
        public void Read(long offset, Span<byte> buffer) => data.AsSpan((int)offset, buffer.Length).CopyTo(buffer);
        public void Dispose() { }
    }

    private abstract record Op;
    private sealed record WriteOp(long Offset, byte[] Data) : Op;
    private sealed record BarrierOp : Op;

    /// <summary>Yazmaları günlüğe alan aygıt; okumalar o ana kadarki bütün yazmaları görür (oturumun gördüğü disk).</summary>
    private sealed class RecordingDevice : IWritableBlockDevice
    {
        private readonly byte[] _current;
        public readonly List<Op> Log = [];
        public RecordingDevice(byte[] image, WriteGrant grant) { ArgumentNullException.ThrowIfNull(grant); _current = (byte[])image.Clone(); }
        public long Length => _current.Length;
        public int SectorSize => 512;
        public void Read(long offset, Span<byte> buffer) => _current.AsSpan((int)offset, buffer.Length).CopyTo(buffer);
        public void Write(long offset, ReadOnlySpan<byte> data) { data.CopyTo(_current.AsSpan((int)offset)); Log.Add(new WriteOp(offset, data.ToArray())); }
        public void Flush() => Log.Add(new BarrierOp());
        public void Dispose() { }
    }

    private static byte[] LoadFixture(string name)
    {
        if (name.StartsWith("reaper-", StringComparison.Ordinal))
        {
            // macOS'un silme sonrası bıraktığı boş reap list'li varyant (ReaperFixture); her kesinti noktasında liste korunmalı
            string tmp = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(tmp, LoadFixture(name[7..]));
                ReaperFixture.InjectIdleReapList(tmp);
                return File.ReadAllBytes(tmp);
            }
            finally { File.Delete(tmp); }
        }
        if (name.StartsWith("enc-", StringComparison.Ordinal))
        {
            // Şifreli varyant: düz görüntü geçici dosyada ImageEncryptor ile dönüştürülür
            string plain = Path.GetTempFileName(), enc = plain + ".enc";
            try
            {
                File.WriteAllBytes(plain, LoadFixture(name[4..]));
                ImageBuilder.ImageEncryptor.Encrypt(plain, enc);
                return File.ReadAllBytes(enc);
            }
            finally { File.Delete(plain); File.Delete(enc); }
        }
        using var input = new GZipStream(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "data", "linux", name + ".img.gz")), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        input.CopyTo(ms);
        return ms.ToArray();
    }

    [Theory]
    [InlineData("mk-ci-64m")]
    [InlineData("pop-ci-64m")]
    [InlineData("enc-pop-ci-64m")]
    [InlineData("reaper-pop-ci-64m")]
    public void EveryCrashPointOpensOldOrNewCheckpointCleanly(string name)
    {
        var image = LoadFixture(name);
        ulong startXid;
        WriteGrant grant;
        using (var c = ApfsContainer.Open(new MemoryDevice(image)))
        {
            startXid = c.Xid;
            grant = WriteGate.Evaluate(c, TestKeys.Of(c)).Grant!;
        }

        var dev = new RecordingDevice(image, grant);
        var expected = new Dictionary<ulong, Dictionary<string, string>> { [startXid] = [] }; // xid → yol → içerik özeti
        using (var s = WriteSession.Open(dev, grant))
        {
            var v = s.Volume(0, TestKeys.First(s.Container));
            for (int i = 0; i < 3; i++)
            {
                // her işlemde dosya sistemi değişikliği: klasör + dosyalar (düğüm bölünmesi dahil)
                ulong dir = v.CreateDirectory(Apfs.Volume.Inode.RootDirId, $"kesinti-{i}");
                for (int j = 0; j < 60; j++) v.CreateFile(dir, $"dosya-{j}.txt");
                // veri: yeni dosyaya yaz, bir öncekinin ortasını değiştir (kalıcı blokların kopyala-yaz'ı), kısalt
                var data = new byte[300_000];
                new Random(i).NextBytes(data);
                ulong f = v.CreateFile(dir, "veri.bin");
                v.Write(f, 0, data);
                if (i > 0)
                {
                    ulong prev = v.FindEntry(v.FindEntry(Apfs.Volume.Inode.RootDirId, $"kesinti-{i - 1}")!.FileId, "veri.bin")!.FileId;
                    v.Write(prev, 50_000, data.AsSpan(0, 20_000));
                    v.SetSize(prev, 123_457);
                }
                s.Commit();
                // Bu checkpoint'te diskte olması gereken içerikler
                var snap = new Dictionary<string, string>();
                for (int k = 0; k <= i; k++)
                {
                    ulong d = v.FindEntry(Apfs.Volume.Inode.RootDirId, $"kesinti-{k}")!.FileId;
                    ulong fid = v.FindEntry(d, "veri.bin")!.FileId;
                    var content = new byte[v.GetSize(fid)];
                    v.Read(fid, 0, content);
                    snap[$"kesinti-{k}/veri.bin"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));
                }
                expected[s.CommittedXid] = snap;
            }
            var lastSnap = expected[s.CommittedXid];
            s.Close();
            expected[s.CommittedXid] = lastSnap; // Close'un commit'i içeriği değiştirmez
        }

        var durable = (byte[])image.Clone();
        var pending = new List<WriteOp>();
        ulong lastDurableXid = startXid;
        var rnd = new Random(42);
        int scenarios = 0;
        foreach (var op in dev.Log)
        {
            if (op is WriteOp w) { pending.Add(w); continue; }

            // Bariyer: bu noktaya kadar kesintide kalıcı olabilecek her durum
            var subsets = new List<IEnumerable<WriteOp>> { Array.Empty<WriteOp>(), pending.ToList() };
            for (int k = 1; k < pending.Count; k++) subsets.Add(pending.Take(k).ToList());   // sıralı önekler
            for (int k = 0; k < 25 && pending.Count > 1; k++)                                 // rastgele alt kümeler
                subsets.Add(pending.Where(_ => rnd.Next(2) == 0).ToList());
            foreach (var subset in subsets)
            {
                var img = (byte[])durable.Clone();
                foreach (var x in subset) x.Data.CopyTo(img, x.Offset);
                lastDurableXid = Verify(img, lastDurableXid, name, expected);
                scenarios++;
            }
            // Yarım yazılmış süper blok (tanımlayıcı halkasında): sağlama toplamı tutmaz, eski checkpoint kullanılmalı
            foreach (var x in pending.Where(p => p.Offset != 0 && IsNxSuperblock(p.Data)))
            {
                var img = (byte[])durable.Clone();
                foreach (var y in pending.Where(p => p != x)) y.Data.CopyTo(img, y.Offset);
                x.Data.AsSpan(0, 2048).CopyTo(img.AsSpan((int)x.Offset));
                Verify(img, lastDurableXid, name, expected);
                scenarios++;
            }

            foreach (var x in pending) x.Data.CopyTo(durable, x.Offset);
            pending.Clear();
            lastDurableXid = Verify(durable, lastDurableXid, name, expected);
        }
        Assert.True(scenarios > 50, $"yalnızca {scenarios} senaryo");
        Assert.Equal(startXid + 4, lastDurableXid);
    }

    private static bool IsNxSuperblock(byte[] b) =>
        b.Length >= 40 && BitConverter.ToUInt32(b, 32) == ApfsConstants.NxMagic;

    /// <summary>Görüntü açılmalı, xid önceki kalıcı xid ya da onun bir sonrakisi olmalı, tam denetim 0 hata vermeli.</summary>
    private static ulong Verify(byte[] img, ulong lastDurableXid, string name, Dictionary<ulong, Dictionary<string, string>> expected)
    {
        using var c = ApfsContainer.Open(new MemoryDevice(img));
        Assert.True(c.Xid == lastDurableXid || c.Xid == lastDurableXid + 1, $"{name}: xid {c.Xid}, beklenen {lastDurableXid} veya +1");
        var r = FullCheck.Run(c, keys: TestKeys.Of(c));
        Assert.True(r.IsClean, $"{name} xid {c.Xid}: " + string.Join("\n", r.Errors));
        // Açılan checkpoint'teki dosya içerikleri o checkpoint yazıldığı andaki içeriklerle aynı olmalı
        if (expected.TryGetValue(c.Xid, out var files))
        {
            var vol = c.OpenVolumes(keys: TestKeys.Of(c))[0].Volume!;
            foreach (var (path, hash) in files)
                Assert.Equal(hash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ReaderTests.ReadAll(vol, vol.Lookup(path)))));
        }
        return c.Xid;
    }
}
