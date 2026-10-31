using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MyFront.Core;

namespace MyFront.UI;

/// <summary>A colour palette. Light keeps the classic Windows look; Dark is VS-Code-like.</summary>
internal sealed class Theme
{
    public required bool IsDark { get; init; }
    /// <summary>AltRow: every second row of a result grid, a shade off <see cref="Window"/>.</summary>
    public required Color Window, AltRow, WindowText, Control, ControlText, GrayText, Border, GridLine, HeaderBack, HeaderText,
        SelectionBack, SelectionText, InputBack, ButtonBack, Hover, Pressed, TabInactive, MenuBack, Ok, Error;
    public required Color IconNeutral, IconBlue, IconGreen, IconRed;
    public required Color EdBack, EdText, EdKeyword, EdFunction, EdString, EdComment, EdNumber, EdOperator, EdIdent,
        EdLineBack, EdLineText, EdCaretLine, EdSelection, EdCaret;

    public static readonly Theme Light = new()
    {
        IsDark = false,
        Window = SystemColors.Window, AltRow = Color.FromArgb(244, 246, 249), WindowText = SystemColors.WindowText,
        Control = SystemColors.Control, ControlText = SystemColors.ControlText, GrayText = SystemColors.GrayText,
        Border = Color.FromArgb(204, 204, 204), GridLine = Color.FromArgb(225, 225, 225),
        HeaderBack = SystemColors.Control, HeaderText = SystemColors.ControlText,
        SelectionBack = Color.FromArgb(204, 228, 247), SelectionText = Color.Black,
        InputBack = SystemColors.Window, ButtonBack = SystemColors.Control,
        Hover = Color.FromArgb(229, 241, 251), Pressed = Color.FromArgb(204, 228, 247),
        TabInactive = SystemColors.Control, MenuBack = Color.FromArgb(249, 249, 249),
        Ok = Color.FromArgb(16, 124, 16), Error = Color.FromArgb(196, 43, 28),
        IconNeutral = Color.FromArgb(80, 80, 80), IconBlue = Color.FromArgb(0, 90, 158),
        IconGreen = Color.FromArgb(16, 124, 16), IconRed = Color.FromArgb(196, 43, 28),
        EdBack = Color.White, EdText = Color.Black, EdKeyword = Color.FromArgb(0, 0, 200), EdFunction = Color.FromArgb(128, 0, 128),
        EdString = Color.FromArgb(163, 21, 21), EdComment = Color.FromArgb(0, 128, 0), EdNumber = Color.FromArgb(9, 134, 88),
        EdOperator = Color.FromArgb(60, 60, 60), EdIdent = Color.FromArgb(0, 102, 153),
        EdLineBack = Color.FromArgb(245, 245, 245), EdLineText = Color.FromArgb(140, 140, 140),
        EdCaretLine = Color.FromArgb(242, 246, 252), EdSelection = Color.FromArgb(173, 214, 255), EdCaret = Color.Black,
    };

    public static readonly Theme Dark = new()
    {
        IsDark = true,
        Window = Color.FromArgb(30, 30, 30), AltRow = Color.FromArgb(40, 40, 43), WindowText = Color.FromArgb(220, 220, 220),
        Control = Color.FromArgb(37, 37, 38), ControlText = Color.FromArgb(220, 220, 220), GrayText = Color.FromArgb(150, 150, 150),
        Border = Color.FromArgb(63, 63, 70), GridLine = Color.FromArgb(55, 55, 58),
        HeaderBack = Color.FromArgb(45, 45, 48), HeaderText = Color.FromArgb(220, 220, 220),
        SelectionBack = Color.FromArgb(38, 79, 120), SelectionText = Color.White,
        InputBack = Color.FromArgb(51, 51, 55), ButtonBack = Color.FromArgb(62, 62, 66),
        Hover = Color.FromArgb(62, 62, 64), Pressed = Color.FromArgb(28, 98, 156),
        TabInactive = Color.FromArgb(45, 45, 48), MenuBack = Color.FromArgb(43, 43, 45),
        Ok = Color.FromArgb(78, 201, 110), Error = Color.FromArgb(241, 98, 91),
        IconNeutral = Color.FromArgb(205, 205, 205), IconBlue = Color.FromArgb(79, 163, 224),
        IconGreen = Color.FromArgb(78, 194, 111), IconRed = Color.FromArgb(240, 98, 91),
        EdBack = Color.FromArgb(30, 30, 30), EdText = Color.FromArgb(212, 212, 212), EdKeyword = Color.FromArgb(86, 156, 214),
        EdFunction = Color.FromArgb(197, 134, 192), EdString = Color.FromArgb(206, 145, 120), EdComment = Color.FromArgb(106, 153, 85),
        EdNumber = Color.FromArgb(181, 206, 168), EdOperator = Color.FromArgb(180, 180, 180), EdIdent = Color.FromArgb(156, 220, 254),
        EdLineBack = Color.FromArgb(30, 30, 30), EdLineText = Color.FromArgb(133, 133, 133),
        EdCaretLine = Color.FromArgb(42, 45, 46), EdSelection = Color.FromArgb(38, 79, 120), EdCaret = Color.FromArgb(220, 220, 220),
    };

