using MyFront.Core.Db;
using MyFront.Core;
using MyFront.Core.Accounts;
using MyFront.Core.Export;
using MyFront.Core.Quick;
using MyFront.UI.Dialogs;

namespace MyFront.UI;

/// <summary>
/// Main window: menu, toolbars and status bar, and one tab per open connection
/// (<see cref="ConnectionView"/>: its own tree, Object Browser / Data Browser / SQL Editor tabs) plus "+" to open another.
/// </summary>
internal sealed class MainForm : Form, IFileDropTarget
{
    private readonly ThemedTabControl _connTabs;
    /// <summary>The "+" tab: opens the Accounts dialog; shows a hint while nothing is connected.</summary>
    private readonly TabPage _startPage;
    private ConnectionView? _activeView;
    private readonly ToolStripStatusLabel _stConn, _stDb, _stRows, _stTime, _stMsg;
    /// <summary>"Connecting to …" while a quick query opens its connection (no tab to keep it with yet).</summary>
    private string? _connecting;
    private readonly ToolStripButton _tbRun, _tbStop;
    /// <summary>The SQL editor's other buttons, here so its own toolbar keeps just Run and Explain.</summary>
    private readonly ToolStripButton _tbRunAll, _tbRunCur, _tbSave, _tbHistory, _tbExportQuery;
    private readonly ToolStripMenuItem _themeButton;
    private readonly QuickQueryBar _quickBar;
    private List<QuickQuery> _quickQueries = new();
    private string? _quickLoadError;
    /// <summary>The connection whose quick query buttons the toolbar shows.</summary>
    private string? _quickBarAccount;

    /// <summary>The open connections, in tab order.</summary>
    internal IEnumerable<ConnectionView> Views => _connTabs.TabPages.OfType<ConnectionView>();
    internal ConnectionView? ActiveView => _connTabs.SelectedTab as ConnectionView;
    internal ConnectionView? ViewOf(ServerSession session) => Views.FirstOrDefault(v => v.Session == session);
    internal IReadOnlyList<ServerSession> Sessions => Views.Select(v => v.Session).ToList();
    internal TabControl ConnectionTabs => _connTabs;

    // self-test entry points: the workspace of the connection whose tab is in front
    private ConnectionView Active => ActiveView ?? throw new InvalidOperationException("No connection is open.");
    internal NavigatorTree Navigator => Active.Navigator;
    internal TabControl Tabs => Active.Tabs;
    internal DataBrowserView DataBrowser => Active.DataBrowser;
    internal ObjectBrowserView ObjectBrowser => Active.ObjectBrowser;
    internal TabPage DataPage => Active.DataPage;
    internal TabPage ObjectPage => Active.ObjectPage;

    /// <summary>The self-test opens sessions itself, so it suppresses the startup Accounts dialog.</summary>
    internal bool ShowAccountsOnStartup { get; set; } = true;

