using System.Security.Cryptography;
using Apfs.Diagnostics;
using Apfs.IO;
using Apfs.Mount;
using Apfs.Write;

namespace Apfs.Tests;

/// <summary>
/// Faz 3: gerçek WinFsp bağlaması (yönetici gerekmez, görüntü dosyası). Windows'un kendi dosya API'leriyle yapılan işlemler
/// sürücü ayrıldıktan sonra diskte aynen bulunmalı ve tam denetimden geçmelidir.
/// </summary>
[Collection("WinFsp")]
public class WritableMountTests(LinuxFixtures fixtures) : IClassFixture<LinuxFixtures>
{
    [Fact]
    public void WindowsFileOperationsLandOnDisk()
    {
        var (path, grant) = FsOpsTests.Prepare(fixtures, "pop-ci-64m");
        var big = new byte[5 * 1024 * 1024 + 123];
        new Random(1).NextBytes(big);
        string src = Path.Combine(Path.GetTempPath(), $"apfs-kaynak-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(src, big);
        var errors = new List<string>();
        try
        {
            using (var dev = new FileImageWriter(path, grant))
            {
                var s = WriteSession.Open(dev, grant);
                using (var m = ApfsWritableMount.Mount(s, 0, null, e => { lock (errors) errors.Add(e); }))
                {
                    string root = m.MountPoint + "\\";
                    Directory.CreateDirectory(root + "Windows Klasörü\\İç İçe");
                    File.Copy(src, root + "Windows Klasörü\\büyük.bin");                       // CopyFileEx: önce boyut, sonra yazma
                    File.WriteAllText(root + "Windows Klasörü\\not.txt", "Merhaba APFS ğüşıöç\r\n");
                    File.AppendAllText(root + "Windows Klasörü\\not.txt", "ikinci satır\r\n");
                    // Office tarzı kaydetme: geçici dosya → asıl dosya silinir → geçici yeniden adlandırılır
                    File.WriteAllText(root + "Windows Klasörü\\~belge.tmp", "yeni sürüm");
                    File.WriteAllText(root + "Windows Klasörü\\belge.docx", "eski sürüm");
                    File.Delete(root + "Windows Klasörü\\belge.docx");
                    File.Move(root + "Windows Klasörü\\~belge.tmp", root + "Windows Klasörü\\belge.docx");
                    File.Move(root + "orta.bin", root + "Windows Klasörü\\İç İçe\\orta.bin");    // Linux dosyasını taşı
                    File.Copy(root + "buyuk.bin", root + "buyuk-kopya.bin");                   // APFS içinde kopya
                    using (var fs = new FileStream(root + "seyrek.bin", FileMode.Open, FileAccess.ReadWrite))
                    {
                        fs.Position = 1000;
                        fs.Write("ortaya yazıldı"u8);
                        fs.SetLength(10_000_000);                                              // kısalt
                    }
                    Directory.Delete(root + "cok", recursive: true);                           // 1333 dosyalı klasör
                    File.SetAttributes(root + "Windows Klasörü\\not.txt", FileAttributes.Hidden);
                    File.SetLastWriteTimeUtc(root + "bos.txt", new DateTime(2021, 5, 6, 7, 8, 9, DateTimeKind.Utc));
                    Assert.Throws<IOException>(() => Directory.Delete(root + "Belgeler"));    // dolu klasör

                    // Bağlıyken okuma (oturum görüntüsünden)
                    Assert.Equal(big, File.ReadAllBytes(root + "Windows Klasörü\\büyük.bin"));
                    Assert.Equal("yeni sürüm", File.ReadAllText(root + "Windows Klasörü\\belge.docx"));
                    Assert.Contains("büyük.bin", Directory.GetFiles(root + "Windows Klasörü").Select(Path.GetFileName));
                    Assert.False(Directory.Exists(root + "cok"));
                    var di = new DriveInfo(m.MountPoint);
                    Assert.True(di.AvailableFreeSpace > 0);
                }
            }

            Assert.Empty(errors); // sürücü işlem sırasında hata bildirmedi

            // Ayrıldıktan sonra disk
            using var c = TestImages.Open(path);
            var r = FullCheck.Run(c);
            Assert.True(r.IsClean, string.Join("\n", r.Errors));
            Assert.Empty(r.Warnings); // düzgün kapatıldı (blok 0 güncel)
            var v = c.OpenVolumes()[0].Volume!;
            Assert.Equal(SHA256.HashData(big), SHA256.HashData(ReaderTests.ReadAll(v, v.Lookup("Windows Klasörü/büyük.bin"))));
            Assert.Equal("Merhaba APFS ğüşıöç\r\nikinci satır\r\n"u8.ToArray(), ReaderTests.ReadAll(v, v.Lookup("Windows Klasörü/not.txt")));
            Assert.True(v.Lookup("Windows Klasörü/not.txt").IsHidden);
            Assert.Equal("yeni sürüm"u8.ToArray(), ReaderTests.ReadAll(v, v.Lookup("Windows Klasörü/belge.docx")));
            Assert.Equal(3145728UL, v.GetFileSize(v.Lookup("Windows Klasörü/İç İçe/orta.bin")));
            Assert.Equal(ReaderTests.ReadAll(v, v.Lookup("buyuk.bin")), ReaderTests.ReadAll(v, v.Lookup("buyuk-kopya.bin")));
            var sey = ReaderTests.ReadAll(v, v.Lookup("seyrek.bin"));
            Assert.Equal(10_000_000, sey.Length);
            Assert.Equal("ortaya yazıldı"u8.ToArray(), sey.AsSpan(1000, "ortaya yazıldı"u8.Length).ToArray());
            Assert.Throws<ApfsException>(() => v.Lookup("cok"));
            Assert.Equal(new DateTime(2021, 5, 6, 7, 8, 9, DateTimeKind.Utc), Apfs.Volume.Inode.ToDateTime(v.Lookup("bos.txt").ModTimeNs));
            DevOutput.Save(path, "winfsp-islemler");
        }
        finally
        {
            File.Delete(src);
        }
    }

    /// <summary>
    /// Yazılabilir sürücüde Windows önbelleği kapalı kalmalı: açıkken yavaş USB bellekte Gezgin'in büyük kopyası önbellek
    /// frenlemesinde kilitlendi (2026-10-05; 15 MB/sn'ye yavaşlatılmış görüntüyle yeniden üretildi, kapatınca geçti).
    /// WinFsp önbelleği yalnız FileInfoTimeout "sonsuz"da açar.
    /// </summary>
    [Fact]
    public void WritableMountKeepsWindowsCacheOff()
    {
        var (path, grant) = FsOpsTests.Prepare(fixtures, "mk-ci-64m");
        using var dev = new FileImageWriter(path, grant);
        var s = WriteSession.Open(dev, grant);
        using var fs = new ApfsWritableFileSystem(s, 0);
        var host = new Fsp.FileSystemHost(fs);
        Assert.Equal(ApfsFileSystem.StatusSuccess, fs.Init(host));
        Assert.NotEqual(uint.MaxValue, host.FileInfoTimeout);
        host.Dispose();
        s.Close();
    }

    /// <summary>Önbelleksiz sürücüde belleğe eşlenmiş dosyaya yazma (bazı uygulamalar dosyaları böyle düzenler) diske ulaşmalı.</summary>
    [Fact]
    public void MemoryMappedWriteLandsOnDisk()
    {
        var (path, grant) = FsOpsTests.Prepare(fixtures, "mk-ci-64m");
        var errors = new List<string>();
        using (var dev = new FileImageWriter(path, grant))
        {
            var s = WriteSession.Open(dev, grant);
            using var m = ApfsWritableMount.Mount(s, 0, null, e => { lock (errors) errors.Add(e); });
            string file = m.MountPoint + "\\eşlem.bin";
            File.WriteAllBytes(file, new byte[1 << 20]);
            using (var mmf = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(file, FileMode.Open, null, 0,
                       System.IO.MemoryMappedFiles.MemoryMappedFileAccess.ReadWrite))
            using (var acc = mmf.CreateViewAccessor())
            {
                acc.WriteArray(4096 * 3 + 7, "eşlenmiş yazma"u8.ToArray(), 0, "eşlenmiş yazma"u8.Length);
                acc.Flush();
            }
        }
        Assert.Empty(errors);
        using var c = TestImages.Open(path);
        Assert.True(FullCheck.Run(c).IsClean);
        var v = c.OpenVolumes()[0].Volume!;
        var data = ReaderTests.ReadAll(v, v.Lookup("eşlem.bin"));
        Assert.Equal(1 << 20, data.Length);
        Assert.Equal("eşlenmiş yazma"u8.ToArray(), data.AsSpan(4096 * 3 + 7, "eşlenmiş yazma"u8.Length).ToArray());
    }
}
