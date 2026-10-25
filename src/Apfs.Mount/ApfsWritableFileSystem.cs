using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Apfs.Util;
using Apfs.Volume;
using Apfs.Write;
using Fsp;
using FileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace Apfs.Mount;

/// <summary>
/// APFS birimini WinFsp üzerinden YAZILABİLİR sürücü olarak sunar (Faz 3). Salt okunur sınıf (<see cref="ApfsFileSystem"/>)
/// değişmeden kalır; bu sınıf yalnızca yazma kapısından geçmiş bir oturumla kurulur.
/// <list type="bullet">
/// <item>Okumalar oturumun güncel görüntüsünden (<see cref="VolumeWriter.View"/>), değişiklikler <see cref="VolumeWriter"/>'dan.</item>
/// <item>Bütün çağrılar tek kilit altında (yazıcı iş parçacığı güvenli değildir).</item>
/// <item>Değişiklikler ~5 sn'de bir, Flush'ta ve ayırmada commit edilir. Windows önbelleği kapalıdır (bkz. Init): her yazma
/// doğrudan buraya gelir, dosya kapandıktan sonra geç yazma olmaz.</item>
/// </list>
/// </summary>
public sealed class ApfsWritableFileSystem : FileSystemBase, IDisposable
{
    internal const int StatusObjectNameCollision = unchecked((int)0xC0000035);
    internal const int StatusDirectoryNotEmpty = unchecked((int)0xC0000101);
    internal const int StatusDiskFull = unchecked((int)0xC000007F);
    internal const int StatusInvalidParameter = unchecked((int)0xC000000D);
    internal const int StatusFileIsADirectory = unchecked((int)0xC00000BA);

    private const uint FileDirectoryFile = 0x1;
    private const uint FileAttributeHidden = 0x2, FileAttributeDirectory = 0x10, FileAttributeNormal = 0x80;


    /// <summary>Herkese tam erişim (yazılabilir birim); sahibi Yöneticiler.</summary>
    private const string SecurityDescriptorSddl = "O:BAG:BAD:P(A;;FA;;;WD)";

    private readonly WriteSession _session;
    private readonly VolumeWriter _vol;
    private readonly ApfsVolume _view;
    private readonly Lock _gate = new();
    private readonly byte[] _securityDescriptor;
    private readonly Timer _commitTimer;
    private readonly uint _bs;
    private int _openFiles;
    private long _lastIoTicks = long.MinValue / 2;
    private bool _disposed;

    public event Action<string>? Error;

    public ApfsWritableFileSystem(WriteSession session, int volumeIndex, TimeSpan? commitInterval = null)
    {
        _session = session;
        _vol = session.Volume(volumeIndex);
        _view = _vol.View(session.Container);
        _bs = session.Container.BlockSize;
        var sd = new RawSecurityDescriptor(SecurityDescriptorSddl);
        _securityDescriptor = new byte[sd.BinaryLength];
        sd.GetBinaryForm(_securityDescriptor, 0);
        var interval = commitInterval ?? TimeSpan.FromSeconds(5);
        _commitTimer = new Timer(_ => CommitIfDirty(), null, interval, interval);
    }

    public int OpenFileCount => Volatile.Read(ref _openFiles);

    /// <summary>
    /// Kullanımda mı: açık dosya ya da son 3 sn'de okuma/yazma (kopyalama sürüyor olabilir). Commit bekleyen değişiklik sayılmaz:
    /// ayırma onları zaten kalıcı yapar.
    /// </summary>
    public bool IsBusy => OpenFileCount > 0 || Environment.TickCount64 - Interlocked.Read(ref _lastIoTicks) < 3000;

    public bool HasPendingChanges { get { lock (_gate) return _vol.IsDirty; } }

    /// <summary>Bekleyen değişiklikleri kalıcı yapar (zamanlayıcı, Flush, ayırma).</summary>
    public void CommitIfDirty()
    {
        lock (_gate)
        {
            if (_disposed || !_vol.IsDirty) return;
            try { _session.Commit(); }
            catch (Exception e) { Error?.Invoke($"Değişiklikler diske yazılamadı: {e.Message}"); }
        }
    }