    public MainForm()
    {
        Text = "MyFront";
        Icon = Ui.AppIcon;
        Size = new Size(LogicalToDeviceUnits(1280), LogicalToDeviceUnits(800));
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = false;

        // ---- menu ---------------------------------------------------------------------------
        var menu = new MenuStrip();
        var file = AddMenu(menu, "&File");
        Item(file, "&Open connection…", Keys.Control | Keys.O, (_, _) => ShowAccounts());
        Item(file, "&Close connection", Keys.None, (_, _) => { if (ActiveView is { } v) CloseSession(v.Session); });
        file.DropDownItems.Add(new ToolStripSeparator());
        Item(file, "&New SQL Editor", Keys.Control | Keys.T, (_, _) => NewSqlTab());
        Item(file, "Open SQL &file…", Keys.None, (_, _) => OpenSqlFile(ActiveSqlTab));
        Item(file, "&Save SQL file", Keys.Control | Keys.S, (_, _) => ActiveSqlTab?.Save(false));
        Item(file, "Save SQL file &as…", Keys.None, (_, _) => ActiveSqlTab?.Save(true));
        Item(file, "Close &tab", Keys.Control | Keys.W, (_, _) => CloseSqlTab(ActiveSqlTab));
        file.DropDownItems.Add(new ToolStripSeparator());
        Item(file, "&Run SQL file…", Keys.None, (_, _) => RunSqlFile());
        file.DropDownItems.Add(new ToolStripSeparator());
        Item(file, "E&xit", Keys.Alt | Keys.F4, (_, _) => Close());

        var view = AddMenu(menu, "&View");
        Item(view, "&Object Browser", Keys.None, (_, _) => ActiveView?.ShowObjectBrowser());
        Item(view, "&Data Browser", Keys.None, (_, _) => ActiveView?.ShowDataBrowser());
        Item(view, "&SQL Editor", Keys.None, (_, _) => { if (ActiveView is { } v) v.ShowFirstEditor(); else ShowAccounts(); });
        view.DropDownItems.Add(new ToolStripSeparator());
        var wrap = new ToolStripMenuItem("&Word wrap") { CheckOnClick = true, Checked = AppSettings.Current.WordWrap };
        wrap.CheckedChanged += (_, _) => SqlEditor.SetWordWrap(wrap.Checked);
        view.DropDownItems.Add(wrap);

        var dbMenu = AddMenu(menu, "&Database");
        Item(dbMenu, "&Refresh", Keys.F5, (_, _) => Ui.Fire(this, RefreshCurrentAsync));
        Item(dbMenu, "Copy &CREATE statement", Keys.None, (_, _) => { if (ActiveView?.Current?.Owner is { } n) Ui.Fire(this, () => CopyCreateAsync(n)); });
        Item(dbMenu, "&Import Excel / CSV…", Keys.Control | Keys.I, (_, _) => ImportCurrent(null));
        Item(dbMenu, "&Export to Excel…", Keys.Control | Keys.E, (_, _) => ExportCurrent("xlsx"));
        Item(dbMenu, "Export to C&SV…", Keys.None, (_, _) => ExportCurrent("csv"));
        dbMenu.DropDownItems.Add(new ToolStripSeparator());
        Item(dbMenu, "&Processes", Keys.None, (_, _) => ActiveView?.SelectServerChild(NodeKind.Processes));
        Item(dbMenu, "&Variables", Keys.None, (_, _) => ActiveView?.SelectServerChild(NodeKind.Variables));

        var query = AddMenu(menu, "&Query");
        Item(query, "&Run (selection, or all)", Keys.F9, (_, _) => RunActive(RunScope.Selection));
        Item(query, "Run &all", Keys.None, (_, _) => RunActive(RunScope.All));
        Item(query, "Run &selection", Keys.Control | Keys.F9, (_, _) => RunActive(RunScope.Selection));
        var cur = Item(query, "Run &current statement", Keys.Control | Keys.Shift | Keys.F9, (_, _) => RunActive(RunScope.Current));
        cur.ShortcutKeyDisplayString = "Ctrl+Enter";
        Item(query, "S&top", Keys.None, (_, _) => ActiveSqlTab?.Stop());
        Item(query, "&Format SQL", Keys.Control | Keys.Shift | Keys.F, (_, _) => ActiveSqlTab?.FormatSql());
        query.DropDownItems.Add(new ToolStripSeparator());
        Item(query, "SQL &history…", Keys.Control | Keys.H, (_, _) => ShowHistory(ActiveSqlTab));

        var help = AddMenu(menu, "&Help");
        Item(help, "&About", Keys.None, (_, _) => Ui.Info(this,
            $"MyFront {Application.ProductVersion.Split('+')[0]}\nLightweight MariaDB / MySQL client.\n\n.NET {Environment.Version}\nData: {Core.AppPaths.DataDir}\n\nOpen-source licenses: THIRD-PARTY-NOTICES.txt next to MyFront.exe"));

        // top-right corner: light / dark switch
        _themeButton = new ToolStripMenuItem
        {
            Alignment = ToolStripItemAlignment.Right,
            DisplayStyle = ToolStripItemDisplayStyle.Image,
            ImageScaling = ToolStripItemImageScaling.None,
            AutoToolTip = false,
        };
        _themeButton.Click += (_, _) => { ThemeManager.Toggle(); UpdateThemeButton(); };
        menu.Items.Add(_themeButton);
        menu.ShowItemToolTips = true;
        UpdateThemeButton();

        // ---- toolbar: app buttons left, quick queries + "+" + "Manage…" right (one row) ------
        _quickBar = new QuickQueryBar(this);
        var bar = _quickBar;
        Ui.Button(bar, "Open connection", Icons.Connect, Icons.Blue, (_, _) => ShowAccounts(), "Open connection (Ctrl+O)");
        bar.Items.Add(new ToolStripSeparator());
        Ui.Button(bar, "New SQL Editor", Icons.Edit, Icons.Blue, (_, _) => NewSqlTab(), "New SQL Editor (Ctrl+T)");
        Ui.Button(bar, "Run SQL file", Icons.Document, Icons.Gray, (_, _) => RunSqlFile(), "Run a .sql file");
        Ui.Button(bar, "Import Excel / CSV", Icons.Import, Icons.Green, (_, _) => ImportCurrent(null), "Import Excel / CSV (Ctrl+I) — or drop a file on this window");
        Ui.Button(bar, "Refresh", Icons.Refresh, Icons.Green, (_, _) => Ui.Fire(this, RefreshCurrentAsync), "Refresh (F5)");
        bar.Items.Add(new ToolStripSeparator());
        _tbRun = Ui.Button(bar, "Run", Icons.PlaySolid, Icons.Green, (_, _) => RunActive(RunScope.Selection), "Run the selection, or everything (F9)");
        _tbRunAll = Ui.Button(bar, "Run all", Icons.Play, Icons.Green, (_, _) => RunActive(RunScope.All), "Run all statements, also when text is selected");
        _tbRunCur = Ui.Button(bar, "Run current statement", Icons.Run, Color.FromArgb(0, 140, 140), (_, _) => RunActive(RunScope.Current),
            "Run the statement at the cursor (Ctrl+Enter / Ctrl+Shift+F9)");
        _tbStop = Ui.Button(bar, "Stop", Icons.Stop, Icons.Red, (_, _) => ActiveSqlTab?.Stop(), "Cancel the running query (KILL QUERY)");
        _tbStop.Enabled = false;
        bar.Items.Add(new ToolStripSeparator());
        Ui.Button(bar, "Open .sql file", Icons.Open, Icons.Gray, (_, _) => OpenSqlFile(ActiveSqlTab), "Open a .sql file into an SQL editor");
        _tbSave = Ui.Button(bar, "Save", Icons.Save, Icons.Gray, (_, _) => ActiveSqlTab?.Save(false), "Save the SQL editor's text to a .sql file (Ctrl+S)");
        _tbHistory = Ui.Button(bar, "SQL history", Icons.History, Icons.Gray, (_, _) => ShowHistory(ActiveSqlTab), "SQL history (Ctrl+H)");
        _tbExportQuery = new ToolStripButton("Export query", Icons.Excel(Icons.Size(this)), (_, _) => ActiveSqlTab?.ExportQuery())
        {
            DisplayStyle = ToolStripItemDisplayStyle.Image,
            ImageScaling = ToolStripItemImageScaling.None,
            ToolTipText = "Export the statement at the cursor straight to Excel / CSV (streamed from the server, any size)",
        };
        bar.Items.Add(_tbExportQuery);

        // ---- body: one tab per connection, then "+" ----------------------------------------
        _startPage = new TabPage("+") { UseVisualStyleBackColor = true, ToolTipText = "Open a connection (Ctrl+O)" };
        var hint = new LinkLabel { Text = "Open a connection…  (Ctrl+O)", AutoSize = true, Location = new Point(LogicalToDeviceUnits(20), LogicalToDeviceUnits(20)) };
        hint.LinkClicked += (_, _) => ShowAccounts();
        _startPage.Controls.Add(hint);
        _connTabs = new ThemedTabControl { Dock = DockStyle.Fill, ShowToolTips = true };
        _connTabs.TabPages.Add(_startPage);
        // "+" is a button, not a page to switch to (unless nothing is open)
        _connTabs.Selecting += (_, e) => { if (e.TabPage == _startPage && Views.Any()) e.Cancel = true; };
        _connTabs.SelectedIndexChanged += (_, _) => OnConnectionChanged();
        _connTabs.MouseUp += OnConnectionTabsMouseUp;

        // ---- status bar ---------------------------------------------------------------------
        var status = new StatusStrip();
        _stConn = StatusLabel(status, 400); // room for "· via SSH · READ-ONLY"
        _stDb = StatusLabel(status, 140);
        _stRows = StatusLabel(status, 120);
        _stTime = StatusLabel(status, 220);
        _stMsg = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        status.Items.Add(_stMsg);

        _quickBar.RunRequested += (q, newTab) => Ui.Fire(this, () => RunQuickQueryAsync(q, newTab));
        _quickBar.EditRequested += q => ShowQuickQueryEditor(q.Id, addNew: false);
        _quickBar.AddRequested += () => ShowQuickQueryEditor(null, addNew: true);
        _quickBar.ManageRequested += () => ShowQuickQueryEditor(null, addNew: false);
        _quickBar.DeleteRequested += DeleteQuickQuery;
        LoadQuickQueries();

        Controls.Add(_connTabs);
        Controls.Add(bar);
        Controls.Add(menu);
        Controls.Add(status);
        MainMenuStrip = menu;

        Shown += (_, _) => { if (ShowAccountsOnStartup) BeginInvoke(ShowAccounts); };
        AllowDrop = true;
        // only an Excel / CSV file is taken here; text (a name dragged from the tree) is for the SQL editors
        DragEnter += (_, e) => e.Effect = ((IFileDropTarget)this).FileDragEffect(e.Data);
        DragDrop += (_, e) => { if (e.Data is not null) ((IFileDropTarget)this).DropFiles(e.Data); };
        FormClosing += OnClosing;
        UpdateStatus();
        ThemeManager.Apply(this);
    }

