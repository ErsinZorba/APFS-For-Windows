using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Apfs.Util;
using Apfs.Volume;
using Fsp;
using FileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace Apfs.Mount;

/// <summary>
/// APFS birimini WinFsp üzerinden Windows'a salt okunur bir sürücü olarak sunar.
/// Değiştiren her işlem STATUS_MEDIA_WRITE_PROTECTED ile reddedilir; birim ayrıca çekirdek seviyesinde
/// salt okunur (ReadOnlyVolume) olarak işaretlenir.
/// </summary>
public sealed class ApfsFileSystem : FileSystemBase
{
    // NTSTATUS değerleri
    internal const int StatusSuccess = 0;
    internal const int StatusObjectNameNotFound = unchecked((int)0xC0000034);
    internal const int StatusObjectPathNotFound = unchecked((int)0xC000003A);
    internal const int StatusMediaWriteProtected = unchecked((int)0xC00000A2);
    internal const int StatusFileCorruptError = unchecked((int)0xC0000102);
    internal const int StatusNotSupported = unchecked((int)0xC00000BB);
    internal const int StatusEndOfFile = unchecked((int)0xC0000011);
    internal const int StatusDeviceNotConnected = unchecked((int)0xC000009D);
    internal const int StatusAccessDenied = unchecked((int)0xC0000022);
    internal const int StatusUnexpectedIoError = unchecked((int)0xC00000E9);
    internal const int StatusNotADirectory = unchecked((int)0xC0000103);

    private const uint FileAttributeReadOnly = 0x1;
    private const uint FileAttributeHidden = 0x2;
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeArchive = 0x20;
    private const uint FileAttributeNormal = 0x80;

    /// <summary>Herkese yalnızca okuma ve çalıştırma izni; sahibi Yöneticiler. Yazma izni hiç kimseye verilmez.</summary>
    private const string SecurityDescriptorSddl = "O:BAG:BAD:P(A;;0x1200A9;;;WD)";

    private readonly ApfsVolume _volume;
    private readonly long _totalBytes;
    private readonly long _freeBytes;
    private readonly byte[] _securityDescriptor;

    /// <summary>Kullanıcıya gösterilecek Türkçe hata mesajları (örneğin bozuk bir dosya okunduğunda).</summary>
    public event Action<string>? Error;

    public ApfsFileSystem(ApfsVolume volume, long totalBytes, long freeBytes)
    {
        _volume = volume;
        _totalBytes = totalBytes;
        _freeBytes = Math.Max(0, freeBytes);
        var sd = new RawSecurityDescriptor(SecurityDescriptorSddl);
        _securityDescriptor = new byte[sd.BinaryLength];
        sd.GetBinaryForm(_securityDescriptor, 0);
    }

    /// <summary>Çağrı istatistikleri (varsayılan kapalı).</summary>
    public CallStats Stats { get; } = new();

    private int _openFiles;
    private long _lastReadTicks = long.MinValue / 2;

    /// <summary>Şu anda açık dosya tanıtıcısı sayısı (klasörler sayılmaz; Gezgin pencereleri klasör tanıtıcısı tutar).</summary>
    public int OpenFileCount => Volatile.Read(ref _openFiles);

    /// <summary>
    /// Birim kullanımda mı: açık bir dosya var veya son 3 saniyede okuma yapıldı (kopyalamada dosyalar arası kısa boşluklar için).
    /// "Güvenle çıkar" bunu sorar; doluysa kullanıcıya kopyalamanın yarıda kalacağı söylenir.
    /// </summary>
    public bool IsBusy => OpenFileCount > 0 || Environment.TickCount64 - Interlocked.Read(ref _lastReadTicks) < 3000;

    /// <summary>ReadOnlyVolume bayrağının çekirdeğe iletilip iletilmediği.</summary>
    public bool KernelReadOnlyFlagSet { get; private set; }