    public override int Init(object host0)
    {
        var host = (FileSystemHost)host0;
        host.SectorSize = 4096;
        host.SectorsPerAllocationUnit = 1;
        host.MaxComponentLength = 255;
        // Windows önbelleği KAPALI (WinFsp yalnız "sonsuz"da açar): yavaş USB bellekte Gezgin'in büyük kopyası önbellek
        // frenlemesinde kilitlendi — istekler hizmete hiç ulaşmadı, dosyayı açmak isteyen her süreç de askıda kaldı (2026-10-05,
        // yavaşlatılmış görüntüyle yeniden üretildi). Önbelleksiz her yazma doğrudan buraya gelir.
        host.FileInfoTimeout = 1_000;
        host.DirInfoTimeout = 1_000;
        host.VolumeInfoTimeout = 1_000;
        host.SecurityTimeout = 60_000;
        host.CaseSensitiveSearch = !_view.IsCaseInsensitive;
        host.CasePreservedNames = true;
        host.UnicodeOnDisk = true;
        host.PersistentAcls = true;
        host.PostCleanupWhenModifiedOnly = false;
        host.FlushAndPurgeOnCleanup = true;
        host.PassQueryDirectoryPattern = false;
        host.FileSystemName = "APFS";
        host.VolumeCreationTime = (ulong)ApfsFileSystem.ToFileTime(_view.LastModTimeNs);
        host.VolumeSerialNumber = BitConverter.ToUInt32(_view.Uuid.ToByteArray(), 0);
        return ApfsFileSystem.StatusSuccess;
    }