    private void UpdateThemeButton()
    {
        bool dark = ThemeManager.Current.IsDark;
        var old = _themeButton.Image;
        // moon in light mode (switch to dark), warm sun in dark mode (switch back)
        _themeButton.Image = dark
            ? Icons.Glyph(Icons.Sun, Color.FromArgb(255, 200, 70), LogicalToDeviceUnits(18))
            : Icons.Glyph(Icons.Moon, Color.FromArgb(70, 80, 150), LogicalToDeviceUnits(18));
        _themeButton.ToolTipText = dark ? "Switch to light mode" : "Switch to dark mode";
        old?.Dispose();
    }

    internal bool IsDarkTheme => ThemeManager.Current.IsDark;
    internal void ToggleTheme() { ThemeManager.Toggle(); UpdateThemeButton(); }

    private static ToolStripMenuItem AddMenu(MenuStrip menu, string text)
    {
        var m = new ToolStripMenuItem(text);
        menu.Items.Add(m);
        return m;
    }

    private static ToolStripMenuItem Item(ToolStripMenuItem parent, string text, Keys keys, EventHandler click)
    {
        var item = new ToolStripMenuItem(text, null, click);
        if (keys != Keys.None) item.ShortcutKeys = keys;
        parent.DropDownItems.Add(item);
        return item;
    }

