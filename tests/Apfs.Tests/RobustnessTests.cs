using System.Diagnostics;
using Apfs.Container;
using Apfs.IO;
using Apfs.Mount;
using Apfs.Partitions;
using Apfs.Util;
using Apfs.Volume;

namespace Apfs.Tests;

/// <summary>M5: çoklu birim, bozuk diskler (fuzz), USB'nin okuma sırasında çekilmesi.</summary>
public class RobustnessTests(TestImages images) : IClassFixture<TestImages>
{
    [Fact]
    public void MultiVolumeContainerOpensEachVolumeSeparately()
    {
        var (path, _) = images.Get("multivolume");
        using var c = TestImages.Open(path);
        var vols = c.OpenVolumes().Select(r => r.Volume!).ToList();
        Assert.Equal(["Belgeler", "Fotoğraflar", "Preboot", "Recovery"], vols.Select(v => v.Name));
        Assert.Equal([true, true, false, false], vols.Select(v => v.IsUserVisible));
        foreach (var v in vols)
            Assert.Equal(v.Name + "\n", System.Text.Encoding.UTF8.GetString(ReaderTests.ReadAll(v, v.Lookup("/hangi-birim.txt"))));
    }

    [Fact]
    public void CorruptVolumeDoesNotHideTheOthers()
    {
        var (src, _) = images.Get("multivolume");
        var bytes = File.ReadAllBytes(src);
        ulong apsb;
        using (var c = ApfsContainer.Open(new MemoryDevice(bytes)))
            apsb = c.Omap.Resolve(c.Superblock.FsOids[1], c.Xid).Paddr; // "Fotoğraflar" birim süper bloğu
        bytes[(int)apsb * 4096 + 1000] ^= 0xFF;

        using var bad = ApfsContainer.Open(new MemoryDevice(bytes));
        var results = bad.OpenVolumes();
        Assert.Null(results[1].Volume);
        Assert.Equal(ApfsErrorCode.ChecksumMismatch, results[1].Error!.Code);
        Assert.Contains("sağlama toplamı", results[1].Error!.Message);
        Assert.Equal(["Belgeler", "Preboot", "Recovery"], results.Where(r => r.Volume is not null).Select(r => r.Volume!.Name));
        var v0 = results[0].Volume!;
        Assert.Equal("Belgeler\n", System.Text.Encoding.UTF8.GetString(ReaderTests.ReadAll(v0, v0.Lookup("/hangi-birim.txt"))));
    }

