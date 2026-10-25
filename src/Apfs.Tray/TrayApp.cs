using Apfs.Tray.Ui;
using Apfs.Windows.Ipc;

namespace Apfs.Tray;

/// <summary>
/// Bildirim alanı uygulaması: tepsi simgesi (sol tık: küçük panel, sağ tık: menü), ana pencere ve hizmet bildirimleri.
/// Tüm iş hizmette yapılır; tepsi yalnızca arayüzdür.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly AppController _app = new();
    private readonly TrayFlyout _flyout;
    private readonly WindowsFormsSynchronizationContext _ui = new();
    private readonly RegisteredWaitHandle _showWait;
    private MainForm? _main;

    /// <param name="showEvent">Başka bir örnek (Başlat menüsü kısayolu) "pencereyi göster" dediğinde işaretlenir.</param>
    public TrayApp(EventWaitHandle showEvent, bool showMain)
    {
        _icon = new NotifyIcon { Icon = Theme.AppIcon(SystemInformation.SmallIconSize.Width), Text = "APFS for Windows", Visible = true, ContextMenuStrip = new ContextMenuStrip() };
        _icon.ContextMenuStrip.Opening += (_, _) => BuildMenu();
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _flyout!.Toggle(); };
        _icon.BalloonTipClicked += (_, _) => ShowMain();

        _flyout = new TrayFlyout(_app, ShowMain);
        _app.Changed += UpdateTooltip;
        _app.Notify += (text, level) => _icon.ShowBalloonTip(5000, "APFS for Windows", text, level);
        _showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => _ui.Post(_ => ShowMain(), null), null, Timeout.Infinite, executeOnlyOnce: false);
        _app.Start();
        if (showMain) ShowMain();
    }

    private void ShowMain()
    {
        if (_main is null || _main.IsDisposed)
        {
            _main = new MainForm(_app);
            _main.HiddenToTray += () => _icon.ShowBalloonTip(4000, "APFS for Windows",
                WinText.U("Tray.StillRunning"), ToolTipIcon.Info);
            _main.RestartRequested += Restart;
        }
        _main.ShowAndActivate();
    }

    private void UpdateTooltip()
    {
        string t;
        if (_app.ServiceDown) t = WinText.U("Tray.TipServiceDown");
        else
        {
            int mounted = _app.MountedVolumes.Count();
            t = mounted == 0 ? WinText.U("Tray.TipNoVolumes") : WinText.U("Tray.TipMounted", mounted);
        }
        _icon.Text = t.Length > 63 ? t[..63] : t;
    }

    private void BuildMenu()
    {
        var m = _icon.ContextMenuStrip!;
        m.Items.Clear();
        m.Items.Add(new ToolStripMenuItem(WinText.U("Action.OpenMain"), null, (_, _) => ShowMain()) { Font = new Font(m.Font, FontStyle.Bold) });
        m.Items.Add(new ToolStripSeparator());
        var status = _app.Status;
        if (_app.ServiceDown || status is null)
            m.Items.Add(new ToolStripMenuItem(WinText.U("Tray.MenuServiceDown")) { Enabled = false });
        else if (status.Disks.Count == 0)
            m.Items.Add(new ToolStripMenuItem(WinText.U("Tray.MenuNoDisk")) { Enabled = false });
        else
            foreach (var d in status.Disks)
            {
                string drives = string.Join(" ", d.Volumes.Where(v => v.State == VolumeStatus.Mounted).Select(v => v.MountPoint));
                m.Items.Add(new ToolStripMenuItem(WinText.U("Tray.MenuEject", d.Description, (drives.Length > 0 ? "  " + drives : "")), null,
                    async (_, _) => await _app.EjectAsync(d, null)) { Enabled = !_app.IsEjecting(d.Id) });
            }
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(WinText.U("Action.Rescan"), null, async (_, _) => await _app.RescanAsync());
        m.Items.Add(WinText.U("Action.OpenLog"), null, (_, _) => AppController.OpenLog());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(WinText.U("Action.Exit"), null, (_, _) => ExitThread());
    }

    /// <summary>
    /// Dil değişti: yeni örneği başlat ve kapan. Yeni örnek bu süreç tek örnek kilidini bırakana kadar bekler (`--restart`).
    /// Yol derlemenin yanındaki exe'dir (geliştirme düzeneğinde de doğru exe başlasın). Başlatılamazsa (ör. Uygulama Denetimi
    /// engelledi) bu örnek açık kalır; seçim kaydedildiği için yeni dil bir sonraki açılışta geçerli olur.
    /// </summary>
    private void Restart()
    {
        string exe = Path.ChangeExtension(typeof(TrayApp).Assembly.Location, ".exe");
        string args = "--restart --show" + (PipeProtocol.PipeName == "APFSforWindows" ? "" : $" --pipe \"{PipeProtocol.PipeName}\"");
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args) { UseShellExecute = false }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException) { return; }
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        _showWait.Unregister(null);
        _app.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _flyout.Dispose();
        _main?.Dispose();
        base.ExitThreadCore();
    }
}
