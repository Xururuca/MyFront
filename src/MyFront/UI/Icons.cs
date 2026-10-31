using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace MyFront.UI;

/// <summary>
/// Icons without image files: toolbar glyphs come from the Windows icon font (Segoe Fluent Icons / MDL2 Assets),
/// tree icons are drawn with GDI+.
/// </summary>
internal static class Icons
{
    public const char Play = '', Stop = '', Refresh = '', Add = '', Delete = '',
        Save = '', Open = '', Connect = '', Filter = '', Search = '', History = '',
        Copy = '', Accept = '', Cancel = '', Prev = '', Next = '', First = '',
        Document = '', Code = '', NewTab = '', Run = '', Edit = '',
        Moon = '', Sun = '', Import = '';

    /// <summary>Run: a filled play triangle, drawn with GDI+ (clearer than the outline font glyph, no font needed).</summary>
    public const char PlaySolid = '\uF5B0';

    /// <summary>Format SQL: left-aligned lines.</summary>
    public const char Format = '\uE8E4';

    private static readonly string GlyphFont = FirstInstalled("Segoe Fluent Icons", "Segoe MDL2 Assets") ?? "Segoe UI Symbol";

    private static string? FirstInstalled(params string[] names)
    {
        foreach (var n in names)
        {
            try
            {
                using var family = new FontFamily(n); // throws when the font is not installed
                return n;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    public static int Size(Control c) => c.LogicalToDeviceUnits(16);

    public static Bitmap Glyph(char glyph, Color color, int size)
    {
        if (glyph == PlaySolid) return SolidPlay(color, size);
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font(GlyphFont, size * 0.78f, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(glyph.ToString(), font, brush, new RectangleF(0, 0, size, size), sf);
        return bmp;
    }

    private static Bitmap SolidPlay(Color color, int size) => Draw(size, (g, _) =>
    {
        using var b = new SolidBrush(color);
        g.FillPolygon(b, new[] { new PointF(3.5f, 1.5f), new PointF(14.5f, 8f), new PointF(3.5f, 14.5f) });
    });

    public static readonly Color Blue = Color.FromArgb(0, 90, 158);
    public static readonly Color Green = Color.FromArgb(16, 124, 16);
    public static readonly Color Red = Color.FromArgb(196, 43, 28);
    public static readonly Color Gray = Color.FromArgb(80, 80, 80);

    /// <summary>Green "X" tile used for the one-click Excel export button.</summary>
    public static Bitmap Excel(int size) => Draw(size, (g, s) =>
    {
        using var green = new SolidBrush(Color.FromArgb(16, 124, 65));
        g.FillRectangle(green, 1.5f, 1.5f, 13, 13);
        using var f = new Font("Segoe UI", 9.5f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.DrawString("X", f, Brushes.White, new RectangleF(1, 1.5f, 14, 14), sf);
    });

    // ---- tree icons -------------------------------------------------------------------------------

    public static ImageList TreeImages(int size)
    {
        var list = new ImageList { ImageSize = new Size(size, size), ColorDepth = ColorDepth.Depth32Bit };
        list.Images.Add("server", Draw(size, DrawServer));
        list.Images.Add("database", Draw(size, DrawDatabase));
        list.Images.Add("table", Draw(size, (g, s) => DrawTable(g, s, Color.FromArgb(70, 120, 190))));
        list.Images.Add("view", Draw(size, (g, s) => DrawTable(g, s, Color.FromArgb(60, 150, 90))));
        list.Images.Add("procedure", Draw(size, (g, s) => DrawLetter(g, s, "P", Color.FromArgb(140, 70, 160))));
        list.Images.Add("function", Draw(size, (g, s) => DrawLetter(g, s, "f", Color.FromArgb(190, 110, 30))));
        list.Images.Add("processes", Draw(size, DrawProcesses));
        list.Images.Add("variables", Draw(size, (g, s) => DrawLetter(g, s, "x", Color.FromArgb(90, 90, 90))));
        list.Images.Add("key", Draw(size, DrawKey));
        list.Images.Add("field", Draw(size, DrawField));
        list.Images.Add("hidden", Draw(size, DrawHiddenFolder));
        list.Images.Add("loading", new Bitmap(size, size));
        return list;
    }

    private static Bitmap Draw(int size, Action<Graphics, float> paint)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.ScaleTransform(size / 16f, size / 16f);
        paint(g, 16f);
        return bmp;
    }

    private static void DrawServer(Graphics g, float s)
    {
        using var body = new SolidBrush(Color.FromArgb(96, 108, 124));
        using var light = new SolidBrush(Color.FromArgb(110, 220, 110));
        for (int i = 0; i < 3; i++)
        {
            var r = new RectangleF(2, 1.5f + i * 4.6f, 12, 3.8f);
            g.FillRectangle(body, r);
            g.FillEllipse(light, 11, r.Y + 1.1f, 1.8f, 1.8f);
        }
    }

    private static void DrawDatabase(Graphics g, float s)
    {
        var top = Color.FromArgb(250, 205, 70);
        var side = Color.FromArgb(225, 160, 30);
        using var sideBrush = new SolidBrush(side);
        using var topBrush = new SolidBrush(top);
        using var pen = new Pen(Color.FromArgb(150, 100, 10), 0.8f);
        g.FillRectangle(sideBrush, 2.5f, 3.5f, 11, 9);
        g.FillEllipse(sideBrush, 2.5f, 10, 11, 4.5f);
        g.FillEllipse(topBrush, 2.5f, 1.5f, 11, 4.5f);
        g.DrawEllipse(pen, 2.5f, 1.5f, 11, 4.5f);
        g.DrawArc(pen, 2.5f, 6.2f, 11, 4.5f, 0, 180);
        g.DrawArc(pen, 2.5f, 10, 11, 4.5f, 0, 180);
        g.DrawLine(pen, 2.5f, 3.75f, 2.5f, 12.25f);
        g.DrawLine(pen, 13.5f, 3.75f, 13.5f, 12.25f);
    }

    private static void DrawTable(Graphics g, float s, Color header)
    {
        using var bg = new SolidBrush(Color.White);
        using var hb = new SolidBrush(header);
        using var pen = new Pen(Color.FromArgb(120, 120, 120), 0.8f);
        var r = new RectangleF(1.5f, 2.5f, 13, 11);
        g.FillRectangle(bg, r);
        g.FillRectangle(hb, r.X, r.Y, r.Width, 3);
        g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
        g.DrawLine(pen, r.X, 9, r.Right, 9);
        g.DrawLine(pen, 6, r.Y + 3, 6, r.Bottom);
    }

    private static void DrawLetter(Graphics g, float s, string letter, Color color)
    {
        using var b = new SolidBrush(color);
        g.FillEllipse(b, 1, 1, 14, 14);
        using var f = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.DrawString(letter, f, Brushes.White, new RectangleF(0, 0, 16, 16.5f), sf);
    }

    /// <summary>A gold key: ring, shaft, two teeth.</summary>
    private static void DrawKey(Graphics g, float s)
    {
        using var gold = new SolidBrush(Color.FromArgb(225, 170, 30));
        using var pen = new Pen(Color.FromArgb(150, 105, 10), 0.8f);
        g.FillEllipse(gold, 1, 4, 7, 7);
        g.DrawEllipse(pen, 1, 4, 7, 7);
        g.FillEllipse(Brushes.White, 3, 6.5f, 2, 2);
        var shaft = new[] { new PointF(7.5f, 6.5f), new PointF(15, 6.5f), new PointF(15, 8.5f), new PointF(7.5f, 8.5f) };
        g.FillPolygon(gold, shaft);
        g.FillRectangle(gold, 11, 8.5f, 1.6f, 2.5f);
        g.FillRectangle(gold, 13.4f, 8.5f, 1.6f, 3.2f);
    }

    /// <summary>A field: a table outline with one column filled in.</summary>
    private static void DrawField(Graphics g, float s)
    {
        using var bg = new SolidBrush(Color.White);
        using var col = new SolidBrush(Color.FromArgb(70, 120, 190));
        using var pen = new Pen(Color.FromArgb(120, 120, 120), 0.8f);
        var r = new RectangleF(2.5f, 2.5f, 11, 11);
        g.FillRectangle(bg, r);
        g.FillRectangle(col, 6, r.Y, 4, r.Height);
        g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
        g.DrawLine(pen, r.X, 6.2f, r.Right, 6.2f);
        g.DrawLine(pen, r.X, 9.9f, r.Right, 9.9f);
    }

    /// <summary>The Hidden folder: a grey folder.</summary>
    private static void DrawHiddenFolder(Graphics g, float s)
    {
        using var back = new SolidBrush(Color.FromArgb(150, 150, 150));
        using var front = new SolidBrush(Color.FromArgb(185, 185, 185));
        using var pen = new Pen(Color.FromArgb(110, 110, 110), 0.8f);
        g.FillPolygon(back, new[] { new PointF(1.5f, 3), new PointF(6, 3), new PointF(7.5f, 4.5f), new PointF(14.5f, 4.5f), new PointF(14.5f, 13), new PointF(1.5f, 13) });
        g.FillRectangle(front, 1.5f, 6.5f, 13, 6.5f);
        g.DrawRectangle(pen, 1.5f, 6.5f, 13, 6.5f);
    }

    private static void DrawProcesses(Graphics g, float s)
    {
        using var pen = new Pen(Color.FromArgb(70, 120, 190), 1.6f);
        g.DrawLines(pen, new[] { new PointF(1, 9), new PointF(4.5f, 9), new PointF(6.5f, 3), new PointF(9.5f, 13), new PointF(11.5f, 7), new PointF(15, 7) });
    }
}
