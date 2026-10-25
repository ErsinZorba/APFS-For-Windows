using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Apfs.Windows;

/// <summary>
/// "Güvenle çıkar": fiziksel diskin çıkarılabilir üst aygıtını (ör. USB kutusu) Windows'a çıkarttırır; Windows'un kendi
/// "Donanımı güvenle kaldır" simgesinin yaptığı işin aynısı. Diske hiçbir şey yazılmaz; aygıtlar yalnızca sorgu
/// erişimiyle (0) açılır. Windows çıkarmayı reddederse (başka bir program diski kullanıyorsa) nedeni Türkçe döner.
/// </summary>
public static class DiskEjector
{
    private static readonly Guid DiskInterface = Guid.Parse("53F56307-B6BF-11D0-94F2-00A0C91EFB8B");
    private static readonly DevPropKey InstanceIdKey = new(Guid.Parse("78C34FC8-104A-4ACA-9EA4-524D52996E57"), 256);

    private const uint CrSuccess = 0, CrBufferSmall = 0x1A;
    private const uint CmDrpCapabilities = 0x10;
    private const uint CmDevcapRemovable = 0x4;
    private const uint FileShareRead = 0x1, FileShareWrite = 0x2, OpenExisting = 3;
    private const uint IoctlStorageGetDeviceNumber = 0x002D1080;
    private const uint FileDeviceDisk = 7;

    public sealed record Target(string DiskInstanceId, string EjectInstanceId, bool Removable);

    /// <summary>\\.\PhysicalDriveN için çıkarılacak aygıtı bulur (çıkarmaz). Bulunamazsa null.</summary>
    public static Target? Find(int diskNumber)
    {
        string? id = FindDiskInstanceId(diskNumber);
        if (id is null || CM_Locate_DevNodeW(out uint node, id, 0) != CrSuccess) return null;
        // Diskin kendisi çoğu zaman "çıkarılabilir" değildir; çıkarılabilir olan atası (USB depolama aygıtı) çıkarılır.
        for (uint n = node, depth = 0; depth < 8; depth++)
        {
            if (IsRemovable(n)) return new Target(id, InstanceId(n) ?? id, true);
            if (CM_Get_Parent(out uint parent, n, 0) != CrSuccess) break;
            n = parent;
        }
        return new Target(id, id, false);
    }

    /// <summary>Aygıtı çıkarır. Başarılıysa (true, null), değilse (false, Türkçe neden).</summary>
    public static (bool Ok, string? Error) Eject(Target target)
    {
        if (CM_Locate_DevNodeW(out uint node, target.EjectInstanceId, 0) != CrSuccess)
            return (true, null); // aygıt zaten yok: çıkarılmış sayılır
        var veto = new char[260];
        // Veto adı için ara bellek verildiğinde Windows kendi penceresini göstermez (hizmetten çağrılabilir).
        uint cr = CM_Request_Device_EjectW(node, out int vetoType, veto, (uint)veto.Length, 0);
        if (cr == CrSuccess && vetoType == 0) return (true, null);
        string who = new string(veto).TrimEnd('\0');
        return (false, vetoType switch
        {
            3 or 4 => $"Disk başka bir program tarafından kullanılıyor ({who}). O programı kapatıp tekrar deneyin.",
            5 or 6 => "Diskte başka bir program açık dosya veya tanıtıcı tutuyor. Açık pencereleri ve programları kapatıp tekrar deneyin.",
            _ => $"Windows diski şu anda çıkaramadı (kod {cr:X}/{vetoType}{(who.Length > 0 ? ", " + who : "")}). Biraz sonra tekrar deneyin.",
        });
    }

    private static bool IsRemovable(uint node)
    {
        uint size = 4;
        return CM_Get_DevNode_Registry_PropertyW(node, CmDrpCapabilities, out _, out uint caps, ref size, 0) == CrSuccess
            && (caps & CmDevcapRemovable) != 0;
    }

    private static string? InstanceId(uint node)
    {
        var buf = new char[400];
        return CM_Get_Device_IDW(node, buf, (uint)buf.Length, 0) == CrSuccess ? new string(buf).TrimEnd('\0') : null;
    }

    /// <summary>Disk arayüzlerini gezer, aygıt numarası eşleşenin örnek kimliğini döndürür.</summary>
    private static string? FindDiskInstanceId(int diskNumber)
    {
        var guid = DiskInterface;
        char[] list;
        uint cr;
        do
        {
            if (CM_Get_Device_Interface_List_SizeW(out uint len, ref guid, null, 0) != CrSuccess) return null;
            list = new char[len];
            cr = CM_Get_Device_Interface_ListW(ref guid, null, list, len, 0);
        } while (cr == CrBufferSmall);
        if (cr != CrSuccess) return null;

        foreach (var path in new string(list).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            using var h = CreateFileW(path, 0, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (h.IsInvalid) continue;
            if (!DeviceIoControl(h, IoctlStorageGetDeviceNumber, IntPtr.Zero, 0, out StorageDeviceNumber sdn, 12, out _, IntPtr.Zero)) continue;
            if (sdn.DeviceType != FileDeviceDisk || sdn.DeviceNumber != diskNumber) continue;

            var key = InstanceIdKey;
            var buf = new byte[800];
            uint size = (uint)buf.Length;
            if (CM_Get_Device_Interface_PropertyW(path, ref key, out _, buf, ref size, 0) != CrSuccess) return null;
            return System.Text.Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0');
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct DevPropKey(Guid FmtId, uint Pid);

    [StructLayout(LayoutKind.Sequential)]
    private struct StorageDeviceNumber
    {
        public uint DeviceType;
        public uint DeviceNumber;
        public uint PartitionNumber;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_List_SizeW(out uint len, ref Guid classGuid, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_ListW(ref Guid classGuid, string? deviceId, [Out] char[] buffer, uint len, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_PropertyW(string iface, ref DevPropKey key, out uint type, [Out] byte[] buffer, ref uint size, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_IDW(uint devInst, [Out] char[] buffer, uint len, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_DevNode_Registry_PropertyW(uint devInst, uint property, out uint type, out uint value, ref uint size, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Request_Device_EjectW(uint devInst, out int vetoType, [Out] char[] vetoName, uint nameLength, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, uint inSize, out StorageDeviceNumber outBuf, uint outSize, out uint returned, IntPtr overlapped);
}
