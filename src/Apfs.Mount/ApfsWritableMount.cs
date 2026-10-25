using Apfs.Write;
using Fsp;

namespace Apfs.Mount;

/// <summary>
/// Bir birimi YAZILABİLİR sürücü harfine bağlar ve ayırır. Ayırma sırası:
/// WinFsp ayrılır (yazılabilir sürücüde Windows önbelleği kapalı olduğu için bütün veri zaten gelmiştir; ayırmadan sonra hiçbir
/// çağrı gelmez) → oturum kapatılır (son commit + blok 0). Aygıtın sahibi çağırandır.
/// </summary>
public sealed class ApfsWritableMount : IDisposable
{
    private readonly FileSystemHost _host;
    private readonly WriteSession _session;
    private readonly bool _closeSession;
    private bool _disposed;

    private ApfsWritableMount(FileSystemHost host, ApfsWritableFileSystem fs, WriteSession session, string mountPoint, bool closeSession)
    {
        _host = host;
        _session = session;
        _closeSession = closeSession;
        FileSystem = fs;
        MountPoint = mountPoint;
    }

    public ApfsWritableFileSystem FileSystem { get; }
    public string MountPoint { get; }

    /// <param name="closeSession">true ise ayırırken oturum da kapatılır (tek birimli kullanım); false ise oturumu çağıran kapatır.</param>
    public static ApfsWritableMount Mount(WriteSession session, int volumeIndex, string? mountPoint, Action<string>? onError = null, bool closeSession = true)
    {
        var fs = new ApfsWritableFileSystem(session, volumeIndex);
        if (onError is not null) fs.Error += onError;
        var host = new FileSystemHost(fs);
        int status;
        try
        {
            status = host.Mount(mountPoint, null, false, 0);
        }
        catch (Exception e) when (e is DllNotFoundException or TypeInitializationException)
        {
            host.Dispose();
            fs.Dispose();
            throw new ApfsException(ApfsErrorCode.Unsupported, CoreText.T("Fs.WinFspMissing"), e);
        }
        if (status != 0)
        {
            host.Dispose();
            fs.Dispose();
            throw new ApfsException(ApfsErrorCode.IoError, CoreText.T("Fs.MountFailed", status));
        }
        return new ApfsWritableMount(host, fs, session, host.MountPoint(), closeSession);
    }

    /// <summary>Ayırır ve oturumu kapatır (son değişiklikler kalıcı olur, disk "düzgün kapatıldı" işaretlenir).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _host.Unmount();
        _host.Dispose();
        FileSystem.Dispose();
        if (_closeSession) _session.Close();
    }
}
