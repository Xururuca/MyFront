using System.Text;
using MyFront.Core.Db;

namespace MyFront.UI;

/// <summary>
/// DataGridView in VirtualMode: only the visible cells are ever formatted, so 1M+ rows scroll smoothly.
/// Rows come from an <see cref="IRowSource"/>; the grid itself stores nothing.
/// </summary>
internal class ResultGrid : DataGridView
{
    private ColumnInfo[] _columns = Array.Empty<ColumnInfo>();
    private IRowSource? _source;
    private int _sortColumn = -1;
    private bool _sortDescending;

    public event Action<int, bool>? SortRequested;
    public event Action<string>? StatusMessage;
    /// <summary>"xlsx" or "csv" from the context menu; the menu items show only when someone listens.</summary>
    public event Action<string>? ExportRequested;
    /// <summary>Enter was pressed: the grid has committed the cell (and moved down when it could).</summary>
    public event Action? EnterPressed;
    /// <summary>Ctrl+V / Paste with cells on the clipboard (rows of fields). Without a listener the grid says it is read-only.</summary>
    public event Action<List<string[]>>? PasteRequested;

    /// <summary>Visible columns in on-screen order (what an export of this grid should contain).</summary>
    public int[] DisplayColumnOrder() => DisplayColumns().ToArray();

    public ResultGrid()
    {
        DoubleBuffered = true;
        VirtualMode = true;
        ReadOnly = true;
        AllowUserToAddRows = false;
        AllowUserToDeleteRows = false;
        AllowUserToResizeRows = false;
        AllowUserToOrderColumns = true;
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        RowHeadersWidthSizeMode = DataGridViewRowHeadersWidthSizeMode.DisableResizing;
        SelectionMode = DataGridViewSelectionMode.RowHeaderSelect;
        EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
        ClipboardCopyMode = DataGridViewClipboardCopyMode.Disable; // custom, source-based copy below
        BorderStyle = BorderStyle.None;
        BackgroundColor = SystemColors.Window;
        GridColor = Color.FromArgb(225, 225, 225);
        ShowCellToolTips = true;
        StandardTab = true;
        DefaultCellStyle.WrapMode = DataGridViewTriState.False;
        DefaultCellStyle.SelectionBackColor = Color.FromArgb(204, 228, 247);
        DefaultCellStyle.SelectionForeColor = Color.Black;
        EnableHeadersVisualStyles = true;
        RowHeadersWidth = LogicalToDeviceUnits(22);
        ColumnHeadersHeight = LogicalToDeviceUnits(24);
        RowTemplate.Height = LogicalToDeviceUnits(21);

        var menu = new ContextMenuStrip();
        menu.Items.Add(MenuItem("Copy", "Ctrl+C", (_, _) => CopySelection(false)));
        var paste = MenuItem("Paste", "Ctrl+V", (_, _) => Paste());
        menu.Items.Add(paste);
        menu.Items.Add(MenuItem("Copy all rows", null, (_, _) => CopyAll()));
        var exportSep = new ToolStripSeparator();
        var toExcel = MenuItem("Export to Excel…", null, (_, _) => ExportRequested?.Invoke("xlsx"));
        var toCsv = MenuItem("Export to CSV…", null, (_, _) => ExportRequested?.Invoke("csv"));
        menu.Items.AddRange(new ToolStripItem[] { exportSep, toExcel, toCsv });
        menu.Opening += (_, _) =>
        {
            exportSep.Visible = toExcel.Visible = toCsv.Visible = ExportRequested is not null;
            paste.Visible = PasteRequested is not null;
        };
        ContextMenuStrip = menu;
        ApplyTheme(ThemeManager.Current);
    }

    /// <summary>The theme's colours, and every second row a shade off (selection and the rest as in other rows).</summary>
    public void ApplyTheme(Theme t)
    {
        AlternatingRowsDefaultCellStyle.BackColor = t.AltRow;
        StyleGrid(this, t);
    }

