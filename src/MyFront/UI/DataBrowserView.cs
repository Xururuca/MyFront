using System.Globalization;
using System.Text;
using MyFront.Core.Db;
using MySqlConnector;

namespace MyFront.UI;

/// <summary>
/// Data Browser: one page of a table (LIMIT/OFFSET), server-side sort, WHERE filter and quick search.
/// Edits post when you leave the row, by primary/unique key, always with parameters.
/// </summary>
internal sealed class DataBrowserView : UserControl
{
    private readonly ResultGrid _grid;
    private readonly ToolStripTextBox _offset, _limit, _filter, _search;
    private readonly ToolStripButton _add, _delete, _post, _revert;
    private readonly ToolStripLabel _info;
    private readonly System.Windows.Forms.Timer _searchDelay = new() { Interval = 450 };

    private ServerSession? _session;
    private string? _database, _table;
    private TableInfo? _tableInfo;
    private ColumnInfo[] _columns = Array.Empty<ColumnInfo>();
    private EditRowSource _source = new();
    private readonly GridEditor _edit;
    /// <summary>Where edits go and how a row is found; null while the page is read-only.</summary>
    private EditTarget? _target;
    /// <summary>Columns a row is found by: the key, or (<see cref="_allValues"/>) every column that compares exactly.</summary>
    private int[] _keyIndexes = Array.Empty<int>();
    private bool _editable, _binding;
    /// <summary>The table has no primary / unique key: rows are matched by all their original values.</summary>
    private bool _allValues;
    private string? _sortColumn;
    private bool _sortDesc;
    private int _version;
    private long? _total;
    private CancellationTokenSource? _countCts;
    private int _loadsRunning;

    public event Action? StatusChanged;
    public bool IsLoading => _loadsRunning > 0;
    public string? StatusRows { get; private set; }
    public string? StatusTime { get; private set; }
    public bool IsOpen => _table is not null;
    public string? Table => _table;
    public string? Database => _database;
    public ServerSession? Session => _session;
    internal ResultGrid Grid => _grid;
    internal EditRowSource Source => _source;