    /// <summary>Maps a "role" icon colour (as passed to Ui.Button) to this theme.</summary>
    public Color Icon(Color role)
    {
        if (role == Icons.Gray) return IconNeutral;
        if (role == Icons.Blue) return IconBlue;
        if (role == Icons.Green) return IconGreen;
        if (role == Icons.Red) return IconRed;
        return IsDark ? ControlPaint.Light(role, 0.6f) : role;
    }
}

/// <summary>Remembers which glyph/colour a toolbar image was made from, so it can be re-rendered per theme.</summary>
internal sealed record GlyphTag(char Glyph, Color Role);

/// <summary>Applies the current theme to controls; switching at runtime re-themes every open form.</summary>
internal static class ThemeManager
{
    public static Theme Current { get; private set; } = Theme.Light;
    public static event Action? Changed;

    private static readonly ConditionalWeakTable<Control, object> Hooked = new();

    public static void Init()
    {
        Current = AppSettings.Current.Theme == "dark" ? Theme.Dark : Theme.Light;
        ToolStripManager.Renderer = new ThemeRenderer();
    }

    public static void Toggle() => Set(Current.IsDark ? Theme.Light : Theme.Dark);

    public static void Set(Theme theme)
    {
        if (theme == Current) return;
        var old = Current;
        Current = theme;
        ToolStripManager.Renderer = new ThemeRenderer();
        foreach (Form f in Application.OpenForms) Apply(f, old);
        AppSettings.Current.Theme = theme.IsDark ? "dark" : "light";
        AppSettings.Current.Save();
        Changed?.Invoke();
    }

    public static void Apply(Control root) => Apply(root, null);

    private static void Apply(Control root, Theme? old)
    {
        root.SuspendLayout();
        try { ApplyTree(root, old); }
        finally { root.ResumeLayout(); }
        root.Invalidate(true);
    }

    private static void ApplyTree(Control c, Theme? old)
    {
        ApplyOne(c, old);
        foreach (Control child in c.Controls) ApplyTree(child, old);
        if (c is ToolStrip ts) ApplyItems(ts.Items, old);
        if (c.ContextMenuStrip is { } cms) ApplyItems(cms.Items, old);
    }