    private ToolStripStatusLabel StatusLabel(StatusStrip strip, int width)
    {
        var l = new ToolStripStatusLabel
        {
            AutoSize = false, Width = LogicalToDeviceUnits(width), TextAlign = ContentAlignment.MiddleLeft,
            BorderSides = ToolStripStatusLabelBorderSides.Right, BorderStyle = Border3DStyle.Etched,
        };
        strip.Items.Add(l);
        return l;
    }

    /// <summary>
    /// The status bar's message belongs to a connection: shown while its tab is in front, kept with it while another
    /// one is (a message about one connection never shows on another).
    /// </summary>
    internal void ShowMessage(ConnectionView view, string text)
    {
        view.Message = text;
        if (view == ActiveView) _stMsg.Text = text;
    }

    // ---- connections ----------------------------------------------------------------------------

    public void ShowAccounts()
    {
        foreach (var v in Views) v.SaveEditors(); // Export… takes the editor texts from their files
        using var dlg = new AccountsForm(id => Views.Any(v => v.Session.Account.Id == id));
        var result = dlg.ShowDialog(this);
        if (dlg.QuickQueriesChanged) LoadQuickQueries();
        // a database set (or cleared) on an open connection limits its tree right away
        var saved = AccountStore.Load();
        foreach (var v in Views)
            if (saved.FirstOrDefault(a => a.Id == v.Session.Account.Id) is { } a) Ui.Fire(v, () => v.SetDefaultDatabaseAsync(a.DefaultDatabase));
        if (result != DialogResult.OK) return;
        if (dlg.OpenAccountId is { } id && Views.FirstOrDefault(v => v.Session.Account.Id == id) is { } open)
            ShowConnection(open);
        else if (dlg.Session is not null)
            Ui.Fire(this, () => AddSessionAsync(dlg.Session));
    }

    /// <summary>Opens a tab for the connection. If its account is already open, that tab is shown and this session is closed.</summary>
    public async Task AddSessionAsync(ServerSession session)
    {
        if (ViewOf(session) is { } shown) { ShowConnection(shown); return; }
        if (Views.FirstOrDefault(v => v.Session.Account.Id == session.Account.Id) is { } open)
        {
            session.Dispose();
            ShowConnection(open);
            return;
        }
        var view = new ConnectionView(this, session);
        view.StatusChanged += () => { if (ActiveView == view) UpdateStatus(); };
        ThemeManager.Apply(view);
        _connTabs.SuspendLayout();
        _connTabs.TabPages.Remove(_startPage); // "+" stays last
        _connTabs.TabPages.Add(view);
        _connTabs.TabPages.Add(_startPage);
        _connTabs.ResumeLayout();
        _connTabs.SelectedTab = view;
        await view.OpenAsync();
    }

    internal void ShowConnection(ConnectionView view)
    {
        if (_connTabs.SelectedTab != view) _connTabs.SelectedTab = view;
    }

    /// <summary>Closes a connection's tab: unsaved rows and running queries are confirmed, the editors are saved.</summary>
    public void CloseSession(ServerSession session)
    {
        var view = ViewOf(session);
        if (view is null || !view.ConfirmClose()) return;
        view.Shutdown();
        int index = _connTabs.TabPages.IndexOf(view);
        if (_activeView == view) _activeView = null;
        _connTabs.TabPages.Remove(view);
        var rest = Views.ToList();
        _connTabs.SelectedTab = rest.Count == 0 ? _startPage : rest[Math.Clamp(index - 1, 0, rest.Count - 1)];
        view.Dispose();
        session.Dispose();
        UpdateStatus();
    }

    private void OnConnectionChanged()
    {
        var view = ActiveView;
        if (_activeView is not null && _activeView != view) _activeView.Deactivate();
        _activeView = view;
        UpdateStatus();
        if (view?.ActiveSqlTab is { } t && IsHandleCreated) BeginInvoke(() => t.Editor.Focus());
    }

    private ConnectionView? ConnectionTabAt(Point p, out bool plus)
    {
        plus = false;
        for (int i = 0; i < _connTabs.TabCount; i++)
        {
            if (!_connTabs.GetTabRect(i).Contains(p)) continue;
            plus = _connTabs.TabPages[i] == _startPage;
            return _connTabs.TabPages[i] as ConnectionView;
        }
        return null;
    }

    private void OnConnectionTabsMouseUp(object? sender, MouseEventArgs e)
    {
        var view = ConnectionTabAt(e.Location, out bool plus);
        if (plus && e.Button == MouseButtons.Left) { BeginInvoke(ShowAccounts); return; }
        if (view is null) return;
        if (e.Button == MouseButtons.Middle) { CloseSession(view.Session); return; }
        if (e.Button != MouseButtons.Right) return;
        ShowConnection(view);
        var m = new ContextMenuStrip();
        m.Items.Add(ResultGrid.MenuItem("New SQL Editor", "Ctrl+T", (_, _) => NewSqlTab(view.Session)));
        m.Items.Add(ResultGrid.MenuItem("Refresh", "F5", (_, _) => Ui.Fire(this, view.RefreshAsync)));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(ResultGrid.MenuItem("Open connection…", "Ctrl+O", (_, _) => ShowAccounts()));
        m.Items.Add(ResultGrid.MenuItem("Close connection", "middle-click", (_, _) => CloseSession(view.Session)));
        m.Closed += (_, _) => BeginInvoke(m.Dispose);
        m.Show(_connTabs, e.Location);
    }

