// Geliştirme aracı (yalnızca testler). Kipler:
//   apfs-devwriter <görüntü.img>      görüntüyü yazma kapısından geçirip yazılabilir bağlar, öldürülene kadar sunar (Faz 3.5).
//   apfs-devwriter --vhd-disk N       YALNIZCA dosya tabanlı sanal disk (takılmış VHD) olan \\.\PhysicalDriveN'i yazılabilir bağlar
//                                     (Faz 0.4/5.1, yönetici); standart girdiden "BITIR" gelince düzgün ayırır ve kapatır.
//   apfs-devwriter --usb-find AD      USB bağlantılı ve APFS bölümlerinden TAM OLARAK BİRİ (kapsayıcısı tek birimli) AD adını
//                                     taşıyan diskleri listeler: "BULUNDU disk<TAB>açıklama<TAB>boyut<TAB>bölüm sırası" (salt okunur,
//                                     yönetici). Çok bölümlü bellekte (2026-10-11: şifresiz + şifreli) yalnız o bölüm kullanılır.
//   apfs-devwriter --usb-disk N --name AD
//                                     Yalnızca yukarıdaki koşulların hepsini sağlayan \\.\PhysicalDriveN'i (Faz 5.2/5.3, kullanıcının
//                                     test için Mac'te silip adlandırdığı bellek) yazılabilir bağlar; sürücü harfi genel olur (Gezgin
//                                     görsün). "BITIR" ile düzgün kapatır.
// Diğer bütün diskler reddedilir: bu araç gerçek veriye yazmaz.
// Şifreli birim (2026-10-11): --password-stdin eklenirse parola standart girdinin İLK satırından okunur (komut satırında ve
// dosyada durmaz); ardından gelen satırlar eskisi gibi ("BITIR"). Şifreli bölüm adıyla parolasız bulunur (ad şifresizdir).
using System.Runtime.InteropServices;
using Apfs;
using Apfs.Container;
using Apfs.Crypto;
using Apfs.IO;
using Apfs.Mount;
using Apfs.Partitions;
using Apfs.Volume;
using Apfs.Windows;
using Apfs.Write;

// (parola StdIn.Password'da; yerel işlevler statik olduğu için)
if (args.Contains("--password-stdin"))
{
    args = [.. args.Where(a => a != "--password-stdin")];
    StdIn.Password = StdIn.Reader.ReadLine();
    if (string.IsNullOrEmpty(StdIn.Password)) { Console.Error.WriteLine("parola okunamadı"); return 2; }
}

if (args.Length == 2 && args[0] == "--vhd-disk" && int.TryParse(args[1], out int disk)) return VhdMode(disk);
if (args.Length == 2 && args[0] == "--usb-find") return UsbFind(args[1]);
if (args.Length == 4 && args[0] == "--used-hash" && int.TryParse(args[1], out int hdisk) && args[2] == "--partition" && int.TryParse(args[3], out int hpart))
    return UsedHash(hdisk, hpart);
if (args.Length == 4 && args[0] == "--usb-disk" && int.TryParse(args[1], out int usb) && args[2] == "--name") return UsbMode(usb, args[3]);
if (args.Length >= 6 && args[0] == "--usb-surface" && int.TryParse(args[1], out int sdisk) && args[2] == "--name" && args[4] == "--mb"
    && int.TryParse(args[5], out int mb))
    return Surface(sdisk, args[3], mb, verifyOnly: args.Contains("--verify-only"));
if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("kullanım: apfs-devwriter <görüntü.img> | --vhd-disk N | --usb-find AD | --usb-disk N --name AD");
    return 2;
}

WriteGrant grant;
VolumeKey? imageKey;
using (var probe = ApfsContainer.Open(new FileImageDevice(args[0]), ownsDevice: true))
{
    imageKey = KeyFor(probe, StdIn.Password);
    var g = WriteGate.Evaluate(probe, imageKey is null ? null : [imageKey]);
    if (!g.Allowed) { Console.Error.WriteLine(string.Join("; ", g.Reasons)); return 3; }
    grant = g.Grant!;
}
IWritableBlockDevice dev = new FileImageWriter(args[0], grant);
// APFS_DEV_SLOW_MBPS: yavaş USB belleği taklit eder (2026-10-05: Gezgin'in kopyalaması yalnız yavaş bellekte takıldı)
if (int.TryParse(Environment.GetEnvironmentVariable("APFS_DEV_SLOW_MBPS"), out int slow) && slow > 0)
    dev = new SlowDevice(dev, slow, grant);