    public DataBrowserView()
    {
        var bar = Ui.NewToolStrip(this);
        Ui.Button(bar, "Refresh", Icons.Refresh, Icons.Green, (_, _) => Ui.Fire(this, RefreshAsync), "Refresh (F5)");
        bar.Items.Add(new ToolStripButton("Export", Icons.Excel(Icons.Size(this)), (_, _) => RequestExport("xlsx"))
        {
            DisplayStyle = ToolStripItemDisplayStyle.Image,
            ImageScaling = ToolStripItemImageScaling.None,
            ToolTipText = "Export the whole table (with the current filter and sort) to Excel / CSV…",
        });
        bar.Items.Add(new ToolStripSeparator());
        Ui.Button(bar, "First page", Icons.First, Icons.Gray, (_, _) => Page(int.MinValue));
        Ui.Button(bar, "Previous page", Icons.Prev, Icons.Gray, (_, _) => Page(-1));
        bar.Items.Add(new ToolStripLabel("Offset"));
        _offset = new ToolStripTextBox { Text = "0", Width = LogicalToDeviceUnits(64), TextBoxTextAlign = HorizontalAlignment.Right };
        bar.Items.Add(_offset);
        bar.Items.Add(new ToolStripLabel("Limit"));
        _limit = new ToolStripTextBox { Text = "1000", Width = LogicalToDeviceUnits(64), TextBoxTextAlign = HorizontalAlignment.Right };
        bar.Items.Add(_limit);
        Ui.Button(bar, "Next page", Icons.Next, Icons.Gray, (_, _) => Page(1));
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add(new ToolStripLabel("Filter") { ToolTipText = "WHERE expression, e.g.  city = 'İzmir' AND avg_price > 100" });
        _filter = new ToolStripTextBox { Width = LogicalToDeviceUnits(260), ToolTipText = "WHERE expression — press Enter to apply" };
        bar.Items.Add(_filter);
        bar.Items.Add(new ToolStripLabel("Search"));
        _search = new ToolStripTextBox { Width = LogicalToDeviceUnits(140), ToolTipText = "Quick search in all text/number columns" };
        bar.Items.Add(_search);
        bar.Items.Add(new ToolStripSeparator());
        _add = Ui.Button(bar, "Add row", Icons.Add, Icons.Green, (_, _) => AddRow());
        _delete = Ui.Button(bar, "Delete rows", Icons.Delete, Icons.Red, (_, _) => Ui.Fire(this, DeleteSelectedAsync));
        _post = Ui.Button(bar, "Post changes", Icons.Accept, Icons.Blue, (_, _) => Ui.Fire(this, PostAllAsync), "Post changed rows");
        _revert = Ui.Button(bar, "Cancel changes", Icons.Cancel, Icons.Gray, (_, _) => RevertCurrent(), "Undo changes in the current row (Esc)");
        // table / range / key info lives in a bottom bar so the toolbar never overflows
        var bottom = Ui.NewToolStrip(this);
        bottom.Dock = DockStyle.Bottom;
        _info = new ToolStripLabel("") { ForeColor = SystemColors.GrayText };
        bottom.Items.Add(_info);

        foreach (var tb in new[] { _offset, _limit, _filter })
            tb.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Ui.Fire(this, () => LoadPageAsync()); } };
        _search.TextChanged += (_, _) => { _searchDelay.Stop(); _searchDelay.Start(); };
        _search.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _searchDelay.Stop(); Ui.Fire(this, () => LoadPageAsync(resetOffset: true)); } };
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); Ui.Fire(this, () => LoadPageAsync(resetOffset: true)); };

        _grid = new ResultGrid { Dock = DockStyle.Fill };
        _grid.SortRequested += (col, desc) =>
        {
            if (col >= _columns.Length) return;
            _sortColumn = _columns[col].Name;
            _sortDesc = desc;
            Ui.Fire(this, () => LoadPageAsync(resetOffset: true));
        };
        _grid.ExportRequested += RequestExport;
        _grid.StatusMessage += OnStatus;
        // Delete on cells sets them to NULL; only rows picked by their headers are deleted (after asking)
        _edit = new GridEditor(_grid, () => _source, () => _binding) { Save = PostRowAsync, DeleteRows = DeleteSelectedAsync };
        _edit.NewRowReverted += RemoveNewRow;
        var menu = _grid.ContextMenuStrip!;
        menu.Items.Add(new ToolStripSeparator());
        var setNull = ResultGrid.MenuItem("Set NULL", "Del", (_, _) => SetSelectedNull());
        var undo = ResultGrid.MenuItem("Undo", "Ctrl+Z", (_, _) => _edit.Undo());
        var addRow = ResultGrid.MenuItem("Add row", null, (_, _) => AddRow());
        var deleteRows = ResultGrid.MenuItem("Delete selected rows…", null, (_, _) => Ui.Fire(this, DeleteSelectedAsync));
        menu.Items.AddRange(new ToolStripItem[] { setNull, undo, addRow, deleteRows });
        menu.Opening += (_, _) =>
        {
            setNull.Enabled = addRow.Enabled = deleteRows.Enabled = _editable;
            undo.Enabled = _editable && _edit.CanUndo;
        };

        Controls.Add(_grid);
        Controls.Add(bar);
        Controls.Add(bottom);
        UpdateButtons();
    }

    // ---- opening & paging ---------------------------------------------------------------------

    public bool HasPendingChanges => _source.Rows.Any(r => r.State is EditState.Dirty or EditState.Error or EditState.New);

    /// <summary>Asks before throwing away unsaved edits. False = stay.</summary>
    public bool ConfirmLeave()
    {
        if (!HasPendingChanges) return true;
        return Ui.Confirm(FindForm(), "The Data Browser has unsaved row changes. Discard them?", warning: true);
    }

    public async Task OpenAsync(ServerSession session, string database, string table)
    {
        if (_session == session && _database == database && _table == table) return;
        if (!ConfirmLeave()) return;
        _loadsRunning++; // covers the gap until LoadPageAsync starts
        _source = new EditRowSource();
        _session = session;
        _database = database;
        _table = table;
        _tableInfo = null;
        _sortColumn = null;
        _sortDesc = false;
        _filter.Text = "";
        _search.Text = "";
        _searchDelay.Stop();
        _offset.Text = "0";
        _columns = Array.Empty<ColumnInfo>();
        try { await LoadPageAsync(); }
        finally { _loadsRunning--; }
    }

    public void Close()
    {
        _version++;
        _countCts?.Cancel();
        _session = null;
        _database = _table = null;
        _binding = true;
        _grid.Clear();
        _binding = false;
        _source = new EditRowSource();
        _info.Text = "";
        StatusRows = StatusTime = null;
        UpdateButtons();
        StatusChanged?.Invoke();
    }

    public async Task RefreshAsync()
    {
        if (_session is null || _database is null || _table is null) return;
        _session.Schema.Invalidate(_database, _table);
        _tableInfo = null;
        _columns = Array.Empty<ColumnInfo>();
        await LoadPageAsync();
    }

    private void Page(int direction)
    {
        int limit = ParseInt(_limit.Text, 1000);
        int offset = ParseInt(_offset.Text, 0);
        offset = direction == int.MinValue ? 0 : Math.Max(0, offset + direction * limit);
        if (direction > 0 && _total is { } total && offset >= total) return;
        _offset.Text = offset.ToString(CultureInfo.InvariantCulture);
        Ui.Fire(this, () => LoadPageAsync());
    }

    private static int ParseInt(string s, int fallback) =>
        int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : fallback;

    public async Task LoadPageAsync(bool resetOffset = false)
    {
        if (_session is null || _database is null || _table is null) return;
        if (!ConfirmLeave()) return;
        _loadsRunning++;
        try { await LoadPageCoreAsync(resetOffset); }
        finally { _loadsRunning--; }
    }

    private async Task LoadPageCoreAsync(bool resetOffset)
    {
        var session = _session!;
        var db = _database!;
        var table = _table!;
        int version = ++_version;
        _countCts?.Cancel();
        if (resetOffset) _offset.Text = "0";
        int limit = Math.Clamp(ParseInt(_limit.Text, 1000), 1, 10_000_000);
        int offset = ParseInt(_offset.Text, 0);
        _limit.Text = limit.ToString(CultureInfo.InvariantCulture);
        _info.Text = "Loading…";

        _tableInfo ??= await session.Schema.GetTableAsync(db, table);
        var info = _tableInfo;
        if (version != _version) return;

        var where = BuildWhere(info, out var parameters);
        var sql = new StringBuilder("SELECT * FROM ").Append(Ident.Quote(db, table)).Append(where).Append(OrderBy(info));
        sql.Append(" LIMIT @__limit OFFSET @__offset");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rows = new EditRowSource();
        var sqlText = sql.ToString();
        var columns = await Task.Run(async () =>
        {
            await using var conn = await session.OpenAsync(db);
            await using var cmd = new MySqlCommand(sqlText, conn);
            foreach (var p in parameters) cmd.Parameters.Add(p);
            cmd.Parameters.AddWithValue("@__limit", limit);
            cmd.Parameters.AddWithValue("@__offset", offset);
            await using var reader = await cmd.ExecuteReaderAsync();
            var cols = ColumnInfo.FromReader(reader);
            int n = reader.FieldCount;
            var interner = new ValueInterner(n);
            while (reader.Read())
            {
                var v = new object?[n];
                reader.GetValues(v!);
                for (int i = 0; i < n; i++) v[i] = interner.Intern(i, CellFormat.Normalize(v[i]));
                rows.Rows.Add(new EditRow(v));
            }
            return cols;
        });
        if (version != _version) return;
        var elapsed = sw.Elapsed;

        bool sameColumns = _columns.Length == columns.Length && _columns.Select(c => c.Name).SequenceEqual(columns.Select(c => c.Name));
        _binding = true;
        try
        {
            _source = rows;
            _columns = columns;
            var widths = sameColumns ? _grid.Columns.Cast<DataGridViewColumn>().Select(c => c.Width).ToArray() : null;
            _grid.Bind(columns, rows);
            if (widths is not null)
                for (int i = 0; i < widths.Length; i++) _grid.Columns[i].Width = widths[i];
            else
                _grid.AutoFitColumns();
            RestoreSortGlyph();
        }
        finally { _binding = false; }

        ConfigureEditing(session, info);
        StatusRows = $"{rows.Count:N0} rows";
        StatusTime = CellFormat.FormatDuration(elapsed);
        _total = null;
        UpdateInfo(offset);
        StatusChanged?.Invoke();
        _ = CountAsync(session, db, table, info, version, offset);
    }

    private string OrderBy(TableInfo info)
    {
        if (_sortColumn is not null)
            return " ORDER BY " + Ident.Quote(_sortColumn) + (_sortDesc ? " DESC" : " ASC");
        if (info.Indexes.FirstOrDefault(i => i.Name == "PRIMARY") is { } pk)
            return " ORDER BY " + string.Join(", ", pk.Columns.Select(Ident.Quote));
        return "";
    }

    /// <summary>Raised with a ready export request; the main window shows the dialog.</summary>
    public event Action<Dialogs.ExportRequest>? ExportRequested;

    /// <summary>The whole table (not just this page) with the current filter, search and sort, streamed.</summary>
    internal Dialogs.ExportRequest? BuildExportRequest(string format)
    {
        if (_session is null || _database is null || _table is null || _tableInfo is null) return null;
        var where = BuildWhere(_tableInfo, out var parameters);
        var sql = $"SELECT * FROM {Ident.Quote(_database, _table)}{where}{OrderBy(_tableInfo)}";
        var filtered = where.Length > 0;
        var table = Core.Export.ExportTable.FromQuery(_session, _database, _table, sql,
            parameters.Select(p => (p.ParameterName, p.Value)).ToList(), _total);
        return new Dialogs.ExportRequest
        {
            Title = $"Table {_database}.{_table}{(filtered ? " — rows matching the current filter / search" : " — all rows")}" +
                    (_total is null ? "" : $" ({_total:N0})"),
            BaseName = _table,
            Items = new() { new Dialogs.ExportItem(table, _tableInfo.IsView, true) },
            Format = format,
        };
    }

    private void RequestExport(string format)
    {
        if (BuildExportRequest(format) is { } request) ExportRequested?.Invoke(request);
    }

    private void RestoreSortGlyph()
    {
        int idx = _sortColumn is null ? -1 : Array.FindIndex(_columns, c => c.Name == _sortColumn);
        _grid.SetSortGlyph(idx, _sortDesc);
    }

    private string BuildWhere(TableInfo info, out List<MySqlParameter> parameters)
    {
        parameters = new List<MySqlParameter>();
        var parts = new List<string>();
        var filter = _filter.Text.Trim();
        if (filter.Length > 0)
        {
            if (SqlClassifier.Tokens(filter).Contains(";"))
                throw new InvalidOperationException("The filter must be a single WHERE expression (no ';').");
            parts.Add("(" + filter + ")");
        }
        var search = _search.Text.Trim();
        if (search.Length > 0)
        {
            var cols = info.Columns.Where(c => !c.IsBinary).Select(c => Ident.Quote(c.Name) + " LIKE @__search").ToList();
            if (cols.Count > 0)
            {
                parts.Add("(" + string.Join(" OR ", cols) + ")");
                parameters.Add(new MySqlParameter("@__search", "%" + Ident.EscapeLike(search) + "%"));
            }
        }
        return parts.Count == 0 ? "" : " WHERE " + string.Join(" AND ", parts);
    }

    private async Task CountAsync(ServerSession session, string db, string table, TableInfo info, int version, int offset)
    {
        var cts = new CancellationTokenSource();
        _countCts = cts;
        try
        {
            var where = BuildWhere(info, out var parameters);
            await using var conn = await session.OpenAsync(db, cts.Token);
            await using var cmd = new MySqlCommand($"SELECT COUNT(*) FROM {Ident.Quote(db, table)}{where}", conn);
            foreach (var p in parameters) cmd.Parameters.Add(p);
            var total = Convert.ToInt64(await cmd.ExecuteScalarAsync(cts.Token), CultureInfo.InvariantCulture);
            if (version != _version) return;
            _total = total;
            UpdateInfo(offset);
        }
        catch { /* the count is informational only */ }
    }

    private void UpdateInfo(int offset)
    {
        int n = _source.Count;
        var range = n == 0 ? "no rows" : $"rows {offset + 1:N0}–{offset + n:N0}";
        var total = _total is null ? "" : $" of {_total:N0}";
        string mode = !_editable ? ReadOnlyReason
            : _allValues ? "no key: rows are matched by all their values" + (_keyIndexes.Length < _columns.Length ? " (FLOAT / DOUBLE / JSON / spatial left out)" : "")
            : $"key: {string.Join(", ", _keyIndexes.Select(i => _columns[i].Name))}";
        _info.Text = $"{_database}.{_table} · {range}{total} · {mode}";
    }

    private string ReadOnlyReason =>
        _session?.ReadOnly == true ? "read-only connection"
        : _tableInfo?.IsView == true || _tableInfo is null ? "read-only"
        : "read-only (no columns to find a row by)";

    private void ConfigureEditing(ServerSession session, TableInfo info)
    {
        var key = info.KeyColumns;
        _allValues = key is null;
        _keyIndexes = key is not null
            ? key.Select(k => Array.FindIndex(_columns, c => c.Name == k)).ToArray()
            : Enumerable.Range(0, _columns.Length)
                .Where(i => info.Columns.FirstOrDefault(c => c.Name == _columns[i].Name) is { } col && RowMatch.CanMatchBy(col.Type)).ToArray();
        _editable = !session.ReadOnly && !info.IsView && _keyIndexes.Length > 0 && _keyIndexes.All(i => i >= 0);
        _target = _editable ? new EditTarget(_database!, _table!, _columns.Select(c => (string?)c.Name).ToArray(), _keyIndexes, _allValues) : null;
        _edit.Enabled = _editable;
        _edit.ReadOnlyReason = ReadOnlyReason;
        if (_editable)
        {
            for (int i = 0; i < _columns.Length; i++)
            {
                var col = info.Columns.FirstOrDefault(c => c.Name == _columns[i].Name);
                _grid.Columns[i].ReadOnly = col is null || col.IsBinary;
            }
        }
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _add.Enabled = _delete.Enabled = _post.Enabled = _revert.Enabled = _editable;
    }

    // ---- editing (typing, Set NULL, Ctrl+Z, Esc and the row marks: GridEditor) ----------------

    private void SetSelectedNull() => _edit.SetSelectedNull();

    private void RevertCurrent() => _edit.RevertCurrent();

    /// <summary>Esc on a new row that was never saved: it goes away.</summary>
    private void RemoveNewRow(int index)
    {
        _binding = true;
        try
        {
            _source.Rows.RemoveAt(index);
            _grid.RowCount = _source.Count;
        }
        finally { _binding = false; }
    }

    private void AddRow()
    {
        if (!_editable) return;
        _grid.EndEdit();
        _source.Rows.Add(new EditRow(new object?[_columns.Length], isNew: true));
        _grid.SyncRowCount();
        int r = _source.Count - 1;
        int c = Enumerable.Range(0, _columns.Length).FirstOrDefault(i => !_grid.Columns[i].ReadOnly && !IsAutoIncrement(i));
        _grid.CurrentCell = _grid[c, r];
        _grid.BeginEdit(true);
    }

    private bool IsAutoIncrement(int column) =>
        _tableInfo?.Columns.FirstOrDefault(c => c.Name == _columns[column].Name)?.IsAutoIncrement == true;

    public async Task PostAllAsync()
    {
        _grid.EndEdit();
        foreach (var row in _source.Rows.Where(r => r.State is EditState.Dirty or EditState.Error || (r.State == EditState.New && r.Values.Any(v => v is not null))).ToList())
            await PostRowAsync(row, showError: true);
    }

    /// <summary>
    /// INSERT of a new row, or UPDATE of a changed one by its key as loaded (<see cref="RowUpdate"/>); then the row as
    /// stored (defaults, ON UPDATE columns, the server's formatting). False when it failed: the row stays marked.
    /// </summary>
    private async Task<bool> PostRowAsync(EditRow row, bool showError)
    {
        if (row.State == EditState.Saving || _session is null || _tableInfo is null || _target is null) return false;
        var session = _session;
        var target = _target;
        var db = _database!;
        var table = _table!;
        var stateBefore = row.State;
        row.State = EditState.Saving;
        _edit.Repaint(row);
        bool reload = false;
        try
        {
            object?[] stored;
            if (row.IsNew)
            {
                var cols = Enumerable.Range(0, _columns.Length).Where(i => row.Values[i] is not null).ToList();
                var sql = $"INSERT INTO {target.QuotedTable} ({string.Join(", ", cols.Select(i => Ident.Quote(_columns[i].Name)))}) " +
                          $"VALUES ({string.Join(", ", cols.Select(i => "@v" + i))})";
                session.EnsureAllowed(sql);
                await using var conn = await session.OpenAsync(db);
                var probe = (object?[])row.Values.Clone();
                await using (var cmd = new MySqlCommand(sql, conn))
                {
                    foreach (var i in cols) cmd.Parameters.AddWithValue("@v" + i, row.Values[i]);
                    await cmd.ExecuteNonQueryAsync();
                    foreach (var i in _keyIndexes) probe[i] ??= IsAutoIncrement(i) ? cmd.LastInsertedId : null;
                }
                // without a key the new row is found by its values: with fields left to their defaults, reload the page instead
                reload = _allValues && _keyIndexes.Any(i => row.Values[i] is null);
                stored = reload ? row.Values : await RowUpdate.ReadBackAsync(conn, target, probe);
            }
            else
            {
                var original = row.Original ?? row.Values;
                var updated = await RowUpdate.UpdateAsync(session, target, original, row.Values);
                if (updated is null)
                {
                    if (!_allValues)
                        throw new InvalidOperationException("Row not found. It may have been changed or deleted by someone else — refresh the page.");
                    // no row has these values any more: drop the edit and show what is there now
                    Array.Copy(original, row.Values, row.Values.Length);
                    row.Original = null;
                    row.State = EditState.Clean;
                    _edit.Repaint(row);
                    Ui.Info(FindForm(), "No row with these values was found: it was changed or deleted by someone else. The page is reloaded.");
                    await LoadPageAsync();
                    return false;
                }
                stored = updated;
            }
            if (stored != row.Values) Array.Copy(stored, row.Values, row.Values.Length);
            if (row.IsNew && _total is not null) _total++;
            row.IsNew = false;
            row.Original = null;
            row.Error = null;
            row.State = EditState.Clean;
            UpdateInfo(ParseInt(_offset.Text, 0));
            OnStatus($"Saved row in {db}.{table}");
        }
        catch (Exception ex)
        {
            row.State = EditState.Error;
            row.Error = Ui.Describe(ex);
            if (stateBefore == EditState.New) row.IsNew = true;
            OnStatus("Error: " + row.Error);
            _edit.Repaint(row);
            if (showError) Ui.Error(FindForm(), ex, "Could not save the row");
            return false;
        }
        _edit.Repaint(row);
        if (reload) await LoadPageAsync();
        return true;
    }

    private List<int> SelectedRowIndexes()
    {
        var set = new SortedSet<int>();
        int r = _grid.Rows.GetFirstRow(DataGridViewElementStates.Selected);
        while (r >= 0) { set.Add(r); r = _grid.Rows.GetNextRow(r, DataGridViewElementStates.Selected); }
        foreach (DataGridViewCell c in _grid.SelectedCells) set.Add(c.RowIndex);
        if (set.Count == 0 && _grid.CurrentCell is not null) set.Add(_grid.CurrentCell.RowIndex);
        return set.Where(i => i < _source.Count).ToList();
    }

    /// <summary>
    /// The rows of the selection (whole rows picked by their headers, or cells in several rows, else the current row):
    /// one confirmation, then DELETE … LIMIT 1 per row by its key, all in one transaction. Toolbar or menu, or the
    /// Delete key with whole rows selected (on cells it sets NULL).
    /// </summary>
    public async Task DeleteSelectedAsync()
    {
        if (_session is null) return;
        if (!_editable)
        {
            OnStatus("Rows can't be deleted here: " + ReadOnlyReason);
            return;
        }
        _grid.EndEdit();
        var indexes = SelectedRowIndexes();
        if (indexes.Count == 0) return;
        var session = _session;
        var db = _database!;
        var table = _table!;
        var rows = indexes.Select(i => _source.Rows[i]).ToList();
        var persisted = rows.Where(r => !r.IsNew).ToList();
        var target = _target!;
        var sql = $"DELETE FROM {target.QuotedTable} WHERE {target.KeyWhere()} LIMIT 1";
        int n = indexes.Count;
        var question = $"Delete {n:N0} {(n == 1 ? "row" : "rows")} from {db}.{table}?";
        if (persisted.Count > 0) question += $"\n\n{sql}\n(once per row with that row's values, all in one transaction)";
        if (!Ui.Confirm(FindForm(), question, "Delete rows", warning: true)) return;

        int deleted = 0;
        if (persisted.Count > 0)
        {
            session.EnsureAllowed(sql);
            var keys = persisted.Select(r => r.Original ?? r.Values).ToList();
            deleted = await Task.Run(async () =>
            {
                int count = 0;
                await using var conn = await session.OpenAsync(db);
                await using var tx = await conn.BeginTransactionAsync();
                foreach (var key in keys)
                {
                    await using var cmd = new MySqlCommand(sql, conn, tx);
                    target.AddKeyParameters(cmd, key);
                    count += await cmd.ExecuteNonQueryAsync();
                }
                await tx.CommitAsync();
                return count;
            });
        }
        _binding = true;
        try
        {
            _grid.ClearSelection();
            foreach (var row in rows) _source.Rows.Remove(row); // by reference: the page may have reloaded meanwhile
            _grid.RowCount = _source.Count;
            _grid.Invalidate();
        }
        finally { _binding = false; }
        if (_total is not null) _total -= deleted;
        UpdateInfo(ParseInt(_offset.Text, 0));
        OnStatus($"Deleted {deleted + (rows.Count - persisted.Count):N0} {(n == 1 ? "row" : "rows")}");
        if (deleted < persisted.Count)
        {
            // pooled connections report matched rows: a row that wasn't there was changed or deleted by someone else
            Ui.Info(FindForm(), $"{persisted.Count - deleted:N0} of the rows weren't found any more (changed or deleted by someone else). The page is reloaded.");
            await LoadPageAsync();
        }
    }

    private void OnStatus(string text)
    {
        StatusTime = text;
        StatusChanged?.Invoke();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _searchDelay.Dispose();
            _countCts?.Cancel();
        }
        base.Dispose(disposing);
    }
}
