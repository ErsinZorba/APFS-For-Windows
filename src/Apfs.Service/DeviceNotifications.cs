using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Apfs.Service;

/// <summary>
/// Windows aygıt bildirimleri (CM_Register_Notification):
/// - disk arayüzü takıldı/çıkarıldı (GUID_DEVINTERFACE_DISK) → yeniden tarama
/// - açık disk tanıtıcısı için "kaldırma sorgusu" (Donanımı güvenli kaldır) → birimleri ayır ve tanıtıcıyı kapat
/// - açık disk tanıtıcısına özel olay (ör. başka bir program bölüm tablosunu yazdı) → tarama (disk kimliği denetlenir)
/// </summary>
internal static class DeviceNotifications
{
    public static readonly Guid DiskInterface = Guid.Parse("53F56307-B6BF-11D0-94F2-00A0C91EFB8B");

    public enum NotifyAction : uint
    {
        InterfaceArrival = 0,
        InterfaceRemoval = 1,
        QueryRemove = 2,
        QueryRemoveFailed = 3,
        RemovePending = 4,
        RemoveComplete = 5,
        /// <summary>Aygıta özel olay (ör. bölüm düzeni değişti); içeriği denetlemek için tarama istenir.</summary>
        CustomEvent = 6,
    }

    private const uint FilterTypeDeviceInterface = 0;
    private const uint FilterTypeDeviceHandle = 1;

    [StructLayout(LayoutKind.Explicit, Size = 416)]
    private struct Filter
    {
        [FieldOffset(0)] public uint CbSize;
        [FieldOffset(4)] public uint Flags;
        [FieldOffset(8)] public uint FilterType;
        [FieldOffset(12)] public uint Reserved;
        [FieldOffset(16)] public Guid ClassGuid;
        [FieldOffset(16)] public IntPtr HTarget;
    }

    internal delegate uint Callback(IntPtr hNotify, IntPtr context, NotifyAction action, IntPtr eventData, uint eventDataSize);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Register_Notification(ref Filter filter, IntPtr context, Callback callback, out IntPtr handle);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Unregister_Notification(IntPtr handle);

    /// <summary>Kayıt; Dispose çağrılana kadar geri çağrı (ve temsilci) canlı tutulur.</summary>
    public sealed class Registration : IDisposable
    {
        private readonly Callback _callback; // GC toplamasın
        private IntPtr _handle;

        internal Registration(Callback callback, IntPtr handle)
        {
            _callback = callback;
            _handle = handle;
        }

        /// <summary>Geri çağrının içinden çağrılmamalı (kilitlenme); bu yüzden kaydı her zaman iş parçacığı havuzunda kaldırır.</summary>
        public void Dispose()
        {
            var h = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (h != IntPtr.Zero) ThreadPool.QueueUserWorkItem(_ => { CM_Unregister_Notification(h); GC.KeepAlive(_callback); });
        }
    }

    public static Registration? WatchDiskInterfaces(Action<NotifyAction> onEvent)
    {
        var filter = new Filter { CbSize = 416, FilterType = FilterTypeDeviceInterface, ClassGuid = DiskInterface };
        Callback cb = (_, _, action, _, _) => { onEvent(action); return 0; };
        return CM_Register_Notification(ref filter, IntPtr.Zero, cb, out var h) == 0 ? new Registration(cb, h) : null;
    }

    /// <summary>Açık tanıtıcı için kaldırma bildirimleri. Geri çağrı 0 (ERROR_SUCCESS) döndürerek kaldırmaya izin verir.</summary>
    public static Registration? WatchHandle(SafeFileHandle handle, Action<NotifyAction> onEvent)
    {
        var filter = new Filter { CbSize = 416, FilterType = FilterTypeDeviceHandle, HTarget = handle.DangerousGetHandle() };
        Callback cb = (_, _, action, _, _) => { onEvent(action); return 0; };
        return CM_Register_Notification(ref filter, IntPtr.Zero, cb, out var h) == 0 ? new Registration(cb, h) : null;
    }
}