    public string? CurrentDatabaseFor(ServerSession session) =>
        ViewOf(session)?.CurrentDatabase ?? session.Account.DefaultDatabase;

    public void SelectDatabase(ServerSession session, string database) => ViewOf(session)?.SelectDatabase(database);

    // ---- export ---------------------------------------------------------------------------------

    /// <summary>Self-test hook: receives export requests instead of the modal dialog.</summary>
    internal Action<ExportRequest>? ExportOverride;

    public void ShowExport(ExportRequest request)
    {
        if (ExportOverride is not null) { ExportOverride(request); return; }
        using var dlg = new ExportForm(request);
        dlg.ShowDialog(this);
    }

    /// <summary>Export for a table / view node (one sheet) or a database node (one sheet per table).</summary>
    public async Task<ExportRequest?> BuildNodeExportAsync(NodeInfo info, string format)
    {
        if (info.Database is null) return null;
        var status = await info.Session.Schema.GetTableStatusAsync(info.Database);
        var db = info.Database;
        if (info.IsTableLike)
        {
            var st = status.FirstOrDefault(s => s.Name == info.Name);
            bool view = info.Kind == NodeKind.View;
            long? rows = view ? null : st?.Rows;
            return new ExportRequest
            {
                Title = $"{(view ? "View" : "Table")} {db}.{info.Name}{(rows is null ? "" : $" (≈{rows:N0} rows)")}",
                BaseName = info.Name!,
                Items = new() { new ExportItem(ExportTable.FromTable(info.Session, db, info.Name!, rows), view, true) },
                Format = format,
            };
        }
        if (info.Kind != NodeKind.Database) return null;
        int views = status.Count(s => s.IsView);
        return new ExportRequest
        {
            Title = $"Database {db}: {status.Count - views} table(s), {views} view(s). " +
                    (format == "csv" ? "One CSV file per table." : "One sheet per table."),
            BaseName = db,
            Items = status.Select(s => new ExportItem(ExportTable.FromTable(info.Session, db, s.Name, s.IsView ? null : s.Rows), s.IsView, !s.IsView)).ToList(),
            IsDatabase = true,
            Format = format,
        };
    }

    // ---- import ---------------------------------------------------------------------------------

    public void ShowImport(ServerSession session, string database, string? table = null, string? file = null)
    {
        if (session.ReadOnly)
        {
            Ui.Info(this, $"{session.DisplayName} is a read-only connection, so importing is not allowed.");
            return;
        }
        using var wizard = new ImportWizard(this, session, database, table, file);
        wizard.ShowDialog(this);
    }

    /// <summary>Ctrl+I / toolbar / dropped file: imports into the selected table, or the current database, of the connection in front.</summary>
    private void ImportCurrent(string? file)
    {
        var view = ActiveView;
        if (view is null) { ShowAccounts(); return; }
        var db = view.CurrentDatabase;
        if (db is null) { Ui.Info(this, "Select a database in the navigator first."); return; }
        ShowImport(view.Session, db, view.Current?.Owner is { Kind: NodeKind.Table } n ? n.Name : null, file);
    }

    public void OpenTable(ServerSession session, string database, string table)
    {
        if (ViewOf(session) is not { } view) return;
        ShowConnection(view);
        Ui.Fire(this, () => view.OpenTableAsync(database, table));
    }

    /// <summary>After an import: the tree (new table) and an open Data Browser show the new rows.</summary>
    public void RefreshAfterImport(ServerSession session, string database, string table)
    {
        if (ViewOf(session) is not { } view) return;
        view.RefreshAfterDdl(database);
        var data = view.DataBrowser;
        if (data.Session == session && data.Database == database && data.Table == table)
            Ui.Fire(this, data.RefreshAsync);
    }

    private static readonly string[] ImportExtensions = { ".xlsx", ".xls", ".csv", ".txt" };

    private static string? DroppedFile(IDataObject? data) =>
        data?.GetDataPresent(DataFormats.FileDrop) == true && data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files
        && ImportExtensions.Contains(Path.GetExtension(files[0]).ToLowerInvariant()) ? files[0] : null;

    DragDropEffects IFileDropTarget.FileDragEffect(IDataObject? data) => DroppedFile(data) is not null ? DragDropEffects.Copy : DragDropEffects.None;

    void IFileDropTarget.DropFiles(IDataObject data)
    {
        if (DroppedFile(data) is { } f) BeginInvoke(() => ImportCurrent(f));
    }

    /// <summary>Ctrl+E / Database menu: the active result tab, the Data Browser table, or the selected tree node.</summary>
    private void ExportCurrent(string format)
    {
        var view = ActiveView;
        if (view is null) return;
        if (view.ActiveSqlTab is { CurrentResult: { } result })
        {
            result.ShowExportDialogFor(format);
            return;
        }
        if (view.Tabs.SelectedTab == view.DataPage && view.DataBrowser.BuildExportRequest(format) is { } dataRequest)
        {
            ShowExport(dataRequest);
            return;
        }
        if (view.Current?.Owner is { } node) Ui.Fire(this, () => ExportNodeAsync(node, format));
    }

