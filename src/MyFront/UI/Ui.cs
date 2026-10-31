using MySqlConnector;

namespace MyFront.UI;

internal static class Ui
{
    /// <summary>Self-test only: receives messages instead of modal message boxes.</summary>
    internal static Action<string>? MessageSink;
    /// <summary>Self-test only: answers confirmations instead of a modal dialog.</summary>
    internal static Func<string, bool>? ConfirmOverride;

    public static void Error(IWin32Window? owner, Exception ex, string? title = null)
    {
        if (MessageSink is not null) { MessageSink("error: " + Describe(ex)); return; }
        MessageBox.Show(owner, Describe(ex), title ?? "MyFront", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    public static void Info(IWin32Window? owner, string text, string? title = null)
    {
        if (MessageSink is not null) { MessageSink("info: " + text); return; }
        MessageBox.Show(owner, text, title ?? "MyFront", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    public static bool Confirm(IWin32Window? owner, string text, string? title = null, bool warning = false)
    {
        if (ConfirmOverride is not null) return ConfirmOverride(text);
        return MessageBox.Show(owner, text, title ?? "MyFront", MessageBoxButtons.OKCancel,
            warning ? MessageBoxIcon.Warning : MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.OK;
    }

    public static string Describe(Exception ex)
    {
        if (ex is AggregateException { InnerExceptions.Count: 1 } agg) ex = agg.InnerException!;
        return ex switch
        {
            MySqlException m when m.Number > 0 => $"SQL Error ({m.Number}): {m.Message}",
            MySqlException m => m.Message + (m.InnerException is null ? "" : "\n" + m.InnerException.Message),
            _ => ex.Message,
        };
    }

    /// <summary>Runs an async UI action and reports failures instead of crashing ("async void" handlers).</summary>
    public static async void Fire(Control owner, Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!owner.IsDisposed) Error(owner.FindForm(), ex);
        }
    }

    public static ToolStripButton Button(ToolStrip strip, string text, char glyph, Color color, EventHandler onClick, string? tooltip = null, bool showText = false)
    {
        var size = Icons.Size(strip);
        var b = new ToolStripButton(text, Icons.Glyph(glyph, ThemeManager.Current.Icon(color), size), onClick)
        {
            DisplayStyle = showText ? ToolStripItemDisplayStyle.ImageAndText : ToolStripItemDisplayStyle.Image,
            ToolTipText = tooltip ?? text,
            ImageScaling = ToolStripItemImageScaling.None,
            Tag = new GlyphTag(glyph, color), // re-rendered when the theme changes
        };
        strip.Items.Add(b);
        return b;
    }

    public static ToolStrip NewToolStrip(Control dpiSource)
    {
        return new ToolStrip
        {
            GripStyle = ToolStripGripStyle.Hidden,
            RenderMode = ToolStripRenderMode.ManagerRenderMode, // ThemeRenderer
            ImageScalingSize = new Size(Icons.Size(dpiSource), Icons.Size(dpiSource)),
            Padding = new Padding(dpiSource.LogicalToDeviceUnits(4), 1, 1, 1),
            CanOverflow = true,
        };
    }

    public static Font Mono(float size = 10f) => new("Consolas", size);

    private static Icon? _appIcon;

    /// <summary>The black "M" on yellow window icon (same file as the exe icon).</summary>
    public static Icon AppIcon
    {
        get
        {
            if (_appIcon is null)
            {
                using var s = typeof(Ui).Assembly.GetManifestResourceStream("MyFront.app.ico")!;
                _appIcon = new Icon(s);
            }
            return _appIcon;
        }
    }

    /// <summary>Self-test only: records paths instead of launching Excel / Explorer.</summary>
    internal static Action<string>? OpenPathOverride;

    /// <summary>Opens a file or folder with its default program (Excel for .xlsx).</summary>
    public static void OpenPath(string path)
    {
        if (OpenPathOverride is not null) { OpenPathOverride(path); return; }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Error(null, ex, "Could not open " + Path.GetFileName(path)); }
    }
}
