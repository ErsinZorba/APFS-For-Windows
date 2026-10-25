using System.Diagnostics;
using Apfs.Tray.Ui;

namespace Apfs.Tray;

/// <summary>
/// Ana pencere: bağlı Mac diskleri kart olarak, altta "Yeniden tara" ve "Günlüğü aç". Kapatınca tepsiye gizlenir;
/// programdan çıkış tepsi menüsündendir (diskler arka planda bağlı kalır, bunu hizmet yapar).
/// </summary>
internal sealed class MainForm : Form
{
    private readonly AppController _app;
    private readonly DiskListView _list;
    private readonly FriendlyButton _rescan;
    private bool _hintShown;

    /// <summary>Pencere ilk kez tepsiye gizlendiğinde (kullanıcıya bir kez bilgi vermek için).</summary>
    public event Action? HiddenToTray;

    public MainForm(AppController app)
    {
        _app = app;
        Text = "APFS for Windows";
        Icon = Theme.AppIcon(32);
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Background;
        Font = Theme.Body;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(this.S(480), this.S(600));
        MinimumSize = new Size(this.S(380), this.S(360));

        // Üst: simge, ad, tek cümlelik açıklama
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, BackColor = Theme.Background,
            Padding = new Padding(this.S(20), this.S(20), this.S(20), this.S(12)),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var logo = new PictureBox
        {
            Image = Theme.AppIcon(256).ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(this.S(44), this.S(44)),
            Margin = new Padding(0, 0, this.S(14), 0),
        };
        header.Controls.Add(logo, 0, 0);
        header.SetRowSpan(logo, 2);
        header.Controls.Add(new Label { Text = "APFS for Windows", Font = Theme.Title, ForeColor = Theme.Text, AutoSize = true, Margin = Padding.Empty }, 1, 0);
        header.Controls.Add(new Label
        {
            Text = "Mac diskleriniz güvende: önce salt okunur, isterseniz yazılabilir.", ForeColor = Theme.Muted, AutoSize = true, Margin = new Padding(0, this.S(2), 0, 0),
        }, 1, 1);

        _list = new DiskListView(app, compact: false)
        {
            Dock = DockStyle.Fill, Padding = new Padding(this.S(20), this.S(4), this.S(20), this.S(4)), DialogOwner = this,
        };

        // Alt: işlemler ve sürüm
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 3, BackColor = Theme.Background,
            Padding = new Padding(this.S(16), this.S(8), this.S(20), this.S(14)),
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _rescan = new FriendlyButton("Yeniden tara", FriendlyButton.Kind.Secondary) { BusyText = "Taranıyor…" };
        _rescan.Click += async (_, _) => await _app.RescanAsync();
        var log = new FriendlyButton("Günlüğü aç", FriendlyButton.Kind.Quiet);
        log.Click += (_, _) => AppController.OpenLog();
        string version = Application.ProductVersion.Split('+')[0];
        footer.Controls.Add(_rescan, 0, 0);
        footer.Controls.Add(log, 1, 0);
        var about = new FriendlyButton($"Sürüm {version}", FriendlyButton.Kind.Quiet) { Anchor = AnchorStyles.Right, Font = Theme.Small };
        about.Click += (_, _) => ShowAbout(version);
        footer.Controls.Add(about, 2, 0);

        Controls.Add(_list);
        Controls.Add(footer);
        Controls.Add(header);

        _app.Changed += OnChanged;
        OnChanged();
    }

    private void OnChanged()
    {
        _rescan.Busy = _app.Rescanning;
        _list.Rebuild();
    }

    /// <summary>
    /// Hakkında penceresi. WinFsp lisansı (FLOSS istisnası) WinFsp telif notunun ve depo bağlantısının arayüzde bulunmasını
    /// şart koşar; not burada durur.
    /// </summary>
    private void ShowAbout(string version)
    {
        var page = new TaskDialogPage
        {
            Caption = "APFS for Windows",
            Heading = $"APFS for Windows {version}",
            Text = "Mac'te biçimlendirilmiş APFS diskleri Windows'ta açar: varsayılan olarak salt okunur, isteğe bağlı yazma (deneysel). Açık kaynak, GPLv3 lisansı.\n" +
                   "<a href=\"https://github.com/ErsinZorba/APFS-for-Windows\">github.com/ErsinZorba/APFS-for-Windows</a>",
            Footnote = new TaskDialogFootnote(
                "WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos\n" +
                "<a href=\"https://github.com/winfsp/winfsp\">github.com/winfsp/winfsp</a>"),
            EnableLinks = true,
            Buttons = { TaskDialogButton.OK },
        };
        page.LinkClicked += (_, e) => Process.Start(new ProcessStartInfo(e.LinkHref) { UseShellExecute = true });
        TaskDialog.ShowDialog(this, page);
    }

    /// <summary>Gizliyse gösterir, simge durumundaysa geri getirir ve öne alır.</summary>
    public void ShowAndActivate()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            if (!_hintShown)
            {
                _hintShown = true;
                HiddenToTray?.Invoke();
            }
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _app.Changed -= OnChanged;
        base.Dispose(disposing);
    }
}
