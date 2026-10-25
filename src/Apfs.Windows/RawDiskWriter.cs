using System.ComponentModel;
using System.Runtime.InteropServices;
using Apfs.IO;
using Microsoft.Win32.SafeHandles;

namespace Apfs.Windows;

/// <summary>
/// Fiziksel diske YAZABİLEN aygıt (\\.\PhysicalDriveN). Yalnızca yazma kapısının verdiği izinle kurulur; okuma yolundaki
/// <see cref="RawDiskDevice"/> değişmeden GENERIC_READ kalır. Yazmalar sektör hizalı olmalıdır (yazıcı her zaman 4 KiB'lık
/// bloklar yazar). <see cref="Flush"/> = FlushFileBuffers (diskin kendi önbelleği dahil kalıcı ortama boşaltma).
/// Not: Windows bölüm için bir birim (ör. RAW) bağlamışsa bölüm içine ham yazmayı engelleyebilir; bu durumda yazma
/// "erişim engellendi" ile başarısız olur ve hiçbir şey yazılmaz (VHD'de ölçüldü: Windows APFS bölümüne birim atamıyor,
/// ham yazma engellenmiyor).
/// </summary>
public sealed class RawDiskWriter : IWritableBlockDevice
{
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x1, FileShareWrite = 0x2;
    private const uint OpenExisting = 3;
    private const uint IoctlDiskGetLengthInfo = 0x0007405C;
    private const uint IoctlDiskGetDriveGeometryEx = 0x000700A0;

    private readonly SafeFileHandle _handle;

    public RawDiskWriter(int diskNumber, WriteGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        Path = $@"\\.\PhysicalDrive{diskNumber}";
        _handle = CreateFileW(Path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (_handle.IsInvalid)
        {
            int err = Marshal.GetLastPInvokeError();
            _handle.Dispose();
            throw err == 5
                ? new ApfsException(ApfsErrorCode.AccessDenied, $"{Path}: diske yazmak için yönetici yetkisi gerekir")
                : new ApfsException(ApfsErrorCode.IoError, $"{Path}: {new Win32Exception(err).Message}");
        }
        Span<byte> len = stackalloc byte[8];
        if (!DeviceIoControl(_handle, IoctlDiskGetLengthInfo, IntPtr.Zero, 0, ref MemoryMarshal.GetReference(len), 8, out _, IntPtr.Zero))
            throw new ApfsException(ApfsErrorCode.IoError, "disk boyutu alınamadı");
        Length = BitConverter.ToInt64(len);
        Span<byte> geo = stackalloc byte[256];
        SectorSize = DeviceIoControl(_handle, IoctlDiskGetDriveGeometryEx, IntPtr.Zero, 0, ref MemoryMarshal.GetReference(geo), 256, out _, IntPtr.Zero)
            && BitConverter.ToInt32(geo[20..]) is 512 or 4096 ? BitConverter.ToInt32(geo[20..]) : 512;
    }

    public string Path { get; }
    /// <summary>"Güvenli kaldır" bildirimine kaydolmak için (hizmet).</summary>
    public SafeFileHandle Handle => _handle;
    public long Length { get; }
    public int SectorSize { get; }

    /// <summary>Okuma: hizasız istekler (dosya verisinin ortasından) sektör hizalı ara bellekle karşılanır.</summary>
    public void Read(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset > Length - buffer.Length) throw new ApfsException(ApfsErrorCode.Corrupt, $"disk sınırı dışında okuma: {offset}+{buffer.Length}");
        if (buffer.IsEmpty) return;
        long start = offset / SectorSize * SectorSize;
        long end = (offset + buffer.Length + SectorSize - 1) / SectorSize * SectorSize;
        var aligned = start == offset && end == offset + buffer.Length ? null : new byte[end - start];
        Span<byte> target = aligned ?? buffer;
        int done = 0;
        try
        {
            while (done < target.Length)
            {
                int n = RandomAccess.Read(_handle, target[done..], start + done);
                if (n <= 0) throw new ApfsException(ApfsErrorCode.IoError, "diskten beklenenden az veri okundu");
                done += n;
            }
        }
        catch (IOException e) { throw new ApfsException(ApfsErrorCode.IoError, e.Message, e); }
        aligned?.AsSpan((int)(offset - start), buffer.Length).CopyTo(buffer);
    }

    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        if (offset < 0 || offset > Length - data.Length) throw new ApfsException(ApfsErrorCode.InvalidArgument, $"disk sınırı dışında yazma: {offset}+{data.Length}");
        Aligned(offset, data.Length);
        try { RandomAccess.Write(_handle, data, offset); }
        catch (UnauthorizedAccessException e) { throw new ApfsException(ApfsErrorCode.AccessDenied, "Windows bu bölüme yazmaya izin vermedi", e); }
        catch (IOException e) { throw new ApfsException(ApfsErrorCode.IoError, e.Message, e); }
    }

    public void Flush()
    {
        try { RandomAccess.FlushToDisk(_handle); }
        catch (IOException e) { throw new ApfsException(ApfsErrorCode.IoError, e.Message, e); }
    }

    /// <summary>Ham disk G/Ç'si sektör hizalı olmalı; yazıcı 4 KiB'lık blokla çalıştığı için hizasızlık programlama hatasıdır.</summary>
    private void Aligned(long offset, int length)
    {
        if (offset % SectorSize != 0 || length % SectorSize != 0)
            throw new ArgumentException($"sektör hizasız ham disk G/Ç'si: {offset}+{length}");
    }

    public void Dispose() => _handle.Dispose();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize,
        ref byte lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);
}