var session = WriteSession.Open(dev, grant);
session.Volume(0, imageKey);
var mount = ApfsWritableMount.Mount(session, 0, null);
Console.WriteLine($"HAZIR {mount.MountPoint}");
Console.Out.Flush();
// Yalnızca sürücüyü sunar; yazmayı başka bir süreç yapar (gerçekte Gezgin gibi). Aynı süreç kendi sürücüsüne yazarsa
// öldürüldüğünde kendi bekleyen G/Ç'sini beklerken kapanamaz.
Thread.Sleep(Timeout.Infinite);
return 0;

static int VhdMode(int disk)
{
    var info = DiskBus.Query(disk);
    if (info is null || !info.IsFileBackedVirtual)
    {
        Console.Error.WriteLine($"REDDEDİLDİ: Disk {disk} sanal disk değil ({info?.Description ?? "sorgulanamadı"}). Bu kip yalnızca takılmış VHD'lere yazar.");
        return 4;
    }
    using (var read = new RawDiskDevice(disk))
        if (PartitionLocator.FindApfsPartitions(read).Count != 1) { Console.Error.WriteLine("tek APFS bölümü yok"); return 3; }
    return Serve(disk, part: 0, globalLetter: null, StdIn.Password);
}

/// <summary>Kapsayıcının ilk birimi şifreliyse parolayla anahtarı (parola yoksa ya da birim şifresizse null).</summary>
static VolumeKey? KeyFor(ApfsContainer c, string? pw)
{
    ulong oid = c.Superblock.FsOids[0];
    if (pw is null || c.OpenVolumes()[0].Error?.Code != ApfsErrorCode.Encrypted) return null;
    return VolumeKey.Unlock(c, c.VolumeUuid(oid), pw);
}

/// <summary>Kilitli şifreli birimin adı (süper blokta şifresizdir; parola istenmez).</summary>
static string? LockedName(ApfsContainer c, ulong oid)
{
    string? name = null;
    try { ApfsVolume.Open(c, oid, info => { name = info.Name; return null; }); }
    catch (ApfsException) { }
    return name;
}

/// <summary>
/// SALT OKUNUR: APFS bölümünün yalnız alan yöneticisinde DOLU işaretli bloklarının SHA-256'sı (blok sırasıyla). Bölümün tamamının
/// özeti ucuz bellekte her okumada farklı çıkıyor (hiç yazılmamış flash; Parça 1 ölçümü); dolu bloklar kararlıdır. Bir bölüme
/// yazıldığında öbür bölümün değişmediğini göstermek için.
/// </summary>
static int UsedHash(int disk, int part)
{
    using var read = new RawDiskDevice(disk);
    using var c = SourceOpener.OpenContainer(read, part, ownsDevice: false);
    var sm = Spaceman.Read(c, Checkpoint.Read(c));
    using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
    ulong used = 0;
    foreach (var ch in sm.MainChunks())
    {
        var bm = sm.ReadChunkBitmap(ch);
        for (uint i = 0; i < ch.BlockCount; i++)
            if ((bm[i >> 3] & (1 << (int)(i & 7))) != 0) { sha.AppendData(c.Reader.ReadBlock(ch.Addr + i)); used++; }
    }
    Console.WriteLine($"DOLU {used} blok, xid {c.Xid}, SHA-256 {Convert.ToHexString(sha.GetHashAndReset())}");
    return 0;
}

static int UsbFind(string name)
{
    int found = 0;
    for (int d = 0; d < 32; d++)
    {
        var info = DiskBus.Query(d);
        if (info is null) continue;
        var (why, part) = UsbTarget(d, name, info);
        if (why is null)
        {
            using var dev = new RawDiskDevice(d);
            Console.WriteLine($"BULUNDU {d}\t{info.Description}\t{dev.Length / 1e9:N1} GB\t{part}");
            found++;
        }
        else if (info.IsUsb) Console.Error.WriteLine($"Disk {d} ({info.Description}): {why}");
    }
    return found == 1 ? 0 : 5;
}

static int UsbMode(int disk, string name)
{
    var info = DiskBus.Query(disk);
    var (why, part) = info is null ? ("sorgulanamadı", 0) : UsbTarget(disk, name, info);
    if (why is not null) { Console.Error.WriteLine($"REDDEDİLDİ: Disk {disk}: {why}"); return 4; }
    char letter = FreeLetter();
    return Serve(disk, part, letter, StdIn.Password);
}

