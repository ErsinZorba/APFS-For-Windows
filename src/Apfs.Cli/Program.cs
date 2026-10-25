using System.Security.Cryptography;
using System.Text;
using Apfs;
using Apfs.Cli;
using Apfs.Container;
using Apfs.Volume;
using Apfs.Windows;

Console.OutputEncoding = Encoding.UTF8;

try
{
    var opts = CliOptions.Parse(args);
    return opts.Command switch
    {
        "info" => Info(opts),
        "ls" => List(opts),
        "extract" => Extract(opts),
        "check" => CheckCommand.Run(opts),
        "dump" => Dump(opts),
        "mount" => MountCommand.Run(opts),
        "launcher-mount" => MountCommand.RunFromLauncher(opts),
        "register" => LauncherRegistration.Register(),
        "unregister" => LauncherRegistration.Unregister(),
        _ => Help(),
    };
}
catch (ApfsException e)
{
    Console.Error.WriteLine($"Hata: {e.Message}");
    return 1;
}
catch (CliUsageException e)
{
    Console.Error.WriteLine($"Hata: {e.Message}");
    Console.Error.WriteLine("Yardım için: apfs help");
    return 2;
}
catch (Exception e)
{
    Console.Error.WriteLine($"Beklenmeyen bir hata oluştu, diskinize hiçbir şey yazılmadı: {e.Message}");
    return 3;
}

static int Help()
{
    Console.WriteLine("""
        APFS for Windows: Mac APFS disklerini Windows'ta salt okunur açar.

        Kullanım:
          apfs info <kaynak>                         Kapsayıcı ve birim bilgilerini gösterir
          apfs ls <kaynak> [yol] [-r] [--hash]       Klasör içeriğini listeler
          apfs extract <kaynak> <yol> <hedef>        Bir dosyayı Windows'a kopyalar
          apfs check <kaynak> [--full]               Salt okunur öz denetim (tüm dosyaları okur, sayaçları ve ad karmalarını karşılaştırır);
                                                     --full: yapısal tutarlılık (blok sahipliği, alan yöneticisi, başvuru sayıları)
          apfs dump <kaynak>                         İç yapıyı döker (geliştirme: checkpoint, alan yöneticisi, birim süper blokları)
          apfs mount <kaynak> [X:]                   Birimleri sürücü harfi olarak bağlar (Ctrl+C ile ayrılır);
                                                     birden çok birim varsa her biri ayrı harf alır, iç birimler için --all
          apfs register / unregister                 (Yönetici) Fiziksel diskler için WinFsp kaydı

        <kaynak>: disk görüntüsü dosyası veya \\.\PhysicalDriveN (yönetici yetkisi gerekir)
        Seçenekler: --volume N (birim sırası, varsayılan 0), --partition N (APFS bölümü sırası)
        Şifreli birimler: --password-prompt (parolayı sorar) veya --password-stdin (parolayı standart girdiden okur)
        """);
    return 0;
}

static int Info(CliOptions o)
{
    using var c = SourceOpener.OpenContainer(o.Require(0, "kaynak"), o.Partition);
    var sb = c.Superblock;
    Console.WriteLine($"APFS kapsayıcısı  {sb.Uuid}");
    Console.WriteLine($"  Blok boyutu     {sb.BlockSize} bayt");
    Console.WriteLine($"  Boyut           {FormatSize((long)sb.BlockCount * sb.BlockSize)} ({sb.BlockCount:N0} blok)");
    Console.WriteLine($"  Kontrol noktası xid {sb.Xid} (blok {sb.Paddr})");
    long free = c.FreeBytes;
    Console.WriteLine($"  Kullanılan      {FormatSize((long)sb.BlockCount * sb.BlockSize - free)}, boş {FormatSize(free)} ({c.FreeSpaceSource})");
    foreach (var w in c.Warnings) Console.WriteLine($"  Uyarı: {w}");
    if (o.Has("--fingerprint"))
    {
        // Diskin değişmediğini göstermek için: blok 0 ve tüm checkpoint tanımlayıcı alanının SHA-256'sı.
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(c.Reader.ReadBlock(0));
        uint descBlocks = sb.XpDescBlocks & 0x7FFFFFFF;
        for (ulong i = 0; i < descBlocks && sb.XpDescBase + i < c.Reader.BlockCount; i++)
            sha.AppendData(c.Reader.ReadBlock(sb.XpDescBase + i));
        Console.WriteLine($"  Parmak izi      {Convert.ToHexString(sha.GetHashAndReset())} (blok 0 + {descBlocks} checkpoint bloğu)");
    }
    Console.WriteLine();
    foreach (var r in c.OpenVolumes())
    {
        if (r.Volume is { } v)
        {
            Console.WriteLine($"Birim {r.Index}: \"{v.Name}\"{(v.RoleName.Length > 0 ? $"  [{v.RoleName}]" : "")}");
            Console.WriteLine($"  UUID            {v.Uuid}");
            Console.WriteLine($"  Harf duyarlılığı {(v.IsCaseInsensitive ? "duyarsız" : "duyarlı")}");
            Console.WriteLine($"  İçerik          {v.NumFiles:N0} dosya, {v.NumDirectories:N0} klasör, {v.NumSymlinks:N0} sembolik bağ");
            Console.WriteLine($"  Kullanılan      {FormatSize((long)v.AllocatedBlocks * sb.BlockSize)}");
        }
        else
        {
            Console.WriteLine($"Birim {r.Index}: açılamadı: {r.Error!.Message}");
        }
        Console.WriteLine();
    }
    return 0;
}

