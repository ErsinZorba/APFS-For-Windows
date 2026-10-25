using Apfs;
using Apfs.Service;
using Apfs.Windows;

// APFS for Windows hizmeti. Windows Hizmet Denetimi (SCM) tarafından LocalSystem olarak başlatılır.
// Geliştirme için: apfs-service --console --image a.img [--image b.img] [--pipe ad] [--lang tr]  (fiziksel diskler yerine
// görüntüler; yönetici gerekmez; --lang: tepsi bağlanmadan önceki dil — testler makinenin dilinden bağımsız olsun diye)
var images = new List<string>();
ServiceLog.EchoToConsole = args.Contains("--console");
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--image") images.Add(Path.GetFullPath(args[i + 1]));
    if (args[i] == "--pipe" && args.Contains("--console")) Apfs.Windows.Ipc.PipeProtocol.PipeName = args[i + 1];
    if (args[i] == "--lang" && args.Contains("--console")) Apfs.Service.ServiceLanguage.Apply(args[i + 1]);
}
return new ApfsService(images.Count > 0 ? images : null).Run();

internal sealed class ApfsService(IReadOnlyList<string>? images) : Fsp.Service("APFSforWindows")
{
    private MountManager? _manager;
    private PipeServer? _pipe;
    private DeviceNotifications.Registration? _diskWatch;
    private Timer? _timer;
    private int _scanning;

    protected override void OnStart(string[] args)
    {
        ServiceLog.Write(images is null ? WinText.T("Svc.Started") : WinText.T("Svc.StartedDev", images.Count));
        _manager = new MountManager(images);
        _manager.ScanRequested += () => ScheduleScan(TimeSpan.FromSeconds(1));
        _pipe = new PipeServer(_manager);
        _pipe.Start();
        // Disk takıldığında/çıkarıldığında hemen; ayrıca bildirim kaçarsa diye 15 saniyede bir tara.
        _diskWatch = DeviceNotifications.WatchDiskInterfaces(_ => ScheduleScan(TimeSpan.FromSeconds(1)));
        _timer = new Timer(_ => ScheduleScan(TimeSpan.Zero), null, TimeSpan.Zero, TimeSpan.FromSeconds(15));
    }

    private void ScheduleScan(TimeSpan delay)
    {
        Task.Delay(delay).ContinueWith(_ =>
        {
            if (Interlocked.Exchange(ref _scanning, 1) == 1) return; // tarama zaten sürüyor
            try { _manager?.Rescan(); }
            catch (Exception e) { ServiceLog.Write(WinText.T("Svc.ScanError", e.Message)); }
            finally { Interlocked.Exchange(ref _scanning, 0); }
        }, TaskScheduler.Default);
    }

    protected override void OnStop()
    {
        _timer?.Dispose();
        _diskWatch?.Dispose();
        _pipe?.Dispose();
        _manager?.Dispose();
        ServiceLog.Write(WinText.T("Svc.Stopped"));
    }
}