/// <summary>
/// USB test belleği koşulları: USB, ≤512 GB ve APFS bölümlerinden TAM OLARAK BİRİNİN tek birimli kapsayıcısında birimin adı
/// birebir <paramref name="name"/>. Tutuyorsa (null, bölüm sırası), yoksa (ret nedeni, -1).
/// </summary>
static (string? Why, int Part) UsbTarget(int disk, string name, DiskBus.Info info)
{
    if (!info.IsUsb) return ("USB bağlantılı değil", -1);
    try
    {
        using var read = new RawDiskDevice(disk);
        if (read.Length <= 0 || read.Length > 512L * 1000 * 1000 * 1000) return ("512 GB'tan büyük (test belleği değil)", -1);
        int count = PartitionLocator.FindApfsPartitions(read).Count;
        if (count == 0) return ("APFS bölümü yok", -1);
        var hits = new List<int>();
        var notes = new List<string>();
        for (int p = 0; p < count; p++)
        {
            using var c = SourceOpener.OpenContainer(read, p, ownsDevice: false);
            var vols = c.OpenVolumes();
            if (vols.Count != 1) { notes.Add($"bölüm {p}: {vols.Count} birim"); continue; }
            string? volName = vols[0].Volume?.Name
                ?? (vols[0].Error?.Code == ApfsErrorCode.Encrypted ? LockedName(c, c.Superblock.FsOids[0]) : null);
            if (volName is null) { notes.Add($"bölüm {p}: birim açılamadı"); continue; }
            if (string.Equals(volName, name, StringComparison.Ordinal)) hits.Add(p);
            else notes.Add($"bölüm {p}: birim adı farklı");
        }
        if (hits.Count > 1) return ($"{hits.Count} bölümde aynı ad var (belirsiz)", -1);
        if (hits.Count == 0) return (string.Join("; ", notes), -1);
        return (null, hits[0]);
    }
    catch (Exception e) { return ($"okunamadı: {e.Message}", -1); }
}

