using System.ComponentModel;
using System.Drawing.Drawing2D;
using Apfs.Windows.Ipc;

namespace Apfs.Tray.Ui;

/// <summary>
/// Disk kartları listesi; ana pencere ve tepsi paneli aynı denetimi kullanır (<see cref="Compact"/> panel için).
/// Durum yalnızca değişince yeniden kurulur (<see cref="AppController.Changed"/>).
/// </summary>
internal sealed class DiskListView : Panel
{
    private readonly AppController _app;
    private readonly TableLayoutPanel _stack;

    public DiskListView(AppController app, bool compact)
    {
        _app = app;
        Compact = compact;
        AutoScroll = true;
        BackColor = Theme.Background;
        DoubleBuffered = true;
        _stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1,
            BackColor = Theme.Background, Margin = Padding.Empty, Padding = Padding.Empty,
        };
        _stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(_stack);
        Rebuild();
    }

    public bool Compact { get; }

    /// <summary>Kart içeren pencerenin sahibi (onay ve ayrıntı pencereleri için); panelde null (panel odak kaybında kapanır).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IWin32Window? DialogOwner { get; set; }

    /// <summary>
    /// İçeriğin kaydırmasız yüksekliği (panel boyunu buna göre ayarlar). Kartlar dolgunun içinde dizilir: ölçüm o genişlikte
    /// yapılmazsa uzun metinlerin (ör. Almanca) satır kaydırması sayılmaz ve panel kısa kalır.
    /// </summary>
    public int ContentHeight => _stack.GetPreferredSize(new Size(Math.Max(0, ClientSize.Width - Padding.Horizontal), 0)).Height;

    public void Rebuild()
    {
        SuspendLayout();
        _stack.SuspendLayout();
        var old = _stack.Controls.Cast<Control>().ToList();
        _stack.Controls.Clear();
        _stack.RowStyles.Clear();
        foreach (var c in old) c.Dispose();

        var s = _app.Status;
        if (_app.ServiceDown)
            AddRow(Message(false, WinText.U("List.ServiceDownHeading"),
                WinText.U("List.ServiceDownBody")));
        else if (s is null)
            AddRow(Message(true, WinText.U("List.Connecting"), ""));
        else if (s.Disks.Count == 0)
            AddRow(Message(true, WinText.U("List.NoDiskHeading"),
                WinText.U("List.NoDiskBody")));
        else
            foreach (var d in s.Disks) AddRow(DiskCard(d));

        _stack.ResumeLayout(true);
        ResumeLayout(true);
    }

    private void AddRow(Control c)
    {
        _stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        c.Dock = DockStyle.Fill;
        _stack.Controls.Add(c);
    }

    // ---------------- Kartlar ----------------

    private Control DiskCard(DiskStatus d)
    {
        int pad = this.S(Compact ? 12 : 16);
        var card = new CardPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3,
            Padding = new Padding(pad, pad - this.S(4), pad - this.S(4), pad - this.S(6)),
            Margin = new Padding(0, 0, 0, this.S(Compact ? 8 : 12)),
        };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var title = Label(d.Description, Theme.Bold, Theme.Text);
        title.Margin = new Padding(0, this.S(4), 0, this.S(6));
        card.Controls.Add(title, 0, 0);
        card.SetColumnSpan(title, 3);

        int row = 1;
        var visible = d.Volumes.Where(v => v.State != VolumeStatus.Hidden).ToList();
        if (visible.Count == 0)
        {
            // Yazma durumu değişirken bölümün birimleri kısa süre listede olmaz (denetlenip yeniden bağlanır)
            var none = Label(_app.IsSwitching(d.Id) ? WinText.U("Card.Checking") : WinText.U("Card.NoVolumes"),
                Theme.Small, Theme.Muted);
            card.Controls.Add(none, 0, row);
            card.SetColumnSpan(none, 3);
            row++;
        }
        // Yazma bölüm başınadır: tek bölümlü diskte kart eskisi gibi (düğme altta); çok bölümlüde her bölümün düğmesi ve ret nedeni
        // o bölümün biriminin hemen altında (yan yana sığmıyor)
        var containers = visible.GroupBy(v => v.Container).OrderBy(g => g.Key).ToList();
        bool multi = containers.Count > 1;
        foreach (var g in containers)
        {
            foreach (var v in g)
            {
                var (color, status, button) = v.State switch
                {
                    VolumeStatus.Mounted => (v.Writable ? Theme.Amber : Theme.Green,
                        WinText.U("Card.DriveState", v.MountPoint, (v.Writable ? WinText.U("Card.Writable") : WinText.U("Card.ReadOnly"))),
                        Button(WinText.U("Action.Open"), FriendlyButton.Kind.Primary, () => AppController.OpenDrive(v.MountPoint!))),
                    VolumeStatus.NeedsPassword => (Theme.Amber, WinText.U("Card.NeedsPassword"), Button(WinText.U("Action.Unlock"), FriendlyButton.Kind.Primary, async () => await _app.UnlockAsync(d, v))),
                    _ => (Theme.Red, WinText.U("Card.Failed"), Button(WinText.U("Action.Details"), FriendlyButton.Kind.Secondary, () => AppController.ShowDetails(DialogOwner, v))),
                };
                var dot = new StatusDot(color) { Margin = new Padding(0, this.S(10), this.S(10), 0), Anchor = AnchorStyles.Top };
                var text = new FlowLayoutPanel
                {
                    FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Fill,
                    Margin = new Padding(0, this.S(4), this.S(8), this.S(4)), BackColor = Theme.Card,
                };
                text.Controls.Add(Label(v.Name, Theme.Body, Theme.Text));
                text.Controls.Add(Label(status, Theme.Small, Theme.Muted));
                button.Anchor = AnchorStyles.Right;
                if (v.State == VolumeStatus.Mounted) button.Enabled = !_app.IsEjecting(d.Id) && !_app.IsSwitching(d.Id);
                card.Controls.Add(dot, 0, row);
                card.Controls.Add(text, 1, row);
                card.Controls.Add(button, 2, row);
                row++;
            }
            if (!multi) continue;
            if (WriteBlockLabel(d, g, multi) is { } why)
            {
                card.Controls.Add(why, 1, row);
                card.SetColumnSpan(why, 2);
                row++;
            }
            if (WriteToggle(d, g, multi) is { } toggle)
            {
                toggle.Anchor = AnchorStyles.Right;
                toggle.Margin = new Padding(this.S(4), 0, this.S(4), this.S(4));
                card.Controls.Add(toggle, 1, row);
                card.SetColumnSpan(toggle, 2);
                row++;
            }
        }
        if (!multi && containers.Count == 1 && WriteBlockLabel(d, containers[0], multi) is { } reasonLabel)
        {
            card.Controls.Add(reasonLabel, 0, row);
            card.SetColumnSpan(reasonLabel, 3);
            row++;
        }

        var actions = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false, AutoSize = true, Anchor = AnchorStyles.Right,
            BackColor = Theme.Card, Margin = new Padding(this.S(4), this.S(6), 0, 0),
        };
        var eject = Button(WinText.U("Action.SafelyEject"), FriendlyButton.Kind.Quiet, async () => await _app.EjectAsync(d, DialogOwner));
        eject.BusyText = WinText.U("Action.Ejecting");
        eject.Busy = _app.IsEjecting(d.Id);
        eject.Margin = new Padding(this.S(4), 0, this.S(4), 0);
        actions.Controls.Add(eject);
        // Denetlenen bölümün birimleri o sırada listede olmaz: düğmesi meşgul olarak dursun
        foreach (int c in _app.SwitchingContainers(d.Id).Where(c => containers.All(g => g.Key != c)))
        {
            var waiting = Button(WinText.U("Action.Checking"), FriendlyButton.Kind.Quiet, () => { });
            waiting.BusyText = WinText.U("Action.Checking");
            waiting.Busy = true;
            waiting.Margin = new Padding(this.S(4), 0, this.S(4), 0);
            actions.Controls.Add(waiting);
            break; // tek satır: aynı anda birden çoğu denetleniyorsa da bir gösterge yeter
        }
        if (!multi && containers.Count == 1 && WriteToggle(d, containers[0], multi) is { } single)
        {
            single.Margin = new Padding(this.S(4), 0, this.S(4), 0);
            actions.Controls.Add(single);
        }
        card.Controls.Add(actions, 0, row);
        card.SetColumnSpan(actions, 3);
        return card;
    }

    /// <summary>Bölümün "Yazmaya izin ver" / "Salt okunura dön" düğmesi; bağlı (açılabilmiş) birimi yoksa null.</summary>
    private FriendlyButton? WriteToggle(DiskStatus d, IGrouping<int, VolumeStatus> g, bool multi)
    {
        if (!g.Any(v => v.State == VolumeStatus.Mounted)) return null;
        bool writable = g.Any(v => v.Writable);
        string name = g.First().Name;
        var toggle = Button(writable ? WinText.U("Action.BackToReadOnly") : WinText.U("Action.AllowWriting"), FriendlyButton.Kind.Quiet,
            async () => await _app.SetWritableAsync(d, g.Key, multi ? $"{d.Description} · \"{name}\"" : d.Description, !writable, DialogOwner));
        toggle.BusyText = writable ? WinText.U("Action.Saving") : WinText.U("Action.Checking");
        toggle.Busy = _app.IsSwitching(d.Id, g.Key);
        toggle.Enabled = !_app.IsEjecting(d.Id);
        return toggle;
    }

    /// <summary>Yazma kapısı bu bölümü reddettiyse nedeni (sade Türkçe); yoksa null.</summary>
    private Label? WriteBlockLabel(DiskStatus d, IGrouping<int, VolumeStatus> g, bool multi)
    {
        if (g.Any(v => v.Writable)) return null;
        // Eski hizmet bölüm başına neden göndermez: tek bölümlüde disk düzeyindeki neden
        string? reason = g.Select(v => v.WriteBlockReason).FirstOrDefault(r => r is { Length: > 0 }) ?? (multi ? null : d.WriteBlockReason);
        if (reason is not { Length: > 0 }) return null;
        var why = Label(WinText.U("Card.WriteRefused", reason), Theme.Small, Theme.Muted);
        why.MaximumSize = new Size(this.S(Compact ? 300 : 460), 0);
        why.Margin = new Padding(0, this.S(4), 0, this.S(2));
        return why;
    }

    private Control Message(bool friendly, string heading, string body)
    {
        var p = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Theme.Background,
            Padding = new Padding(this.S(8), this.S(Compact ? 16 : 40), this.S(8), this.S(16)),
        };
        int iconSize = this.S(Compact ? 40 : 64);
        var icon = new PictureBox
        {
            Image = Theme.AppIcon(256).ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(iconSize, iconSize),
            Margin = new Padding(0, 0, 0, this.S(12)),
        };
        if (!friendly) icon.Image = Desaturate(icon.Image);
        p.Controls.Add(icon);
        p.Controls.Add(Label(heading, Theme.Font(Compact ? 10.5f : 12f, FontStyle.Bold), Theme.Text));
        if (body.Length > 0)
        {
            var b = Label(body, Theme.Body, Theme.Muted);
            b.Margin = new Padding(0, this.S(4), 0, 0);
            p.Controls.Add(b);
        }
        // Metinler kart genişliğinde kaydırılsın
        p.SizeChanged += (_, _) =>
        {
            foreach (Control c in p.Controls)
                if (c is Label l) l.MaximumSize = new Size(Math.Max(this.S(120), p.Width - p.Padding.Horizontal), 0);
        };
        return p;
    }

    private static Image Desaturate(Image src)
    {
        var bmp = new Bitmap(src.Width, src.Height);
        using var g = Graphics.FromImage(bmp);
        var m = new System.Drawing.Imaging.ColorMatrix(
        [
            [0.3f, 0.3f, 0.3f, 0, 0], [0.59f, 0.59f, 0.59f, 0, 0], [0.11f, 0.11f, 0.11f, 0, 0], [0, 0, 0, 0.6f, 0], [0, 0, 0, 0, 1],
        ]);
        using var attrs = new System.Drawing.Imaging.ImageAttributes();
        attrs.SetColorMatrix(m);
        g.DrawImage(src, new Rectangle(0, 0, bmp.Width, bmp.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
        return bmp;
    }

    private static Label Label(string text, Font font, Color color) => new()
    {
        Text = text, Font = font, ForeColor = color, AutoSize = true, Margin = Padding.Empty, UseMnemonic = false,
        BackColor = Color.Transparent,
    };

    private FriendlyButton Button(string text, FriendlyButton.Kind kind, Action onClick)
    {
        var b = new FriendlyButton(text, kind);
        // İş tıklama olayı bittikten sonra başlasın: durum değişince liste yeniden kurulur ve bu düğme elden çıkarılır.
        b.Click += (_, _) => BeginInvoke(onClick);
        return b;
    }

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        _stack.MaximumSize = new Size(ClientSize.Width, 0); // içerik yatay kaydırmasın
    }

    /// <summary>Yuvarlak köşeli beyaz kart.</summary>
    private sealed class CardPanel : TableLayoutPanel
    {
        public CardPanel()
        {
            DoubleBuffered = true;
            BackColor = Theme.Card;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Background);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = Theme.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), this.S(10));
            using var fill = new SolidBrush(Theme.Card);
            using var pen = new Pen(Theme.CardBorder);
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }
    }

    /// <summary>Durum noktası (yeşil: bağlı, sarı: parola bekliyor, kırmızı: açılamadı).</summary>
    private sealed class StatusDot : Control
    {
        private readonly Color _color;

        public StatusDot(Color color)
        {
            _color = color;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Size = new Size(this.S(10), this.S(10));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? Theme.Card);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var b = new SolidBrush(_color);
            e.Graphics.FillEllipse(b, 0, 0, Width - 1, Height - 1);
        }
    }
}
