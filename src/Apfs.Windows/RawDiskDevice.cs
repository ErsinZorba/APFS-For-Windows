using System.ComponentModel;
using System.Runtime.InteropServices;
using Apfs.IO;
using Microsoft.Win32.SafeHandles;

namespace Apfs.Windows;

/// <summary>
/// Fiziksel disk (\\.\PhysicalDriveN). Disk YALNIZCA GENERIC_READ erişimiyle açılır; bu handle ile
/// çekirdek seviyesinde diske yazmak mümkün değildir. Okumalar sektör hizalı yapılır.
/// </summary>
public sealed class RawDiskDevice : IBlockDevice
{
    /// <summary>İstenen erişim hakkı. Test ile yalnızca GENERIC_READ olduğu doğrulanır.</summary>
    internal const uint DesiredAccess = GenericRead;

    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x1, FileShareWrite = 0x2;
    private const uint OpenExisting = 3;
    private const uint IoctlDiskGetLengthInfo = 0x0007405C;
    private const uint IoctlDiskGetDriveGeometryEx = 0x000700A0;

    private readonly SafeFileHandle _handle;
    private readonly Lock _lock = new();

    public RawDiskDevice(int diskNumber) : this($@"\\.\PhysicalDrive{diskNumber}") { }

    public RawDiskDevice(string devicePath)
    {
        Path = devicePath;
        _handle = Open(devicePath);
        if (_handle.IsInvalid)
        {
            int err = Marshal.GetLastPInvokeError();
            _handle.Dispose();
            throw err switch
            {
                5 => new ApfsException(ApfsErrorCode.AccessDenied, $"{devicePath}: ham diski okumak için yönetici yetkisi gerekir"),
                2 or 3 => new ApfsException(ApfsErrorCode.NotFound, devicePath),
                _ => new ApfsException(ApfsErrorCode.IoError, $"{devicePath}: {new Win32Exception(err).Message}"),
            };
        }
        Length = QueryLength();
        SectorSize = QuerySectorSize();
    }

    /// <summary>Handle açan tek yer. Erişim hakkı her zaman <see cref="DesiredAccess"/>'tir.</summary>
    private static SafeFileHandle Open(string path) =>
        CreateFileW(path, DesiredAccess, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

    public string Path { get; }

    /// <summary>Yalnızca okuma haklı tanıtıcı; "güvenli kaldır" bildirimine kaydolmak için (hizmet) kullanılır.</summary>
    public SafeFileHandle Handle => _handle;
    public long Length { get; }
    public int SectorSize { get; }

    public void Read(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset > Length - buffer.Length)
            throw new ApfsException(ApfsErrorCode.Corrupt, $"disk sınırı dışında okuma: {offset}+{buffer.Length}");
        if (buffer.IsEmpty) return;

        long alignedStart = offset / SectorSize * SectorSize;
        long alignedEnd = (offset + buffer.Length + SectorSize - 1) / SectorSize * SectorSize;
        int alignedLen = checked((int)(alignedEnd - alignedStart));

        // Sektör hizalı ara bellek (ham disk okumaları hizalı bellek ve uzunluk ister)
        unsafe
        {
            void* mem = NativeMemory.AlignedAlloc((nuint)alignedLen, (nuint)Math.Max(SectorSize, 4096));
            try
            {
                var span = new Span<byte>(mem, alignedLen);
                int done = 0;
                while (done < alignedLen)
                {
                    int n;
                    try
                    {
                        n = RandomAccess.Read(_handle, span[done..], alignedStart + done);
                    }
                    catch (IOException e) { throw new ApfsException(ApfsErrorCode.IoError, e.Message, e); }
                    catch (ObjectDisposedException e) { throw new ApfsException(ApfsErrorCode.IoError, "disk kapatıldı", e); }
                    if (n <= 0) throw new ApfsException(ApfsErrorCode.IoError, "diskten beklenenden az veri okundu");
                    done += n;
                }
                span.Slice((int)(offset - alignedStart), buffer.Length).CopyTo(buffer);
            }
            finally
            {
                NativeMemory.AlignedFree(mem);
            }
        }
    }

    private long QueryLength()
    {
        Span<byte> outBuf = stackalloc byte[8];
        if (!DeviceIoControl(_handle, IoctlDiskGetLengthInfo, IntPtr.Zero, 0, ref MemoryMarshal.GetReference(outBuf), 8, out _, IntPtr.Zero))
            throw new ApfsException(ApfsErrorCode.IoError, $"disk boyutu alınamadı: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
        return BitConverter.ToInt64(outBuf);
    }

    private int QuerySectorSize()
    {
        Span<byte> outBuf = stackalloc byte[256];
        if (!DeviceIoControl(_handle, IoctlDiskGetDriveGeometryEx, IntPtr.Zero, 0, ref MemoryMarshal.GetReference(outBuf), 256, out _, IntPtr.Zero))
            return 512;
        // DISK_GEOMETRY_EX.Geometry.BytesPerSector: Cylinders(8) + MediaType(4) + TracksPerCylinder(4) + SectorsPerTrack(4)
        int bps = BitConverter.ToInt32(outBuf[20..]);
        return bps is 512 or 1024 or 2048 or 4096 ? bps : 512;
    }

    public void Dispose() => _handle.Dispose();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize,
        ref byte lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);
}