    public override int GetVolumeInfo(out VolumeInfo volumeInfo)
    {
        volumeInfo = default;
        lock (_gate)
        {
            volumeInfo.TotalSize = _session.Container.Superblock.BlockCount * _bs;
            volumeInfo.FreeSize = _session.FreeBlocks * _bs;
        }
        volumeInfo.SetVolumeLabel(_view.Name.Length > 32 ? _view.Name[..32] : _view.Name);
        return ApfsFileSystem.StatusSuccess;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Düğümler ve yol çözümü

    private sealed class Node(ulong id)
    {
        public ulong Id { get; } = id;
    }

    private sealed class OpenHandle(bool isDirectory)
    {
        public bool IsDirectory { get; } = isDirectory;
        public int Released;
        public List<(string Name, FileInfo Info)>? Listing;
    }

    private static List<string> Components(string fileName) =>
        [.. fileName.Split('\\', StringSplitOptions.RemoveEmptyEntries).Select(WindowsNames.FromWindows)];

    private Inode Resolve(string fileName, out List<string> actual) => _view.LookupComponents(Components(fileName), out actual);

    /// <summary>Yolun üst klasörü ve son bileşenin APFS adı.</summary>
    private (Inode Parent, string Name) ResolveParent(string fileName)
    {
        var parts = Components(fileName);
        if (parts.Count == 0) throw new ApfsException(ApfsErrorCode.InvalidArgument, "kök klasör");
        var parent = _view.LookupComponents(parts[..^1], out _);
        if (!parent.IsDirectory) throw new ApfsException(ApfsErrorCode.NotFound, fileName);
        return (parent, parts[^1]);
    }

    private FileInfo InfoOf(Inode ino, string name)
    {
        ulong size = ino.IsDirectory ? 0 : ino.IsSymlink ? (ulong)System.Text.Encoding.UTF8.GetByteCount(_view.ReadSymlinkTarget(ino.Id) ?? "") : _view.GetFileSize(ino);
        uint attr = ino.IsDirectory ? FileAttributeDirectory : 0;
        if (ino.IsHidden || (name.StartsWith('.') && name is not "." and not "..")) attr |= FileAttributeHidden;
        var fi = default(FileInfo);
        fi.FileAttributes = attr == 0 ? FileAttributeNormal : attr;
        fi.FileSize = size;
        fi.AllocationSize = (size + _bs - 1) / _bs * _bs;
        fi.CreationTime = (ulong)ApfsFileSystem.ToFileTime(ino.CreateTimeNs);
        fi.LastAccessTime = (ulong)ApfsFileSystem.ToFileTime(ino.AccessTimeNs);
        fi.LastWriteTime = (ulong)ApfsFileSystem.ToFileTime(ino.ModTimeNs);
        fi.ChangeTime = (ulong)ApfsFileSystem.ToFileTime(ino.ChangeTimeNs);
        fi.IndexNumber = ino.Id;
        fi.HardLinks = ino.IsDirectory ? 0 : (uint)Math.Max(1, ino.ChildrenOrLinks);
        return fi;
    }

    private FileInfo InfoOf(ulong id) => InfoOf(_view.GetInode(id), "");

    private void Touched() => Interlocked.Exchange(ref _lastIoTicks, Environment.TickCount64);

    private static long? FromFileTime(ulong ft) =>
        ft == 0 || ft == ulong.MaxValue ? null : ((long)ft - 116444736000000000L) * 100;

    // ---------------------------------------------------------------------------------------------------------------
    // Açma, okuma, listeleme

    public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
    {
        lock (_gate)
        {
            var ino = Resolve(fileName, out var actual);
            fileAttributes = InfoOf(ino, actual.Count > 0 ? actual[^1] : "").FileAttributes;
        }
        if (securityDescriptor is not null) securityDescriptor = _securityDescriptor;
        return ApfsFileSystem.StatusSuccess;
    }

    public override int Open(string fileName, uint createOptions, uint grantedAccess,
        out object fileNode, out object fileDesc, out FileInfo fileInfo, out string normalizedName)
    {
        lock (_gate)
        {
            var ino = Resolve(fileName, out var actual);
            fileNode = new Node(ino.Id);
            fileDesc = new OpenHandle(ino.IsDirectory);
            if (!ino.IsDirectory) Interlocked.Increment(ref _openFiles);
            fileInfo = InfoOf(ino, actual.Count > 0 ? actual[^1] : "");
            normalizedName = ApfsFileSystem.SameLengthOrNull("\\" + string.Join('\\', actual.Select(WindowsNames.ToWindows)), fileName);
            return ApfsFileSystem.StatusSuccess;
        }
    }

    public override int Create(string fileName, uint createOptions, uint grantedAccess, uint fileAttributes, byte[] securityDescriptor,
        ulong allocationSize, out object fileNode, out object fileDesc, out FileInfo fileInfo, out string normalizedName)
    {
        lock (_gate)
        {
            var (parent, name) = ResolveParent(fileName);
            bool dir = (createOptions & FileDirectoryFile) != 0;
            ulong id = dir ? _vol.CreateDirectory(parent.Id, name) : _vol.CreateFile(parent.Id, name);
            if ((fileAttributes & FileAttributeHidden) != 0) _vol.SetHidden(id, true);
            fileNode = new Node(id);
            fileDesc = new OpenHandle(dir);
            if (!dir) Interlocked.Increment(ref _openFiles);
            fileInfo = InfoOf(id);
            normalizedName = null!;
            Touched();
            return ApfsFileSystem.StatusSuccess;
        }
    }

    public override int Overwrite(object fileNode, object fileDesc, uint fileAttributes, bool replaceFileAttributes, ulong allocationSize, out FileInfo fileInfo)
    {
        lock (_gate)
        {
            ulong id = ((Node)fileNode).Id;
            // macOS'un sıkıştırdığı dosya bu sürümde salt okunur (kullanıcı kararı 2026-10-05: gerçek Mac dosyasında sınanmadı);
            // SetSize bunu RequireWritableFile'da reddeder → "erişim engellendi"
            _vol.SetSize(id, 0);
            bool hidden = (fileAttributes & FileAttributeHidden) != 0;
            if (replaceFileAttributes || hidden) _vol.SetHidden(id, hidden);
            fileInfo = InfoOf(id);
            Touched();
            return ApfsFileSystem.StatusSuccess;
        }
    }

    public override void Close(object fileNode, object fileDesc) => Release(fileDesc);

    public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags)
    {
        Release(fileDesc);
        if ((flags & CleanupDelete) == 0 || fileName is null) return;
        lock (_gate)
        {
            try
            {
                var (parent, name) = ResolveParent(fileName);
                _vol.Delete(parent.Id, name);
                Touched();
            }
            catch (ApfsException e) when (e.Code is not ApfsErrorCode.IoError)
            {
                Error?.Invoke($"\"{fileName}\" silinemedi: {e.Message}");
            }
        }
    }

    private void Release(object fileDesc)
    {
        if (fileDesc is OpenHandle h && !h.IsDirectory && Interlocked.Exchange(ref h.Released, 1) == 0)
            Interlocked.Decrement(ref _openFiles);
    }

    public override int GetFileInfo(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        lock (_gate) fileInfo = InfoOf(((Node)fileNode).Id);
        return ApfsFileSystem.StatusSuccess;
    }

    public override int GetSecurity(object fileNode, object fileDesc, ref byte[] securityDescriptor)
    {
        securityDescriptor = _securityDescriptor;
        return ApfsFileSystem.StatusSuccess;
    }

    public override int SetSecurity(object fileNode, object fileDesc, AccessControlSections sections, byte[] securityDescriptor) =>
        ApfsFileSystem.StatusSuccess; // APFS'te Windows erişim listesi saklanmaz; kopyalama başarısız olmasın diye sessizce kabul edilir

