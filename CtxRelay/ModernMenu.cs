using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CtxRelay;

/// <summary>Палитра меню. Тема берётся из темы панели задач (SystemUsesLightTheme).</summary>
sealed record Palette(Color Back, Color Hover, Color Border, Color Separator, Color Text, Color TextDim, Color Accent)
{
    public static readonly Palette Dark = new(
        Color.FromArgb(43, 43, 43), Color.FromArgb(61, 61, 61), Color.FromArgb(70, 70, 70),
        Color.FromArgb(68, 68, 68), Color.FromArgb(240, 240, 240), Color.FromArgb(150, 150, 150),
        Color.FromArgb(76, 194, 255));

    public static readonly Palette Light = new(
        Color.FromArgb(249, 249, 249), Color.FromArgb(232, 232, 232), Color.FromArgb(220, 220, 220),
        Color.FromArgb(225, 225, 225), Color.FromArgb(28, 28, 28), Color.FromArgb(120, 120, 120),
        Color.FromArgb(0, 103, 192));

    public static Palette Current()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("SystemUsesLightTheme") is int v && v == 1 ? Light : Dark;
        }
        catch { return Dark; }
    }
}

sealed class ModernRenderer : ToolStripProfessionalRenderer
{
    public Palette P { get; set; }

    public ModernRenderer(Palette p) : base(new Colors(p)) { P = p; RoundedEdges = false; }

    sealed class Colors(Palette p) : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => p.Back;
        public override Color ImageMarginGradientBegin => p.Back;
        public override Color ImageMarginGradientMiddle => p.Back;
        public override Color ImageMarginGradientEnd => p.Back;
        public override Color MenuBorder => p.Border;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => p.Hover;
        public override Color SeparatorDark => p.Separator;
        public override Color SeparatorLight => p.Separator;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(P.Back);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        using var path = Round(r, ModernMenu.Radius);
        using var pen = new Pen(P.Border);
        g.DrawPath(pen, path);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
        using var path = Round(r, 4);
        using var b = new SolidBrush(P.Hover);
        g.FillPath(b, path);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Height / 2;
        using var pen = new Pen(P.Separator);
        e.Graphics.DrawLine(pen, 1, y, e.Item.Width - 1, y);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var dim = !e.Item.Enabled || e.Item.Tag as string == ModernMenu.DimTag;
        // правый текст (ShortcutKeyDisplayString) всегда приглушён
        var isShortcut = e.Item is ToolStripMenuItem mi && mi.ShortcutKeyDisplayString == e.Text && e.Text != mi.Text;
        e.TextColor = dim || isShortcut ? P.TextDim : P.Text;
        e.TextFormat |= TextFormatFlags.NoPrefix;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = P.TextDim;
        base.OnRenderArrow(e);
    }

    public static GraphicsPath Round(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>Стилизация ContextMenuStrip и фабрика пунктов.</summary>
static class ModernMenu
{
    public const int Radius = 8;
    public const string DimTag = "dim";

    // Segoe MDL2 Assets (есть в Windows 10/11)
    public const char GlyphRefresh = '\uE72C', GlyphFolder = '\uE8B7', GlyphPower = '\uE7E8',
                      GlyphCheck = '\uE73E', GlyphStartup = '\uE7B5', GlyphLog = '\uE9F9';

    [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

    public static void Apply(ContextMenuStrip menu)
    {
        var p = Palette.Current();
        menu.Renderer = new ModernRenderer(p);
        menu.Font = new Font("Segoe UI", 9.5f);
        menu.ShowCheckMargin = false;
        menu.ShowImageMargin = true;
        menu.Padding = new Padding(0, 6, 0, 6);
        menu.Opening += (_, _) => ((ModernRenderer)menu.Renderer).P = Palette.Current();
        // скругляем окно меню; регион пересчитываем при каждом изменении размера
        menu.SizeChanged += (_, _) => RoundWindow(menu);
        menu.Opened += (_, _) => RoundWindow(menu);
    }

    static void RoundWindow(Control c)
    {
        try { if (c.IsHandleCreated) SetWindowRgn(c.Handle, CreateRoundRectRgn(0, 0, c.Width + 1, c.Height + 1, Radius * 2, Radius * 2), true); }
        catch { }
    }

    static Palette Pal(ContextMenuStrip m) => ((ModernRenderer)m.Renderer).P;

    public static ToolStripMenuItem Item(ContextMenuStrip m, string text, string? right = null, Image? image = null, EventHandler? onClick = null)
    {
        var it = new ToolStripMenuItem(text, image, onClick)
        {
            Padding = new Padding(4, 5, 4, 5),
            ShortcutKeyDisplayString = right,
            ShowShortcutKeys = right != null,
            ImageScaling = ToolStripItemImageScaling.None,
        };
        return it;
    }

    /// <summary>Заголовок/подпись: приглушённый, но не «disabled» (тогда WinForms рисует его серым по-своему).</summary>
    public static ToolStripMenuItem Caption(ContextMenuStrip m, string text, Image? image = null, string? right = null)
    {
        var it = Item(m, text, right, image);
        it.Tag = DimTag;
        it.Enabled = true;
        it.Click += (_, _) => { };
        return it;
    }

    public static ToolStripSeparator Separator() => new() { Margin = new Padding(0, 3, 0, 3) };

    public static Bitmap Glyph(ContextMenuStrip m, char glyph, Color? color = null)
    {
        var scale = m.DeviceDpi / 96f;
        var size = (int)Math.Round(16 * scale);
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var f = new Font("Segoe MDL2 Assets", 12f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        using var b = new SolidBrush(color ?? Pal(m).Text);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(glyph.ToString(), f, b, new RectangleF(0, 0, size, size), sf);
        return bmp;
    }

    public static Bitmap Dot(ContextMenuStrip m, Color color, bool hollow = false)
    {
        var scale = m.DeviceDpi / 96f;
        var size = (int)Math.Round(16 * scale);
        var d = 8 * scale;
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF((size - d) / 2, (size - d) / 2, d, d);
        if (hollow) { using var pen = new Pen(color, 1.2f * scale); g.DrawEllipse(pen, r); }
        else { using var b = new SolidBrush(color); g.FillEllipse(b, r); }
        return bmp;
    }

    public static Color Accent(ContextMenuStrip m) => Pal(m).Accent;
    public static Color Dim(ContextMenuStrip m) => Pal(m).TextDim;
}
