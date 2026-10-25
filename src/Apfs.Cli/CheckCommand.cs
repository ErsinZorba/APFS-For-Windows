using System.Diagnostics;
using Apfs.Compression;
using Apfs.Util;
using Apfs.Volume;
using Apfs.Windows;

namespace Apfs.Cli;

/// <summary>
/// `apfs check`: salt okunur öz denetim. Tüm ağacı gezer, sayıları birim süper bloğundaki sayaçlarla karşılaştırır, her dosyanın
/// başını ve sonunu okur (şifre çözme ve sıkıştırma da sınanır) ve dizin girdilerindeki ad karmalarını kendi hesabıyla karşılaştırır.
/// Diske hiçbir şey yazmaz.
/// </summary>
internal static class CheckCommand
{
    public static int Run(CliOptions o)
    {
        using var c = SourceOpener.OpenContainer(o.Require(0, "kaynak"), o.Partition);
        var passwords = Passwords.FromOptions(o);
        if (o.Has("--gate")) return RunGate(c, passwords);
        if (o.Has("--full")) return RunFull(c, passwords, o.Has("--gizle"));
        int problems = 0;
        foreach (var r in c.OpenVolumes(passwords))
        {
            if (o.Has("--volume-set") && r.Index != o.Volume) continue;
            if (r.Volume is not { } v)
            {
                Console.WriteLine($"Birim {r.Index}: denetlenemedi: {r.Error!.Message}");
                continue;
            }
            problems += CheckVolume(r.Index, v);
        }
        Console.WriteLine(problems == 0 ? "Sonuç: sorun bulunmadı." : $"Sonuç: {problems} sorun bulundu (ayrıntılar yukarıda).");
        return problems == 0 ? 0 : 1;
    }

    /// <summary>`--full`: yapısal tutarlılık (blok sahipliği, checkpoint, alan yöneticisi, başvuru sayıları); yazma kapısının denetimi.</summary>
    /// <summary>`--gate`: yazma kapısının bu disk için kararı (salt okunur; diske hiçbir şey yazmaz).</summary>
    private static int RunGate(Container.ApfsContainer c, Func<VolumeLockInfo, string?>? passwords)
    {
        // Parola verildiyse şifreli birimlerin kilidi açılır; kapı ancak anahtarla şifreli birimi değerlendirebilir
        var keys = new List<Apfs.Crypto.VolumeKey>();
        if (passwords is not null)
        {
            var asked = new Dictionary<Guid, string?>(); // istem parolayı birim başına bir kez sorsun
            string? Once(VolumeLockInfo i) => asked.TryGetValue(i.Uuid, out var p) ? p : asked[i.Uuid] = passwords(i);
            foreach (var r in c.OpenVolumes(Once))
                if (r.Volume is { IsEncrypted: true } v && asked.GetValueOrDefault(v.Uuid) is { } pw)
                    keys.Add(Apfs.Crypto.VolumeKey.Unlock(c, v.Uuid, pw));
        }
        var g = Apfs.Write.WriteGate.Evaluate(c, keys);
        Console.WriteLine(g.Allowed ? "Yazma kapısı: AÇILABİLİR (bu disk yazmaya açılabilir)." : "Yazma kapısı: KAPALI (disk salt okunur kalır).");
        foreach (var r in g.Reasons) Console.WriteLine($"  Neden: {r}");
        if (g.Report is { IsClean: false } rep) foreach (var e in rep.Errors.Take(10)) Console.WriteLine($"  Denetim: {e}");
        return 0;
    }

    /// <summary>--gizle: tırnak içindeki adlar (dosya/klasör adları) "…" ile değiştirilir; kişisel adlar rapora girmesin.</summary>
    private static string Hide(string s, bool hide) => hide ? Apfs.Diagnostics.CheckReport.HideNames(s) : s;

    private static int RunFull(Container.ApfsContainer c, Func<VolumeLockInfo, string?>? passwords, bool hide)
    {
        var sw = Stopwatch.StartNew();
        var report = Apfs.Diagnostics.FullCheck.Run(c, passwords);
        foreach (var l in report.Info) Console.WriteLine($"  {Hide(l, hide)}");
        foreach (var w in report.Warnings) Console.WriteLine($"  Uyarı: {Hide(w, hide)}");
        foreach (var e in report.Errors) Console.WriteLine($"  HATA: {Hide(e, hide)}");
        if (report.SuppressedErrors > 0) Console.WriteLine($"  … ve {report.SuppressedErrors} hata daha");
        Console.WriteLine(report.IsClean
            ? $"Sonuç: yapısal tutarlılık tam ({sw.Elapsed.TotalSeconds:N1} sn)."
            : $"Sonuç: {report.Errors.Count + report.SuppressedErrors} tutarsızlık bulundu ({sw.Elapsed.TotalSeconds:N1} sn).");
        return report.IsClean ? 0 : 1;
    }

