using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Apfs.Tray.Ui;

/// <summary>Renkler, yazı tipleri ve ortak çizim yardımcıları. Tek yerde tutulur ki pencere, panel ve parola penceresi aynı görünsün.</summary>
internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(244, 246, 250);
    public static readonly Color Card = Color.White;
    public static readonly Color CardBorder = Color.FromArgb(226, 230, 237);
    public static readonly Color Text = Color.FromArgb(28, 32, 40);
    public static readonly Color Muted = Color.FromArgb(104, 112, 126);

    public static readonly Color Accent = Color.FromArgb(46, 116, 206);
    public static readonly Color AccentHover = Color.FromArgb(36, 100, 186);
    public static readonly Color AccentPressed = Color.FromArgb(28, 84, 160);

    public static readonly Color Subtle = Color.FromArgb(236, 239, 245);
    public static readonly Color SubtleHover = Color.FromArgb(224, 229, 238);
    public static readonly Color SubtlePressed = Color.FromArgb(210, 216, 228);

    public static readonly Color Green = Color.FromArgb(40, 167, 92);
    public static readonly Color Amber = Color.FromArgb(226, 152, 28);
    public static readonly Color Red = Color.FromArgb(214, 72, 60);

    private static readonly string FontName =
        new InstalledFontCollection().Families.Any(f => f.Name == "Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";

    private static readonly Dictionary<(float, FontStyle), Font> Fonts = [];

    /// <summary>Paylaşılan yazı tipi (önbellekten; elden çıkarılmamalı).</summary>
    public static Font Font(float size = 9.75f, FontStyle style = FontStyle.Regular)
    {
        if (!Fonts.TryGetValue((size, style), out var f))
            Fonts[(size, style)] = f = new Font(FontName, size, style);
        return f;
    }

    public static Font Body => Font();
    public static Font Bold => Font(9.75f, FontStyle.Bold);
    public static Font Small => Font(8.75f);
    public static Font Title => Font(15f, FontStyle.Bold);

    /// <summary>96 DPI'a göre verilen pikseli denetimin ekranına ölçekler.</summary>
    public static int S(this Control c, int px) => (int)Math.Round(px * c.DeviceDpi / 96f);

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0)
        {
            p.AddRectangle(r);
            return p;
        }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static Color Lerp(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    private static readonly Dictionary<int, Icon> Icons = [];

    /// <summary>Uygulama simgesi (gömülü app.ico) istenen boyutta.</summary>
    public static Icon AppIcon(int size)
    {
        if (Icons.TryGetValue(size, out var icon)) return icon;
        using var s = typeof(Theme).Assembly.GetManifestResourceStream("app.ico")!;
        return Icons[size] = new Icon(s, size, size);
    }
}
