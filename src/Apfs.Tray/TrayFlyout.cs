using System.Runtime.InteropServices;
using Apfs.Tray.Ui;

namespace Apfs.Tray;

/// <summary>
/// Tepsi simgesine sol tıklayınca saatin üstünde açılan küçük panel: diskler, "Aç" / "Kilidi aç" / "Güvenle çıkar" ve
/// "Ana pencereyi aç". Başka bir yere tıklanınca kapanır.
/// </summary>
internal sealed class TrayFlyout : Form
{
    private readonly AppController _app;
    private readonly DiskListView _list;
    private readonly System.Windows.Forms.Timer _fade = new() { Interval = 15 };
    private int _targetTop;
    private long _hiddenAt;

    public TrayFlyout(AppController app, Action openMain)
    {
        _app = app;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Background;
        Font = Theme.Body;
        Width = this.S(360);
        Padding = new Padding(1); // ince kenarlık (Windows 10'da köşe yuvarlatma yok)

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, BackColor = Theme.Background,
            Padding = new Padding(this.S(14), this.S(12), this.S(14), this.S(6)),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.Controls.Add(new PictureBox
        {
            Image = Theme.AppIcon(32).ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(this.S(22), this.S(22)),
            Margin = new Padding(0, 0, this.S(8), 0),
        }, 0, 0);
        header.Controls.Add(new Label
        {
            Text = "APFS for Windows", Font = Theme.Bold, ForeColor = Theme.Text, AutoSize = true, Anchor = AnchorStyles.Left,
        }, 1, 0);

        _list = new DiskListView(app, compact: true)
        {
            Dock = DockStyle.Fill, Padding = new Padding(this.S(12), this.S(2), this.S(12), 0),
        };

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, BackColor = Theme.Background, FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(this.S(8), this.S(2), this.S(8), this.S(8)),
        };
        var main = new FriendlyButton("Ana pencereyi aç", FriendlyButton.Kind.Quiet);
        main.Click += (_, _) => { Hide(); openMain(); };
        var rescan = new FriendlyButton("Yeniden tara", FriendlyButton.Kind.Quiet) { BusyText = "Taranıyor…" };
        rescan.Click += async (_, _) => await _app.RescanAsync();
        footer.Controls.Add(main);
        footer.Controls.Add(rescan);

        Controls.Add(_list);
        Controls.Add(footer);
        Controls.Add(header);

        _app.Changed += () =>
        {
            rescan.Busy = _app.Rescanning;
            _list.Rebuild();
            if (Visible) FitHeight();
        };
        _fade.Tick += (_, _) => FadeStep();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2; // DWMWCP_ROUND: Windows 11'de yuvarlak köşe (Windows 10'da etkisiz)
        _ = DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        using var pen = new Pen(Theme.CardBorder);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    /// <summary>Simgeye tıklanınca: açıksa kapat, kapalıysa imlecin yanında (görev çubuğunun hemen üstünde) aç.</summary>
    public void Toggle()
    {
        if (Visible) { Hide(); return; }
        // Panel açıkken simgeye tıklamak önce paneli kapatır (odak kaybı); aynı tık yeniden açmasın.
        if (Environment.TickCount64 - _hiddenAt < 300) return;
        _list.Rebuild();
        FitHeight();
        var cursor = Cursor.Position;
        var wa = Screen.FromPoint(cursor).WorkingArea;
        int gap = this.S(12);
        Left = Math.Clamp(cursor.X - Width / 2, wa.Left + gap, wa.Right - Width - gap);
        _targetTop = Math.Clamp(cursor.Y - Height - gap, wa.Top + gap, wa.Bottom - Height - gap);
        Top = _targetTop + this.S(10);
        Opacity = 0;
        Show();
        Activate();
        _fade.Start();
    }

    private void FitHeight()
    {
        int chrome = Controls.Cast<Control>().Where(c => c != _list).Sum(c => c.Height) + Padding.Vertical;
        int wanted = chrome + _list.Padding.Vertical + _list.ContentHeight;
        int max = (int)(Screen.FromControl(this).WorkingArea.Height * 0.8);
        int old = Height;
        Height = Math.Clamp(wanted, this.S(200), max);
        if (Visible) Top -= Height - old; // alt kenar sabit kalsın
        _targetTop -= Height - old;
    }

    private void FadeStep()
    {
        Opacity = Math.Min(1, Opacity + 0.15);
        int step = Math.Max(1, (Top - _targetTop + 1) / 2);
        Top = Math.Max(_targetTop, Top - step);
        if (Opacity >= 1 && Top == _targetTop) _fade.Stop();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Hide();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible)
        {
            _hiddenAt = Environment.TickCount64;
            _fade.Stop();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _fade.Dispose();
        base.Dispose(disposing);
    }
}