    internal async Task ExportNodeAsync(NodeInfo info, string format)
    {
        var request = await BuildNodeExportAsync(info, format);
        if (request is not null) ShowExport(request);
    }

    internal async Task CopyCreateAsync(NodeInfo info)
    {
        if (info.Database is null) return;
        var name = info.Kind == NodeKind.Database ? null : info.Name;
        var sql = await info.Session.Schema.GetCreateStatementAsync(info.Database, name, info.ObjectType);
        Clipboard.SetText(sql.Replace("\r\n", "\n").Replace("\n", Environment.NewLine) + ";");
        if (ViewOf(info.Session) is { } view) ShowMessage(view, $"CREATE statement of {(name ?? info.Database)} copied to the clipboard");
    }

    public Task RefreshCurrentAsync() => ActiveView?.RefreshAsync() ?? Task.CompletedTask;

    /// <summary>After CREATE/DROP/ALTER: reload that database's node (or the server node when database is null).</summary>
    public void RefreshAfterDdl(ServerSession session, string? database) => ViewOf(session)?.RefreshAfterDdl(database);

    // ---- SQL editor tabs ----------------------------------------------------------------------

    internal SqlEditorTab? ActiveSqlTab => ActiveView?.ActiveSqlTab;

    /// <summary>The editor tabs of a connection, in tab order (self-test).</summary>
    internal IReadOnlyList<SqlEditorTab> EditorTabsOf(ServerSession session) =>
        ViewOf(session)?.Editors ?? Array.Empty<SqlEditorTab>();

    /// <summary>Selects an editor tab, bringing its connection's tab to the front.</summary>
    internal void ShowSqlTab(SqlEditorTab tab)
    {
        if (ViewOf(tab.Session) is not { } view) return;
        ShowConnection(view);
        view.ShowSqlTab(tab);
    }

    public SqlEditorTab? NewSqlTab() => NewSqlTab(ActiveView?.Session);

    /// <summary>A new extra tab (SQL Editor 6, 7 …) of that connection, autosaved like the fixed ones.</summary>
    public SqlEditorTab? NewSqlTab(ServerSession? session, bool select = true)
    {
        var view = session is null ? null : ViewOf(session);
        if (view is null)
        {
            ShowAccounts();
            return null;
        }
        var tab = view.NewSqlTab();
        if (select) ShowSqlTab(tab);
        return tab;
    }

    public void UpdateTabTitle(SqlEditorTab tab)
    {
        tab.Text = tab.BaseTitle;
        tab.ToolTipText = tab.Session.Summary + (tab.IsFixed ? "\nSaved automatically; always here for this connection" : "");
    }

    /// <summary>Opens a .sql file: into the given tab if it is empty, otherwise into a new tab (nothing is overwritten).</summary>
    internal void OpenSqlFile(SqlEditorTab? target)
    {
        using var dlg = new OpenFileDialog { Filter = "SQL files (*.sql)|*.sql|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        OpenSqlFile(target, dlg.FileName);
    }

    internal SqlEditorTab? OpenSqlFile(SqlEditorTab? target, string path)
    {
        var tab = target is { Editor.TextLength: 0 } ? target : NewSqlTab(target?.Session ?? ActiveView?.Session, select: false);
        if (tab is null) return null;
        tab.LoadFile(path);
        ShowSqlTab(tab);
        return tab;
    }

    private void CloseSqlTab(SqlEditorTab? tab, bool force = false)
    {
        if (tab is not null) ViewOf(tab.Session)?.CloseSqlTab(tab, force);
    }

    private void RunActive(RunScope scope)
    {
        var view = ActiveView;
        if (view is null) return;
        var tab = view.ActiveSqlTab;
        if (tab is null)
        {
            tab = view.Editors.FirstOrDefault();
            if (tab is null) return;
            view.ShowSqlTab(tab);
        }
        tab.Run(scope);
    }

    public void OnEditorRunningChanged(SqlEditorTab tab)
    {
        if (ActiveSqlTab == tab) UpdateStatus();
    }

    public void ShowHistory(SqlEditorTab? tab)
    {
        var session = tab?.Session ?? ActiveView?.Session;
        if (session is null) return;
        using var dlg = new HistoryForm(session.Account.Id, session.DisplayName);
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.SelectedSql is not null)
        {
            var view = ViewOf(session);
            var target = tab ?? view?.ActiveSqlTab ?? view?.Editors.FirstOrDefault() ?? NewSqlTab(session);
            if (target is null) return;
            ShowSqlTab(target);
            target.InsertText(dlg.SelectedSql);
        }
    }

