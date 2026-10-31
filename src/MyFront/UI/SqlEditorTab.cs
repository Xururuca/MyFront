using System.Data;
using System.Text;
using MyFront.Core;
using MyFront.Core.Db;
using MyFront.Core.Editors;
using MyFront.Core.Export;
using MyFront.Core.Quick;
using MyFront.UI.Dialogs;
using MySqlConnector;

namespace MyFront.UI;

/// <summary>All statements; the selection (all statements when nothing is selected); the statement at the cursor.</summary>
internal enum RunScope { All, Selection, Current }

/// <summary>SQL editor with its own dedicated connection and a result tab per result set.</summary>
internal sealed class SqlEditorTab : TabPage, IQueryListener
{
    private readonly MainForm _main;
    private readonly SqlEditor _editor;
    private readonly TabControl _results;
    private readonly TabPage _messagesPage;
    private readonly ListView _messages;
    private readonly ToolStripButton _run, _explain, _format;
    private readonly ToolStripLabel _state;
    private readonly SplitContainer _split;
    /// <summary>Red ✕ left of the result tabs: closes the results, the editor gets the full height.</summary>
    private readonly Label _closeResults;
    private TabPage? _explainPage;
    private bool _closeAfterRun;
    /// <summary>The last statement run here that EXPLAIN can take (with its {{parameter}} values): what Explain explains.</summary>
    private (string Sql, List<(string Name, object? Value)>? Parameters)? _lastExplainable;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 150 };
    /// <summary>Autosave shortly after typing stops (the app also saves on close).</summary>
    private readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = 700 };
    private bool _dirty;
    private readonly List<ResultTab> _resultTabs = new();

    private MySqlConnection? _conn;
    private string? _connDatabase;
    private CancellationTokenSource? _cts;
    private int _offsetBase;
    private string? _filePath;
    private DateTime _runStarted;

    public ServerSession Session { get; }
    internal MainForm Main => _main;
    public bool IsRunning => _cts is not null;
    public string BaseTitle { get; set; } = "SQL Editor";
    /// <summary>Autosave slot of this connection: 1 … 5 fixed, 6+ added; 0 = not saved (quick query tabs).</summary>
    public int Slot { get; init; }
    public bool IsFixed => Slot is >= 1 and <= EditorStore.FixedTabs;
    public string DefaultTitle => Slot > 0 ? $"SQL Editor {Slot}" : "SQL Editor";
    internal string? FilePath => _filePath;
    public string? StatusRows { get; private set; }
    public string? StatusTime { get; private set; }
    public event Action? StatusChanged;
    internal SqlEditor Editor => _editor;
    internal IReadOnlyList<ResultTab> ResultTabs => _resultTabs;
    internal ListView Messages => _messages;
    internal ResultTab? CurrentResult => _results.SelectedTab as ResultTab;

    public SqlEditorTab(MainForm main, ServerSession session)
    {
        _main = main;
        Session = session;
        Text = BaseTitle;
        UseVisualStyleBackColor = true;

        ScintillaLoader.Ensure();
        _editor = new SqlEditor { Dock = DockStyle.Fill };

        // just Run and Explain here (and Format on the right); Run all / current, Stop, Open, Save, History and Export query are in the main toolbar
        var bar = Ui.NewToolStrip(main);
        _run = Ui.Button(bar, "Run", Icons.PlaySolid, Icons.Green, (_, _) => Run(RunScope.Selection),
            "Run the selected text, or all statements when nothing is selected (F9)", showText: true);
        _explain = Ui.Button(bar, "Explain", Icons.Search, Icons.Blue, (_, _) => Ui.Fire(this, ExplainAsync),
            "Explain the last query run here (or the one at the cursor): which indexes it uses, full scans, sorting", showText: true);
        bar.Items.Add(new ToolStripSeparator());
        _state = new ToolStripLabel("Ready") { ForeColor = SystemColors.GrayText };
        bar.Items.Add(_state);
        _format = Ui.Button(bar, "Format", Icons.Format, Icons.Blue, (_, _) => FormatSql(),
            "Format the SQL so it reads easily — the selection, or everything when nothing is selected (Ctrl+Shift+F)", showText: true);
        _format.Alignment = ToolStripItemAlignment.Right;

        _messages = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            BorderStyle = BorderStyle.None,
            ShowItemToolTips = true,
        };
        _messages.Columns.Add("#", main.LogicalToDeviceUnits(36));
        _messages.Columns.Add("Statement", main.LogicalToDeviceUnits(460));
        _messages.Columns.Add("Result", main.LogicalToDeviceUnits(360));
        _messages.Columns.Add("Time", main.LogicalToDeviceUnits(80));
        _messages.DoubleClick += (_, _) =>
        {
            if (_messages.SelectedItems.Count == 1 && _messages.SelectedItems[0].Tag is SqlStatement s)
            {
                _editor.SetSelection(s.End + _offsetBase, s.Start + _offsetBase);
                _editor.ScrollCaret();
                _editor.Focus();
            }
        };
        _messagesPage = new TabPage("Messages") { UseVisualStyleBackColor = true };
        _messagesPage.Controls.Add(_messages);

        _results = new ThemedTabControl { Dock = DockStyle.Fill };
        _results.TabPages.Add(_messagesPage);
        _results.SelectedIndexChanged += (_, _) => UpdateStatus();

        var split = _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = main.LogicalToDeviceUnits(5),
            BorderStyle = BorderStyle.None,
        };
        int closeSize = main.LogicalToDeviceUnits(22);
        _closeResults = new Label
        {
            Text = "✕", AutoSize = false, Size = new Size(closeSize, closeSize), Dock = DockStyle.Top, TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = ThemeManager.Current.Error, Font = new Font(Font.FontFamily, 10.5f, FontStyle.Bold), Cursor = Cursors.Hand,
        };
        new ToolTip().SetToolTip(_closeResults, "Close the results (the editor gets the full height)");
        _closeResults.Click += (_, _) => CloseResults();
        var closeGutter = new Panel { Dock = DockStyle.Left, Width = closeSize + main.LogicalToDeviceUnits(2) };
        closeGutter.Controls.Add(_closeResults);
        split.Panel1.Controls.Add(_editor);
        split.Panel2.Controls.Add(_results);
        split.Panel2.Controls.Add(closeGutter);
        Controls.Add(split);
        Controls.Add(bar);
        HandleCreated += (_, _) => split.SplitterDistance = Math.Max(main.LogicalToDeviceUnits(120), split.Height * 2 / 5);

        _timer.Tick += (_, _) => RefreshResults();
        _editor.TextChanged += (_, _) =>
        {
            _editor.ClearError();
            if (Slot <= 0) return;
            _dirty = true;
            _saveTimer.Stop();
            _saveTimer.Start();
        };
        _saveTimer.Tick += (_, _) => SaveNow();
    }

    // ---- format ---------------------------------------------------------------------------------

    /// <summary>Formats the selection, or all of it when nothing is selected, as one undo step; the caret stays at the same code.</summary>
    public void FormatSql()
    {
        bool selection = _editor.SelectionStart != _editor.SelectionEnd;
        int start = selection ? _editor.SelectionStart : 0;
        int end = selection ? _editor.SelectionEnd : _editor.TextLength;
        var text = selection ? _editor.SelectedText : _editor.Text;
        var formatted = SqlFormatter.Format(text, _editor.EolMode == ScintillaNET.Eol.Lf ? "\n" : "\r\n");
        if (formatted == text)
        {
            SetState(text.Trim().Length == 0 ? "Nothing to format" : "Already formatted");
            return;
        }
        int caret = SqlFormatter.MapPosition(text, formatted, Math.Clamp(_editor.CurrentPosition - start, 0, text.Length));
        int firstLine = _editor.FirstVisibleLine;
        _editor.BeginUndoAction();
        _editor.TargetStart = start;
        _editor.TargetEnd = end;
        _editor.ReplaceTarget(formatted);
        _editor.EndUndoAction();
        if (selection) _editor.SetSelection(start + formatted.Length, start);
        else
        {
            _editor.GotoPosition(caret);
            _editor.FirstVisibleLine = Math.Min(firstLine, _editor.LineFromPosition(caret));
        }
        SetState(selection ? "Formatted the selection" : "Formatted");
    }

    // ---- autosave ------------------------------------------------------------------------------

    /// <summary>Writes the text to its slot if it changed. Never asks anything.</summary>
    public void SaveNow()
    {
        _saveTimer.Stop();
        if (Slot <= 0 || !_dirty || IsDisposed) return;
        try
        {
            EditorStore.WriteText(Session.Account.Id, Slot, _editor.Text);
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetState("Autosave failed: " + ex.Message);
        }
    }

    public EditorTabState State => new()
    {
        Slot = Slot, FilePath = _filePath, Caret = _editor.CurrentPosition, FirstLine = _editor.FirstVisibleLine,
    };

    /// <summary>Puts back a saved tab: text, file title, caret and scroll position.</summary>
    public void Restore(EditorTabState state, string text)
    {
        _editor.Text = text;
        _editor.EmptyUndoBuffer();
        _editor.SetSavePoint();
        _dirty = false;
        _saveTimer.Stop();
        _filePath = state.FilePath;
        BaseTitle = _filePath is null ? DefaultTitle : Path.GetFileName(_filePath);
        var caret = Math.Clamp(state.Caret, 0, _editor.TextLength);
        _editor.SetEmptySelection(caret);
        _editor.FirstVisibleLine = Math.Max(0, state.FirstLine);
    }

    // ---- running -------------------------------------------------------------------------------

    public void Run(RunScope scope) => Ui.Fire(this, () => RunAsync(scope));

    public async Task RunAsync(RunScope scope)
    {
        if (IsRunning) return;
        var text = _editor.Text;
        List<SqlStatement> statements;
        _offsetBase = 0;
        switch (scope)
        {
            case RunScope.Selection when _editor.SelectionEnd > _editor.SelectionStart:
                _offsetBase = _editor.SelectionStart;
                statements = SqlSplitter.Split(_editor.SelectedText);
                break;
            case RunScope.Current:
            {
                var all = SqlSplitter.Split(text);
                var s = SqlSplitter.StatementAt(all, _editor.CurrentPosition);
                statements = s is null ? new() : new() { s.Value };
                break;
            }
            default:
                statements = SqlSplitter.Split(text);
                break;
        }
        if (statements.Count == 0)
        {
            SetState("Nothing to run");
            return;
        }

        // {{placeholders}} → ask, then bind as real parameters (offsets stay those of the editor text)
        List<(string Name, object? Value)>? parameters = null;
        if (ParameterParser.HasPlaceholders(string.Join("\n", statements.Select(s => s.Text))))
        {
            var values = ParameterDialog.Ask(FindForm(), BaseTitle, string.Join(";\n", statements.Select(s => s.Text)));
            if (values is null) { SetState("Cancelled"); return; }
            var binder = new ParameterParser.ParameterBinder(values);
            try { statements = statements.Select(s => s with { Text = binder.Bind(s.Text) }).ToList(); }
            catch (ParameterException ex) { Ui.Info(FindForm(), ex.Message); return; }
            parameters = binder.Parameters;
        }

        if (!ConfirmDiscardEdits()) return;
        if (statements.LastOrDefault(st => ExplainAnalyzer.CanExplain(st.Text)) is { Text.Length: > 0 } last)
            _lastExplainable = (last.Text, parameters);
        _cts = new CancellationTokenSource();
        SetRunning(true);
        ClearResults();
        _runStarted = DateTime.Now;
        SetState($"Running {statements.Count} statement(s)…");
        _timer.Start();
        List<StatementOutcome>? outcomes = null;
        string? startDatabase = null;
        try
        {
            var conn = await EnsureConnectionAsync(_cts.Token);
            startDatabase = _connDatabase;
            outcomes = await QueryExecutor.RunAsync(Session, conn, statements, this, _cts.Token, _connDatabase, parameters);
            DrainPending();

            if (statements.Any(s => SqlClassifier.FirstKeyword(s.Text) == "USE") && conn.State == ConnectionState.Open)
            {
                _connDatabase = await QueryExecutor.CurrentDatabaseAsync(conn);
                if (_connDatabase is not null) _main.SelectDatabase(Session, _connDatabase);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddMessage(null, "", "Error: " + Ui.Describe(ex), "", isError: true);
            _results.SelectedTab = _messagesPage;
        }
        finally
        {
            _timer.Stop();
            DrainPending();
            RefreshResults();
            if (_conn is not null && _conn.State != ConnectionState.Open)
            {
                // the server closed the connection; reconnect on the next run
                await _conn.DisposeAsync();
                _conn = null;
            }
            _cts.Dispose();
            _cts = null;
            SetRunning(false);
        }
        if (_closeAfterRun)
        {
            _closeAfterRun = false;
            CloseResults();
            return;
        }

        var total = DateTime.Now - _runStarted;
        if (outcomes is not null)
        {
            var failed = outcomes.FirstOrDefault(o => o.Error is not null);
            var cancelled = outcomes.Any(o => o.Cancelled);
            SetState(failed is not null ? $"Error in statement {failed.Index + 1}"
                : cancelled ? "Cancelled"
                : $"{outcomes.Count} statement(s) · {CellFormat.FormatDuration(total)}");
            if (failed is not null)
            {
                _results.SelectedTab = _messagesPage;
                _editor.MarkError(failed.Statement.Start + _offsetBase, failed.Statement.End + _offsetBase);
            }
            else if (_resultTabs.Count > 0)
            {
                _results.SelectedTab = _resultTabs[0];
            }
            var ddl = outcomes.Where(o => o.Error is null && !o.Cancelled && IsDdl(o.Statement.Text)).ToList();
            if (ddl.Count > 0)
            {
                bool serverLevel = ddl.Any(o => SqlClassifier.Tokens(o.Statement.Text).Skip(1).Take(2).Any(w => w is "DATABASE" or "SCHEMA"));
                _main.RefreshAfterDdl(Session, serverLevel ? null : startDatabase);
            }
        }
        UpdateStatus();
    }

    /// <summary>Rows typed into a result grid and not saved yet (or that failed to save).</summary>
    public bool HasPendingEdits => _resultTabs.Any(t => t.HasPendingEdits);

    /// <summary>Asks before the result grids go away with unsaved row changes. False = keep them.</summary>
    public bool ConfirmDiscardEdits() =>
        !HasPendingEdits || Ui.Confirm(FindForm(), $"{BaseTitle} has unsaved row changes in its results. Discard them?", warning: true);

    private static bool IsDdl(string sql) =>
        SqlClassifier.FirstKeyword(sql) is "CREATE" or "DROP" or "ALTER" or "RENAME" or "TRUNCATE";

    /// <summary>Quick query tabs: the button this tab belongs to, and its fixed database.</summary>
    public string? QuickQueryId { get; set; }
    /// <summary>The connection's one page for quick query buttons: each click shows its query here instead of opening a tab.</summary>
    public bool IsQuickPage { get; set; }
    public string? PinnedDatabase { get; set; }

    public void SetSql(string sql)
    {
        _editor.Text = sql;
        _editor.EmptyUndoBuffer();
    }

    private async Task<MySqlConnection> EnsureConnectionAsync(CancellationToken ct)
    {
        var target = PinnedDatabase ?? _main.CurrentDatabaseFor(Session);
        if (_conn is null || _conn.State != ConnectionState.Open)
        {
            if (_conn is not null) await _conn.DisposeAsync();
            _conn = await Session.OpenDedicatedAsync(target, ct);
            _connDatabase = target;
        }
        else if (!string.IsNullOrEmpty(target) && target != _connDatabase)
        {
            await _conn.ChangeDatabaseAsync(target, ct);
            _connDatabase = target;
        }
        return _conn;
    }

    /// <summary>Streams the statement under the cursor (or the selection) to a file without loading it into a grid.</summary>
    public void ExportQuery()
    {
        string? sql = _editor.SelectionEnd > _editor.SelectionStart
            ? SqlSplitter.Split(_editor.SelectedText).Select(s => s.Text).FirstOrDefault()
            : SqlSplitter.StatementAt(SqlSplitter.Split(_editor.Text), _editor.CurrentPosition)?.Text;
        if (string.IsNullOrWhiteSpace(sql))
        {
            Ui.Info(FindForm(), "Put the cursor in a SELECT statement (or select one) first.");
            return;
        }
        if (!SqlClassifier.IsReadOnlySafe(sql))
        {
            Ui.Info(FindForm(), "Only statements that just read data (SELECT, SHOW, WITH …) can be exported.");
            return;
        }
        var preview = sql.Length > 160 ? sql[..160] + "…" : sql;
        var values = ParameterDialog.Ask(FindForm(), BaseTitle, sql);
        if (values is null) return;
        List<(string Name, object? Value)> parameters;
        try { (sql, parameters) = ParameterParser.Bind(sql, values); }
        catch (ParameterException ex) { Ui.Info(FindForm(), ex.Message); return; }
        _main.ShowExport(new ExportRequest
        {
            Title = "Query, streamed from the server (all rows, not limited by the grid):\n" + preview.Replace("\r", ""),
            BaseName = QuickQueryId is null ? "query" : BaseTitle.TrimStart('⚡', ' '),
            Items = new() { new ExportItem(ExportTable.FromQuery(Session, PinnedDatabase ?? _main.CurrentDatabaseFor(Session), "query", sql, parameters), false, true) },
        });
    }

    public void Stop()
    {
        if (_cts is null) return;
        SetState("Cancelling…");
        _cts.Cancel();
    }

    private void SetRunning(bool running)
    {
        _run.Enabled = _explain.Enabled = !running;
        UpdateStatus(); // e.g. "running…" while Explain put its tab in front
        _main.OnEditorRunningChanged(this);
    }

    internal bool ResultsClosed => _split.Panel2Collapsed;

    /// <summary>The red ✕: results go away and the editor takes the whole height (a running query is cancelled first).</summary>
    public void CloseResults()
    {
        if (IsRunning)
        {
            _closeAfterRun = true;
            Stop();
            return;
        }
        ClearResults();
        _split.Panel2Collapsed = true;
        SetState("Results closed");
        UpdateStatus();
    }

    private void ShowResultsArea() => _split.Panel2Collapsed = false;

    // ---- explain ---------------------------------------------------------------------------------

    /// <summary>
    /// EXPLAIN (never runs the query) of the last query run in this tab, or of the statement at the cursor before anything
    /// ran: an "Explain" tab with plain findings (indexes used, full scans, filesort, temporary tables) above EXPLAIN's rows.
    /// </summary>
    public async Task ExplainAsync()
    {
        if (IsRunning) return;
        var target = _lastExplainable;
        if (target is null)
        {
            var here = _editor.SelectionEnd > _editor.SelectionStart
                ? SqlSplitter.Split(_editor.SelectedText).Select(x => (SqlStatement?)x).FirstOrDefault()
                : SqlSplitter.StatementAt(SqlSplitter.Split(_editor.Text), _editor.CurrentPosition);
            if (here is { } st && ExplainAnalyzer.CanExplain(st.Text) && !ParameterParser.HasPlaceholders(st.Text)) target = (st.Text, null);
        }
        if (target is not { } t)
        {
            SetState("Explain: run a SELECT / UPDATE / DELETE / INSERT first, or put the cursor in one");
            return;
        }
        var sql = "EXPLAIN " + t.Sql;
        Session.EnsureAllowed(sql);
        _cts = new CancellationTokenSource();
        SetRunning(true);
        SetState("Explaining…");
        try
        {
            var ct = _cts.Token;
            var conn = await EnsureConnectionAsync(ct); // on the UI thread like Run: it reads which database is selected
            var rows = await Task.Run(async () =>
            {
                await using var cmd = new MySqlCommand(sql, conn);
                if (t.Parameters is { } ps) foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                return await ExplainAnalyzer.ReadAsync(r, ct);
            }, ct);
            ShowExplain(t.Sql, rows, ExplainAnalyzer.Findings(rows));
            var real = rows.Where(x => !string.IsNullOrEmpty(x.Table) && !x.Table.StartsWith('<')).ToList();
            SetState($"Explain: {real.Count} table(s), {real.Count(x => string.Equals(x.Type, "ALL", StringComparison.OrdinalIgnoreCase))} full scan(s)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetState("Explain failed: " + Ui.Describe(ex).Replace('\n', ' '));
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetRunning(false);
        }
    }

    /// <summary>The findings of the last Explain (self-test).</summary>
    internal IReadOnlyList<string> ExplainFindings { get; private set; } = Array.Empty<string>();
    internal TabPage? ExplainPage => _explainPage;

    private void ShowExplain(string sql, IReadOnlyList<ExplainRow> rows, List<string> findings)
    {
        ExplainFindings = findings;
        if (_explainPage is not null)
        {
            _results.TabPages.Remove(_explainPage);
            _explainPage.Dispose();
        }
        var page = new TabPage("Explain") { UseVisualStyleBackColor = true };
        var text = new TextBox
        {
            Dock = DockStyle.Top, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, WordWrap = true,
            ScrollBars = ScrollBars.Vertical, Font = Font,
            Height = _main.LogicalToDeviceUnits(18) * Math.Min(8, findings.Count + 2) + _main.LogicalToDeviceUnits(6),
            Text = string.Join(Environment.NewLine, findings) + Environment.NewLine + Environment.NewLine +
                   "EXPLAIN " + (sql.Length > 400 ? sql[..400] + "…" : sql).Replace("\r", "").Replace("\n", " "),
        };
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, BorderStyle = BorderStyle.None, ShowItemToolTips = true };
        foreach (var (title, width) in new[] { ("id", 36), ("select_type", 100), ("table", 140), ("type", 70), ("possible_keys", 160),
                     ("key", 130), ("key_len", 60), ("ref", 120), ("rows", 80), ("Extra", 300) })
            list.Columns.Add(title, _main.LogicalToDeviceUnits(width));
        foreach (var r in rows)
        {
            var item = new ListViewItem(new[] { r.Id ?? "", r.SelectType ?? "", r.Table ?? "", r.Type ?? "", r.PossibleKeys ?? "",
                r.Key ?? "", r.KeyLen ?? "", r.Ref ?? "", r.Rows?.ToString("N0") ?? "", r.Extra ?? "" });
            if (string.Equals(r.Type, "ALL", StringComparison.OrdinalIgnoreCase)) item.ForeColor = ThemeManager.Current.Error;
            list.Items.Add(item);
        }
        page.Controls.Add(list);
        page.Controls.Add(text);
        ThemeManager.Apply(page);
        _explainPage = page;
        _results.TabPages.Insert(_results.TabPages.IndexOf(_messagesPage), page);
        ShowResultsArea();
        _results.SelectedTab = page;
    }

    private void SetState(string text) => _state.Text = text;

    // ---- IQueryListener (worker thread) ----------------------------------------------------------
    // Updates go through one queue: BeginInvoke and await continuations use different WinForms
    // callback lists, so their order is not guaranteed. RunAsync drains the queue before it returns.

    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _pending = new();

    private void Enqueue(Action update)
    {
        _pending.Enqueue(update);
        if (IsHandleCreated) BeginInvoke(DrainPending);
    }

    private void DrainPending()
    {
        while (_pending.TryDequeue(out var update)) update();
    }

    void IQueryListener.ResultSetStarted(ResultSetData rs) => Enqueue(() => AddResultTab(rs));

    void IQueryListener.StatementFinished(StatementOutcome o) => Enqueue(() =>
    {
        string result = o.Error is not null ? Ui.Describe(o.Error)
            : o.Cancelled ? "Cancelled"
            : o.ResultSets > 0 ? $"{o.RowsReturned:N0} row(s) returned"
            : $"{Math.Max(0, o.RecordsAffected):N0} row(s) affected";
        AddMessage(o.Statement, (o.Index + 1).ToString(), result, CellFormat.FormatDuration(o.Elapsed), o.Error is not null);
    });

    private void AddMessage(SqlStatement? stmt, string index, string result, string time, bool isError)
    {
        var text = stmt?.Text ?? "";
        var oneLine = text.Length > 300 ? text[..300] : text;
        oneLine = oneLine.Replace("\r", " ").Replace("\n", " ");
        var item = new ListViewItem(new[] { index, oneLine, result, time }) { Tag = stmt, ToolTipText = result };
        if (isError) item.ForeColor = ThemeManager.Current.Error;
        _messages.Items.Add(item);
        item.EnsureVisible();
    }

    // ---- result tabs ---------------------------------------------------------------------------

    private void AddResultTab(ResultSetData rs)
    {
        var tab = new ResultTab(this, rs, _resultTabs.Count + 1);
        ThemeManager.Apply(tab);
        _resultTabs.Add(tab);
        _results.TabPages.Insert(_results.TabPages.Count - 1, tab);
        if (_resultTabs.Count == 1) _results.SelectedTab = tab;
        tab.Refresh(force: true);
    }

    private void RefreshResults()
    {
        foreach (var t in _resultTabs) t.Refresh(force: false);
        UpdateStatus();
    }

    private void ClearResults()
    {
        ShowResultsArea();
        foreach (var t in _resultTabs)
        {
            _results.TabPages.Remove(t);
            t.Dispose();
        }
        _resultTabs.Clear();
        if (_explainPage is not null)
        {
            _results.TabPages.Remove(_explainPage);
            _explainPage.Dispose();
            _explainPage = null;
        }
        _messages.Items.Clear();
        _editor.ClearError();
    }

    internal void UpdateStatus()
    {
        if (_results.SelectedTab is ResultTab rt)
        {
            StatusRows = $"{rt.Data.Rows.Count:N0} rows";
            StatusTime = rt.Data.Completed
                ? $"exec {CellFormat.FormatDuration(rt.Data.ExecTime)} · fetch {CellFormat.FormatDuration(rt.Data.FetchTime)}"
                : "fetching…";
        }
        else
        {
            StatusRows = null;
            StatusTime = IsRunning ? "running…" : null;
        }
        StatusChanged?.Invoke();
    }

    // ---- files ---------------------------------------------------------------------------------

    public void OpenFileDialog() => _main.OpenSqlFile(this);

    public void LoadFile(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > 20 * 1024 * 1024 &&
            !Ui.Confirm(this, $"{Path.GetFileName(path)} is {CellFormat.FormatBytes(info.Length)}. Large files run faster with File → Run SQL file. Open in the editor anyway?"))
            return;
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        _editor.Text = reader.ReadToEnd();
        _editor.EmptyUndoBuffer();
        _editor.SetSavePoint();
        _filePath = path;
        BaseTitle = Path.GetFileName(path);
        _main.UpdateTabTitle(this);
    }

    public void Save(bool saveAs)
    {
        if (_filePath is null || saveAs)
        {
            using var dlg = new SaveFileDialog { Filter = "SQL files (*.sql)|*.sql|All files (*.*)|*.*", FileName = _filePath ?? "query.sql" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _filePath = dlg.FileName;
        }
        File.WriteAllText(_filePath, _editor.Text, new UTF8Encoding(false));
        _editor.SetSavePoint();
        BaseTitle = Path.GetFileName(_filePath);
        _main.UpdateTabTitle(this);
    }

    public void InsertText(string sql)
    {
        _editor.ReplaceSelection(sql);
        _editor.Focus();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts?.Cancel();
            _timer.Dispose();
            _saveTimer.Dispose();
            _conn?.Dispose();
            _conn = null;
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// One result set: a virtual grid over the streaming row store. When all rows are there it becomes editable if its
/// columns come from one table and include a way to find each row (<see cref="ResultEdit"/>); otherwise the info line
/// says why it stays read-only.
/// </summary>
internal sealed class ResultTab : TabPage
{
    private readonly SqlEditorTab _owner;
    private readonly int _number;
    private readonly ToolStripLabel _info;
    private readonly ResultEditRows _rows;
    private readonly GridEditor _edit;
    private bool _fitted, _refitted, _planning;
    /// <summary>How (or whether) the grid can be edited; null until all rows are loaded.</summary>
    private ResultEditPlan? _plan;
    /// <summary>The last thing that happened in the grid (saved, error, copied …), after the info line.</summary>
    private string? _status;

    public ResultSetData Data { get; }
    public ResultGrid Grid { get; }
    internal GridEditor Editor => _edit;
    internal ResultEditRows EditRows => _rows;
    internal ResultEditPlan? EditPlan => _plan;
    /// <summary>Completes when the editability check has run (self-test).</summary>
    internal Task EditPlanned => _planned.Task;
    private readonly TaskCompletionSource _planned = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool HasPendingEdits => _rows.HasPendingChanges;

    public ResultTab(SqlEditorTab owner, ResultSetData data, int number)
    {
        _owner = owner;
        _number = number;
        Data = data;
        Text = $"Result {number}";
        UseVisualStyleBackColor = true;

        _rows = new ResultEditRows(data.Rows);
        Grid = new ResultGrid { Dock = DockStyle.Fill };
        Grid.Bind(data.Columns, _rows);
        Grid.SortRequested += (col, desc) => Ui.Fire(this, () => SortAsync(col, desc));
        Grid.ExportRequested += format => ShowExportDialog(format);
        Grid.StatusMessage += SetStatus;
        _edit = new GridEditor(Grid, () => _rows) { Save = SaveRowAsync, ReadOnlyReason = "read-only until all rows are loaded" };
        var menu = Grid.ContextMenuStrip!;
        var nullSeparator = new ToolStripSeparator();
        var setNull = ResultGrid.MenuItem("Set NULL", "Del", (_, _) => _edit.SetSelectedNull());
        var undo = ResultGrid.MenuItem("Undo", "Ctrl+Z", (_, _) => _edit.Undo());
        menu.Items.AddRange(new ToolStripItem[] { nullSeparator, setNull, undo });
        menu.Opening += (_, _) =>
        {
            nullSeparator.Visible = setNull.Visible = undo.Visible = _edit.Enabled;
            undo.Enabled = _edit.CanUndo;
        };

        var bar = Ui.NewToolStrip(owner);
        bar.Dock = DockStyle.Bottom;
        _info = new ToolStripLabel("");
        bar.Items.Add(_info);
        _exportDialog = Ui.Button(bar, "Export…", Icons.Save, Icons.Gray, (_, _) => ShowExportDialog("xlsx"), "Export this result to Excel or CSV…");
        _exportDialog.Alignment = ToolStripItemAlignment.Right;
        _excel = new ToolStripButton("Excel", Icons.Excel(Icons.Size(owner)), (_, _) => Ui.Fire(this, QuickExportAsync))
        {
            Alignment = ToolStripItemAlignment.Right,
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            ImageScaling = ToolStripItemImageScaling.None,
            ToolTipText = "Export to Excel in one click (saved to the export folder)",
        };
        bar.Items.Add(_excel);
        Controls.Add(Grid);
        Controls.Add(bar);
    }

    private readonly ToolStripButton _excel, _exportDialog;
    private CancellationTokenSource? _exportCts;

    /// <summary>Path of the last one-click export (shown in the status, used by the self-test).</summary>
    internal string? LastExportPath { get; private set; }

    private string ExportName()
    {
        var tables = Data.Columns.Select(c => c.BaseTable).Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList();
        return tables.Count == 1 ? tables[0]! : $"Result {_number}";
    }

    private ExportTable ExportSource() =>
        ExportTable.FromResult(ExportName(), Data.Columns, Data.Rows, Grid.DisplayColumnOrder());

    internal void ShowExportDialogFor(string format) => ShowExportDialog(format);

    private void ShowExportDialog(string format)
    {
        if (!Data.Completed) { Ui.Info(FindForm(), "Wait until all rows are loaded (or stop the query) before exporting."); return; }
        _owner.Main.ShowExport(new ExportRequest
        {
            Title = $"{Text}: {Data.Rows.Count:N0} rows × {Data.Columns.Length} columns (as shown in the grid)",
            BaseName = ExportName(),
            Items = new() { new ExportItem(ExportSource(), false, true) },
            Format = format,
        });
    }

    /// <summary>One click: writes {export folder}\{name}_{timestamp}.xlsx and opens it (if that option is on).</summary>
    internal async Task QuickExportAsync()
    {
        if (_exportCts is not null) { _exportCts.Cancel(); return; }
        if (!Data.Completed) { Ui.Info(FindForm(), "Wait until all rows are loaded (or stop the query) before exporting."); return; }
        var settings = AppSettings.Current;
        var name = ExportName();
        var path = Path.Combine(settings.ResolvedExportFolder,
            $"{ExportValues.FileName(name)}_{DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture)}.xlsx");
        var progress = new ExportProgress { TotalRowsEstimate = Data.Rows.Count };
        _exportCts = new CancellationTokenSource();
        _excel.Text = "Cancel";
        using var timer = new System.Windows.Forms.Timer { Interval = 200 };
        timer.Tick += (_, _) => _info.Text = $"Exporting… {progress.Rows:N0} / {Data.Rows.Count:N0} rows";
        timer.Start();
        try
        {
            var r = await ExcelExporter.ExportAsync(path, new[] { ExportSource() }, progress, _exportCts.Token);
            LastExportPath = path;
            _info.Text = $"Saved {Path.GetFileName(path)} · {r.Rows:N0} rows · {CellFormat.FormatDuration(r.Elapsed)}";
            _info.ToolTipText = path;
            if (settings.OpenAfterExport) Ui.OpenPath(path);
        }
        catch (Exception ex) when (_exportCts.IsCancellationRequested)
        {
            _ = ex;
            _info.Text = "Export cancelled";
        }
        finally
        {
            timer.Stop();
            _exportCts.Dispose();
            _exportCts = null;
            _excel.Text = "Excel";
        }
    }

    public void Refresh(bool force)
    {
        if (!force && Data.Completed && Grid.RowCount == Data.Rows.Count && _refitted) return;
        Grid.SyncRowCount();
        if (!_fitted && (Data.Rows.Count >= 100 || Data.Completed))
        {
            Grid.AutoFitColumns();
            _fitted = true;
        }
        if (Data.Completed && !_refitted)
        {
            // values at the end can be wider (ids 1..100 vs 199,999)
            if (Data.Rows.Count > 100) Grid.AutoFitColumns(widenOnly: true);
            _refitted = true;
        }
        Text = $"Result {_number} ({Data.Rows.Count:N0})";
        UpdateInfo();
        if (Data.Completed && _plan is null && !_planning)
        {
            _planning = true;
            Ui.Fire(this, PlanEditingAsync);
        }
    }

    private void UpdateInfo()
    {
        var rows = Data.Rows.Count;
        _info.Text = Data.Completed
            ? $"{rows:N0} rows · exec {CellFormat.FormatDuration(Data.ExecTime)} · fetch {CellFormat.FormatDuration(Data.FetchTime)}{(Data.Cancelled ? " · cancelled" : "")}" +
              (_plan is null ? "" : " · " + _plan.Describe(Data.Columns)) + (_status is null ? "" : " · " + _status)
            : $"{rows:N0} rows · fetching…";
    }

    private void SetStatus(string text)
    {
        _status = text;
        UpdateInfo();
    }

    // ---- editing ------------------------------------------------------------------------------

    /// <summary>Editable when the columns come from one table and include its key (or all its fields); never on a read-only connection.</summary>
    private async Task PlanEditingAsync()
    {
        try
        {
            var session = _owner.Session;
            ResultEditPlan plan;
            var (db, table, reason) = ResultEdit.SourceTable(Data.Columns, Data.Sql);
            if (session.ReadOnly) plan = ResultEditPlan.ReadOnly("read-only connection");
            else if (reason is not null) plan = ResultEditPlan.ReadOnly(reason);
            else
            {
                try
                {
                    session.Schema.Invalidate(db, table); // its fields or keys may have changed since they were cached
                    plan = ResultEdit.Plan(Data.Columns, await session.Schema.GetTableAsync(db!, table!));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    plan = ResultEditPlan.ReadOnly($"read-only: {table} can't be read ({Ui.Describe(ex).Replace('\n', ' ')})");
                }
            }
            if (IsDisposed) return;
            _plan = plan;
            _edit.ReadOnlyReason = plan.ReadOnlyReason ?? "";
            _edit.EditableColumns = plan.EditableColumns;
            _edit.Enabled = plan.Editable;
            for (int i = 0; i < Grid.ColumnCount && i < Data.Columns.Length; i++)
                if (plan.Editable && !plan.EditableColumns[i])
                    Grid.Columns[i].ToolTipText = $"{Data.Columns[i].Name}  {Data.Columns[i].DataTypeName}  (read-only: not a field of {plan.Target!.Table})";
            UpdateInfo();
        }
        finally { _planned.TrySetResult(); }
    }

    /// <summary>UPDATE … WHERE key LIMIT 1 for a changed row (<see cref="RowUpdate"/>); the row store gets the values as stored.</summary>
    private async Task<bool> SaveRowAsync(EditRow row, bool showError)
    {
        if (row.State == EditState.Saving || _plan?.Target is not { } target) return false;
        var original = row.Original ?? row.Values;
        row.State = EditState.Saving;
        _edit.Repaint(row);
        try
        {
            var stored = await RowUpdate.UpdateAsync(_owner.Session, target, original, row.Values)
                         ?? throw new InvalidOperationException("Row not found: it was changed or deleted by someone else since the query ran. Run the query again.");
            Array.Copy(stored, row.Values, row.Values.Length);
            row.Original = null;
            row.Error = null;
            row.State = EditState.Clean;
            SetStatus($"saved a row of {target.Table}");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            row.State = EditState.Error;
            row.Error = Ui.Describe(ex);
            SetStatus("error: " + row.Error.Replace('\n', ' '));
            if (showError) Ui.Error(FindForm(), ex, "Could not save the row");
            return false;
        }
        finally
        {
            if (!IsDisposed) _edit.Repaint(row);
        }
    }

    private async Task SortAsync(int column, bool descending)
    {
        if (!Data.Completed) return;
        await _edit.CommitCurrentAsync(); // the current row moves away with the sort: save it first
        Cursor = Cursors.WaitCursor;
        try
        {
            var order = await Task.Run(() => Data.Rows.BuildSortOrder(column, descending));
            Data.Rows.ApplySortOrder(order);
            Grid.SetSortGlyph(column, descending);
            Grid.Invalidate();
        }
        finally { Cursor = Cursors.Default; }
    }
}
