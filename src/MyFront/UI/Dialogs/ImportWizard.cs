using System.Diagnostics;
using MyFront.Core;
using MyFront.Core.Db;
using MyFront.Core.Import;
using MySqlConnector;

namespace MyFront.UI.Dialogs;

/// <summary>Grid that paints "⚠ …" cells (conversion errors) red.</summary>
internal sealed class PreviewGrid : ResultGrid
{
    protected override void OnCellFormatting(DataGridViewCellFormattingEventArgs e)
    {
        base.OnCellFormatting(e);
        if (e.Value is string s && s.StartsWith('⚠')) e.CellStyle!.ForeColor = ThemeManager.Current.Error;
    }
}

/// <summary>
/// Excel / CSV import: 1 Source (file, sheet, preview) → 2 Target (existing or new table) →
/// 3 Mapping and mode (auto-matched, converted preview) → 4 Run (progress, cancel, summary, failed rows).
/// A remembered mapping for the file turns a repeat import into "choose file → Import now".
/// </summary>
internal sealed class ImportWizard : Form
{
    private const int PreviewRows = 50;
    private const string Skip = "— skip —";

    private readonly MainForm _main;
    private readonly ServerSession _session;
    private readonly string _database;
    private readonly Panel[] _pages = new Panel[4];
    private int _page;
    private readonly Label _title, _status;
    private readonly Button _back, _next, _import, _close;

    // 1 source
    private readonly TextBox _file;
    private readonly ComboBox _sheet, _csvDelimiter, _csvEncoding;
    private readonly CheckBox _headerRow;
    private readonly RadioButton _numTr, _numDot;
    private readonly FlowLayoutPanel _csvRow, _remember;
    private readonly Label _rememberText, _sourceInfo;
    private readonly Button _importNow;
    private readonly ResultGrid _rawPreview;
    // 2 target
    private readonly RadioButton _existing, _newTable;
    private readonly ComboBox _tables;
    private readonly TextBox _newName;
    private readonly DataGridView _newColumns;
    private readonly Label _targetInfo;
    // 3 mapping
    private readonly DataGridView _mapGrid;
    private readonly RadioButton _mInsert, _mReplace, _mUpsert, _mIgnore;
    private readonly CheckBox _realTruncate, _continue;
    private readonly Label _mapInfo;
    private readonly PreviewGrid _convPreview;
    // 4 run
    private readonly Label _runSummary, _runCounters, _runResult;
    private readonly ProgressBar _runBar;
    private readonly Button _saveFailed, _openTable;

    private IReadOnlyList<string> _headers = Array.Empty<string>();
    private List<object?[]> _rows = new();
    private long? _rowEstimate;
    private SavedImport? _remembered;
    private TableInfo? _tableInfo;
    private string? _tableEngine;
    private List<InferredColumn>? _inferred;
    private List<TargetColumn> _targets = new();
    private int _version;
    private CancellationTokenSource? _analyzeCts, _runCts;
    private bool _mappingBuilt;

    internal ImportResult? Result { get; private set; }
    internal string? TargetTable { get; private set; }
    internal bool IsRunning => _runCts is not null;