    /// <summary>Theme colours for any DataGridView (result grids and the import wizard's editable grids).</summary>
    public static void StyleGrid(DataGridView g, Theme t)
    {
        g.BackgroundColor = t.Window;
        g.GridColor = t.GridLine;
        g.DefaultCellStyle.BackColor = t.Window;
        g.DefaultCellStyle.ForeColor = t.WindowText;
        g.DefaultCellStyle.SelectionBackColor = t.SelectionBack;
        g.DefaultCellStyle.SelectionForeColor = t.SelectionText;
        // native (visual-style) headers ignore colours, so dark mode draws them flat
        g.EnableHeadersVisualStyles = !t.IsDark;
        g.ColumnHeadersBorderStyle = t.IsDark ? DataGridViewHeaderBorderStyle.Single : DataGridViewHeaderBorderStyle.Raised;
        g.RowHeadersBorderStyle = t.IsDark ? DataGridViewHeaderBorderStyle.Single : DataGridViewHeaderBorderStyle.Raised;
        foreach (var style in new[] { g.ColumnHeadersDefaultCellStyle, g.RowHeadersDefaultCellStyle })
        {
            style.BackColor = t.HeaderBack;
            style.ForeColor = t.HeaderText;
            style.SelectionBackColor = t.IsDark ? t.SelectionBack : SystemColors.Highlight;
            style.SelectionForeColor = t.IsDark ? t.SelectionText : SystemColors.HighlightText;
        }
        ThemeScrollBars(g, t);
        g.Invalidate();
    }

    private static void ThemeScrollBars(DataGridView g, Theme t)
    {
        foreach (Control c in g.Controls)
            if (c is ScrollBar) ThemeManager.SetExplorerTheme(c, t.IsDark);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ThemeScrollBars(this, ThemeManager.Current);
    }

    public static ToolStripMenuItem MenuItem(string text, string? shortcut, EventHandler click) =>
        new(text, null, click) { ShortcutKeyDisplayString = shortcut };

    public ColumnInfo[] ColumnInfos => _columns;
    public IRowSource? Source => _source;

    public void Bind(ColumnInfo[] columns, IRowSource source)
    {
        SuspendLayout();
        RowCount = 0;
        Columns.Clear();
        _columns = columns;
        _source = source;
        _sortColumn = -1;
        _userSized.Clear();
        var cols = new DataGridViewColumn[columns.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            var c = columns[i];
            var col = new DataGridViewTextBoxColumn
            {
                Name = "c" + i,
                HeaderText = c.Name,
                ToolTipText = $"{c.Name}  {c.DataTypeName}",
                SortMode = DataGridViewColumnSortMode.Programmatic,
                Width = LogicalToDeviceUnits(100),
                MinimumWidth = LogicalToDeviceUnits(24),
                FillWeight = 1,
            };
            if (c.IsNumeric) col.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            cols[i] = col;
        }
        Columns.AddRange(cols);
        ResumeLayout();
        SyncRowCount();
    }

    public void Clear()
    {
        RowCount = 0;
        Columns.Clear();
        _columns = Array.Empty<ColumnInfo>();
        _source = null;
    }

    /// <summary>Picks up rows appended by the background reader.</summary>
    public void SyncRowCount()
    {
        if (_source is null || Columns.Count == 0) return;
        int n = _source.Count;
        if (RowCount != n) RowCount = n;
    }

    public void InvalidateRows() => Invalidate();

    private readonly HashSet<int> _userSized = new();
    private bool _fitting;

    /// <summary>
    /// Sizes columns from the header and a sample of rows (first rows, plus the last rows when
    /// <paramref name="widenOnly"/> re-fits after loading). Never measures all rows; never touches columns the user resized.
    /// </summary>
    public void AutoFitColumns(int sampleRows = 100, bool widenOnly = false)
    {
        if (_source is null) return;
        int count = _source.Count;
        var rows = Enumerable.Range(0, Math.Min(sampleRows, count));
        if (widenOnly) rows = rows.Concat(Enumerable.Range(Math.Max(0, count - sampleRows), Math.Min(sampleRows, count))).Distinct();
        var sample = rows.ToArray();
        int min = LogicalToDeviceUnits(40), max = LogicalToDeviceUnits(360), pad = LogicalToDeviceUnits(14);
        using var bold = new Font(Font, FontStyle.Bold);
        _fitting = true;
        try
        {
            for (int c = 0; c < _columns.Length; c++)
            {
                if (_userSized.Contains(c)) continue;
                int w = TextRenderer.MeasureText(_columns[c].Name, bold).Width + LogicalToDeviceUnits(18);
                foreach (var r in sample)
                {
                    var text = CellFormat.ToDisplay(GetRawValue(r, c), _columns[c].Kind);
                    if (text.Length > 120) text = text[..120];
                    w = Math.Max(w, TextRenderer.MeasureText(text, Font).Width + pad);
                    if (w >= max) break;
                }
                w = Math.Clamp(w, min, max);
                if (!widenOnly || w > Columns[c].Width) Columns[c].Width = w;
            }
        }
        finally { _fitting = false; }
    }