    /// <summary>
    /// Fuzz: görüntülerin metadata bloklarına rastgele bayt, alan ve blok bozulmaları uygulanır; her deneme 5 saniye sınırlıdır.
    /// Sonuç ya geçerli veri ya da ApfsException olmalıdır: başka istisna (çökme) veya zaman aşımı (donma) hatadır.
    /// </summary>
    [Fact]
    public async Task FuzzedImagesNeverCrashOrHang()
    {
        var sources = new[] { "basic", "unicode", "compressed", "multivolume", "manyfiles" }
            .Select(s => File.ReadAllBytes(images.Get(s).Path)).ToArray();
        var metaBlocks = sources.Select(img => Enumerable.Range(0, img.Length / 4096)
            .Where(b => Fletcher64.Verify(img.AsSpan(b * 4096, 4096))).ToArray()).ToArray();

        var rnd = new Random(20261004);
        int trials = 2000, apfsErrors = 0, clean = 0;
        var failures = new List<string>();
        for (int t = 0; t < trials; t++)
        {
            int which = rnd.Next(sources.Length);
            var img = (byte[])sources[which].Clone();
            int mode = rnd.Next(4);
            int block = metaBlocks[which][rnd.Next(metaBlocks[which].Length)];
            int baseOff = block * 4096;
            switch (mode)
            {
                case 0: // birkaç rastgele bayt
                    for (int k = rnd.Next(1, 8); k > 0; k--) img[baseOff + rnd.Next(4096)] ^= (byte)rnd.Next(1, 256);
                    break;
                case 1: // alan bozma: 2/4/8 baytlık değere uç değer yaz
                {
                    int off = baseOff + 32 + rnd.Next(4096 - 40) / 2 * 2;
                    ulong v = rnd.Next(4) switch { 0 => 0, 1 => ulong.MaxValue, 2 => (ulong)rnd.NextInt64(), _ => 1 };
                    int width = new[] { 2, 4, 8 }[rnd.Next(3)];
                    for (int i = 0; i < width && off + i < img.Length; i++) img[off + i] = (byte)(v >> (8 * i));
                    break;
                }
                case 2: // tüm bloğu başka bir blokla değiştir (yanlış ama sağlaması geçerli nesne)
                {
                    int other = metaBlocks[which][rnd.Next(metaBlocks[which].Length)];
                    Array.Copy(img, other * 4096, img, baseOff, 4096);
                    break;
                }
                default: // sağlama toplamını yeniden hesaplayarak içeriği boz (doğrulamayı atlatan bozulma)
                {
                    for (int k = rnd.Next(1, 6); k > 0; k--) img[baseOff + 32 + rnd.Next(4096 - 32)] ^= (byte)rnd.Next(1, 256);
                    BitConverter.TryWriteBytes(img.AsSpan(baseOff, 8), Fletcher64.Compute(img.AsSpan(baseOff, 4096)));
                    break;
                }
            }

            var task = Task.Run(() =>
            {
                try
                {
                    using var c = ApfsContainer.Open(new MemoryDevice(img));
                    foreach (var r in c.OpenVolumes())
                        if (r.Volume is { } v) Traverse(v);
                    return (Exception?)null;
                }
                catch (Exception e) { return e; }
            });
            if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5))) != task)
            {
                failures.Add($"deneme {t}: 5 sn içinde bitmedi (mod {mode}, blok {block})");
                continue;
            }
            switch (await task)
            {
                case null: clean++; break;
                case ApfsException: apfsErrors++; break;
                case var e: failures.Add($"deneme {t} (mod {mode}, blok {block}): {e.GetType().Name}: {e.Message}"); break;
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
        Assert.True(apfsErrors > 100, $"bozulmaların çoğu fark edilmeli (ApfsException {apfsErrors}, temiz {clean})");
    }

    [Fact]
    public void UnpluggedDeviceGivesIoErrorAndMapsToDeviceNotConnected()
    {
        var (path, _) = images.Get("large");
        var inner = new FileImageDevice(path);
        var faulty = new UnpluggableDevice(inner);
        using var c = ApfsContainer.Open(faulty, ownsDevice: true);
        var v = c.OpenVolumes()[0].Volume!;
        var reader = v.OpenFile(v.Lookup("/orta.bin"));
        var buf = new byte[1 << 20];
        Assert.Equal(buf.Length, reader.Read(0, buf));

        faulty.Unplugged = true; // okuma sırasında USB çekildi
        var ex = Assert.Throws<ApfsException>(() => reader.Read(100L << 20, buf));
        Assert.Equal(ApfsErrorCode.IoError, ex.Code);
        Assert.Contains("çıkarılmış", ex.Message);
        Assert.Equal(unchecked((int)0xC000009D), ApfsFileSystem.ToNtStatus(ex.Code)); // STATUS_DEVICE_NOT_CONNECTED
        // Diske giden başka okumalar da aynı anlaşılır hatayı verir (önbellekteki metadata ise hâlâ okunabilir; bu zararsızdır)
        var video = v.OpenFile(v.Lookup("/Videolar/tatil.mov"));
        Assert.Equal(ApfsErrorCode.IoError, Assert.Throws<ApfsException>(() => video.Read(5L << 30, new byte[4096])).Code);
    }

    [Theory]
    [InlineData(ApfsErrorCode.NotFound, 0xC0000034u)]
    [InlineData(ApfsErrorCode.Corrupt, 0xC0000102u)]
    [InlineData(ApfsErrorCode.ChecksumMismatch, 0xC0000102u)]
    [InlineData(ApfsErrorCode.Unsupported, 0xC00000BBu)]
    [InlineData(ApfsErrorCode.Encrypted, 0xC00000BBu)]
    [InlineData(ApfsErrorCode.IoError, 0xC000009Du)]
    public void ErrorsMapToMeaningfulNtStatus(ApfsErrorCode code, uint status) =>
        Assert.Equal(unchecked((int)status), ApfsFileSystem.ToNtStatus(code));

    private static void Traverse(ApfsVolume v)
    {
        int budget = 5000;
        void Walk(Inode dir, int depth)
        {
            if (depth > 32) return;
            foreach (var e in v.ListDirectory(dir.Id))
            {
                if (--budget < 0) return;
                var ino = v.TryGetInode(e.FileId);
                if (ino is null) continue;
                _ = v.GetFileSize(ino);
                if (ino.IsDirectory) Walk(ino, depth + 1);
                else if (ino.IsRegularFile)
                {
                    var r = v.OpenFile(ino);
                    var buf = new byte[Math.Min(r.Length, 1 << 20)];
                    r.Read(0, buf);
                    if (r.Length > (ulong)buf.Length) r.Read(r.Length - 100, new byte[100]);
                }
                else if (ino.IsSymlink) v.ReadSymlinkTarget(ino.Id);
            }
        }
        Walk(v.Root, 0);
    }
}

/// <summary>Bellekteki görüntü (testler için).</summary>
internal sealed class MemoryDevice(byte[] data) : IBlockDevice
{
    public long Length => data.Length;
    public int SectorSize => 512;

    public void Read(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset > data.Length - buffer.Length)
            throw new ApfsException(ApfsErrorCode.Corrupt, "aygıt sınırı dışında okuma");
        data.AsSpan((int)offset, buffer.Length).CopyTo(buffer);
    }

    public void Dispose() { }
}

/// <summary>Çekilebilen USB disk benzetimi: Unplugged olduktan sonra her okuma G/Ç hatası verir.</summary>
internal sealed class UnpluggableDevice(IBlockDevice inner) : IBlockDevice
{
    public volatile bool Unplugged;
    public long Length => inner.Length;
    public int SectorSize => inner.SectorSize;

    public void Read(long offset, Span<byte> buffer)
    {
        if (Unplugged) throw new ApfsException(ApfsErrorCode.IoError, "aygıt bağlı değil");
        inner.Read(offset, buffer);
    }

    public void Dispose() => inner.Dispose();
}