    public ImportWizard(MainForm main, ServerSession session, string database, string? table = null, string? file = null)
    {
        _main = main;
        _session = session;
        _database = database;
        Text = $"Import Excel / CSV — {session.DisplayName} / {database}";
        Icon = Ui.AppIcon;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        Size = new Size(W(980), W(680));
        MinimumSize = new Size(W(760), W(520));

        _title = new Label { Dock = DockStyle.Top, Height = W(34), Font = new Font(Font.FontFamily, 11f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(W(10), 0, 0, 0) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = W(44), Padding = new Padding(W(8)) };
        _close = Btn("Close", (_, _) => { if (_runCts is not null) _runCts.Cancel(); else Close(); });
        _import = Btn("Import", (_, _) => Ui.Fire(this, RunImportAsync));
        _next = Btn("Next >", (_, _) => Ui.Fire(this, NextAsync));
        _back = Btn("< Back", (_, _) => ShowPage(_page - 1));
        buttons.Controls.AddRange(new Control[] { _close, _import, _next, _back });
        _status = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, W(8), W(20), 0) };
        buttons.Controls.Add(_status);
        Button Btn(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(W(90), W(28)) };
            b.Click += click;
            return b;
        }

        // ---- page 1: source ---------------------------------------------------------------------
        var p1 = Page(0, "1  Source file");
        var top1 = Table(p1, 3);
        var browse = new Button { Text = "Browse…", AutoSize = true };
        _file = AddRow(top1, "File", new TextBox { Dock = DockStyle.Fill, PlaceholderText = "Excel (.xlsx, .xls) or CSV file — or drop a file on the main window" }, browse);
        browse.Click += (_, _) => Browse();
        _file.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Ui.Fire(this, () => LoadFileAsync(_file.Text.Trim())); } };
        var sheetRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        _sheet = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = W(260) };
        _headerRow = new CheckBox { Text = "First row has column names", AutoSize = true, Checked = true, Margin = new Padding(W(16), W(4), 0, 0) };
        sheetRow.Controls.AddRange(new Control[] { _sheet, _headerRow });
        AddRow(top1, "Sheet", sheetRow);
        var numRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        _numTr = new RadioButton { Text = "Turkish  1.234,56", AutoSize = true, Checked = true };
        _numDot = new RadioButton { Text = "Dot decimal  1,234.56", AutoSize = true, Margin = new Padding(W(16), W(3), 0, 0) };
        numRow.Controls.AddRange(new Control[] { _numTr, _numDot });
        AddRow(top1, "Numbers in text", numRow);
        _csvRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        _csvDelimiter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = W(120) };
        _csvDelimiter.Items.AddRange(new object[] { "Auto", "; (semicolon)", ", (comma)", "Tab", "| (pipe)" });
        _csvDelimiter.SelectedIndex = 0;
        _csvEncoding = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = W(170) };
        _csvEncoding.Items.AddRange(new object[] { "Auto", "UTF-8", "Windows-1254 (Turkish)" });
        _csvEncoding.SelectedIndex = 0;
        _csvRow.Controls.AddRange(new Control[] { new Label { Text = "Delimiter", AutoSize = true, Margin = new Padding(0, W(6), W(4), 0) }, _csvDelimiter,
            new Label { Text = "Encoding", AutoSize = true, Margin = new Padding(W(16), W(6), W(4), 0) }, _csvEncoding });
        AddRow(top1, "CSV", _csvRow);
        _remember = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, W(4), 0, W(4)), Visible = false };
        _rememberText = new Label { AutoSize = true, ForeColor = Theme.Light.Ok, Margin = new Padding(0, W(7), W(10), 0) };
        _importNow = new Button { Text = "Import now", AutoSize = true, MinimumSize = new Size(W(100), W(28)) };
        _importNow.Click += (_, _) => Ui.Fire(this, ImportNowAsync);
        _remember.Controls.AddRange(new Control[] { _rememberText, _importNow });
        AddRow(top1, "", _remember);
        _sourceInfo = AddRow(top1, "", new Label { AutoSize = true, ForeColor = SystemColors.GrayText });
        _rawPreview = new ResultGrid { Dock = DockStyle.Fill };
        p1.Controls.Add(_rawPreview);
        _rawPreview.BringToFront();
        _sheet.SelectedIndexChanged += (_, _) => { if (!_loadingFile) Ui.Fire(this, LoadPreviewAsync); };
        _headerRow.CheckedChanged += (_, _) => { if (!_loadingFile) Ui.Fire(this, LoadPreviewAsync); };
        _csvDelimiter.SelectedIndexChanged += (_, _) => Ui.Fire(this, LoadPreviewAsync);
        _csvEncoding.SelectedIndexChanged += (_, _) => Ui.Fire(this, LoadPreviewAsync);
        _numTr.CheckedChanged += (_, _) => _mappingBuilt = false;

        // ---- page 2: target ---------------------------------------------------------------------
        var p2 = Page(1, "2  Target table");
        var top2 = Table(p2, 2);
        var existingRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        _existing = new RadioButton { Text = "Existing table", AutoSize = true, Checked = true, Margin = new Padding(0, W(4), W(8), 0) };
        _tables = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = W(320) };
        existingRow.Controls.AddRange(new Control[] { _existing, _tables });
        AddRow(top2, "", existingRow);
        var newRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        _newTable = new RadioButton { Text = "New table", AutoSize = true, Margin = new Padding(0, W(4), W(30), 0) };
        _newName = new TextBox { Width = W(320) };
        var reanalyze = new Button { Text = "Analyze again", AutoSize = true, Margin = new Padding(W(8), 0, 0, 0) };
        reanalyze.Click += (_, _) => Ui.Fire(this, AnalyzeAsync);
        newRow.Controls.AddRange(new Control[] { _newTable, _newName, reanalyze });
        AddRow(top2, "", newRow);
        _targetInfo = AddRow(top2, "", new Label { AutoSize = true, MaximumSize = new Size(W(880), 0), ForeColor = SystemColors.GrayText });
        _newColumns = NewGrid();
        _newColumns.Columns.Add(new DataGridViewCheckBoxColumn { Name = "inc", HeaderText = "Import", Width = W(55) });
        _newColumns.Columns.Add(new DataGridViewTextBoxColumn { Name = "src", HeaderText = "File column", ReadOnly = true, Width = W(200) });
        _newColumns.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "Column name", Width = W(200) });
        var typeCol = new DataGridViewComboBoxColumn { Name = "type", HeaderText = "Type (editable)", Width = W(170), FlatStyle = FlatStyle.Flat };
        typeCol.Items.AddRange("INT", "BIGINT", "TINYINT(1)", "DECIMAL(18,2)", "DECIMAL(20,4)", "DOUBLE", "DATE", "DATETIME", "TIME",
            "VARCHAR(20)", "VARCHAR(50)", "VARCHAR(100)", "VARCHAR(255)", "VARCHAR(1000)", "TEXT", "MEDIUMTEXT");
        _newColumns.Columns.Add(typeCol);
        _newColumns.Columns.Add(new DataGridViewTextBoxColumn { Name = "filled", HeaderText = "Filled cells", ReadOnly = true, Width = W(90) });
        _newColumns.Columns.Add(new DataGridViewTextBoxColumn { Name = "len", HeaderText = "Longest text", ReadOnly = true, Width = W(90) });
        _newColumns.DataError += (_, e) => e.ThrowException = false;
        _newColumns.EditingControlShowing += (_, e) =>
        {
            if (e.Control is ComboBox cb) cb.DropDownStyle = ComboBoxStyle.DropDown; // types can be typed in
        };
        _newColumns.CellValidating += (_, e) =>
        {
            if (e.ColumnIndex == 3 && e.FormattedValue is string v && v.Length > 0 && !typeCol.Items.Contains(v)) typeCol.Items.Add(v);
        };
        p2.Controls.Add(_newColumns);
        _newColumns.BringToFront();
        // the two radio buttons live in different rows (panels), so WinForms doesn't make them exclusive by itself
        _existing.CheckedChanged += (_, _) => { if (_existing.Checked) _newTable.Checked = false; OnTargetModeChanged(); };
        _newTable.CheckedChanged += (_, _) => { if (_newTable.Checked) _existing.Checked = false; };
        _tables.SelectedIndexChanged += (_, _) => { if (_existing.Checked) Ui.Fire(this, LoadTableInfoAsync); };

        // ---- page 3: mapping --------------------------------------------------------------------
        var p3 = Page(2, "3  Columns and import mode");
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        _mapGrid = NewGrid();
        _mapGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "src", HeaderText = "File column", ReadOnly = true, Width = W(220) });
        _mapGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "sample", HeaderText = "First value", ReadOnly = true, Width = W(230) });
        _mapGrid.Columns.Add(new DataGridViewComboBoxColumn { Name = "target", HeaderText = "Goes into column", Width = W(260), FlatStyle = FlatStyle.Flat });
        _mapGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "type", HeaderText = "Column type", ReadOnly = true, Width = W(150) });
        _mapGrid.DataError += (_, e) => e.ThrowException = false;
        _mapGrid.CurrentCellDirtyStateChanged += (_, _) => { if (_mapGrid.IsCurrentCellDirty) _mapGrid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _mapGrid.CellValueChanged += (_, e) => { if (e.ColumnIndex == 2 && e.RowIndex >= 0 && !_building) OnMappingChanged(); };
        var mapTop = new Panel { Dock = DockStyle.Fill };
        var modeBox = Table(mapTop, 2);
        var modes = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = Padding.Empty, MaximumSize = new Size(W(900), 0) };
        _mInsert = new RadioButton { Text = "Insert", AutoSize = true, Checked = true };
        _mReplace = new RadioButton { Text = "Truncate + Insert", AutoSize = true };
        _mUpsert = new RadioButton { Text = "Upsert (update rows with the same key)", AutoSize = true };
        _mIgnore = new RadioButton { Text = "Insert Ignore (skip rows whose key exists)", AutoSize = true };
        modes.Controls.AddRange(new Control[] { _mInsert, _mReplace, _mUpsert, _mIgnore });
        AddRow(modeBox, "Mode", modes);
        var opts = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = Padding.Empty, MaximumSize = new Size(W(900), 0) };
        _realTruncate = new CheckBox { Text = "Use real TRUNCATE (faster, but cannot be undone if the import fails)", AutoSize = true, Enabled = false };
        _continue = new CheckBox { Text = "Continue on error and log bad rows (otherwise the first bad row cancels everything)", AutoSize = true };
        opts.Controls.AddRange(new Control[] { _realTruncate, _continue });
        AddRow(modeBox, "Options", opts);
        _mapInfo = AddRow(modeBox, "", new Label { AutoSize = true, MaximumSize = new Size(W(900), 0) });
        var autoMatch = new Button { Text = "Auto-match", AutoSize = true };
        autoMatch.Click += (_, _) => { AutoMatch(); OnMappingChanged(); };
        var skipAll = new Button { Text = "Skip all", AutoSize = true };
        skipAll.Click += (_, _) => { foreach (DataGridViewRow r in _mapGrid.Rows) r.Cells[2].Value = Skip; OnMappingChanged(); };
        var mapButtons = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty };
        mapButtons.Controls.AddRange(new Control[] { autoMatch, skipAll });
        AddRow(modeBox, "", mapButtons);
        mapTop.Controls.Add(_mapGrid);
        _mapGrid.BringToFront();
        split.Panel1.Controls.Add(mapTop);
        _convPreview = new PreviewGrid { Dock = DockStyle.Fill };
        var previewLabel = new Label { Dock = DockStyle.Top, Height = W(20), Text = $"Preview: the first {PreviewRows} rows as they will be stored (⚠ = this row would fail)", ForeColor = SystemColors.GrayText };
        split.Panel2.Controls.Add(_convPreview);
        split.Panel2.Controls.Add(previewLabel);
        p3.Controls.Add(split);
        split.BringToFront();
        Shown += (_, _) => split.SplitterDistance = Math.Max(W(220), split.Height * 55 / 100);
        foreach (var rb in new[] { _mInsert, _mReplace, _mUpsert, _mIgnore })
            rb.CheckedChanged += (_, _) => _realTruncate.Enabled = _mReplace.Checked;

        // ---- page 4: run ------------------------------------------------------------------------
        var p4 = Page(3, "4  Import");
        var run = Table(p4, 2);
        _runSummary = AddRow(run, "", new Label { AutoSize = true, MaximumSize = new Size(W(880), 0) });
        _runBar = AddRow(run, "", new ProgressBar { Width = W(700), Height = W(18), Maximum = 1000 });
        _runCounters = AddRow(run, "", new Label { AutoSize = true, Font = Ui.Mono(9.5f) });
        _runResult = AddRow(run, "", new Label { AutoSize = true, MaximumSize = new Size(W(880), 0) });
        var after = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty };
        _saveFailed = new Button { Text = "Save failed rows…", AutoSize = true, Enabled = false };
        _saveFailed.Click += (_, _) => Ui.Fire(this, SaveFailedAsync);
        _openTable = new Button { Text = "Open table", AutoSize = true, Enabled = false };
        _openTable.Click += (_, _) => { if (TargetTable is not null) { _main.OpenTable(_session, _database, TargetTable); Close(); } };
        after.Controls.AddRange(new Control[] { _saveFailed, _openTable });
        AddRow(run, "", after);

        Controls.Add(_pages[0]);
        Controls.Add(_pages[1]);
        Controls.Add(_pages[2]);
        Controls.Add(_pages[3]);
        Controls.Add(_title);
        Controls.Add(buttons);
        FormClosing += (_, e) =>
        {
            if (_runCts is null) return;
            e.Cancel = true; // cancel first; the import rolls back, then the window can close
            _runCts.Cancel();
        };

        ShowPage(0);
        Load += (_, _) => Ui.Fire(this, () => InitAsync(table, file));
    }

    private int W(int logical) => LogicalToDeviceUnits(logical);

    private Panel Page(int index, string title)
    {
        var p = new Panel { Dock = DockStyle.Fill, Padding = new Padding(W(10), 0, W(10), 0), Tag = title, Visible = false };
        _pages[index] = p;
        return p;
    }

    private TableLayoutPanel Table(Control parent, int columns)
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = columns, Padding = new Padding(0, 0, 0, W(6)) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, W(120)));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        if (columns > 2) t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        parent.Controls.Add(t);
        return t;
    }

    private T AddRow<T>(TableLayoutPanel t, string label, T control, Control? extra = null) where T : Control
    {
        int r = t.RowCount++;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, W(7), W(6), 0) }, 0, r);
        control.Margin = new Padding(0, W(3), 0, W(3));
        t.Controls.Add(control, 1, r);
        if (extra is not null) t.Controls.Add(extra, 2, r);
        return control;
    }

    private DataGridView NewGrid() => new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.CellSelect, BorderStyle = BorderStyle.None,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None, EditMode = DataGridViewEditMode.EditOnEnter,
        RowTemplate = { Height = W(23) },
    };

    protected override void OnLoad(EventArgs e)
    {
        ThemeManager.Apply(this);
        base.OnLoad(e);
    }

    // ---- navigation ---------------------------------------------------------------------------

    private void ShowPage(int index)
    {
        if (index < 0 || index > 3) return;
        _page = index;
        for (int i = 0; i < 4; i++) _pages[i].Visible = i == index;
        _pages[index].BringToFront();
        _title.Text = (string)_pages[index].Tag!;
        _back.Enabled = index is 1 or 2;
        _next.Visible = index < 2;
        _import.Visible = index == 2;
        _close.Text = "Close";
        _status.Text = "";
    }

    private async Task NextAsync()
    {
        if (_page == 0)
        {
            if (_headers.Count == 0) { Ui.Info(this, "Choose a file with at least one column."); return; }
            ShowPage(1);
            if (_newTable.Checked && _inferred is null) await AnalyzeAsync();
        }
        else if (_page == 1)
        {
            if (!await PrepareTargetsAsync()) return;
            if (!_mappingBuilt) BuildMapping();
            ShowPage(2);
            OnMappingChanged();
        }
    }

    private async Task InitAsync(string? table, string? file)
    {
        var objects = await _session.Schema.GetObjectsAsync(_database);
        _tables.Items.AddRange(objects.Where(o => o.Type == DbObjectType.Table).Select(o => (object)o.Name).ToArray());
        if (table is not null && _tables.Items.Contains(table)) _tables.SelectedItem = table;
        else if (_tables.Items.Count > 0) _tables.SelectedIndex = 0;
        else _newTable.Checked = true;
        _launchTable = table;
        if (!string.IsNullOrEmpty(file)) await LoadFileAsync(file);
    }

    private string? _launchTable;
    private bool _loadingFile; // set while LoadFileAsync fills the controls

    // ---- page 1 -------------------------------------------------------------------------------

    private void Browse()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Excel and CSV (*.xlsx;*.xls;*.csv;*.txt)|*.xlsx;*.xls;*.csv;*.txt|All files (*.*)|*.*",
            InitialDirectory = File.Exists(_file.Text) ? Path.GetDirectoryName(_file.Text) : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) Ui.Fire(this, () => LoadFileAsync(dlg.FileName));
    }

    /// <summary>Reads sheets and the preview; applies a remembered mapping for this file if there is one.</summary>
    internal async Task LoadFileAsync(string path)
    {
        if (!File.Exists(path)) { Ui.Info(this, "File not found: " + path); return; }
        _file.Text = path;
        _inferred = null;
        _mappingBuilt = false;
        _loadingFile = true;
        try { await FillSourceControlsAsync(path); }
        finally { _loadingFile = false; }
        await LoadPreviewAsync();
        ShowRemembered();
    }

    private async Task FillSourceControlsAsync(string path)
    {
        var kind = ImportFile.KindOf(path);
        _csvRow.Enabled = kind == ImportFileKind.Csv;
        _remembered = MappingStore.Find(path, _database, _launchTable) ?? (_launchTable is null ? null : MappingStore.Find(path, _database));
        if (_remembered is not null && !_tables.Items.Contains(_remembered.Table)) _remembered = null;
        if (_remembered is not null)
        {
            _headerRow.Checked = _remembered.HeaderRow;
            (_remembered.Numbers == NumberFormat.Dot ? _numDot : _numTr).Checked = true;
        }
        _status.Text = "Reading sheets…";
        var sheets = await Task.Run(() => ImportFile.GetSheets(path));
        _sheet.BeginUpdate();
        _sheet.Items.Clear();
        _sheet.Items.AddRange(sheets.Cast<object>().ToArray());
        _sheet.EndUpdate();
        _sheet.Enabled = kind != ImportFileKind.Csv && sheets.Count > 1;
        int idx = _remembered?.Sheet is { } rs ? sheets.IndexOf(rs) : -1;
        _sheet.SelectedIndex = Math.Max(0, idx);
        _newName.Text = SuggestTableName(path);
    }

    private void ShowRemembered()
    {
        if (_remembered is not null)
        {
            _existing.Checked = true;
            _tables.SelectedItem = _remembered.Table;
            var mapped = _remembered.Columns.Count(kv => kv.Value is not null);
            _rememberText.Text = $"Remembered for this file: → {_remembered.Table}, {mapped} column(s), {ModeText(_remembered.Mode)} (last used {_remembered.SavedAt:yyyy-MM-dd HH:mm})";
            _remember.Visible = true;
        }
        else _remember.Visible = false;
    }

    private static string ModeText(ImportMode m) => m switch
    {
        ImportMode.Replace => "Truncate + Insert",
        ImportMode.Upsert => "Upsert",
        ImportMode.InsertIgnore => "Insert Ignore",
        _ => "Insert",
    };

    private static string SuggestTableName(string path)
    {
        var key = MappingStore.FileKey(path);
        var chars = key.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        var name = new string(chars).Trim('_');
        return name.Length == 0 ? "imported" : name.Length > 60 ? name[..60] : name;
    }

    private CsvReadOptions CsvOptions() => new()
    {
        Delimiter = _csvDelimiter.SelectedIndex switch { 1 => ';', 2 => ',', 3 => '\t', 4 => '|', _ => null },
        Encoding = _csvEncoding.SelectedIndex switch { 1 => "utf-8", 2 => "windows-1254", _ => null },
    };

    private string? SheetName => _sheet.SelectedItem as string is { } s && ImportFile.KindOf(_file.Text) != ImportFileKind.Csv ? s : null;

    private IImportReader OpenReader() => ImportFile.Open(_file.Text, SheetName, _headerRow.Checked, CsvOptions());

    private NumberFormat Numbers => _numDot.Checked ? NumberFormat.Dot : NumberFormat.Turkish;

    internal async Task LoadPreviewAsync()
    {
        var path = _file.Text;
        if (!File.Exists(path) || _sheet.SelectedIndex < 0) return;
        int version = ++_version;
        _status.Text = "Reading preview…";
        var (headers, rows, estimate) = await Task.Run(() =>
        {
            using var r = OpenReader();
            var list = new List<object?[]>();
            while (list.Count < PreviewRows && r.Read())
                if (!ImportFile.IsEmptyRow(r.Current)) list.Add(r.Current);
            long? est = ImportFile.CountRows(path, SheetName, _headerRow.Checked);
            return (r.Headers, list, est);
        });
        if (version != _version) return;
        _headers = headers;
        _rows = rows;
        _rowEstimate = estimate;
        _inferred = null;
        _mappingBuilt = false;
        var cols = headers.Select(h => new ColumnInfo { Name = h, Kind = ColumnKind.Text }).ToArray();
        var store = new RowStore();
        foreach (var r in rows) store.Add(r);
        _rawPreview.Bind(cols, store);
        _rawPreview.AutoFitColumns();
        _sourceInfo.Text = $"{headers.Count} column(s) · {(estimate is null ? "counting rows…" : $"{estimate:N0} rows")} · showing the first {rows.Count}";
        _status.Text = "";
        if (estimate is null) _ = CountInBackgroundAsync(version, headers.Count, rows.Count);
    }

    private CancellationTokenSource? _countCts;

    /// <summary>The exact row count (for the label and the progress bar) while the user sets up the import.</summary>
    private async Task CountInBackgroundAsync(int version, int columns, int shown)
    {
        _countCts?.Cancel();
        var cts = _countCts = new CancellationTokenSource();
        var path = _file.Text;
        var sheet = SheetName;
        bool header = _headerRow.Checked;
        var csv = CsvOptions();
        try
        {
            long n = await Task.Run(() => ImportFile.CountRowsByReading(path, sheet, header, csv, cts.Token), cts.Token);
            if (version != _version || IsDisposed) return;
            _rowEstimate = n;
            _sourceInfo.Text = $"{columns} column(s) · {n:N0} rows · showing the first {shown}";
        }
        catch (OperationCanceledException) { }
        catch (Exception) when (version != _version) { }
    }

    // ---- page 2 -------------------------------------------------------------------------------

    private void OnTargetModeChanged()
    {
        _tables.Enabled = _existing.Checked;
        _newName.Enabled = !_existing.Checked;
        _newColumns.Visible = _newTable.Checked;
        _mappingBuilt = false;
        if (_newTable.Checked && _page == 1 && _inferred is null) Ui.Fire(this, AnalyzeAsync);
        if (_existing.Checked) Ui.Fire(this, LoadTableInfoAsync);
    }

    private async Task LoadTableInfoAsync()
    {
        if (_tables.SelectedItem is not string table) return;
        _mappingBuilt = false;
        _tableInfo = await _session.Schema.GetTableAsync(_database, table);
        var status = (await _session.Schema.GetTableStatusAsync(_database)).FirstOrDefault(s => s.Name == table);
        _tableEngine = status?.Engine;
        var key = _tableInfo.KeyColumns;
        _targetInfo.ForeColor = ThemeManager.Current.GrayText;
        _targetInfo.Text = $"{_tableInfo.Columns.Count} columns · ≈{status?.Rows ?? 0:N0} rows · engine {_tableEngine}" +
                           (key is null ? " · no primary/unique key (Upsert not possible)" : $" · key: {string.Join(", ", key)}");
        if (_tableEngine is { } eng && !eng.Equals("InnoDB", StringComparison.OrdinalIgnoreCase))
        {
            _targetInfo.ForeColor = ThemeManager.Current.Error;
            _targetInfo.Text += $"\n{eng} tables are not transactional: a cancelled or failed import cannot be rolled back.";
        }
    }

    /// <summary>Infers column types from the whole sheet (up to 500k rows) for a new table.</summary>
    private async Task AnalyzeAsync()
    {
        _analyzeCts?.Cancel();
        var cts = _analyzeCts = new CancellationTokenSource();
        _newColumns.Rows.Clear();
        _targetInfo.ForeColor = ThemeManager.Current.GrayText;
        _targetInfo.Text = "Analyzing the data to suggest column types…";
        var nf = Numbers;
        try
        {
            var cols = await Task.Run(() =>
            {
                using var r = OpenReader();
                return TypeInference.Infer(r, nf, 500_000, cts.Token,
                    n => BeginInvoke(() => { if (!cts.IsCancellationRequested) _targetInfo.Text = $"Analyzing… {n:N0} rows"; }));
            }, cts.Token);
            if (cts.IsCancellationRequested) return;
            _inferred = cols;
            var combo = (DataGridViewComboBoxColumn)_newColumns.Columns[3];
            foreach (var type in cols.Select(c => c.SqlType).Distinct())
                if (!combo.Items.Contains(type)) combo.Items.Add(type); // a combo cell only accepts listed values
            foreach (var c in cols)
                _newColumns.Rows.Add(c.Include, c.Source, c.Name, c.SqlType, c.NonEmpty.ToString("N0"), c.MaxLength.ToString("N0"));
            _targetInfo.Text = "Suggested types (from the data, with some room to grow). Change names or types if you like; every column allows NULL.";
            _mappingBuilt = false;
        }
        catch (OperationCanceledException) { }
    }

    private async Task<bool> PrepareTargetsAsync()
    {
        if (_existing.Checked)
        {
            if (_tables.SelectedItem is not string) { Ui.Info(this, "Choose a table."); return false; }
            if (_tableInfo is null || _tableInfo.Name != (string)_tables.SelectedItem!) await LoadTableInfoAsync();
            _targets = _tableInfo!.Columns.Select(TargetColumn.From).ToList();
            TargetTable = _tableInfo.Name;
            return true;
        }
        var name = _newName.Text.Trim();
        if (name.Length == 0) { Ui.Info(this, "Enter a name for the new table."); return false; }
        if (_tables.Items.Cast<string>().Any(t => t.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            Ui.Info(this, $"A table named {name} already exists. Choose \"Existing table\" or another name.");
            return false;
        }
        if (_inferred is null) { await AnalyzeAsync(); if (_inferred is null) return false; }
        _newColumns.EndEdit();
        for (int i = 0; i < _inferred.Count; i++)
        {
            var row = _newColumns.Rows[i];
            _inferred[i].Include = row.Cells[0].Value is true;
            _inferred[i].Name = Convert.ToString(row.Cells[2].Value)?.Trim() ?? _inferred[i].Name;
            _inferred[i].SqlType = Convert.ToString(row.Cells[3].Value)?.Trim() ?? _inferred[i].SqlType;
        }
        try { ImportEngine.CreateTableSql(_database, name, _inferred); }
        catch (InvalidOperationException ex) { Ui.Info(this, ex.Message); return false; }
        _targets = _inferred.Where(c => c.Include).Select(c => TargetColumn.From(c.Name, c.SqlType)).ToList();
        TargetTable = name;
        _mappingBuilt = false;
        return true;
    }

    // ---- page 3 -------------------------------------------------------------------------------

    private bool _building; // BuildMapping fills cells; OnMappingChanged runs once at the end

    private void BuildMapping()
    {
        _building = true;
        try { BuildMappingCore(); }
        finally { _building = false; }
    }

    private void BuildMappingCore()
    {
        var combo = (DataGridViewComboBoxColumn)_mapGrid.Columns[2];
        combo.Items.Clear();
        combo.Items.Add(Skip);
        foreach (var t in _targets) combo.Items.Add(t.Name);
        combo.ReadOnly = _newTable.Checked;
        _mapGrid.Rows.Clear();
        for (int i = 0; i < _headers.Count; i++)
        {
            var sample = _rows.Select(r => i < r.Length ? r[i] : null).FirstOrDefault(v => v is not null);
            _mapGrid.Rows.Add(_headers[i], sample is null ? "" : ValueConverter.ToText(sample), Skip, "");
        }
        if (_newTable.Checked && _inferred is not null)
        {
            foreach (var c in _inferred)
                _mapGrid.Rows[c.SourceIndex].Cells[2].Value = c.Include ? c.Name.Trim() : Skip;
        }
        else if (_remembered is not null && _remembered.Table == TargetTable)
        {
            AutoMatch();
            for (int i = 0; i < _headers.Count; i++)
                if (_remembered.Columns.TryGetValue(_headers[i], out var t))
                    _mapGrid.Rows[i].Cells[2].Value = t is not null && _targets.Any(x => x.Name == t) ? t : Skip;
            (_remembered.Mode switch { ImportMode.Replace => _mReplace, ImportMode.Upsert => _mUpsert, ImportMode.InsertIgnore => _mIgnore, _ => _mInsert }).Checked = true;
            _realTruncate.Checked = _remembered.RealTruncate;
            _continue.Checked = _remembered.ContinueOnError;
        }
        else AutoMatch();
        foreach (var rb in new[] { _mReplace, _mUpsert, _mIgnore }) rb.Enabled = !_newTable.Checked;
        if (_newTable.Checked) _mInsert.Checked = true;
        _mappingBuilt = true;
    }

    private void AutoMatch()
    {
        var match = HeaderMatcher.Match(_headers, _targets.Select(t => t.Name).ToList());
        for (int i = 0; i < match.Length && i < _mapGrid.Rows.Count; i++)
            _mapGrid.Rows[i].Cells[2].Value = match[i] ?? Skip;
    }

    /// <summary>Source index → target for every mapped row.</summary>
    private List<ColumnMap> CurrentMap()
    {
        var list = new List<ColumnMap>();
        for (int i = 0; i < _mapGrid.Rows.Count; i++)
            if (_mapGrid.Rows[i].Cells[2].Value is string t && t != Skip && _targets.Any(x => x.Name == t)) list.Add(new ColumnMap(i, t));
        return list;
    }

    private void OnMappingChanged()
    {
        var map = CurrentMap();
        for (int i = 0; i < _mapGrid.Rows.Count; i++)
        {
            var t = _mapGrid.Rows[i].Cells[2].Value as string;
            _mapGrid.Rows[i].Cells[3].Value = _targets.FirstOrDefault(x => x.Name == t)?.Type ?? "";
        }
        var dup = map.GroupBy(m => m.Target).FirstOrDefault(g => g.Count() > 1);
        var warnings = new List<string>();
        if (dup is not null) warnings.Add($"Column {dup.Key} is chosen twice.");
        if (_existing.Checked && _tableInfo is not null)
        {
            var mapped = map.Select(m => m.Target).ToHashSet();
            var required = _tableInfo.Columns.Where(c => !c.Nullable && c.Default is null && !c.IsAutoIncrement && !mapped.Contains(c.Name)
                                                         && !c.Extra.Contains("GENERATED", StringComparison.OrdinalIgnoreCase)).Select(c => c.Name).ToList();
            if (required.Count > 0) warnings.Add("Required (NOT NULL, no default) but not mapped: " + string.Join(", ", required));
            var keyOk = _tableInfo.Indexes.Any(ix => ix.Unique && ix.Columns.All(mapped.Contains));
            _mUpsert.Enabled = _mIgnore.Enabled = keyOk;
            if (!keyOk && (_mUpsert.Checked || _mIgnore.Checked)) _mInsert.Checked = true;
        }
        int unmatched = map.Count == 0 ? _headers.Count : _headers.Count - map.Count;
        _mapInfo.ForeColor = warnings.Count > 0 ? ThemeManager.Current.Error : ThemeManager.Current.GrayText;
        _mapInfo.Text = warnings.Count > 0 ? string.Join("\n", warnings)
            : $"{map.Count} of {_headers.Count} file columns mapped{(unmatched > 0 ? $", {unmatched} skipped" : "")}. Empty cells become NULL, text is trimmed.";
        _import.Enabled = map.Count > 0 && dup is null;
        UpdateConvertedPreview(map);
    }

    private void UpdateConvertedPreview(List<ColumnMap> map)
    {

        var targets = map.Select(m => _targets.First(t => t.Name == m.Target)).ToList();
        var cols = targets.Select(t => new ColumnInfo { Name = t.Name, DataTypeName = t.Type, Kind = KindFor(t) }).ToArray();
        var store = new RowStore();
        var nf = Numbers;
        int bad = 0;
        foreach (var raw in _rows)
        {
            var row = new object?[map.Count];
            bool rowBad = false;
            for (int i = 0; i < map.Count; i++)
            {
                var v = ValueConverter.Convert(map[i].SourceIndex < raw.Length ? raw[map[i].SourceIndex] : null, targets[i], nf, true, out var err);
                row[i] = err is not null ? "⚠ " + err : v;
                rowBad |= err is not null;
            }
            if (rowBad) bad++;
            store.Add(row);
        }
        _convPreview.Bind(cols, store);
        _convPreview.AutoFitColumns();
        if (bad > 0) _status.Text = $"{bad} of the first {_rows.Count} rows would fail";
        else _status.Text = "";
    }

    private static ColumnKind KindFor(TargetColumn t) => t.Kind switch
    {
        TargetKind.Integer or TargetKind.Year => ColumnKind.Integer,
        TargetKind.Decimal => ColumnKind.Decimal,
        TargetKind.Float => ColumnKind.Float,
        TargetKind.Date => ColumnKind.Date,
        TargetKind.DateTime => ColumnKind.DateTime,
        TargetKind.Time => ColumnKind.Time,
        _ => ColumnKind.Text,
    };

    private ImportOptions Options() => new()
    {
        Mode = _mReplace.Checked ? ImportMode.Replace : _mUpsert.Checked ? ImportMode.Upsert : _mIgnore.Checked ? ImportMode.InsertIgnore : ImportMode.Insert,
        RealTruncate = _mReplace.Checked && _realTruncate.Checked,
        ContinueOnError = _continue.Checked,
        Numbers = Numbers,
        Trim = true,
    };

    // ---- run ----------------------------------------------------------------------------------

    /// <summary>The "2 clicks" path: remembered table, mapping and mode, straight to the import.</summary>
    internal async Task ImportNowAsync()
    {
        if (_remembered is null) return;
        _existing.Checked = true;
        _tables.SelectedItem = _remembered.Table;
        if (!await PrepareTargetsAsync()) return;
        _mappingBuilt = false;
        BuildMapping();
        ShowPage(2);
        OnMappingChanged();
        if (!_import.Enabled) { Ui.Info(this, "The remembered mapping does not fit this file any more; check the columns."); return; }
        await RunImportAsync();
    }

    internal async Task RunImportAsync()
    {
        if (_runCts is not null) return;
        _mapGrid.EndEdit();
        var map = CurrentMap();
        if (map.Count == 0) { Ui.Info(this, "Map at least one column."); return; }
        if (_mapInfo.ForeColor == ThemeManager.Current.Error && _mapInfo.Text.Length > 0) { Ui.Info(this, _mapInfo.Text); return; }
        var options = Options();
        var table = TargetTable!;
        if (_existing.Checked && _tableEngine is { } eng && !eng.Equals("InnoDB", StringComparison.OrdinalIgnoreCase)
            && !Ui.Confirm(this, $"{table} uses {eng}, which cannot roll back. If the import fails, rows already imported stay. Continue?", warning: true))
            return;
        if (options.Mode == ImportMode.Replace)
        {
            var what = options.RealTruncate ? "TRUNCATE (cannot be undone, even if the import fails)" : "delete (undone if the import fails)";
            if (!Ui.Confirm(this, $"All existing rows of {table} will be {what} before the import. Continue?", warning: true)) return;
        }

        string? created = null;
        if (_newTable.Checked)
        {
            var sql = ImportEngine.CreateTableSql(_database, table, _inferred!);
            await using (var conn = await _session.OpenAsync(_database))
            await using (var cmd = new MySqlCommand(sql, conn))
            {
                _session.EnsureAllowed(sql);
                await cmd.ExecuteNonQueryAsync();
            }
            created = table;
        }

        ShowPage(3);
        _back.Enabled = false;
        _close.Text = "Cancel";
        _saveFailed.Enabled = _openTable.Enabled = false;
        _runResult.Text = "";
        var file = Path.GetFileName(_file.Text);
        _runSummary.Text = $"{file}{(SheetName is { } sh ? $" [{sh}]" : "")} → {_database}.{table}{(created is null ? "" : " (new)")} · " +
                           $"{ModeText(options.Mode)} · {map.Count} column(s){(options.ContinueOnError ? " · continue on error" : "")}";
        var progress = new ImportProgress();
        _runCts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        using var timer = new System.Windows.Forms.Timer { Interval = 200 };
        timer.Tick += (_, _) => ShowCounters(progress, sw.Elapsed);
        timer.Start();
        _runBar.Style = _rowEstimate is > 0 || ImportFile.KindOf(_file.Text) != ImportFileKind.Xlsx ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
        ImportResult result;
        try
        {
            var path = _file.Text;
            var sheet = SheetName;
            bool header = _headerRow.Checked;
            var csv = CsvOptions();
            result = await ImportEngine.RunAsync(_session, _database, table, map, () => ImportFile.Open(path, sheet, header, csv), options, progress, _runCts.Token);
        }
        finally
        {
            timer.Stop();
            _runCts.Dispose();
            _runCts = null;
            _close.Text = "Close";
        }
        ShowCounters(progress, sw.Elapsed);
        Result = result;

        if (!result.Committed && created is not null)
        {
            // nothing was imported: remove the table this wizard created
            try
            {
                await using var conn = await _session.OpenAsync(_database);
                await using var cmd = new MySqlCommand($"DROP TABLE IF EXISTS {Ident.Quote(_database, created)}", conn);
                await cmd.ExecuteNonQueryAsync();
            }
            catch { /* best effort */ }
        }

        var t = ThemeManager.Current;
        if (result.Committed)
        {
            _runBar.Style = ProgressBarStyle.Continuous;
            _runBar.Value = _runBar.Maximum;
            _runBar.Value = _runBar.Maximum - 1; // stepping back skips the slow fill animation
            _runBar.Value = _runBar.Maximum;
            _runResult.ForeColor = result.Failed > 0 ? t.Error : t.Ok;
            _runResult.Text = $"Done in {CellFormat.FormatDuration(result.Elapsed)}: {result.Inserted:N0} inserted" +
                              (options.Mode == ImportMode.Upsert ? $", {result.Updated:N0} updated, {result.Unchanged:N0} unchanged" : "") +
                              (result.Skipped > 0 ? $", {result.Skipped:N0} skipped (key exists)" : "") +
                              $", {result.Failed:N0} failed" + (result.EmptyRows > 0 ? $" · {result.EmptyRows:N0} empty row(s) ignored" : "") +
                              (result.Failed > 0 ? "\nThe failed rows were not imported — save them, fix and import that file again." : "");
            SaveMapping(map, options, table);
            _openTable.Enabled = true;
            _main.RefreshAfterImport(_session, _database, table);
        }
        else
        {
            _runResult.ForeColor = result.Cancelled ? t.GrayText : t.Error;
            _runResult.Text = (result.Error ?? "Stopped.") + (created is not null ? $"\nThe new table {created} was removed again." : "");
        }
        _saveFailed.Enabled = result.FailedRows.Count > 0;
        _back.Enabled = true;
    }

    private void ShowCounters(ImportProgress p, TimeSpan elapsed)
    {
        double fraction = p.Fraction;
        if (fraction < 0 && _rowEstimate is > 0) fraction = p.Read / (double)_rowEstimate.Value;
        if (fraction >= 0 && _runBar.Style == ProgressBarStyle.Continuous) _runBar.Value = (int)Math.Clamp(fraction * _runBar.Maximum, 0, _runBar.Maximum);
        var rate = elapsed.TotalSeconds > 0.5 ? $" · {p.Read / elapsed.TotalSeconds:N0} rows/s" : "";
        _runCounters.Text = $"{p.Phase,-24} read {p.Read,10:N0}{(_rowEstimate is { } e ? $" / {e:N0}" : "")}\n" +
                            $"inserted {p.Inserted,10:N0}   updated {p.Updated,8:N0}   unchanged {p.Unchanged,8:N0}   skipped {p.Skipped,8:N0}   failed {p.Failed,6:N0}\n" +
                            $"{CellFormat.FormatDuration(elapsed)}{rate}";
    }

    private void SaveMapping(List<ColumnMap> map, ImportOptions o, string table)
    {
        try
        {
            var entry = new SavedImport
            {
                FileKey = MappingStore.FileKey(_file.Text),
                Database = _database,
                Table = table,
                Sheet = SheetName,
                HeaderRow = _headerRow.Checked,
                Mode = o.Mode,
                RealTruncate = o.RealTruncate,
                ContinueOnError = o.ContinueOnError,
                Numbers = o.Numbers,
            };
            for (int i = 0; i < _headers.Count; i++)
                entry.Columns[_headers[i]] = map.FirstOrDefault(m => m.SourceIndex == i)?.Target;
            MappingStore.Save(entry);
        }
        catch (IOException) { /* remembering is a convenience */ }
    }

    private async Task SaveFailedAsync()
    {
        if (Result is null || Result.FailedRows.Count == 0) return;
        var suggested = Path.Combine(Path.GetDirectoryName(_file.Text) ?? "", Path.GetFileNameWithoutExtension(_file.Text) + "_failed.xlsx");
        using var dlg = new SaveFileDialog { Filter = "Excel workbook (*.xlsx)|*.xlsx", FileName = Path.GetFileName(suggested), InitialDirectory = Path.GetDirectoryName(suggested) };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        await SaveFailedToAsync(dlg.FileName);
    }

    internal async Task SaveFailedToAsync(string path)
    {
        await FailedRowsExporter.SaveAsync(path, Result!);
        _status.Text = $"Saved {Result!.FailedRows.Count:N0} failed row(s) to {Path.GetFileName(path)}";
        if (AppSettings.Current.OpenAfterExport) Ui.OpenPath(path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _analyzeCts?.Cancel();
            _countCts?.Cancel();
            _runCts?.Cancel();
        }
        base.Dispose(disposing);
    }
}
