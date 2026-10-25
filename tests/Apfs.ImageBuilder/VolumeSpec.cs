namespace Apfs.ImageBuilder;

/// <summary>Görüntüye yazılacak bir birimin içeriği.</summary>
public sealed class VolumeSpec
{
    internal VolumeSpec(string name, bool caseInsensitive, ushort role)
    {
        Name = name;
        CaseInsensitive = caseInsensitive;
        Role = role;
        Root = new DirSpec("") { Ino = 2 };
    }

    public string Name { get; }
    public bool CaseInsensitive { get; }
    public ushort Role { get; }
    /// <summary>Verilirse birim yazılımla şifrelenir (AES-XTS-128, parola → KEK → VEK).</summary>
    public string? Password { get; set; }
    public string? PasswordHint { get; set; }
    /// <summary>PBKDF2 yineleme sayısı (testlerde hız için düşük; macOS'ta yüz binler mertebesindedir).</summary>
    public int Iterations { get; set; } = 2000;
    internal DirSpec Root { get; }
    private ulong _nextIno = 16;

    public VolumeSpec AddDirectory(string path)
    {
        GetOrCreateDir(path);
        return this;
    }

    /// <param name="fragments">1'den büyükse dosya verisi diskte bitişik olmayan bu kadar parçaya bölünür.</param>
    /// <param name="explicitHoles">Seyrek dosyalarda boşluklar fiziksel adresi 0 olan extent'lerle yazılsın mı (aksi halde hiç extent yazılmaz).</param>
    public VolumeSpec AddFile(string path, FileContent content, int fragments = 1, bool explicitHoles = true, bool hidden = false)
    {
        var (dir, name) = Parent(path);
        dir.Add(name, new FileSpec(name, content) { Ino = _nextIno++, Fragments = fragments, ExplicitHoles = explicitHoles, Hidden = hidden });
        return this;
    }

    /// <summary>macOS dosya sistemi sıkıştırmasıyla (decmpfs) saklanan dosya.</summary>
    public VolumeSpec AddCompressedFile(string path, FileContent content, CompressionSpec compression)
    {
        var (dir, name) = Parent(path);
        dir.Add(name, new FileSpec(name, content) { Ino = _nextIno++, Compression = compression });
        return this;
    }

    public VolumeSpec AddSymlink(string path, string target)
    {
        var (dir, name) = Parent(path);
        dir.Add(name, new SymlinkSpec(name, target) { Ino = _nextIno++ });
        return this;
    }

    /// <summary>Var olan bir dosyaya ikinci bir ad (sert bağ).</summary>
    public VolumeSpec AddHardLink(string path, string existingPath)
    {
        var (srcDir, srcName) = Parent(existingPath);
        if (srcDir.Children.FirstOrDefault(c => c.Name == srcName) is not FileSpec file)
            throw new InvalidOperationException($"sert bağ kaynağı bulunamadı: {existingPath}");
        var (dir, name) = Parent(path);
        file.LinkCount++;
        dir.Add(name, new HardLinkSpec(name, file));
        return this;
    }

    public VolumeSpec SetXattr(string path, string name, byte[] data)
    {
        var node = Find(path) ?? throw new InvalidOperationException($"bulunamadı: {path}");
        node.Xattrs[name] = data;
        return this;
    }

    /// <summary>Testlerin beklenen sonucu (gerçek değer) olarak kullandığı içerik listesi.</summary>
    public IEnumerable<ExpectedEntry> Enumerate()
    {
        IEnumerable<ExpectedEntry> Walk(DirSpec dir, string prefix)
        {
            foreach (var c in dir.Children)
            {
                string p = prefix.Length == 0 ? c.Name : $"{prefix}/{c.Name}";
                var t = c is HardLinkSpec h ? h.Target : c;
                switch (t)
                {
                    case DirSpec d:
                        yield return new ExpectedEntry(p, ExpectedKind.Directory, null, null, d.Hidden);
                        foreach (var e in Walk(d, p)) yield return e;
                        break;
                    case FileSpec f:
                        yield return new ExpectedEntry(p, ExpectedKind.File, f.Content, null, f.Hidden);
                        break;
                    case SymlinkSpec s:
                        yield return new ExpectedEntry(p, ExpectedKind.Symlink, null, s.Target, s.Hidden);
                        break;
                }
            }
        }
        return Walk(Root, "");
    }

    private EntrySpec? Find(string path)
    {
        EntrySpec cur = Root;
        foreach (var part in Split(path))
        {
            if (cur is not DirSpec d) return null;
            var next = d.Children.FirstOrDefault(c => c.Name == part);
            if (next is null) return null;
            cur = next is HardLinkSpec h ? h.Target : next;
        }
        return cur;
    }

    private DirSpec GetOrCreateDir(string path)
    {
        var cur = Root;
        foreach (var part in Split(path))
        {
            var next = cur.Children.FirstOrDefault(c => c.Name == part);
            if (next is null)
            {
                var d = new DirSpec(part) { Ino = _nextIno++ };
                cur.Add(part, d);
                cur = d;
            }
            else cur = next as DirSpec ?? throw new InvalidOperationException($"klasör değil: {part}");
        }
        return cur;
    }

    private (DirSpec Dir, string Name) Parent(string path)
    {
        var parts = Split(path);
        if (parts.Length == 0) throw new ArgumentException("boş yol");
        var dir = GetOrCreateDir(string.Join('/', parts[..^1]));
        return (dir, parts[^1]);
    }

    private static string[] Split(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries);
}

public enum ExpectedKind { Directory, File, Symlink }

public sealed record ExpectedEntry(string Path, ExpectedKind Kind, FileContent? Content, string? SymlinkTarget, bool Hidden);

internal abstract class EntrySpec(string name)
{
    public string Name { get; } = name;
    public ulong Ino { get; set; }
    public Dictionary<string, byte[]> Xattrs { get; } = new(StringComparer.Ordinal);
    public bool Hidden { get; set; }
}

internal sealed class DirSpec(string name) : EntrySpec(name)
{
    public List<EntrySpec> Children { get; } = [];

    public void Add(string name, EntrySpec e)
    {
        if (Children.Any(c => c.Name == name)) throw new InvalidOperationException($"aynı ad iki kez: {name}");
        Children.Add(e);
    }
}

internal sealed class FileSpec(string name, FileContent content) : EntrySpec(name)
{
    public FileContent Content { get; } = content;
    public int Fragments { get; set; } = 1;
    public bool ExplicitHoles { get; set; }
    public int LinkCount { get; set; } = 1;
    public CompressionSpec? Compression { get; set; }
    /// <summary>Yazım sırasında doldurulur: (mantıksal ofset, uzunluk, fiziksel blok; 0 = boşluk).</summary>
    public List<(ulong Logical, ulong Length, ulong Phys, ulong CryptoId)> Extents { get; } = [];
}

internal sealed class SymlinkSpec(string name, string target) : EntrySpec(name)
{
    public string Target { get; } = target;
}

internal sealed class HardLinkSpec(string name, FileSpec target) : EntrySpec(name)
{
    public FileSpec Target { get; } = target;
}
