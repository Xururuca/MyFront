using System.Globalization;
using MyFront.Core.Quick;

namespace MyFront.UI;

/// <summary>Icons a quick query button can use (Segoe Fluent / MDL2 glyphs).</summary>
internal static class QuickQueryIcons
{
    public static readonly (string Key, string Label, char Glyph)[] All =
    {
        ("play", "Play", ''), ("search", "Search", ''), ("calendar", "Calendar", ''),
        ("star", "Star", ''), ("clock", "Clock", ''), ("filter", "Filter", ''),
        ("document", "Document", ''), ("globe", "Globe", ''), ("people", "People", ''),
        ("flash", "Lightning", ''), ("flag", "Flag", ''), ("calculator", "Calculator", ''),
    };

    public static char? Glyph(string? key) => All.FirstOrDefault(i => i.Key == key) is { Key: not null } i ? i.Glyph : null;

    public static Color? ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var h = hex.Trim().TrimStart('#');
        return h.Length == 6 && int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
            ? Color.FromArgb(255, (v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF) : null;
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Button image: the chosen glyph in the chosen colour, the Excel tile, or a coloured dot.</summary>
    public static Bitmap Image(QuickQuery q, int size)
    {
        var color = ParseColor(q.Color);
        if (Glyph(q.Icon) is { } g) return Icons.Glyph(g, ThemeManager.Current.Icon(color ?? Icons.Gray), size);
        if (q.ToExcel) return Icons.Excel(size);
        var bmp = new Bitmap(size, size);
        using var gr = Graphics.FromImage(bmp);
        gr.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var b = new SolidBrush(color ?? Color.FromArgb(0, 120, 212));
        float pad = size * 0.2f;
        gr.FillEllipse(b, pad, pad, size - 2 * pad, size - 2 * pad);
        return bmp;
    }
}

/// <summary>
/// The main toolbar. The main window adds its own buttons on the left; on the right, the quick query buttons
/// (with their hotkey, e.g. "Warns C+1"), then "+" and "Manage…" — one row instead of a second toolbar.
/// </summary>
internal sealed class QuickQueryBar : ToolStrip
{
    private readonly ToolStripButton _add, _manage;
    private readonly ToolStripLabel _hint;

    public event Action<QuickQuery, bool>? RunRequested;   // (query, in a new tab)
    public event Action<QuickQuery>? EditRequested;
    public event Action<QuickQuery>? DeleteRequested;
    public event Action? AddRequested;
    public event Action? ManageRequested;

    public QuickQueryBar(Control dpiSource)
    {
        GripStyle = ToolStripGripStyle.Hidden;
        RenderMode = ToolStripRenderMode.ManagerRenderMode;
        ImageScalingSize = new Size(Icons.Size(dpiSource), Icons.Size(dpiSource));
        Padding = new Padding(dpiSource.LogicalToDeviceUnits(4), 1, 1, 1);
        ShowItemToolTips = true;
        CanOverflow = true;
        // right-aligned items are laid out from the right edge in collection order: Manage…, +, the buttons, the hint
        _manage = Ui.Button(this, "Manage…", Icons.Edit, Icons.Gray, (_, _) => ManageRequested?.Invoke(), "Add, edit, reorder, import or export quick queries", showText: true);
        _manage.Alignment = ToolStripItemAlignment.Right;
        _add = Ui.Button(this, "New quick query", Icons.Add, Icons.Green, (_, _) => AddRequested?.Invoke(), "New quick query: a button for a query you run often");
        _add.Alignment = ToolStripItemAlignment.Right;
        _hint = new ToolStripLabel("Quick queries →") { ForeColor = SystemColors.GrayText, Alignment = ToolStripItemAlignment.Right };
        Items.Add(_hint);
    }

    public void Show(IReadOnlyList<QuickQuery> queries, Func<QuickQuery, string> describeTarget)
    {
        SuspendLayout();
        foreach (var old in Items.OfType<ToolStripButton>().Where(b => b.Tag is QuickQuery).ToList())
        {
            Items.Remove(old);
            old.Image?.Dispose();
            old.Dispose();
        }
        int size = ImageScalingSize.Width;
        foreach (var q in queries)
        {
            var b = new ToolStripButton(Label(q), QuickQueryIcons.Image(q, size))
            {
                Tag = q,
                DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
                ImageScaling = ToolStripItemImageScaling.None,
                AutoToolTip = false,
                ToolTipText = Tooltip(q, describeTarget(q)),
                Alignment = ToolStripItemAlignment.Right,
            };
            var color = QuickQueryIcons.ParseColor(q.Color);
            if (color is not null && q.Icon is null) b.Font = new Font(Font, FontStyle.Bold);
            b.MouseUp += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) RunRequested?.Invoke(q, ModifierKeys.HasFlag(Keys.Shift));
                else if (e.Button == MouseButtons.Right) Menu(q).Show(this, PointToClient(Cursor.Position));
            };
            // each one right after "+": the first query ends up leftmost
            Items.Insert(Items.IndexOf(_add) + 1, b);
        }
        _hint.Visible = queries.Count == 0;
        ResumeLayout();
    }

    /// <summary>The quick query buttons, left to right.</summary>
    internal IEnumerable<ToolStripButton> QueryButtons => Items.OfType<ToolStripButton>().Where(b => b.Tag is QuickQuery).Reverse();

    /// <summary>"Warns C+1": the name and, if it has one, its hotkey (Ctrl+1 … Ctrl+9).</summary>
    internal static string Label(QuickQuery q) => q.Hotkey is { } k ? $"{q.Name} C+{k}" : q.Name;

    private static string Tooltip(QuickQuery q, string target)
    {
        var sql = q.Sql.Replace("\r", "").Trim();
        if (sql.Length > 300) sql = sql[..300] + "…";
        var flags = new List<string>();
        if (q.Hotkey is { } k) flags.Add($"Ctrl+{k}");
        if (q.ToExcel) flags.Add("result → Excel");
        var ph = ParameterParserSafe(q.Sql);
        if (ph.Length > 0) flags.Add("asks for " + ph);
        return $"{q.Name}{(flags.Count > 0 ? "  (" + string.Join(", ", flags) + ")" : "")}\n→ {target}\n\n{sql}\n\nClick: run · Shift+click: new tab · right-click: more";
    }

    private static string ParameterParserSafe(string sql)
    {
        try { return string.Join(", ", ParameterParser.Find(sql).Select(p => "{{" + p.Name + "}}")); }
        catch (ParameterException) { return ""; }
    }

    private ContextMenuStrip Menu(QuickQuery q)
    {
        var m = new ContextMenuStrip();
        m.Items.Add(ResultGrid.MenuItem("Run", q.Hotkey is { } k ? $"Ctrl+{k}" : null, (_, _) => RunRequested?.Invoke(q, false)));
        if (!q.ToExcel) m.Items.Add(ResultGrid.MenuItem("Run in a new tab", "Shift+click", (_, _) => RunRequested?.Invoke(q, true)));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(ResultGrid.MenuItem("Edit…", null, (_, _) => EditRequested?.Invoke(q)));
        m.Items.Add(ResultGrid.MenuItem("Delete", null, (_, _) => DeleteRequested?.Invoke(q)));
        m.Closed += (_, _) => BeginInvoke(m.Dispose);
        return m;
    }
}