static int Dump(CliOptions o)
{
    using var c = SourceOpener.OpenContainer(o.Require(0, "kaynak"), o.Partition);
    if (o.Positional.Count > 1)
    {
        // apfs dump <kaynak> <yol>: o yolun ham kayıtları (geliştirme)
        var v = OpenVolume(c, o.Volume, Passwords.FromOptions(o));
        foreach (var line in Apfs.Diagnostics.RecordDump.Lines(v, o.Positional[1])) Console.WriteLine(line);
        return 0;
    }
    int problems = 0;
    foreach (var line in Apfs.Diagnostics.ContainerDump.Lines(c))
    {
        Console.WriteLine(line);
        if (line.Contains("SORUN:", StringComparison.Ordinal)) problems++;
    }
    return problems == 0 ? 0 : 1;
}

static int List(CliOptions o)
{
    using var c = SourceOpener.OpenContainer(o.Require(0, "kaynak"), o.Partition);
    var v = OpenVolume(c, o.Volume, Passwords.FromOptions(o));
    string path = o.Positional.Count > 1 ? o.Positional[1] : "/";
    var start = v.Lookup(path);
    if (!start.IsDirectory)
    {
        PrintEntry(v, path.Trim('/'), start, o.Has("--hash"));
        return 0;
    }
    void Walk(Inode dir, string prefix)
    {
        foreach (var e in v.ListDirectory(dir.Id).OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            var ino = v.GetInode(e.FileId);
            string p = prefix.Length == 0 ? e.Name : $"{prefix}/{e.Name}";
            PrintEntry(v, p, ino, o.Has("--hash"));
            if (ino.IsDirectory && o.Has("-r")) Walk(ino, p);
        }
    }
    Walk(start, path.Trim('/', '\\').Replace('\\', '/'));
    return 0;
}

static void PrintEntry(ApfsVolume v, string path, Inode ino, bool hash)
{
    if (ino.IsDirectory)
        Console.WriteLine($"{path}\td\t-\t-");
    else if (ino.IsSymlink)
        Console.WriteLine($"{path}\tl\t-\t->{v.ReadSymlinkTarget(ino.Id)}");
    else
    {
        string h = "-";
        if (hash)
        {
            try { h = HashFile(v, ino); }
            catch (ApfsException e) { h = $"HATA: {e.Message}"; }
        }
        Console.WriteLine($"{path}\tf\t{v.GetFileSize(ino)}\t{h}");
    }
}

static string HashFile(ApfsVolume v, Inode ino)
{
    var reader = v.OpenFile(ino);
    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    var buf = new byte[1 << 20];
    ulong pos = 0;
    while (pos < reader.Length)
    {
        int n = reader.Read(pos, buf);
        sha.AppendData(buf, 0, n);
        pos += (ulong)n;
    }
    return Convert.ToHexString(sha.GetHashAndReset());
}

static int Extract(CliOptions o)
{
    using var c = SourceOpener.OpenContainer(o.Require(0, "kaynak"), o.Partition);
    var v = OpenVolume(c, o.Volume, Passwords.FromOptions(o));
    var ino = v.Lookup(o.Require(1, "yol"));
    string target = o.Require(2, "hedef");
    var reader = v.OpenFile(ino);
    using var fs = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
    var buf = new byte[1 << 20];
    ulong pos = 0;
    while (pos < reader.Length)
    {
        int n = reader.Read(pos, buf);
        fs.Write(buf, 0, n);
        pos += (ulong)n;
    }
    Console.WriteLine($"Kopyalandı: {target} ({FormatSize((long)reader.Length)})");
    return 0;
}

static ApfsVolume OpenVolume(ApfsContainer c, int index, Func<VolumeLockInfo, string?>? passwords)
{
    var all = c.OpenVolumes(passwords);
    var r = all.FirstOrDefault(x => x.Index == index)
        ?? throw new ApfsException(ApfsErrorCode.InvalidArgument, $"birim {index} yok; kapsayıcıda {all.Count} birim var");
    return r.Volume ?? throw r.Error!;
}

static string FormatSize(long bytes) => bytes switch
{
    >= 1L << 40 => $"{bytes / (double)(1L << 40):0.##} TB",
    >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB",
    >= 1L << 20 => $"{bytes / (double)(1L << 20):0.##} MB",
    >= 1L << 10 => $"{bytes / (double)(1L << 10):0.##} KB",
    _ => $"{bytes} bayt",
};