    public override int Init(object host0)
    {
        var host = (FileSystemHost)host0;
        host.SectorSize = 4096;
        host.SectorsPerAllocationUnit = 1;
        host.MaxComponentLength = 255;
        // İçerik hiç değişmez: çekirdek dosya, klasör ve birim bilgisini güvenle önbellekte tutabilir.
        // FileInfoTimeout "sonsuz" (0xFFFFFFFF) olduğunda WinFsp Windows önbellek yöneticisini açar (src/sys/create.c:
        // FO_CACHE_SUPPORTED): tekrar okumalar diske gitmez, önden okuma devreye girer. Disk salt okunur, içerik değişmez; güvenli.
        host.FileInfoTimeout = uint.MaxValue;
        host.DirInfoTimeout = 60_000;
        host.VolumeInfoTimeout = 60_000;
        host.SecurityTimeout = 60_000;
        host.CaseSensitiveSearch = !_volume.IsCaseInsensitive;
        host.CasePreservedNames = true;
        host.UnicodeOnDisk = true;
        host.PersistentAcls = true;
        // Her tanıtıcı kapanışında Cleanup gelsin: açık dosya sayacı (IsBusy) buna dayanır. Salt okunur birimde "yalnızca
        // değişince" seçeneği Cleanup'ı hiç göndermez.
        host.PostCleanupWhenModifiedOnly = false;
        host.PassQueryDirectoryPattern = false;
        host.FileSystemName = "APFS";
        host.VolumeCreationTime = (ulong)ToFileTime(_volume.LastModTimeNs);
        host.VolumeSerialNumber = BitConverter.ToUInt32(_volume.Uuid.ToByteArray(), 0);
        KernelReadOnlyFlagSet = TrySetReadOnlyVolume(host);
        return StatusSuccess;
    }

