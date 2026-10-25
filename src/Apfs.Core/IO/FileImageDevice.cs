using Microsoft.Win32.SafeHandles;

namespace Apfs.IO;

/// <summary>
/// Disk görüntüsü dosyası. Dosya yalnızca okuma izniyle açılır. Fiziksel disk gibi başka süreçler görüntüye yazabilir
/// (hizmetin "disk içeriği değişti" denetimi bununla sınanır).
/// </summary>
public sealed class FileImageDevice : IBlockDevice
{
    private readonly SafeFileHandle _handle;

    public FileImageDevice(string path)
    {
        if (Directory.Exists(path))
            throw new ApfsException(ApfsErrorCode.InvalidArgument, $"\"{path}\" bir klasör; disk görüntüsü dosyası veya \\\\.\\PhysicalDriveN bekleniyor");
        try
        {
            _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (FileNotFoundException e) { throw new ApfsException(ApfsErrorCode.NotFound, path, e); }
        catch (DirectoryNotFoundException e) { throw new ApfsException(ApfsErrorCode.NotFound, path, e); }
        catch (UnauthorizedAccessException e) { throw new ApfsException(ApfsErrorCode.AccessDenied, path, e); }
        catch (IOException e) { throw new ApfsException(ApfsErrorCode.IoError, e.Message, e); }
        Length = RandomAccess.GetLength(_handle);
    }

    public long Length { get; }
    public int SectorSize => 512;

    public void Read(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset > Length - buffer.Length)
            throw ApfsException.Corrupt($"aygıt sınırı dışında okuma: {offset}+{buffer.Length}");
        int done = 0;
        try
        {
            while (done < buffer.Length)
            {
                int n = RandomAccess.Read(_handle, buffer[done..], offset + done);
                if (n <= 0) throw new ApfsException(ApfsErrorCode.IoError, "beklenmeyen dosya sonu");
                done += n;
            }
        }
        catch (IOException e) { throw new ApfsException(ApfsErrorCode.IoError, e.Message, e); }
        catch (ObjectDisposedException e) { throw new ApfsException(ApfsErrorCode.IoError, "aygıt kapatıldı", e); }
    }

    public void Dispose() => _handle.Dispose();
}