    protected override void OnColumnWidthChanged(DataGridViewColumnEventArgs e)
    {
        base.OnColumnWidthChanged(e);
        if (!_fitting && MouseButtons == MouseButtons.Left) _userSized.Add(e.Column.Index);
    }

    public void SetSortGlyph(int column, bool descending)
    {
        foreach (DataGridViewColumn col in Columns) col.HeaderCell.SortGlyphDirection = SortOrder.None;
        _sortColumn = column;
        _sortDescending = descending;
        if (column >= 0 && column < Columns.Count)
            Columns[column].HeaderCell.SortGlyphDirection = descending ? SortOrder.Descending : SortOrder.Ascending;
    }

    protected virtual object? GetRawValue(int row, int column)
    {
        var src = _source;
        if (src is null || row >= src.Count) return null;
        var values = src.GetRow(row);
        return column < values.Length ? values[column] : null;
    }

    protected override void OnCellValueNeeded(DataGridViewCellValueEventArgs e)
    {
        if (e.ColumnIndex < _columns.Length)
            e.Value = CellFormat.ToDisplay(GetRawValue(e.RowIndex, e.ColumnIndex), _columns[e.ColumnIndex].Kind);
        base.OnCellValueNeeded(e);
    }

    protected override void OnCellFormatting(DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex >= 0 && e.ColumnIndex >= 0 && e.ColumnIndex < _columns.Length)
        {
            var raw = GetRawValue(e.RowIndex, e.ColumnIndex);
            if (raw is null || raw is byte[] { Length: > 64 })
                e.CellStyle!.ForeColor = ThemeManager.Current.GrayText;
        }
        base.OnCellFormatting(e);
    }

    protected override void OnColumnHeaderMouseClick(DataGridViewCellMouseEventArgs e)
    {
        base.OnColumnHeaderMouseClick(e);
        if (e.Button != MouseButtons.Left || e.ColumnIndex < 0) return;
        bool desc = _sortColumn == e.ColumnIndex && !_sortDescending;
        SortRequested?.Invoke(e.ColumnIndex, desc);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!IsCurrentCellInEditMode)
        {
            if (keyData == (Keys.Control | Keys.C)) { CopySelection(false); return true; }
            if (keyData == (Keys.Control | Keys.Shift | Keys.C)) { CopySelection(true); return true; }
            if (keyData == (Keys.Control | Keys.V)) { Paste(); return true; }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        bool handled = base.ProcessDialogKey(keyData);
        if (keyData == Keys.Enter) EnterPressed?.Invoke();
        return handled;
    }

    protected override bool ProcessDataGridViewKey(KeyEventArgs e)
    {
        bool handled = base.ProcessDataGridViewKey(e);
        if (e.KeyData == Keys.Enter) EnterPressed?.Invoke();
        return handled;
    }

    // ---- clipboard ----------------------------------------------------------------------------

    /// <summary>Clipboard format next to the text: the copied cells as tab-separated, quoted fields, so a single value
    /// with line breaks pastes back into one cell.</summary>
    private const string CellsFormat = "MyFront.Cells";

    private List<int> DisplayColumns() =>
        Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).OrderBy(c => c.DisplayIndex).Select(c => c.Index).ToList();

    /// <summary>
    /// Copies selected rows (via row states, no row un-sharing) or selected cells as tab-separated rows, as Excel does;
    /// NULL is an empty field. A single cell is copied as its plain text.
    /// </summary>
    public void CopySelection(bool withHeaders)
    {
        if (_source is null) return;
        var cols = DisplayColumns();
        var rows = new List<string?[]>();
        int selectedRows = Rows.GetRowCount(DataGridViewElementStates.Selected);
        if (selectedRows > 0)
        {
            if (withHeaders) rows.Add(cols.Select(c => (string?)_columns[c].Name).ToArray());
            int r = Rows.GetFirstRow(DataGridViewElementStates.Selected);
            while (r >= 0)
            {
                rows.Add(cols.Select(c => CellText(r, c)).ToArray());
                r = Rows.GetNextRow(r, DataGridViewElementStates.Selected);
            }
        }
        else
        {
            var cells = SelectedCells.Cast<DataGridViewCell>().ToList();
            if (cells.Count == 0) return;
            if (cells.Count == 1 && !withHeaders)
            {
                var single = CellText(cells[0].RowIndex, cells[0].ColumnIndex);
                SetClipboard(single ?? "", GridClipboard.Format(new[] { new[] { single } }));
                return;
            }
            var rowIdx = cells.Select(c => c.RowIndex).Distinct().OrderBy(i => i).ToList();
            var colSet = cells.Select(c => c.ColumnIndex).ToHashSet();
            var useCols = cols.Where(colSet.Contains).ToList();
            if (withHeaders) rows.Add(useCols.Select(c => (string?)_columns[c].Name).ToArray());
            var selected = cells.Select(c => (c.RowIndex, c.ColumnIndex)).ToHashSet();
            foreach (var r in rowIdx)
                rows.Add(useCols.Select(c => selected.Contains((r, c)) ? CellText(r, c) : null).ToArray());
        }
        var text = GridClipboard.Format(rows);
        SetClipboard(text, text);
    }

    public void CopyAll()
    {
        if (_source is null) return;
        var cols = DisplayColumns();
        var rows = new List<string?[]> { cols.Select(c => (string?)_columns[c].Name).ToArray() };
        for (int r = 0; r < _source.Count; r++) rows.Add(cols.Select(c => CellText(r, c)).ToArray());
        var text = GridClipboard.Format(rows);
        SetClipboard(text, text);
    }

    /// <summary>A cell as copied: the shown text, null for NULL.</summary>
    private string? CellText(int r, int c)
    {
        var v = GetRawValue(r, c);
        return v is null ? null : CellFormat.ToDisplay(v, _columns[c].Kind);
    }

    private void SetClipboard(string text, string cells)
    {
        if (text.Length == 0 && cells.Length == 0) return;
        try
        {
            var data = new DataObject();
            data.SetText(text.Length == 0 ? "\r\n" : text, TextDataFormat.UnicodeText);
            data.SetData(CellsFormat, new MemoryStream(Encoding.UTF8.GetBytes(cells)));
            Clipboard.SetDataObject(data, copy: true);
            StatusMessage?.Invoke($"Copied {text.Length:N0} characters");
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            StatusMessage?.Invoke("Clipboard busy: " + ex.Message);
        }
    }

    /// <summary>Ctrl+V / Paste: the clipboard's cells (copied here, or text from Excel, an editor …) to the listener.</summary>
    public void Paste()
    {
        if (PasteRequested is null)
        {
            StatusMessage?.Invoke("Can't paste: this grid is read-only");
            return;
        }
        string? text;
        try
        {
            var data = Clipboard.GetDataObject();
            text = data?.GetData(CellsFormat) is MemoryStream cells ? Encoding.UTF8.GetString(cells.ToArray())
                : data?.GetDataPresent(DataFormats.UnicodeText) == true ? data.GetData(DataFormats.UnicodeText) as string
                : null;
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            StatusMessage?.Invoke("Clipboard busy: " + ex.Message);
            return;
        }
        var block = GridClipboard.Parse(text ?? "");
        if (block.Count == 0)
        {
            StatusMessage?.Invoke("Nothing to paste: the clipboard has no text");
            return;
        }
        PasteRequested(block);
    }

    /// <summary>Shows a message where this grid's owner shows its status (the info line or the status bar).</summary>
    internal void ReportStatus(string text) => StatusMessage?.Invoke(text);
}