    private static void ApplyOne(Control c, Theme? old)
    {
        var t = Current;
        switch (c)
        {
            case Form f:
                f.BackColor = t.Control;
                f.ForeColor = t.ControlText;
                Native(f, h => SetDarkTitleBar(h, t.IsDark));
                break;
            case ResultGrid g:
                g.ApplyTheme(t);
                break;
            case DataGridView dgv:
                ResultGrid.StyleGrid(dgv, t);
                break;
            case SqlEditor e:
                e.ApplyTheme(t);
                Native(e, h => SetWindowTheme(h, t.IsDark ? "DarkMode_Explorer" : null, null));
                break;
            case ThemedTabControl tc:
                tc.ApplyTheme(t);
                break;
            case TabPage p:
                if (t.IsDark) { p.UseVisualStyleBackColor = false; p.BackColor = t.Control; }
                else p.UseVisualStyleBackColor = true;
                break;
            case TreeView tv:
                tv.BackColor = t.Window;
                tv.ForeColor = t.WindowText;
                tv.LineColor = t.GrayText;
                Native(tv, h => SetWindowTheme(h, t.IsDark ? "DarkMode_Explorer" : null, null));
                break;
            case ListView lv:
                lv.BackColor = t.Window;
                lv.ForeColor = t.WindowText;
                foreach (ListViewItem item in lv.Items) item.ForeColor = MapFore(item.ForeColor, old, t.WindowText);
                if (lv.View == View.Details) HookHeaderPainting(lv);
                lv.OwnerDraw = t.IsDark && lv.View == View.Details; // only the header is custom; items draw natively
                Native(lv, h =>
                {
                    SetWindowTheme(h, t.IsDark ? "DarkMode_Explorer" : null, null);
                    var header = SendMessage(h, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
                    if (header != IntPtr.Zero) SetWindowTheme(header, t.IsDark ? "DarkMode_ItemsView" : null, null);
                });
                break;
            case TextBoxBase tb:
                tb.BackColor = t.InputBack;
                tb.ForeColor = t.WindowText;
                if (tb.Multiline) Native(tb, h => SetWindowTheme(h, t.IsDark ? "DarkMode_Explorer" : null, null));
                break;
            case ListBox lb: // also CheckedListBox
                lb.BackColor = t.Window;
                lb.ForeColor = t.WindowText;
                Native(lb, h => SetWindowTheme(h, t.IsDark ? "DarkMode_Explorer" : null, null));
                break;
            case LinkLabel link:
                link.LinkColor = t.IsDark ? t.IconBlue : Color.FromArgb(0, 102, 204);
                link.ActiveLinkColor = t.IsDark ? Color.White : Color.Red;
                link.ForeColor = MapFore(link.ForeColor, old, t.ControlText);
                break;
            case ComboBox cb:
                cb.BackColor = t.InputBack;
                cb.ForeColor = t.WindowText;
                cb.FlatStyle = t.IsDark ? FlatStyle.Flat : FlatStyle.Standard;
                Native(cb, h => SetWindowTheme(h, t.IsDark ? "DarkMode_CFD" : null, null));
                break;
            case NumericUpDown nud:
                nud.BackColor = t.InputBack;
                nud.ForeColor = t.WindowText;
                break;
            case Button b:
                if (t.IsDark)
                {
                    b.FlatStyle = FlatStyle.Flat;
                    b.BackColor = t.ButtonBack;
                    b.ForeColor = t.ControlText;
                    b.FlatAppearance.BorderColor = t.Border;
                    b.FlatAppearance.MouseOverBackColor = t.Hover;
                    b.FlatAppearance.MouseDownBackColor = t.Pressed;
                }
                else
                {
                    b.FlatStyle = FlatStyle.Standard;
                    b.BackColor = SystemColors.Control;
                    b.UseVisualStyleBackColor = true;
                    b.ForeColor = t.ControlText;
                }
                break;
            case CheckBox chk:
                chk.FlatStyle = t.IsDark ? FlatStyle.Flat : FlatStyle.Standard;
                chk.ForeColor = MapFore(chk.ForeColor, old, t.ControlText);
                break;
            case Label l:
                l.ForeColor = MapFore(l.ForeColor, old, t.ControlText);
                break;
            case ToolStrip strip:
                strip.RenderMode = ToolStripRenderMode.ManagerRenderMode;
                strip.BackColor = t.Control;
                strip.ForeColor = t.ControlText;
                break;
            case SplitContainer sc:
                sc.BackColor = t.IsDark ? t.Border : SystemColors.Control;
                sc.Panel1.BackColor = sc.Panel2.BackColor = t.Control;
                break;
        }
    }

    private static void ApplyItems(ToolStripItemCollection items, Theme? old)
    {
        var t = Current;
        foreach (ToolStripItem item in items)
        {
            if (item.Tag is GlyphTag g && item.Image is { } img)
            {
                item.Image = Icons.Glyph(g.Glyph, t.Icon(g.Role), img.Width);
                img.Dispose();
            }
            if (item is ToolStripControlHost host) ApplyTree(host.Control, old);
            if (item is ToolStripDropDownItem dd && dd.HasDropDownItems) ApplyItems(dd.DropDownItems, old);
        }
    }

    /// <summary>Keeps intentional colours (errors, "ok", grey hints) but swaps them for the new theme's version.</summary>
    public static Color MapFore(Color c, Theme? old, Color normal)
    {
        var t = Current;
        if (c == SystemColors.GrayText || c == Theme.Light.GrayText || c == Theme.Dark.GrayText) return t.GrayText;
        if (c == Theme.Light.Ok || c == Theme.Dark.Ok) return t.Ok;
        if (c == Theme.Light.Error || c == Theme.Dark.Error) return t.Error;
        if (c == SystemColors.ControlText || c == SystemColors.WindowText || c == Theme.Dark.ControlText || c == Theme.Dark.WindowText
            || (old is not null && (c == old.ControlText || c == old.WindowText)))
            return normal;
        return c;
    }

    private static readonly ConditionalWeakTable<ListView, object> HeaderHooked = new();

    /// <summary>The dark header theme keeps dark text, so in dark mode the column headers are drawn here.</summary>
    private static void HookHeaderPainting(ListView lv)
    {
        if (HeaderHooked.TryGetValue(lv, out _)) return;
        HeaderHooked.Add(lv, new object());
        lv.DrawItem += (_, e) => e.DrawDefault = true;
        lv.DrawSubItem += (_, e) => e.DrawDefault = true;
        lv.DrawColumnHeader += (_, e) =>
        {
            var t = Current;
            using (var b = new SolidBrush(t.HeaderBack)) e.Graphics.FillRectangle(b, e.Bounds);
            using (var p = new Pen(t.Border))
            {
                e.Graphics.DrawLine(p, e.Bounds.Right - 1, e.Bounds.Top + 3, e.Bounds.Right - 1, e.Bounds.Bottom - 4);
                e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            }
            var align = e.Header?.TextAlign switch
            {
                HorizontalAlignment.Right => TextFormatFlags.Right,
                HorizontalAlignment.Center => TextFormatFlags.HorizontalCenter,
                _ => TextFormatFlags.Left,
            };
            var r = Rectangle.Inflate(e.Bounds, -lv.LogicalToDeviceUnits(6), 0);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text, e.Font, r, t.HeaderText,
                align | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        };
    }

    /// <summary>Runs a native call now if the handle exists, and again whenever the handle is (re)created.</summary>
    private static void Native(Control c, Action<IntPtr> action)
    {
        if (c.IsHandleCreated) action(c.Handle);
        if (Hooked.TryGetValue(c, out _)) return;
        Hooked.Add(c, new object());
        c.HandleCreated += (_, _) => ApplyNativeOnly(c);
    }

    private static void ApplyNativeOnly(Control c)
    {
        var t = Current;
        switch (c)
        {
            case Form f: SetDarkTitleBar(f.Handle, t.IsDark); break;
            case ComboBox cb: SetWindowTheme(cb.Handle, t.IsDark ? "DarkMode_CFD" : null, null); break;
            case ListView lv:
                SetWindowTheme(lv.Handle, t.IsDark ? "DarkMode_Explorer" : null, null);
                var header = SendMessage(lv.Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
                if (header != IntPtr.Zero) SetWindowTheme(header, t.IsDark ? "DarkMode_ItemsView" : null, null);
                break;
            default: if (t.IsDark) SetWindowTheme(c.Handle, "DarkMode_Explorer", null); break;
        }
    }

    // ---- native -------------------------------------------------------------------------------

    private const int LVM_GETHEADER = 0x1000 + 31;

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>Dark (or default) native scrollbars for a child window such as a grid's scrollbar.</summary>
    public static void SetExplorerTheme(Control c, bool dark)
    {
        if (c.IsHandleCreated) SetWindowTheme(c.Handle, dark ? "DarkMode_Explorer" : null, null);
    }

    private static void SetDarkTitleBar(IntPtr hwnd, bool dark)
    {
        int value = dark ? 1 : 0;
        // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 20H1+), 19 on older builds
        if (DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref value, sizeof(int));
        // SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED: repaint the caption now
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
    }
}

/// <summary>Flat menus/toolbars in both themes; colours come from the current theme.</summary>
internal sealed class ThemeRenderer : ToolStripProfessionalRenderer
{
    public ThemeRenderer() : base(new ThemeColorTable()) => RoundedEdges = false;

    private static Theme T => ThemeManager.Current;

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var item = e.Item;
        e.TextColor = !item.Enabled ? T.GrayText : ThemeManager.MapFore(item.ForeColor, null, T.ControlText);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item?.Enabled == false ? T.GrayText : T.ControlText;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is ToolStripDropDown)
        {
            using var pen = new Pen(T.Border);
            var r = e.AffectedBounds;
            e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
        }
        // toolbars, menu bar and status bar stay flat
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var r = e.Item.ContentRectangle;
        using var pen = new Pen(T.Border);
        if (e.Vertical) e.Graphics.DrawLine(pen, r.Width / 2, r.Top + 3, r.Width / 2, r.Bottom - 3);
        else e.Graphics.DrawLine(pen, r.Left + 4, r.Height / 2, r.Right - 4, r.Height / 2);
    }