    public override unsafe int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, out uint bytesTransferred)
    {
        bytesTransferred = 0;
        lock (_gate)
        {
            Touched();
            var ino = _view.GetInode(((Node)fileNode).Id);
            if (ino.IsDirectory) return ApfsFileSystem.StatusNotSupported;
            var span = new Span<byte>((void*)buffer, (int)Math.Min(length, int.MaxValue));
            if (ino.IsSymlink)
            {
                var target = System.Text.Encoding.UTF8.GetBytes(_view.ReadSymlinkTarget(ino.Id) ?? "");
                if (offset >= (ulong)target.Length) return ApfsFileSystem.StatusEndOfFile;
                int n = (int)Math.Min((ulong)span.Length, (ulong)target.Length - offset);
                target.AsSpan((int)offset, n).CopyTo(span);
                bytesTransferred = (uint)n;
                return ApfsFileSystem.StatusSuccess;
            }
            if (offset >= _view.GetFileSize(ino)) return ApfsFileSystem.StatusEndOfFile;
            bytesTransferred = (uint)_view.OpenFile(ino).Read(offset, span);
            return ApfsFileSystem.StatusSuccess;
        }
    }

    public override bool ReadDirectoryEntry(object fileNode, object fileDesc, string pattern, string marker,
        ref object context, out string fileName, out FileInfo fileInfo)
    {
        if (context is not IEnumerator<(string Name, FileInfo Info)> en)
        {
            var h = (OpenHandle)fileDesc;
            lock (_gate) h.Listing ??= BuildListing(((Node)fileNode).Id);
            IEnumerable<(string Name, FileInfo Info)> seq = h.Listing;
            if (marker is not null) seq = seq.Where(e => DirOrder.Compare(e.Name, marker) > 0);
            en = seq.GetEnumerator();
            context = en;
        }
        if (en.MoveNext()) { (fileName, fileInfo) = en.Current; return true; }
        fileName = null!;
        fileInfo = default;
        return false;
    }

    private static readonly Comparer<string> DirOrder = Comparer<string>.Create((a, b) =>
    {
        int ra = a == "." ? 0 : a == ".." ? 1 : 2;
        int rb = b == "." ? 0 : b == ".." ? 1 : 2;
        return ra != rb ? ra.CompareTo(rb) : string.CompareOrdinal(a, b);
    });

    private List<(string Name, FileInfo Info)> BuildListing(ulong dirId)
    {
        var dir = _view.GetInode(dirId);
        var list = new List<(string Name, FileInfo Info)>();
        if (dirId != Inode.RootDirId)
        {
            list.Add((".", InfoOf(dir, ".")));
            var parent = _view.TryGetInode(dir.ParentId);
            list.Add(("..", parent is null ? InfoOf(dir, "..") : InfoOf(parent, "..")));
        }
        foreach (var e in _view.ListDirectory(dirId))
        {
            try { list.Add((WindowsNames.ToWindows(e.Name), InfoOf(_view.GetInode(e.FileId), e.Name))); }
            catch (ApfsException ex) when (ex.Code is not ApfsErrorCode.IoError) { Error?.Invoke($"\"{e.Name}\" okunamadı: {ex.Message}"); }
        }
        list.Sort((a, b) => DirOrder.Compare(a.Name, b.Name));
        return list;
    }

    public override int GetDirInfoByName(object parentNode, object fileDesc, string fileName, out string normalizedName, out FileInfo fileInfo)
    {
        lock (_gate)
        {
            var entry = _view.FindEntry(((Node)parentNode).Id, WindowsNames.FromWindows(fileName));
            if (entry is null) { normalizedName = null!; fileInfo = default; return ApfsFileSystem.StatusObjectNameNotFound; }
            normalizedName = ApfsFileSystem.SameLengthOrNull(WindowsNames.ToWindows(entry.Name), fileName);
            fileInfo = InfoOf(_view.GetInode(entry.FileId), entry.Name);
            return ApfsFileSystem.StatusSuccess;
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Değiştirme

    public override unsafe int Write(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, bool writeToEndOfFile,
        bool constrainedIo, out uint bytesTransferred, out FileInfo fileInfo)
    {
        lock (_gate)
        {
            ulong id = ((Node)fileNode).Id;
            ulong size = _vol.GetSize(id);
            if (constrainedIo)
            {
                if (offset >= size) { bytesTransferred = 0; fileInfo = InfoOf(id); return ApfsFileSystem.StatusSuccess; }
                length = (uint)Math.Min(length, size - offset);
            }
            if (writeToEndOfFile) offset = size;
            _vol.Write(id, offset, new ReadOnlySpan<byte>((void*)buffer, (int)length));
            bytesTransferred = length;
            fileInfo = InfoOf(id);
            Touched();
            return ApfsFileSystem.StatusSuccess;
        }
    }

    public override int SetFileSize(object fileNode, object fileDesc, ulong newSize, bool setAllocationSize, out FileInfo fileInfo)
    {
        lock (_gate)
        {
            ulong id = ((Node)fileNode).Id;
            // Ayırma boyutu yalnızca bir ipucu; dosyadan küçükse dosya kısalır (WinFsp sözleşmesi)
            if (!setAllocationSize || newSize < _vol.GetSize(id)) _vol.SetSize(id, newSize);
            fileInfo = InfoOf(id);
            Touched();
            return ApfsFileSystem.StatusSuccess;
        }
    }

    public override int SetBasicInfo(object fileNode, object fileDesc, uint fileAttributes, ulong creationTime, ulong lastAccessTime,
        ulong lastWriteTime, ulong changeTime, out FileInfo fileInfo)
    {
        lock (_gate)
        {
            ulong id = ((Node)fileNode).Id;
            if (fileAttributes != uint.MaxValue) _vol.SetHidden(id, (fileAttributes & FileAttributeHidden) != 0);
            if (creationTime != 0 || lastAccessTime != 0 || lastWriteTime != 0 || changeTime != 0)
                _vol.SetTimes(id, FromFileTime(creationTime), FromFileTime(lastWriteTime), FromFileTime(changeTime), FromFileTime(lastAccessTime));
            fileInfo = InfoOf(id);
            return ApfsFileSystem.StatusSuccess;
        }
    }

    public override int CanDelete(object fileNode, object fileDesc, string fileName)
    {
        lock (_gate)
        {
            var ino = _view.GetInode(((Node)fileNode).Id);
            return ino.IsDirectory && ino.ChildrenOrLinks > 0 ? StatusDirectoryNotEmpty : ApfsFileSystem.StatusSuccess;
        }
    }

    public override int Rename(object fileNode, object fileDesc, string fileName, string newFileName, bool replaceIfExists)
    {
        lock (_gate)
        {
            var (srcParent, srcName) = ResolveParent(fileName);
            var (dstParent, dstName) = ResolveParent(newFileName);
            _vol.Rename(srcParent.Id, srcName, dstParent.Id, dstName, replaceIfExists);
            Touched();
            return ApfsFileSystem.StatusSuccess;
        }
    }

    public override int Flush(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        CommitIfDirty();
        lock (_gate) fileInfo = fileNode is Node n ? InfoOf(n.Id) : default;
        return ApfsFileSystem.StatusSuccess;
    }

    public override int SetVolumeLabel(string volumeLabel, out VolumeInfo volumeInfo)
    {
        volumeInfo = default;
        return ApfsFileSystem.StatusNotSupported;
    }

    public override int ExceptionHandler(Exception ex)
    {
        if (ex is ApfsException ae)
        {
            if (ae.Code is not ApfsErrorCode.NotFound and not ApfsErrorCode.AlreadyExists) Error?.Invoke(ae.Message);
            return ToNtStatus(ae.Code);
        }
        Error?.Invoke($"Beklenmeyen hata: {ex.Message}");
        return ApfsFileSystem.StatusUnexpectedIoError;
    }

    internal static int ToNtStatus(ApfsErrorCode code) => code switch
    {
        ApfsErrorCode.AlreadyExists => StatusObjectNameCollision,
        ApfsErrorCode.DirectoryNotEmpty => StatusDirectoryNotEmpty,
        ApfsErrorCode.DiskFull => StatusDiskFull,
        ApfsErrorCode.InvalidArgument => StatusInvalidParameter,
        // Bu sürümde değiştirilemeyen (klon, sıkıştırılmış) dosya: uygulamalar "erişim engellendi" gösterir
        ApfsErrorCode.Unsupported => ApfsFileSystem.StatusAccessDenied,
        _ => ApfsFileSystem.ToNtStatus(code),
    };

    /// <summary>Zamanlayıcıyı durdurur; oturumu KAPATMAZ (bkz. <see cref="ApfsWritableMount"/>).</summary>
    public void Dispose()
    {
        lock (_gate) _disposed = true;
        _commitTimer.Dispose();
    }
}