    private void RunSqlFile()
    {
        var view = ActiveView;
        if (view is null) { ShowAccounts(); return; }
        using var dlg = new RunSqlFileForm(view.Session, view.CurrentDatabase, null);
        dlg.ShowDialog(this);
        view.RefreshAfterDdl(null);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Enter) && ActiveSqlTab is { } tab && tab.Editor.Focused)
        {
            tab.Run(RunScope.Current);
            return true;
        }
        // Ctrl+1 … Ctrl+9 (top row or numpad) → quick query with that hotkey
        if ((keyData & Keys.Modifiers) == Keys.Control)
        {
            var key = keyData & Keys.KeyCode;
            int n = key is >= Keys.D1 and <= Keys.D9 ? key - Keys.D0 : key is >= Keys.NumPad1 and <= Keys.NumPad9 ? key - Keys.NumPad0 : 0;
            if (n > 0 && _quickQueries.FirstOrDefault(q => q.Hotkey == n && q.ShowsOn(_quickBarAccount)) is { } quick)
            {
                Ui.Fire(this, () => RunQuickQueryAsync(quick, newTab: false));
                return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---- quick queries --------------------------------------------------------------------------

    internal IReadOnlyList<QuickQuery> QuickQueries => _quickQueries;
    internal QuickQueryBar QuickBar => _quickBar;

    private void LoadQuickQueries()
    {
        try
        {
            _quickQueries = QuickQueryStore.Load();
            QuickQueryStore.Relink(_quickQueries, AccountStore.Load());
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            _quickQueries = new List<QuickQuery>();
            _quickLoadError = ex.Message;
        }
        RefreshQuickBar();
    }

    internal void SetQuickQueries(List<QuickQuery> queries, bool save = true)
    {
        _quickQueries = queries;
        if (save) QuickQueryStore.Save(queries);
        RefreshQuickBar();
    }

    /// <summary>Shows the buttons of the connection in front (and the ones for every connection).</summary>
    private void RefreshQuickBar()
    {
        _quickBarAccount = ActiveView?.Session.Account.Id;
        _quickBar.Show(_quickQueries.Where(q => q.ShowsOn(_quickBarAccount)).ToList(), DescribeTarget);
        ThemeManager.Apply(_quickBar);
    }

    private string DescribeTarget(QuickQuery q)
    {
        var account = q.AccountId is null ? "every connection (the one in front)"
            : AccountStore.Load().FirstOrDefault(a => a.Id == q.AccountId)?.Name ?? (q.AccountName ?? "?") + " (missing)";
        return account + (q.Database is null ? "" : " / " + q.Database);
    }

    private void ShowQuickQueryEditor(string? selectId, bool addNew)
    {
        var accounts = AccountStore.Load();
        using var editor = new QuickQueryEditor(_quickQueries, accounts, async accountId =>
        {
            var session = accountId is null ? ActiveView?.Session : Views.FirstOrDefault(v => v.Session.Account.Id == accountId)?.Session;
            return session is null ? new List<string>() : await session.Schema.GetDatabasesAsync();
        }, selectId, addNew, ActiveView?.Session.Account.Id);
        if (editor.ShowDialog(this) == DialogResult.OK) SetQuickQueries(editor.Result);
    }

    private void DeleteQuickQuery(QuickQuery q)
    {
        if (!Ui.Confirm(this, $"Delete the quick query \"{q.Name}\"?", "Delete", warning: true)) return;
        SetQuickQueries(_quickQueries.Where(x => x.Id != q.Id).ToList());
    }

    /// <summary>The button's connection: already open, opened now (saved password or prompt), or the one in front.</summary>
    private async Task<ServerSession?> ResolveSessionAsync(QuickQuery q)
    {
        if (q.AccountId is null)
        {
            var s = ActiveView?.Session;
            if (s is null) ShowAccounts();
            return s;
        }
        var open = Views.FirstOrDefault(v => v.Session.Account.Id == q.AccountId);
        if (open is not null) return open.Session;
        var account = AccountStore.Load().FirstOrDefault(a => a.Id == q.AccountId);
        if (account is null)
        {
            Ui.Info(this, $"The connection \"{q.AccountName}\" of \"{q.Name}\" doesn't exist on this PC. Edit the button and choose one.");
            return null;
        }
        var password = account.GetSavedPassword();
        if (password is null)
        {
            using var prompt = new PasswordPrompt($"{account.User}@{account.Host}");
            if (prompt.ShowDialog(this) != DialogResult.OK) return null;
            password = prompt.Password;
        }
        _connecting = $"Connecting to {account.Name}…";
        _stMsg.Text = _connecting;
        var prompts = new SshPromptsUi(this);
        ServerSession session;
        try
        {
            session = await ServerSession.ConnectAsync(account, password, default, prompts);
        }
        finally
        {
            _connecting = null;
            _stMsg.Text = ActiveView?.Message ?? "";
            if (prompts.AcceptedHostKey is { } key)
            {
                account.Ssh!.HostKey = key;
                AccountStore.Upsert(account);
            }
        }
        await AddSessionAsync(session);
        return session;
    }

    /// <summary>Runs a quick query button in its connection's tab: its own result tab (reused) or straight to Excel.</summary>
    public async Task RunQuickQueryAsync(QuickQuery q, bool newTab)
    {
        var session = await ResolveSessionAsync(q);
        if (session is null || ViewOf(session) is not { } view) return;
        ShowConnection(view);
        var db = q.Database ?? session.Account.DefaultDatabase ?? view.CurrentDatabase;
        if (q.ConfirmDangerous && SqlClassifier.ContainsDangerous(q.Sql))
        {
            var words = string.Join(", ", SqlClassifier.Tokens(q.Sql).Where(t => t is "UPDATE" or "DELETE" or "DROP" or "TRUNCATE" or "ALTER").Distinct());
            var preview = q.Sql.Trim().Length > 300 ? q.Sql.Trim()[..300] + "…" : q.Sql.Trim();
            if (!Ui.Confirm(this, $"\"{q.Name}\" changes data or structure ({words}) on {session.DisplayName}{(db is null ? "" : " / " + db)}:\n\n{preview}\n\nRun it?",
                    "Quick query", warning: true))
                return;
        }

        if (!q.ToExcel)
        {
            // every button uses the connection's one quick query page (Shift+click asks for a tab of its own)
            var tab = newTab ? null : view.Editors.FirstOrDefault(t => t.IsQuickPage);
            if (tab is null)
            {
                tab = view.CreateSqlTab(0); // not saved
                tab.IsQuickPage = !newTab;
            }
            if (tab.IsRunning)
            {
                tab.Stop(); // the page shows the button pressed last
                var waited = System.Diagnostics.Stopwatch.StartNew();
                while (tab.IsRunning && waited.ElapsedMilliseconds < 10_000) await Task.Delay(25);
                if (tab.IsRunning) return;
            }
            tab.QuickQueryId = q.Id;
            tab.BaseTitle = "⚡ " + q.Name;
            UpdateTabTitle(tab);
            tab.PinnedDatabase = db;
            tab.SetSql(q.Sql);
            ShowSqlTab(tab);
            await tab.RunAsync(RunScope.All);
            return;
        }

        var values = ParameterDialog.Ask(this, q.Name, q.Sql);
        if (values is null) return;
        string sql;
        List<(string Name, object? Value)> parameters;
        try { (sql, parameters) = ParameterParser.Bind(q.Sql, values); }
        catch (ParameterException ex) { Ui.Info(this, ex.Message); return; }
        var path = Path.Combine(AppSettings.Current.ResolvedExportFolder,
            $"{ExportValues.FileName(q.Name)}_{DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture)}.xlsx");
        var progress = new ExportProgress();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var wait = new ProgressForm($"{q.Name} → Excel", () => $"{progress.Rows:N0} rows · {CellFormat.FormatDuration(sw.Elapsed)}");
        wait.Show(this);
        try
        {
            var r = await QuickExport.ToExcelAsync(session, db, q.Name, sql, parameters, path, progress, wait.Token);
            LastQuickExport = path;
            ShowMessage(view, $"{q.Name}: {r.Rows:N0} rows → {Path.GetFileName(path)}");
            Ui.OpenPath(path);
        }
        catch (Exception ex) when (wait.Token.IsCancellationRequested)
        {
            _ = ex;
            ShowMessage(view, $"{q.Name}: cancelled");
        }
        finally { wait.Finish(); }
    }

    /// <summary>Last straight-to-Excel file (self-test).</summary>
    internal string? LastQuickExport { get; private set; }

    // ---- status -------------------------------------------------------------------------------

    /// <summary>Window title and status bar follow the connection in front and its selected tab.</summary>
    private void UpdateStatus()
    {
        var view = ActiveView;
        var session = view?.Session;
        Text = view is null ? "MyFront" : $"MyFront — {view.Session.DisplayName}{(view.Current?.Database is { } cdb ? " / " + cdb : "")}";
        _stConn.Text = session?.Summary ?? "Not connected";
        _stConn.ForeColor = session?.ReadOnly == true ? ThemeManager.Current.Error : SystemColors.ControlText;
        _stDb.Text = view is null ? "" : "DB: " + (view.CurrentDatabase ?? "-");
        string? rows = null, time = null;
        var editor = view?.ActiveSqlTab;
        // the SQL editor's buttons work on the editor in front; nothing to act on without one
        _tbRunAll.Enabled = _tbRunCur.Enabled = editor is { IsRunning: false };
        _tbSave.Enabled = _tbExportQuery.Enabled = editor is not null;
        _tbHistory.Enabled = view is not null;
        if (editor is { } t)
        {
            rows = t.StatusRows;
            time = t.StatusTime;
            _tbRun.Enabled = !t.IsRunning;
            _tbStop.Enabled = t.IsRunning;
        }
        else
        {
            _tbRun.Enabled = true;
            _tbStop.Enabled = false;
            if (view is not null && view.Tabs.SelectedTab == view.DataPage) { rows = view.DataBrowser.StatusRows; time = view.DataBrowser.StatusTime; }
        }
        _stRows.Text = rows ?? "";
        _stTime.Text = time ?? "";
        _stMsg.Text = _connecting ?? view?.Message ?? "";
        if (session?.Account.Id != _quickBarAccount) RefreshQuickBar();
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        var views = Views.ToList();
        if (views.Any(v => !v.ConfirmLeaveEdits())) { e.Cancel = true; return; }
        if (views.Any(v => v.IsRunning) && !Ui.Confirm(this, "Queries are still running. Cancel them and exit?"))
        {
            e.Cancel = true;
            return;
        }
        foreach (var v in views) v.Shutdown();
        foreach (var v in views) v.Session.Dispose();
    }
}
