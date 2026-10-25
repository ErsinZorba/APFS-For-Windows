using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Apfs.Tray.Ui;

/// <summary>
/// Yuvarlak köşeli, kendi çizen düğme. Üzerine gelince renk yumuşakça değişir, basınca hafifçe koyulaşıp içe çöker.
/// "Meşgul" durumunda dönen bir halka ve <see cref="BusyText"/> gösterir, tıklanamaz.
/// </summary>
internal sealed class FriendlyButton : Control, IButtonControl
{
    public enum Kind { Primary, Secondary, Quiet }

    private static readonly System.Windows.Forms.Timer Ticker = new() { Interval = 15 };
    private static readonly HashSet<FriendlyButton> Animating = [];

    private float _hover;          // 0 → 1 (üzerine gelme geçişi)
    private bool _mouseOver;
    private bool _pressed;
    private bool _busy;
    private float _spin;
    private readonly Kind _kind;

    static FriendlyButton() => Ticker.Tick += (_, _) =>
    {
        foreach (var b in Animating.ToList()) b.Step();
        if (Animating.Count == 0) Ticker.Stop();
    };

    public FriendlyButton(string text, Kind kind = Kind.Primary)
    {
        _kind = kind;
        Text = text;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
        SetStyle(ControlStyles.StandardDoubleClick, false); // hızlı iki tık iki ayrı tık sayılsın
        Font = kind == Kind.Quiet ? Theme.Body : Theme.Bold;
        Cursor = Cursors.Hand;
        TabStop = true;
        AutoSize = true;
        Margin = new Padding(this.S(4));
        AccessibleRole = AccessibleRole.PushButton;
    }

    /// <summary>Meşgulken gösterilecek metin (ör. "Çıkarılıyor…").</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? BusyText { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Busy
    {
        get => _busy;
        set
        {
            if (_busy == value) return;
            _busy = value;
            Cursor = value ? Cursors.Default : Cursors.Hand;
            if (value) StartAnimation();
            PerformLayout(); // metin değişir: boyut yeniden hesaplanır
            Parent?.PerformLayout();
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public DialogResult DialogResult { get; set; }
    public void NotifyDefault(bool value) { }

    public void PerformClick()
    {
        if (CanClick) OnClick(EventArgs.Empty);
    }

    private bool CanClick => Enabled && !_busy && Visible;

    private string ShownText => _busy && BusyText is not null ? BusyText : Text;

    public override Size GetPreferredSize(Size proposedSize)
    {
        var t = TextRenderer.MeasureText(ShownText, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        int spinner = _busy ? this.S(20) : 0;
        int padX = _kind == Kind.Quiet ? this.S(10) : this.S(16);
        return new Size(t.Width + padX * 2 + spinner, Math.Max(this.S(32), t.Height + this.S(14)));
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Parent?.PerformLayout();
        Invalidate();
    }

    // ---- Fare ve klavye ----

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _mouseOver = true; StartAnimation(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _mouseOver = false; _pressed = false; StartAnimation(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !CanClick) return;
        _pressed = true;
        Focus();
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _pressed = false;
        Invalidate();
    }

    protected override void OnClick(EventArgs e)
    {
        if (CanClick) base.OnClick(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter && CanClick)
        {
            _pressed = true;
            Invalidate();
            if (e.KeyCode == Keys.Enter) { _pressed = false; PerformClick(); }
            e.Handled = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode == Keys.Space && _pressed)
        {
            _pressed = false;
            Invalidate();
            PerformClick();
        }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); _pressed = false; Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

    // ---- Animasyon ----

    private void StartAnimation()
    {
        if (IsDisposed) return;
        Animating.Add(this);
        Ticker.Start();
    }

    private void Step()
    {
        if (IsDisposed) { Animating.Remove(this); return; }
        float target = _mouseOver && CanClick ? 1 : 0;
        _hover += Math.Sign(target - _hover) * 0.12f; // ~120 ms
        if (Math.Abs(target - _hover) < 0.12f) _hover = target;
        if (_busy) _spin = (_spin + 9) % 360;
        if (_hover == target && !_busy) Animating.Remove(this);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        Animating.Remove(this);
        base.Dispose(disposing);
    }

    // ---- Çizim ----

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var (normal, hover, pressed, fore) = _kind switch
        {
            Kind.Primary => (Theme.Accent, Theme.AccentHover, Theme.AccentPressed, Color.White),
            Kind.Secondary => (Theme.Subtle, Theme.SubtleHover, Theme.SubtlePressed, Theme.Text),
            _ => (Color.FromArgb(0, Theme.Subtle), Theme.Subtle, Theme.SubtleHover, Theme.Accent),
        };
        Color fill = _pressed ? pressed : Theme.Lerp(normal, hover, _hover);
        if (!Enabled || _busy)
        {
            fill = Theme.Lerp(normal, Theme.Background, _kind == Kind.Primary ? 0.35f : 0.2f);
            fore = Theme.Lerp(fore, Theme.Muted, 0.45f);
        }

        var bg = Parent?.BackColor ?? Theme.Background;
        fill = Theme.Lerp(bg, Color.FromArgb(255, fill), fill.A / 255f); // yarı saydam rengi zemine karıştır (metin için opak zemin)

        float inset = _pressed ? this.S(1) + 0.5f : 0.5f; // basınca içe çökme
        var r = new RectangleF(inset, inset, Width - 1 - inset * 2, Height - 1 - inset * 2);
        using (var path = Theme.RoundedRect(r, this.S(7)))
        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, path);

        if (Focused && ShowFocusCues)
        {
            using var path = Theme.RoundedRect(new RectangleF(1.5f, 1.5f, Width - 4, Height - 4), this.S(7));
            using var pen = new Pen(Theme.Lerp(Theme.Accent, Color.White, _kind == Kind.Primary ? 0.55f : 0f), this.S(2));
            g.DrawPath(pen, path);
        }

        var textRect = ClientRectangle;
        if (_pressed) textRect.Offset(0, 1);
        if (_busy)
        {
            int d = this.S(12);
            var size = TextRenderer.MeasureText(ShownText, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            int total = d + this.S(8) + size.Width;
            int x = (Width - total) / 2;
            using var pen = new Pen(fore, Math.Max(1.5f, this.S(2))) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, x, (Height - d) / 2f, d, d, _spin, 270);
            textRect = new Rectangle(x + d + this.S(8), 0, size.Width + this.S(4), Height);
            TextRenderer.DrawText(g, ShownText, Font, textRect, fore, fill, TextFormatFlags.VerticalCenter | TextFormatFlags.Left |
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            return;
        }
        TextRenderer.DrawText(g, ShownText, Font, textRect, fore, fill,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    }
}