    private sealed class ThemeColorTable : ProfessionalColorTable
    {
        public ThemeColorTable() => UseSystemColors = false;
        private static Theme T => ThemeManager.Current;

        public override Color ToolStripGradientBegin => T.Control;
        public override Color ToolStripGradientMiddle => T.Control;
        public override Color ToolStripGradientEnd => T.Control;
        public override Color ToolStripBorder => T.Control;
        public override Color MenuStripGradientBegin => T.Control;
        public override Color MenuStripGradientEnd => T.Control;
        public override Color StatusStripGradientBegin => T.Control;
        public override Color StatusStripGradientEnd => T.Control;
        public override Color ToolStripDropDownBackground => T.MenuBack;
        public override Color ImageMarginGradientBegin => T.MenuBack;
        public override Color ImageMarginGradientMiddle => T.MenuBack;
        public override Color ImageMarginGradientEnd => T.MenuBack;
        public override Color MenuBorder => T.Border;
        public override Color MenuItemBorder => T.Hover;
        public override Color MenuItemSelected => T.Hover;
        public override Color MenuItemSelectedGradientBegin => T.Hover;
        public override Color MenuItemSelectedGradientEnd => T.Hover;
        public override Color MenuItemPressedGradientBegin => T.MenuBack;
        public override Color MenuItemPressedGradientMiddle => T.MenuBack;
        public override Color MenuItemPressedGradientEnd => T.MenuBack;
        public override Color ButtonSelectedHighlight => T.Hover;
        public override Color ButtonSelectedHighlightBorder => T.Hover;
        public override Color ButtonSelectedGradientBegin => T.Hover;
        public override Color ButtonSelectedGradientMiddle => T.Hover;
        public override Color ButtonSelectedGradientEnd => T.Hover;
        public override Color ButtonSelectedBorder => T.Hover;
        public override Color ButtonPressedHighlight => T.Pressed;
        public override Color ButtonPressedHighlightBorder => T.Pressed;
        public override Color ButtonPressedGradientBegin => T.Pressed;
        public override Color ButtonPressedGradientMiddle => T.Pressed;
        public override Color ButtonPressedGradientEnd => T.Pressed;
        public override Color ButtonPressedBorder => T.Pressed;
        public override Color ButtonCheckedHighlight => T.Pressed;
        public override Color ButtonCheckedHighlightBorder => T.Pressed;
        public override Color ButtonCheckedGradientBegin => T.Pressed;
        public override Color ButtonCheckedGradientMiddle => T.Pressed;
        public override Color ButtonCheckedGradientEnd => T.Pressed;
        public override Color CheckBackground => T.Pressed;
        public override Color CheckSelectedBackground => T.Pressed;
        public override Color CheckPressedBackground => T.Pressed;
        public override Color SeparatorDark => T.Border;
        public override Color SeparatorLight => T.Control;
        public override Color GripDark => T.Border;
        public override Color GripLight => T.Control;
        public override Color OverflowButtonGradientBegin => T.Control;
        public override Color OverflowButtonGradientMiddle => T.Control;
        public override Color OverflowButtonGradientEnd => T.Control;
        public override Color ToolStripContentPanelGradientBegin => T.Control;
        public override Color ToolStripContentPanelGradientEnd => T.Control;
        public override Color ToolStripPanelGradientBegin => T.Control;
        public override Color ToolStripPanelGradientEnd => T.Control;
        public override Color RaftingContainerGradientBegin => T.Control;
        public override Color RaftingContainerGradientEnd => T.Control;
    }
}

