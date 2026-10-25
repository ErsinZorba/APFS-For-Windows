using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Apfs.ImageBuilder;

/// <summary>NTFS seyrek dosya işareti (FSCTL_SET_SPARSE). Desteklenmezse sessizce normal dosya olarak kalır.</summary>
[SupportedOSPlatform("windows")]
internal static class NtfsSparse
{
    private const uint FsctlSetSparse = 0x000900C4;

    public static void Mark(FileStream fs) =>
        DeviceIoControl(fs.SafeFileHandle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, uint inSize, IntPtr outBuf, uint outSize,
        out uint returned, IntPtr overlapped);
}