    private static int CheckVolume(int index, ApfsVolume v)
    {
        var sw = Stopwatch.StartNew();
        Console.WriteLine($"Birim {index}: \"{v.Name}\"{(v.IsEncrypted ? " (şifreli, kilidi açıldı)" : "")}");
        var seen = new HashSet<ulong>();
        long files = 0, dirs = 0, symlinks = 0, others = 0, bytes = 0, hashOk = 0, hashBad = 0;
        var compressed = new Dictionary<string, int>();
        var errors = new List<string>();
        var hashExamples = new List<string>();
        var buf = new byte[65536];

        void Walk(Inode dir, string path, int depth)
        {
            if (depth > 256) { errors.Add($"{path}: klasör derinliği 256'yı aştı (döngü olabilir)"); return; }
            IReadOnlyList<DirEntry> entries;
            try
            {
                entries = v.ListDirectory(dir.Id);
                foreach (var (name, stored) in v.ListDirectoryHashes(dir.Id))
                {
                    if (ApfsNames.Hash(name, v.IsCaseInsensitive) == stored) hashOk++;
                    else { hashBad++; if (hashExamples.Count < 5) hashExamples.Add($"{path}/{name}"); }
                }
            }
            catch (ApfsException e) { errors.Add($"{path}/: {e.Message}"); return; }

            foreach (var e in entries)
            {
                string p = $"{path}/{e.Name}";
                try
                {
                    var ino = v.GetInode(e.FileId);
                    if (ino.IsDirectory) { dirs++; Walk(ino, p, depth + 1); continue; }
                    if (!seen.Add(ino.Id)) continue; // sert bağ: bir kez say
                    if (ino.IsSymlink) { symlinks++; v.ReadSymlinkTarget(ino.Id); continue; }
                    if (!ino.IsRegularFile) { others++; continue; }
                    files++;
                    if (ino.IsCompressed)
                    {
                        var attr = v.ReadXattr(ino.Id, Decmpfs.AttrName, 64 * 1024);
                        string kind = attr is null ? "bilinmiyor" : Decmpfs.TypeName(Decmpfs.ParseHeader(attr).Type);
                        compressed[kind] = compressed.GetValueOrDefault(kind) + 1;
                    }
                    var reader = v.OpenFile(ino);
                    bytes += (long)reader.Length;
                    reader.Read(0, buf);
                    if (reader.Length > (ulong)buf.Length) reader.Read(reader.Length - (ulong)buf.Length, buf);
                }
                catch (ApfsException ex) when (ex.Code is not ApfsErrorCode.IoError)
                {
                    errors.Add($"{p}: {ex.Message}");
                }
            }
        }
        Walk(v.Root, "", 0);

        Console.WriteLine($"  Okunan: {files:N0} dosya, {dirs:N0} klasör, {symlinks:N0} sembolik bağ, {others:N0} diğer; {bytes / 1048576.0:N1} MB, {sw.Elapsed.TotalSeconds:N1} sn");
        Console.WriteLine($"  Süper blok sayaçları: {v.NumFiles:N0} dosya, {v.NumDirectories:N0} klasör, {v.NumSymlinks:N0} sembolik bağ" +
            (files == (long)v.NumFiles && symlinks == (long)v.NumSymlinks ? "  (eşleşiyor)" : "  (fark var; gizli sistem klasörleri ve anlık görüntüler sayılmamış olabilir)"));
        if (compressed.Count > 0)
            Console.WriteLine($"  Sıkıştırılmış: {string.Join(", ", compressed.Select(kv => $"{kv.Key} {kv.Value:N0}"))}");
        Console.WriteLine($"  Ad karmaları: {hashOk:N0} doğru, {hashBad:N0} farklı" + (hashBad > 0 ? $" (örnek: {string.Join("; ", hashExamples)}; aramayı etkilemez)" : ""));
        foreach (var e in errors.Take(20)) Console.WriteLine($"  Okunamadı: {e}");
        if (errors.Count > 20) Console.WriteLine($"  … ve {errors.Count - 20} hata daha");
        return errors.Count;
    }
}