/// <summary>TabControl that paints its own tab strip in dark mode (the native one can't be coloured).</summary>
internal sealed class ThemedTabControl : TabControl
{
    private bool _dark;

    public void ApplyTheme(Theme t)
    {
        _dark = t.IsDark;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, _dark);
        UpdateStyles();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!_dark)
        {
            base.OnPaint(e);
            return;
        }
        var t = ThemeManager.Current;
        var g = e.Graphics;
        g.Clear(t.Control);
        var page = DisplayRectangle;
        using var border = new Pen(t.Border);
        g.DrawRectangle(border, page.X - 1, page.Y - 1, page.Width + 1, page.Height + 1);
        for (int i = 0; i < TabCount; i++)
        {
            var r = GetTabRect(i);
            bool selected = i == SelectedIndex;
            using (var b = new SolidBrush(selected ? t.Window : t.TabInactive)) g.FillRectangle(b, r);
            g.DrawRectangle(border, r.X, r.Y, r.Width - 1, r.Height - (selected ? 0 : 1));
            if (selected)
            {
                using var accent = new Pen(t.IconBlue, 2);
                g.DrawLine(accent, r.X + 1, r.Y + 1, r.Right - 2, r.Y + 1);
            }
            TextRenderer.DrawText(g, TabPages[i].Text, Font, r, selected ? t.ControlText : t.GrayText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }
    }
}