/// <summary>
/// Donanım yüzey testi (2026-10-05, USB bellekte 1024 bayt önceki verinin 8 baytlık kopyası bulundu): kapsayıcının alan
/// yöneticisine göre TAMAMEN boş parçalarına (chunk) APFS yazıcısını hiç kullanmadan belirlenimci veri yazar, diske boşaltır ve
/// ayrı bir okuma tanıtıcısıyla geri okuyup karşılaştırır. Dolu bloklara ve hiçbir APFS yapısına dokunmaz.
/// Her 512 baytlık kesimin ilk 8 baytı kesimin diskteki bayt konumudur (yanlış yere yazılan kesim de görünsün).
/// </summary>
static int Surface(int disk, string name, int mb, bool verifyOnly)
{
    var info = DiskBus.Query(disk);
    var (why, part) = info is null ? ("sorgulanamadı", 0) : UsbTarget(disk, name, info);
    if (why is not null) { Console.Error.WriteLine($"REDDEDİLDİ: Disk {disk}: {why}"); return 4; }
    try
    {
        WriteGrant grant;
        long partOffset, partLength;
        List<(ulong Addr, uint Blocks)> chunks;
        uint bs;
        using (var read = new RawDiskDevice(disk))
        {
            var p = PartitionLocator.FindApfsPartitions(read)[part];
            partOffset = p.Offset; partLength = p.Length;
            using var c = SourceOpener.OpenContainer(read, part, ownsDevice: false);
            var g = WriteGate.Evaluate(c);
            if (!g.Allowed) { Console.Error.WriteLine("KAPI: " + string.Join("; ", g.Reasons)); return 3; }
            grant = g.Grant!;
            bs = c.Superblock.BlockSize;
            var sm = Spaceman.Read(c, Checkpoint.Read(c));
            long want = (long)mb * 1024 * 1024;
            chunks = [];
            long have = 0;
            // Sondan başa: yazıcı baştan ayırdığı için sondaki boş parçalar en uzun süre boş kalır
            foreach (var ch in sm.MainChunks().Where(x => x.FreeCount == x.BlockCount && x.BlockCount > 0).OrderByDescending(x => x.Addr))
            {
                if (have >= want) break;
                // İkinci güvence: parçanın bitmap'i tamamen sıfır olmalı ve parça bölümün içinde kalmalı
                if (sm.ReadChunkBitmap(ch).Any(b => b != 0)) continue;
                if ((long)(ch.Addr + ch.BlockCount) * bs > partLength) continue;
                // Kapsayıcının son parçası kısa olabilir (2026-10-05: 5110 blok); 1 MB'lık dilimlere bölünmeyeni atla
                if ((long)ch.BlockCount * bs % (1 << 20) != 0) continue;
                chunks.Add((ch.Addr, ch.BlockCount));
                have += (long)ch.BlockCount * bs;
            }
            if (have < want) { Console.Error.WriteLine($"yeterli boş parça yok ({have / 1048576} MB)"); return 3; }
        }
        Console.WriteLine($"Alan: {chunks.Count} boş parça, {chunks.Sum(x => (long)x.Blocks) * bs / 1048576} MB (blok {chunks[^1].Addr}…{chunks[0].Addr + chunks[0].Blocks})");

        const int Piece = 1 << 20;
        var buf = new byte[Piece];
        if (!verifyOnly)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var raw = new RawDiskWriter(disk, grant);
            foreach (var (addr, blocks) in chunks)
                for (long off = 0; off < (long)blocks * bs; off += Piece)
                {
                    long abs = partOffset + (long)addr * bs + off;
                    Fill(buf, abs);
                    raw.Write(abs, buf);
                }
            raw.Flush();
            Console.WriteLine($"Yazıldı ve diske boşaltıldı: {sw.Elapsed.TotalSeconds:N1} sn");
        }

        var expect = new byte[Piece];
        long bad = 0, words = 0;
        int shown = 0;
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        using (var read = new RawDiskDevice(disk))
            foreach (var (addr, blocks) in chunks)
                for (long off = 0; off < (long)blocks * bs; off += Piece)
                {
                    long abs = partOffset + (long)addr * bs + off;
                    read.Read(abs, buf);
                    Fill(expect, abs);
                    for (int i = 0; i < Piece; i += 8)
                    {
                        words++;
                        ulong got = BitConverter.ToUInt64(buf, i), exp = BitConverter.ToUInt64(expect, i);
                        if (got == exp) continue;
                        bad++;
                        if (shown++ >= 30) continue;
                        string src = "";
                        foreach (int d in new[] { 512, 1024, 2048, 4096 })
                            if (i >= d && got == BitConverter.ToUInt64(expect, i - d)) src = $" = {d} bayt önceki veri";
                        Console.WriteLine($"  FARK disk ofseti {abs + i} (kesim içi {(abs + i) % 512}): {got:X16} beklenen {exp:X16}{src}");
                    }
                }
        Console.WriteLine($"Geri okundu: {sw2.Elapsed.TotalSeconds:N1} sn; {words * 8 / 1048576} MB, farklı 8 baytlık sözcük: {bad}");
        Console.WriteLine(bad == 0 ? "SONUÇ: TEMİZ" : "SONUÇ: DONANIM HATASI (veri APFS yazıcısı kullanılmadan bozuldu)");
        return bad == 0 ? 0 : 6;
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"HATA: {e.GetType().Name}: {e.Message}");
        return 1;
    }

    static void Fill(byte[] b, long abs)
    {
        new Random(unchecked((int)(abs / 1048576 * 7919 + 17))).NextBytes(b);
        for (int s = 0; s < b.Length; s += 512) BitConverter.TryWriteBytes(b.AsSpan(s), abs + s);
    }
}

static char FreeLetter()
{
    // Yönetici süreci kullanıcının ağ sürücülerini görmez; onlar çoğunlukla sondaki harflerdedir (Z:, Y:…), ortadan başla.
    uint used = GetLogicalDrives();
    foreach (char l in "TSRQPONMLKJIHGF")
        if ((used & (1u << (l - 'A'))) == 0) return l;
    throw new InvalidOperationException("boş sürücü harfi yok");
}

