using Microsoft.Win32.SafeHandles;

namespace Apfs.IO;

/// <summary>
/// Yazılabilir disk görüntüsü dosyası (geliştirme ve testler). Fiziksel disk gibi başka süreçler de görüntüye yazabilir
/// (hizmetin "disk içeriği değişti" denetimi bununla sınanır).
/// </summary>
public sealed class FileImageWriter : IWritableBlockDevice
{
    private readonly SafeFileHandle _handle;

    public FileImageWriter(string path, WriteGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        try
        {
            _handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        catch (FileNotFoundException e) { throw new ApfsException(ApfsErrorCode.NotFound, path, e); }
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
    }

    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        if (offset < 0 || offset > Length - data.Length)
            throw new ApfsException(ApfsErrorCode.InvalidArgument, $"aygıt sınırı dışında yazma: {offset}+{data.Length}");
        try { RandomAccess.Write(_handle, data, offset); }
        catch (IOException e) { throw new ApfsException(ApfsErrorCode.IoError, e.Message, e); }
    }

    public void Flush()
    {
        try { RandomAccess.FlushToDisk(_handle); }
        catch (IOException e) { throw new ApfsException(ApfsErrorCode.IoError, e.Message, e); }
    }

    public void Dispose() => _handle.Dispose();
}