    /// <summary>
    /// winfsp.net, VolumeParams.ReadOnlyVolume bayrağını dışarıya açmıyor. Bayrak iç alana yansıma ile eklenir.
    /// Başarısız olursa yine de güvenlidir: değiştiren tüm çağrılar bu sınıfta reddedilir ve erişim listesi yazma izni vermez.
    /// </summary>
    internal static bool TrySetReadOnlyVolume(FileSystemHost host)
    {
        try
        {
            var vpField = typeof(FileSystemHost).GetField("_VolumeParams", BindingFlags.Instance | BindingFlags.NonPublic);
            if (vpField is null) return false;
            object vp = vpField.GetValue(host)!;
            var vpType = vp.GetType();
            var flagsField = vpType.GetField("Flags", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var roField = vpType.GetField("ReadOnlyVolume", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (flagsField is null || roField is null) return false;
            uint ro = Convert.ToUInt32(roField.GetValue(null));
            uint flags = (uint)flagsField.GetValue(vp)!;
            flagsField.SetValue(vp, flags | ro);
            vpField.SetValue(host, vp);
            return ((uint)flagsField.GetValue(vpField.GetValue(host)!)! & ro) != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public override int GetVolumeInfo(out VolumeInfo volumeInfo)
    {
        volumeInfo = default;
        volumeInfo.TotalSize = (ulong)_totalBytes;
        volumeInfo.FreeSize = (ulong)_freeBytes;
        volumeInfo.SetVolumeLabel(_volume.Name.Length > 32 ? _volume.Name[..32] : _volume.Name);
        return StatusSuccess;
    }

    public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
    {
        long t = Stats.Start();
        try
        {
            var node = Resolve(fileName, out _);
            fileAttributes = node.Attributes;
            if (securityDescriptor is not null) securityDescriptor = _securityDescriptor;
            return StatusSuccess;
        }
        finally { Stats.Stop("GetSecurityByName", t); }
    }

    public override int Open(string fileName, uint createOptions, uint grantedAccess,
        out object fileNode, out object fileDesc, out FileInfo fileInfo, out string normalizedName)
    {
        long t = Stats.Start();
        try
        {
            var node = Resolve(fileName, out var normalized);
            fileNode = node;
            fileDesc = new OpenHandle(node.IsDirectory);
            if (!node.IsDirectory) Interlocked.Increment(ref _openFiles);
            fileInfo = node.Info;
            normalizedName = SameLengthOrNull(normalized, fileName);
            return StatusSuccess;
        }
        finally { Stats.Stop("Open", t); }
    }

    public override void Close(object fileNode, object fileDesc)
    {
        Release(fileDesc);
        Stats.Stop("Close", Stats.Start());
    }

    public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags)
    {
        Release(fileDesc);
        Stats.Stop("Cleanup", Stats.Start());
    }

    /// <summary>
    /// Tanıtıcı başına bir kez sayaç azaltır: normalde Cleanup'ta, çekirdek açılışı sonradan reddedip yalnızca Close gönderirse
    /// Close'ta. Böylece sayaç sızmaz (aksi halde disk hep "kullanımda" görünürdü).
    /// </summary>
    private void Release(object fileDesc)
    {
        if (fileDesc is OpenHandle h && !h.IsDirectory && Interlocked.Exchange(ref h.Released, 1) == 0)
            Interlocked.Decrement(ref _openFiles);
    }

    public override int GetFileInfo(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        long t = Stats.Start();
        fileInfo = ((Node)fileNode).Info;
        Stats.Stop("GetFileInfo", t);
        return StatusSuccess;
    }

    public override int GetSecurity(object fileNode, object fileDesc, ref byte[] securityDescriptor)
    {
        securityDescriptor = _securityDescriptor;
        return StatusSuccess;
    }

    public override unsafe int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, out uint bytesTransferred)
    {
        long t = Stats.Start();
        var node = (Node)fileNode;
        bytesTransferred = 0;
        Interlocked.Exchange(ref _lastReadTicks, Environment.TickCount64);
        try
        {
            return ReadCore(node, buffer, offset, length, out bytesTransferred);
        }
        catch (ApfsException e) when (e.Code is not ApfsErrorCode.NotFound)
        {
            // Kullanıcıya hangi dosyanın okunamadığını söyle (bildirim ve günlük için)
            throw new ApfsException(e.Code, $"\"{WindowsNames.ToWindows(node.Name)}\": {e.Detail}", e);
        }
        finally { Stats.Stop("Read", t, bytesTransferred); }
    }

    private unsafe int ReadCore(Node node, IntPtr buffer, ulong offset, uint length, out uint bytesTransferred)
    {
        bytesTransferred = 0;
        if (node.IsDirectory) return StatusNotSupported;
        if (offset >= node.Size) return StatusEndOfFile;
        var span = new Span<byte>((void*)buffer, (int)Math.Min(length, int.MaxValue));
        int n;
        if (node.InlineData is { } inline)
        {
            n = (int)Math.Min((ulong)span.Length, (ulong)inline.Length - offset);
            inline.AsSpan((int)offset, n).CopyTo(span);
        }
        else
        {
            n = node.GetReader(_volume).Read(offset, span);
        }
        bytesTransferred = (uint)n;
        return StatusSuccess;
    }

    public override bool ReadDirectoryEntry(object fileNode, object fileDesc, string pattern, string marker,
        ref object context, out string fileName, out FileInfo fileInfo)
    {
        long t = Stats.Start();
        try
        {
            return ReadDirectoryEntryCore((Node)fileNode, marker, ref context, out fileName, out fileInfo);
        }
        finally { Stats.Stop("ReadDirectoryEntry", t); }
    }

    private bool ReadDirectoryEntryCore(Node node, string marker, ref object context, out string fileName, out FileInfo fileInfo)
    {
        if (context is not IEnumerator<(string Name, FileInfo Info)> en)
        {
            // Liste açık tanıtıcı başına bir kez kurulur; büyük klasörlerde WinFsp ReadDirectory'yi birden çok kez çağırır.
            var entries = node.SortedChildren ??= SortChildren(node.GetChildren(this));
            IEnumerable<(string Name, FileInfo Info)> seq = entries;
            if (marker is not null) seq = seq.Where(e => DirOrder.Compare(e.Name, marker) > 0);
            en = seq.GetEnumerator();
            context = en;
        }
        if (en.MoveNext())
        {
            (fileName, fileInfo) = en.Current;
            return true;
        }
        fileName = null!;
        fileInfo = default;
        return false;
    }

    public override int GetDirInfoByName(object parentNode, object fileDesc, string fileName, out string normalizedName, out FileInfo fileInfo)
    {
        var parent = (Node)parentNode;
        var entry = _volume.FindEntry(parent.Inode.Id, WindowsNames.FromWindows(fileName));
        if (entry is null)
        {
            normalizedName = null!;
            fileInfo = default;
            return StatusObjectNameNotFound;
        }
        var child = MakeNode(_volume.GetInode(entry.FileId), entry.Name);
        normalizedName = SameLengthOrNull(WindowsNames.ToWindows(entry.Name), fileName);
        fileInfo = child.Info;
        return StatusSuccess;
    }

    // ---- Değiştiren işlemler: hepsi reddedilir ----

    public override int Create(string fileName, uint createOptions, uint grantedAccess, uint fileAttributes, byte[] securityDescriptor,
        ulong allocationSize, out object fileNode, out object fileDesc, out FileInfo fileInfo, out string normalizedName)
    {
        fileNode = null!; fileDesc = null!; fileInfo = default; normalizedName = null!;
        return StatusMediaWriteProtected;
    }

    public override int Overwrite(object fileNode, object fileDesc, uint fileAttributes, bool replaceFileAttributes, ulong allocationSize, out FileInfo fileInfo)
    {
        fileInfo = default;
        return StatusMediaWriteProtected;
    }

    public override int Write(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, bool writeToEndOfFile,
        bool constrainedIo, out uint bytesTransferred, out FileInfo fileInfo)
    {
        bytesTransferred = 0; fileInfo = default;
        return StatusMediaWriteProtected;
    }

    public override int SetBasicInfo(object fileNode, object fileDesc, uint fileAttributes, ulong creationTime, ulong lastAccessTime,
        ulong lastWriteTime, ulong changeTime, out FileInfo fileInfo)
    {
        fileInfo = default;
        return StatusMediaWriteProtected;
    }

    public override int SetFileSize(object fileNode, object fileDesc, ulong newSize, bool setAllocationSize, out FileInfo fileInfo)
    {
        fileInfo = default;
        return StatusMediaWriteProtected;
    }

    public override int CanDelete(object fileNode, object fileDesc, string fileName) => StatusMediaWriteProtected;
    public override int SetDelete(object fileNode, object fileDesc, string fileName, bool deleteFile) => StatusMediaWriteProtected;
    public override int Rename(object fileNode, object fileDesc, string fileName, string newFileName, bool replaceIfExists) => StatusMediaWriteProtected;
    public override int SetSecurity(object fileNode, object fileDesc, AccessControlSections sections, byte[] securityDescriptor) => StatusMediaWriteProtected;

    public override int SetVolumeLabel(string volumeLabel, out VolumeInfo volumeInfo)
    {
        volumeInfo = default;
        return StatusMediaWriteProtected;
    }

    public override int Flush(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        fileInfo = fileNode is Node n ? n.Info : default;
        return StatusSuccess; // yazılacak bir şey yok
    }

    /// <summary>Okuyucudan gelen hataları NTSTATUS'a çevirir; süreç asla çökmez.</summary>
    public override int ExceptionHandler(Exception ex)
    {
        Stats.Stop(ex is ApfsException a ? $"Hata:{a.Code}" : $"Hata:{ex.GetType().Name}", Stats.Start());
        if (ex is ApfsException ae)
        {
            if (ae.Code is not ApfsErrorCode.NotFound) Error?.Invoke(ae.Message);
            return ToNtStatus(ae.Code);
        }
        Error?.Invoke($"Beklenmeyen hata: {ex.Message}");
        return StatusUnexpectedIoError;
    }

    internal static int ToNtStatus(ApfsErrorCode code) => code switch
    {
        ApfsErrorCode.NotFound => StatusObjectNameNotFound,
        ApfsErrorCode.Corrupt or ApfsErrorCode.ChecksumMismatch or ApfsErrorCode.NoValidCheckpoint => StatusFileCorruptError,
        ApfsErrorCode.Unsupported or ApfsErrorCode.Encrypted => StatusNotSupported,
        ApfsErrorCode.IoError => StatusDeviceNotConnected,
        ApfsErrorCode.AccessDenied => StatusAccessDenied,
        _ => StatusUnexpectedIoError,
    };

    // ---- Düğümler ----

    /// <summary>
    /// Windows yolunu çözer: bileşenler SFM eşlemesinden geri çevrilir ve birimin ad kurallarıyla tek tek aranır
    /// (APFS adında "\" geçebileceği için yol bir bütün olarak bölünmez). <paramref name="normalizedPath"/> diskteki gerçek adlardır.
    /// </summary>
    private Node Resolve(string fileName, out string normalizedPath)
    {
        var parts = fileName.Split('\\', StringSplitOptions.RemoveEmptyEntries).Select(WindowsNames.FromWindows).ToList();
        var inode = _volume.LookupComponents(parts, out var actual);
        normalizedPath = "\\" + string.Join('\\', actual.Select(WindowsNames.ToWindows));
        return MakeNode(inode, actual.Count > 0 ? actual[^1] : "");
    }

    private Node MakeNode(Inode inode, string name)
    {
        byte[]? inline = null;
        if (inode.IsSymlink)
            inline = System.Text.Encoding.UTF8.GetBytes(_volume.ReadSymlinkTarget(inode.Id) ?? "");
        ulong size = inline is not null ? (ulong)inline.Length : inode.IsDirectory ? 0 : _volume.GetFileSize(inode);
        return new Node(inode, name, inline, size);
    }

    /// <summary>"." ve ".." her zaman önce, diğerleri ordinal sırayla. Marker karşılaştırması da bu sırayı kullanır.</summary>
    private static readonly Comparer<string> DirOrder = Comparer<string>.Create((a, b) =>
    {
        int ra = a == "." ? 0 : a == ".." ? 1 : 2;
        int rb = b == "." ? 0 : b == ".." ? 1 : 2;
        return ra != rb ? ra.CompareTo(rb) : string.CompareOrdinal(a, b);
    });

    /// <summary>
    /// WinFsp, çekirdekteki adı yerinde değiştirdiği için "gerçek ad" istenen adla AYNI UZUNLUKTA olmalıdır
    /// (yalnızca harf büyüklüğü farkı). Normalizasyon (NFC/NFD) veya ß/ss gibi uzunluk değiştiren eşleşmelerde
    /// gerçek ad bildirilmez; aksi halde Windows STATUS_OBJECT_NAME_INVALID döndürür ("sözdizimi hatalı").
    /// </summary>
    internal static string SameLengthOrNull(string actual, string requested) =>
        actual.Length == requested.Length ? actual : null!;

    private static List<(string Name, FileInfo Info)> SortChildren(IEnumerable<(string Name, FileInfo Info)> children)
    {
        var list = children.ToList();
        list.Sort((a, b) => DirOrder.Compare(a.Name, b.Name));
        return list;
    }

    internal static long ToFileTime(long unixNs) =>
        unixNs <= 0 ? 116444736000000000L : unixNs / 100 + 116444736000000000L;

    /// <summary>Açık tanıtıcı (WinFsp FileDesc): her Open için yeni bir nesne.</summary>
    private sealed class OpenHandle(bool isDirectory)
    {
        public bool IsDirectory { get; } = isDirectory;
        public int Released;
    }

    private sealed class Node(Inode inode, string name, byte[]? inline, ulong size)
    {
        private IDataReader? _reader;

        /// <summary>Sıralı klasör içeriği (yalnızca klasörlerde, ilk ReadDirectory'de kurulur).</summary>
        public List<(string Name, FileInfo Info)>? SortedChildren { get; set; }

        public Inode Inode { get; } = inode;
        public string Name { get; } = name;
        public byte[]? InlineData { get; } = inline;
        public bool IsDirectory => Inode.IsDirectory;
        /// <summary>Görünen boyut (sıkıştırılmış dosyalarda açılmış boyut).</summary>
        public ulong Size { get; } = size;

        public uint Attributes
        {
            get
            {
                uint a = IsDirectory ? FileAttributeDirectory : 0;
                if (Inode.IsHidden || (Name.StartsWith('.') && Name is not "." and not "..")) a |= FileAttributeHidden;
                return a == 0 ? FileAttributeNormal : a;
            }
        }

        public FileInfo Info
        {
            get
            {
                var fi = default(FileInfo);
                fi.FileAttributes = Attributes;
                fi.FileSize = Size;
                fi.AllocationSize = (Size + 4095) / 4096 * 4096;
                fi.CreationTime = (ulong)ToFileTime(Inode.CreateTimeNs);
                fi.LastAccessTime = (ulong)ToFileTime(Inode.AccessTimeNs);
                fi.LastWriteTime = (ulong)ToFileTime(Inode.ModTimeNs);
                fi.ChangeTime = (ulong)ToFileTime(Inode.ChangeTimeNs);
                fi.IndexNumber = Inode.Id;
                fi.HardLinks = IsDirectory ? 0 : (uint)Math.Max(1, Inode.ChildrenOrLinks);
                return fi;
            }
        }

        public IDataReader GetReader(ApfsVolume v) => _reader ??= v.OpenFile(Inode);

        public IEnumerable<(string Name, FileInfo Info)> GetChildren(ApfsFileSystem fs)
        {
            if (Inode.Id != Inode.RootDirId)
            {
                yield return (".", Info);
                var parent = fs._volume.TryGetInode(Inode.ParentId);
                yield return ("..", parent is null ? Info : fs.MakeNode(parent, "..").Info);
            }
            foreach (var e in fs._volume.ListDirectory(Inode.Id))
            {
                Node child;
                try
                {
                    child = fs.MakeNode(fs._volume.GetInode(e.FileId), e.Name);
                }
                catch (ApfsException ex) when (ex.Code is not ApfsErrorCode.IoError)
                {
                    // Bozuk bir girdi tüm klasörü gizlemesin: atla ve bildir.
                    fs.Error?.Invoke($"\"{e.Name}\" okunamadı: {ex.Message}");
                    continue;
                }
                yield return (WindowsNames.ToWindows(e.Name), child.Info);
            }
        }
    }
}