static int Serve(int disk, int part, char? globalLetter, string? pw)
{
    // Disk çekilince her okuma aynı hatayı verir (2026-10-05: ~8700 satır); her iletiyi en çok 3 kez yaz, kalanını say.
    var seen = new Dictionary<string, int>();
    VolumeKey? key = null;
    try
    {
        WriteGrant grant;
        using (var read = new RawDiskDevice(disk))
        {
            using var c = SourceOpener.OpenContainer(read, part, ownsDevice: false);
            key = KeyFor(c, pw);
            var g = WriteGate.Evaluate(c, key is null ? null : [key]);
            if (g.Report is { } r)
            {
                foreach (var l in r.Info) Console.Error.WriteLine("DENETİM: " + l);
                foreach (var l in r.Warnings) Console.Error.WriteLine("DENETİM UYARI: " + l);
                foreach (var l in r.Errors) Console.Error.WriteLine("DENETİM HATA: " + l);
            }
            if (!g.Allowed) { Console.Error.WriteLine("KAPI: " + string.Join("; ", g.Reasons)); return 3; }
            grant = g.Grant!;
        }
        using var raw = new RawDiskWriter(disk, grant);
        var p = PartitionLocator.FindApfsPartitions(raw)[part];
        using var window = new WritableOffsetDevice(raw, p.Offset, p.Length, grant);
        var session = WriteSession.Open(window, grant);
        session.Volume(0, key);
        // \\.\X: = genel sürücü harfi: yönetici sürecinin bağladığı sürücüyü yönetici olmayan Gezgin de görür.
        string? mp = globalLetter is { } gl ? $@"\\.\{gl}:" : null;
        void OnError(string e)
        {
            lock (seen)
            {
                int n = seen[e] = seen.GetValueOrDefault(e) + 1;
                if (n <= 3) Console.Error.WriteLine("SÜRÜCÜ: " + e);
            }
        }
        var mount = ApfsWritableMount.Mount(session, 0, mp, OnError);
        Console.WriteLine($"HAZIR {(globalLetter is { } l2 ? $"{l2}:" : mount.MountPoint)}");
        Console.Out.Flush();
        while (StdIn.Reader.ReadLine() is { } line && line.Trim() != "BITIR") { }
        mount.Dispose(); // ayır → son commit + blok 0
        Console.WriteLine($"KAPANDI xid {session.CommittedXid}");
        return 0;
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"HATA: {e.GetType().Name}: {e.Message}");
        return 1;
    }
    finally
    {
        key?.Dispose();
        lock (seen)
            foreach (var (msg, n) in seen)
                if (n > 3) Console.Error.WriteLine($"SÜRÜCÜ: (aynı ileti {n - 3} kez daha) {msg}");
    }
}

[DllImport("kernel32.dll")]
static extern uint GetLogicalDrives();

/// <summary>Yazmaları saniyede <c>mbps</c> MB'a yavaşlatan sarmalayıcı (yalnız geliştirme; yavaş USB belleği taklit eder).</summary>
internal sealed class SlowDevice(IWritableBlockDevice inner, int mbps, WriteGrant grant) : IWritableBlockDevice
{
    private readonly WriteGrant _grant = grant;
    public long Length => inner.Length;
    public int SectorSize => inner.SectorSize;
    public void Read(long offset, Span<byte> buffer) => inner.Read(offset, buffer);
    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        inner.Write(offset, data);
        Thread.Sleep(TimeSpan.FromSeconds((double)data.Length / (mbps * 1048576.0)));
    }
    public void Flush() { inner.Flush(); Thread.Sleep(20); }
    public void Dispose() { GC.KeepAlive(_grant); inner.Dispose(); }
}

/// <summary>Diskin bağlantı türü ve adı (IOCTL_STORAGE_QUERY_PROPERTY); disk yalnızca sorgu erişimiyle (0) açılır.</summary>
internal static class DiskBus
{
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const int BusTypeUsb = 0x7, BusTypeFileBackedVirtual = 0xF;

    public sealed record Info(int BusType, string Vendor, string Product)
    {
        public bool IsUsb => BusType == BusTypeUsb;
        public bool IsFileBackedVirtual => BusType == BusTypeFileBackedVirtual && Product.Contains("Virtual Disk", StringComparison.OrdinalIgnoreCase);
        public string Description => $"bağlantı türü {BusType}, {Vendor} {Product}".Trim();
    }

    public static Info? Query(int disk)
    {
        using var h = CreateFileW($@"\\.\PhysicalDrive{disk}", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;
        var query = new byte[12]; // PropertyId=StorageDeviceProperty(0), QueryType=PropertyStandardQuery(0)
        var buf = new byte[1024];
        if (!DeviceIoControl(h, IoctlStorageQueryProperty, query, query.Length, buf, buf.Length, out _, IntPtr.Zero)) return null;
        return new Info(BitConverter.ToInt32(buf, 28), ReadString(buf, BitConverter.ToInt32(buf, 12)).Trim(),
            ReadString(buf, BitConverter.ToInt32(buf, 16)).Trim());
    }

    private static string ReadString(byte[] b, int off)
    {
        if (off <= 0 || off >= b.Length) return "";
        int end = Array.IndexOf(b, (byte)0, off);
        return System.Text.Encoding.ASCII.GetString(b, off, (end < 0 ? b.Length : end) - off);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code, byte[]? inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr overlapped);
}

/// <summary>Standart girdinin tek UTF-8 okuyucusu: önce parola (--password-stdin), sonra "BITIR" aynı okuyucudan.</summary>
internal static class StdIn
{
    public static readonly TextReader Reader = new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false));

    /// <summary>--password-stdin ile okunan parola (yalnız bellekte).</summary>
    public static string? Password;
}
