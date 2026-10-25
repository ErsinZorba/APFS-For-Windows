using Apfs.Container;
using Apfs.Volume;
using Fsp;

namespace Apfs.Mount;

/// <summary>Bir APFS birimini sürücü harfine bağlar ve ayırır.</summary>
public sealed class ApfsMount : IDisposable
{
    private readonly FileSystemHost _host;

    private ApfsMount(FileSystemHost host, ApfsFileSystem fs, string mountPoint)
    {
        _host = host;
        FileSystem = fs;
        MountPoint = mountPoint;
    }

    public ApfsFileSystem FileSystem { get; }
    public string MountPoint { get; }

    /// <param name="mountPoint">"X:" biçiminde sürücü harfi; null ise boştaki ilk harf (Z'den geriye) kullanılır.</param>
    public static ApfsMount Mount(ApfsVolume volume, string? mountPoint, Action<string>? onError = null)
    {
        var c = volume.Container;
        // APFS birimleri kapsayıcının alanını paylaşır: her birimde toplam = kapsayıcı boyutu, boş = kapsayıcıdaki boş alan.
        long total = (long)c.Superblock.BlockCount * c.BlockSize;
        var fs = new ApfsFileSystem(volume, total, c.FreeBytes);
        if (onError is not null) fs.Error += onError;

        var host = new FileSystemHost(fs);
        int status;
        try
        {
            status = host.Mount(mountPoint, null, false, 0);
        }
        catch (DllNotFoundException e)
        {
            host.Dispose();
            throw new ApfsException(ApfsErrorCode.Unsupported, "WinFsp kurulu değil. Kurmak için: winget install WinFsp.WinFsp", e);
        }
        catch (TypeInitializationException e)
        {
            host.Dispose();
            throw new ApfsException(ApfsErrorCode.Unsupported,
                $"WinFsp kurulu değil veya yüklenemedi ({e.InnerException?.Message ?? e.Message}). Kurmak için: winget install WinFsp.WinFsp", e);
        }
        if (status != 0)
        {
            host.Dispose();
            throw new ApfsException(ApfsErrorCode.IoError, $"sürücü bağlanamadı (NTSTATUS 0x{status:X8}); harf kullanımda olabilir");
        }
        return new ApfsMount(host, fs, host.MountPoint());
    }

    public void Dispose()
    {
        _host.Unmount();
        _host.Dispose();
    }
}
