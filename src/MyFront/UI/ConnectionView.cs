using MyFront.Core.Accounts;
using MyFront.Core.Db;
using MyFront.Core.Editors;
using MyFront.Core.History;
using MyFront.UI.Dialogs;

namespace MyFront.UI;

/// <summary>
/// One connection's workspace, a top-level tab: its own navigator tree on the left and its own
/// Object Browser / Data Browser / SQL Editor 1 … 5 (+ extra) tabs on the right. The tabs stay here while the connection is open.
/// </summary>
internal sealed class ConnectionView : TabPage
{
    private readonly MainForm _main;
    private readonly NavigatorTree _nav;
    private readonly ThemedTabControl _tabs;
    private readonly TabPage _objectPage, _dataPage;
    private readonly Panel _objectHost;
    private readonly ObjectBrowserView _objects;
    private readonly DataBrowserView _data;
    private readonly SplitContainer _split;
    private ProcessesView? _processes;
    private VariablesView? _variables;
    private readonly List<SqlEditorTab> _editors = new();
    private SqlEditorTab? _lastEditor;
    private NodeInfo? _current;
    private string? _lastDatabase;
    private bool _splitFitted;

    public ServerSession Session { get; }
    internal NavigatorTree Navigator => _nav;
    internal ThemedTabControl Tabs => _tabs;
    internal TabPage ObjectPage => _objectPage;
    internal TabPage DataPage => _dataPage;
    internal ObjectBrowserView ObjectBrowser => _objects;
    internal DataBrowserView DataBrowser => _data;
    /// <summary>This connection's editor tabs, in tab order.</summary>
    internal IReadOnlyList<SqlEditorTab> Editors => _editors;
    /// <summary>The node selected in this connection's tree.</summary>
    internal NodeInfo? Current => _current;
    internal SqlEditorTab? ActiveSqlTab => _tabs.SelectedTab as SqlEditorTab;
    internal bool IsRunning => _editors.Any(t => t.IsRunning);

    /// <summary>The last status bar message about this connection (<see cref="MainForm.ShowMessage"/>).</summary>
    public string Message { get; set; } = "";

    /// <summary>What the status bar or the window title shows of this connection changed.</summary>
    public event Action? StatusChanged;

    public ConnectionView(MainForm main, ServerSession session)
    {
        _main = main;
        Session = session;
        Text = session.DisplayName;
        ToolTipText = session.Summary;
        UseVisualStyleBackColor = true;

        _nav = new NavigatorTree { Dock = DockStyle.Fill };
        _nav.NodeSelected += OnNodeSelected;
        _nav.NodeActivated += OnNodeActivated;
        _nav.CommandRequested += OnTreeCommand;

        _objects = new ObjectBrowserView { Dock = DockStyle.Fill };
        _objects.EditFieldRequested += EditField;
        _objects.AddKeyRequested += AddKey;
        _objects.EditKeyRequested += EditKey;
        _objectHost = new Panel { Dock = DockStyle.Fill };
        _objectHost.Controls.Add(_objects);
        _data = new DataBrowserView { Dock = DockStyle.Fill };
        _data.StatusChanged += () => StatusChanged?.Invoke();
        _data.ExportRequested += _main.ShowExport;

        _objectPage = new TabPage("Object Browser") { UseVisualStyleBackColor = true };
        _objectPage.Controls.Add(_objectHost);
        _dataPage = new TabPage("Data Browser") { UseVisualStyleBackColor = true };
        _dataPage.Controls.Add(_data);
        _tabs = new ThemedTabControl { Dock = DockStyle.Fill };
        _tabs.TabPages.Add(_objectPage);
        _tabs.TabPages.Add(_dataPage);
        _tabs.SelectedIndexChanged += (_, _) => OnTabChanged();
        _tabs.MouseUp += OnTabsMouseUp;
        _objects.OpenObjectRequested += (node, name) => Ui.Fire(this, async () =>
        {
            if (node.Kind == NodeKind.Server) { await _nav.SelectDatabaseAsync(Session, name, expand: true); return; }
            var target = await _nav.SelectObjectAsync(Session, node.Database!, name);
            if (NavigatorTree.Info(target)?.IsTableLike == true) _tabs.SelectedTab = _dataPage;
        });

        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            SplitterWidth = LogicalToDeviceUnits(5),
        };
        _split.Panel1.Controls.Add(_nav);
        _split.Panel2.Controls.Add(_tabs);
        // the tree gets its width once the page has its real size
        _split.SizeChanged += (_, _) =>
        {
            if (_splitFitted || _split.Width < LogicalToDeviceUnits(500)) return;
            _split.SplitterDistance = LogicalToDeviceUnits(250);
            _splitFitted = true;
        };
        Controls.Add(_split);
    }

    /// <summary>Restores the editor tabs, then loads the tree and selects the default database.</summary>
    public async Task OpenAsync()
    {
        RestoreEditors();
        var node = _nav.AddSession(Session);
        _nav.SelectedNode = node;
        await _nav.LoadChildrenAsync(node);
        node.Expand();
        var db = Session.Account.DefaultDatabase;
        if (!string.IsNullOrEmpty(db)) await _nav.SelectDatabaseAsync(Session, db, expand: true);
    }

    /// <summary>The connection's database was changed in its settings: the tree shows only that one now (or all again).</summary>
    public async Task SetDefaultDatabaseAsync(string? database)
    {
        if (Session.Account.DefaultDatabase == database) return;
        Session.Account.DefaultDatabase = database;
        if (_nav.FindServer(Session) is not { } server) return;
        await _nav.RefreshNodeAsync(server);
        if (database is not null) await _nav.SelectDatabaseAsync(Session, database, expand: true);
        await _objects.ReloadAsync(); // the server's database list, if that is what it shows
    }

    /// <summary>The database queries of this connection run in: the selected one, else the last one, else the default.</summary>
    public string? CurrentDatabase => _current?.Database ?? _lastDatabase ?? Session.Account.DefaultDatabase;

    public void SelectDatabase(string database) => Ui.Fire(this, () => _nav.SelectDatabaseAsync(Session, database));

    // ---- navigation ---------------------------------------------------------------------------

    private void OnNodeSelected(NodeInfo? info)
    {
        _current = info;
        if (info?.Database is not null) _lastDatabase = info.Database;
        ShowObjectView(info);
        if (_tabs.SelectedTab == _dataPage) OpenDataBrowserForCurrent();
        StatusChanged?.Invoke();
    }

    /// <summary>
    /// A table or view the user clicked (or reached with the keys) while an SQL editor is in front shows in the Object Browser;
    /// in the Data Browser it simply opens there. Selections made by code (USE in the editor …) never leave the editor.
    /// </summary>
    private void OnNodeActivated(NodeInfo info)
    {
        if (info.Owner.IsTableLike && _tabs.SelectedTab is SqlEditorTab) _tabs.SelectedTab = _objectPage;
    }

    private void ShowObjectView(NodeInfo? info)
    {
        Control view;
        switch (info?.Kind)
        {
            case NodeKind.Processes:
                if (_processes is null) ThemeManager.Apply(_processes = new ProcessesView { Dock = DockStyle.Fill });
                view = _processes;
                Ui.Fire(this, () => _processes.ShowAsync(info.Session));
                break;
            case NodeKind.Variables:
                if (_variables is null) ThemeManager.Apply(_variables = new VariablesView { Dock = DockStyle.Fill });
                view = _variables;
                Ui.Fire(this, () => _variables.ShowAsync(info.Session));
                break;
            default:
                view = _objects;
                _objects.ShowNode(info);
                break;
        }
        if (view != _processes) _processes?.Pause();
        if (_objectHost.Controls.Count != 1 || _objectHost.Controls[0] != view)
        {
            _objectHost.Controls.Clear();
            _objectHost.Controls.Add(view);
        }
        if (info?.Kind is NodeKind.Processes or NodeKind.Variables) _tabs.SelectedTab = _objectPage;
    }

    private void OpenDataBrowserForCurrent()
    {
        var info = _current?.Owner; // a key or field: its table
        if (info is { IsTableLike: true })
            Ui.Fire(this, () => _data.OpenAsync(info.Session, info.Database!, info.Name!));
    }

    private void OnTabChanged()
    {
        if (_tabs.SelectedTab == _dataPage) OpenDataBrowserForCurrent();
        if (ActiveSqlTab is { Slot: > 0 } editor) _lastEditor = editor;
        StatusChanged?.Invoke();
        if (ActiveSqlTab is { } t && _main.IsHandleCreated) _main.BeginInvoke(() => t.Editor.Focus());
    }

    /// <summary>Another connection's tab was selected.</summary>
    public void Deactivate() => _processes?.Pause();

    public void ShowObjectBrowser() => _tabs.SelectedTab = _objectPage;

    public void ShowDataBrowser() => _tabs.SelectedTab = _dataPage;

    public void SelectServerChild(NodeKind kind)
    {
        var server = _nav.FindServer(Session);
        Ui.Fire(this, async () =>
        {
            if (server is null) return;
            if (server.Nodes.Count == 1) await _nav.LoadChildrenAsync(server);
            server.Expand();
            var node = server.Nodes.Cast<TreeNode>().FirstOrDefault(n => NavigatorTree.Info(n)?.Kind == kind);
            if (node is not null) _nav.SelectedNode = node;
        });
    }

    public async Task OpenTableAsync(string database, string table)
    {
        await _nav.SelectObjectAsync(Session, database, table);
        _tabs.SelectedTab = _dataPage;
        OpenDataBrowserForCurrent();
    }

    private void OnTreeCommand(NodeInfo info, string command)
    {
        switch (command)
        {
            case "browse":
                _tabs.SelectedTab = _dataPage;
                OpenDataBrowserForCurrent();
                break;
            case "newsql":
                _main.NewSqlTab(Session);
                break;
            case "copycreate":
                Ui.Fire(this, () => _main.CopyCreateAsync(info));
                break;
            case "editfield":
                if (info is { Kind: NodeKind.Field, Child: { } field }) EditField(info.Owner, field);
                break;
            case "renamefield":
                RenameField(info);
                break;
            case "editkey":
                if (info is { Kind: NodeKind.Key, Child: { } key }) EditKey(info.Owner, key);
                break;
            case "copyname":
                if (info.PlainName is { } plain)
                {
                    Clipboard.SetText(plain);
                    _main.ShowMessage(this, $"\"{plain}\" copied to the clipboard");
                }
                break;
            case "properties":
                if (info is { IsTableLike: true, Database: { } db, Name: { } name })
                {
                    using var dlg = new TablePropertiesForm(Session, db, name);
                    dlg.ShowDialog(FindForm());
                }
                break;
            case "export-xlsx":
            case "export-csv":
                Ui.Fire(this, () => _main.ExportNodeAsync(info, command[7..]));
                break;
            case "import":
                if (info.Database is not null) _main.ShowImport(Session, info.Database, info.Kind == NodeKind.Table ? info.Name : null);
                break;
            case "export-sql":
                Ui.Fire(this, () => ExportSqlAsync(info));
                break;
            case "import-sql":
                ImportSql(info);
                break;
            case "hide":
            case "unhide":
                SetHidden(info, command == "hide");
                break;
            case "rename":
                RenameObject(info);
                break;
            case "drop":
                DropObject(info);
                break;
            case "refresh":
                Ui.Fire(this, RefreshAsync);
                break;
            case "close":
                _main.CloseSession(Session);
                break;
        }
    }

    /// <summary>F5: the open Data Browser page, Processes / Variables, or the selected tree node and the Object Browser.</summary>
    public async Task RefreshAsync()
    {
        if (_tabs.SelectedTab == _dataPage && _data.IsOpen)
        {
            if (_data.ConfirmLeave()) await _data.RefreshAsync();
            return;
        }
        if (_current?.Kind == NodeKind.Processes && _processes is not null) { await _processes.LoadAsync(); return; }
        if (_current?.Kind == NodeKind.Variables && _variables is not null) { await _variables.LoadAsync(); return; }
        var node = _nav.SelectedNode;
        if (node is not null) await _nav.RefreshNodeAsync(node);
        await _objects.ReloadAsync();
    }

    /// <summary>After CREATE/DROP/ALTER: reload that database's node (or the server node when database is null).</summary>
    public void RefreshAfterDdl(string? database)
    {
        var server = _nav.FindServer(Session);
        if (server is null) return;
        var dbNode = database is null ? null
            : server.Nodes.Cast<TreeNode>().FirstOrDefault(n => NavigatorTree.Info(n) is { Kind: NodeKind.Database } i && i.Database == database);
        Ui.Fire(this, async () =>
        {
            await _nav.RefreshNodeAsync(dbNode ?? server);
            await _objects.ReloadAsync();
        });
    }

    // ---- table structure ------------------------------------------------------------------------

    /// <summary>Changing a table's structure: not on read-only connections, not for views.</summary>
    private bool CanAlter(NodeInfo node)
    {
        if (Session.ReadOnly)
        {
            Ui.Info(FindForm(), $"{Session.DisplayName} is a read-only connection, so the structure of {node.Name} can't be changed.");
            return false;
        }
        if (node.Kind == NodeKind.View)
        {
            Ui.Info(FindForm(), $"{node.Name} is a view: its fields come from its SELECT, so they can't be edited here.");
            return false;
        }
        return node is { Kind: NodeKind.Table, Database: not null, Name: not null };
    }

    /// <summary>Rename / delete / import: refused (with a message) on a read-only connection.</summary>
    private bool WriteAllowed(string what)
    {
        if (!Session.ReadOnly) return true;
        Ui.Info(FindForm(), $"{Session.DisplayName} is a read-only connection, so {what} is not allowed.");
        return false;
    }

    private bool DataBrowserShows(string database, string table) =>
        _data.Session == Session && _data.Database == database && _data.Table == table;

    private TreeNode? DatabaseNode(string database) =>
        _nav.FindServer(Session)?.Nodes.Cast<TreeNode>().FirstOrDefault(n => NavigatorTree.Info(n) is { Kind: NodeKind.Database } i && i.Database == database);

    /// <summary>Hide / Unhide a table or view in the tree; kept with the connection (and in its Export… file).</summary>
    private void SetHidden(NodeInfo info, bool hidden)
    {
        if (info is not { IsTableLike: true, Database: { } db, Name: { } name }) return;
        Session.Account.SetHidden(db, name, hidden);
        SaveHidden();
        _nav.MoveHidden(Session, db, name);
        _main.ShowMessage(this, hidden ? $"{name} hidden — in the Hidden folder at the bottom of {db}" : $"{name} is back in {db}");
    }

    private void SaveHidden()
    {
        try { AccountStore.SaveHiddenTables(Session.Account); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { Ui.Error(FindForm(), ex); }
    }

    private void RenameObject(NodeInfo info)
    {
        if (info is not { IsTableLike: true, Database: { } db, Name: { } name } || !WriteAllowed("renaming")) return;
        using var dlg = new RenameForm(Session, db, name, info.Kind == NodeKind.View);
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        if (DataBrowserShows(db, name)) _data.Close();
        Session.Schema.Invalidate(db, name);
        var newName = dlg.NewName;
        if (Session.Account.IsHidden(db, name))
        {
            Session.Account.RenameHidden(db, name, newName);
            SaveHidden();
        }
        Ui.Fire(this, async () =>
        {
            if (DatabaseNode(db) is { } dbNode) await _nav.RefreshNodeAsync(dbNode);
            await _nav.SelectObjectAsync(Session, db, newName);
        });
    }

    private void DropObject(NodeInfo info)
    {
        if (info is not { IsTableLike: true, Database: { } db, Name: { } name } || !WriteAllowed("deleting")) return;
        bool view = info.Kind == NodeKind.View;
        var sql = TableDdl.Drop(db, name, view);
        var text = view
            ? $"Delete the view {db}.{name}? Its definition is gone afterwards (the tables it reads stay)."
            : $"Delete the table {db}.{name} with all its rows, keys and triggers? This can't be undone.";
        if (!Ui.Confirm(FindForm(), text + "\n\n" + sql, view ? "Delete view" : "Delete table", warning: true)) return;
        Ui.Fire(this, async () =>
        {
            await TableDdl.ExecuteAsync(Session, db, sql, view ? "delete view" : "delete table");
            if (DataBrowserShows(db, name)) _data.Close();
            Session.Schema.Invalidate(db, name);
            if (Session.Account.IsHidden(db, name))
            {
                Session.Account.SetHidden(db, name, false);
                SaveHidden();
            }
            RefreshAfterDdl(db);
        });
    }

    /// <summary>Tree → a field → Rename…: CHANGE COLUMN with the field's whole definition, only the name changed.</summary>
    private void RenameField(NodeInfo info)
    {
        if (info is not { Kind: NodeKind.Field, Child: { } column } || !CanAlter(info.Owner)) return;
        using var dlg = new RenameForm(Session, info.Database!, info.Name!, column);
        if (dlg.ShowDialog(FindForm()) == DialogResult.OK) AfterTableChanged(info.Database!, info.Name!);
    }

    private async Task ExportSqlAsync(NodeInfo info)
    {
        if (info is not { IsTableLike: true, Database: { } db, Name: { } name }) return;
        bool view = info.Kind == NodeKind.View;
        long? rows = view ? null : (await Session.Schema.GetTableStatusAsync(db)).FirstOrDefault(t => t.Name == name)?.Rows;
        using var dlg = new SqlExportForm(Session, db, name, view, rows);
        dlg.ShowDialog(FindForm());
    }

    /// <summary>Database → Import SQL…: runs a .sql file (a MyFront export, a dump …) in that database.</summary>
    private void ImportSql(NodeInfo info)
    {
        if (info is not { Kind: NodeKind.Database, Database: { } db } || !WriteAllowed("importing")) return;
        using var dlg = new RunSqlFileForm(Session, db, null, "Import SQL into " + db);
        dlg.ShowDialog(FindForm());
        Session.Schema.Invalidate(db);
        RefreshAfterDdl(db);
    }

    private void EditField(NodeInfo node, string column)
    {
        if (!CanAlter(node)) return;
        using var dlg = new EditFieldForm(Session, node.Database!, node.Name!, column);
        if (dlg.ShowDialog(FindForm()) == DialogResult.OK) AfterTableChanged(node.Database!, node.Name!);
    }

    private void AddKey(NodeInfo node)
    {
        if (!CanAlter(node)) return;
        using var dlg = new AddKeyForm(Session, node.Database!, node.Name!);
        if (dlg.ShowDialog(FindForm()) == DialogResult.OK) AfterTableChanged(node.Database!, node.Name!);
    }

    /// <summary>Edit key…: the Add key dialog filled with the key; OK runs DROP …, ADD … in one ALTER TABLE.</summary>
    private void EditKey(NodeInfo node, string key)
    {
        if (!CanAlter(node)) return;
        using var dlg = new AddKeyForm(Session, node.Database!, node.Name!, key);
        if (dlg.ShowDialog(FindForm()) == DialogResult.OK) AfterTableChanged(node.Database!, node.Name!);
    }

    /// <summary>After ALTER TABLE: fresh metadata, tree, Object Browser, and the Data Browser if it shows that table.</summary>
    internal void AfterTableChanged(string database, string table)
    {
        Session.Schema.Invalidate(database, table);
        RefreshAfterDdl(database);
        if (_data.Session == Session && _data.Database == database && _data.Table == table)
            Ui.Fire(this, _data.RefreshAsync);
    }

    // ---- SQL editor tabs ----------------------------------------------------------------------

    /// <summary>SQL Editor 1 … 5 plus the extra tabs this connection had, with their autosaved text.</summary>
    private void RestoreEditors()
    {
        var id = Session.Account.Id;
        EditorSet set;
        try { set = EditorStore.Load(id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _main.ShowMessage(this, "Saved SQL editor tabs could not be read: " + ex.Message);
            set = new EditorSet { Tabs = Enumerable.Range(1, EditorStore.FixedTabs).Select(i => new EditorTabState { Slot = i }).ToList() };
        }
        foreach (var state in set.Tabs)
        {
            var tab = CreateSqlTab(state.Slot);
            string text;
            try { text = EditorStore.ReadText(id, state.Slot); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { text = ""; _main.ShowMessage(this, ex.Message); }
            tab.Restore(state, text);
            _main.UpdateTabTitle(tab);
            if (state.Slot == set.Selected) _lastEditor = tab;
        }
        // closed while an editor was in front → opens in that editor again
        if (set.EditorActive && _lastEditor is not null) _tabs.SelectedTab = _lastEditor;
    }

    /// <summary>Saves every tab's text and the tab list (on close, when leaving the app, when tabs are added or closed).</summary>
    public void SaveEditors()
    {
        foreach (var t in _editors) t.SaveNow();
        var active = ActiveSqlTab is { Slot: > 0 } a ? a : null;
        var selected = (active ?? _lastEditor)?.Slot ?? 1;
        try
        {
            EditorStore.SaveIndex(Session.Account.Id, new EditorSet
            {
                Tabs = _editors.Where(t => t.Slot > 0).Select(t => t.State).ToList(),
                Selected = selected > 0 ? selected : 1,
                EditorActive = active is not null,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _main.ShowMessage(this, "SQL editor tabs could not be saved: " + ex.Message);
        }
    }

    /// <summary>A new editor tab of this connection; slot 0 = not saved (quick query results).</summary>
    internal SqlEditorTab CreateSqlTab(int slot)
    {
        var tab = new SqlEditorTab(_main, Session) { Slot = slot };
        tab.BaseTitle = tab.DefaultTitle;
        tab.StatusChanged += () => { if (_tabs.SelectedTab == tab) StatusChanged?.Invoke(); };
        ThemeManager.Apply(tab);
        _editors.Add(tab);
        _tabs.TabPages.Add(tab);
        _main.UpdateTabTitle(tab);
        return tab;
    }

    /// <summary>A new extra tab (SQL Editor 6, 7 …), autosaved like the fixed ones.</summary>
    internal SqlEditorTab NewSqlTab()
    {
        var used = _editors.Select(t => t.Slot).Append(EditorStore.FixedTabs);
        var tab = CreateSqlTab(used.Max() + 1);
        SaveEditors();
        return tab;
    }

    internal void ShowSqlTab(SqlEditorTab tab) => _tabs.SelectedTab = tab;

    internal void ShowFirstEditor()
    {
        if (_editors.FirstOrDefault() is { } t) ShowSqlTab(t);
        else _main.NewSqlTab(Session);
    }

    internal void CloseSqlTab(SqlEditorTab tab, bool force = false)
    {
        if (!force && tab.IsFixed)
        {
            _main.ShowMessage(this, "SQL Editor 1–5 always stay; their text is saved automatically. Extra tabs (Ctrl+T) can be closed.");
            return;
        }
        if (!force && tab.IsRunning && !Ui.Confirm(FindForm(), "A query is running in this tab. Cancel it and close the tab?")) return;
        tab.Stop();
        if (force) tab.SaveNow();
        else if (tab.Slot > 0)
        {
            // closed for good, without a prompt: its text stays findable in SQL history (Ctrl+H)
            var text = tab.Editor.Text;
            if (text.Trim().Length > 0)
                SqlHistory.Append(Session.Account.Id, CurrentDatabase, text, $"closed tab {tab.BaseTitle}");
            EditorStore.Remove(Session.Account.Id, tab.Slot);
        }
        _editors.Remove(tab);
        if (_lastEditor == tab) _lastEditor = null;
        _tabs.TabPages.Remove(tab);
        tab.Dispose();
        if (!force && tab.Slot > 0) SaveEditors();
    }

    private void OnTabsMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Middle) return;
        for (int i = 0; i < _tabs.TabCount; i++)
            if (_tabs.GetTabRect(i).Contains(e.Location) && _tabs.TabPages[i] is SqlEditorTab t)
                CloseSqlTab(t);
    }

    // ---- closing --------------------------------------------------------------------------------

    /// <summary>Unsaved rows of the Data Browser and of the editors' result grids are confirmed. False = stay.</summary>
    public bool ConfirmLeaveEdits() => _data.ConfirmLeave() && _editors.All(e => e.ConfirmDiscardEdits());

    /// <summary>Unsaved rows and running queries are confirmed first. False = keep the connection open.</summary>
    public bool ConfirmClose()
    {
        if (!ConfirmLeaveEdits()) return false;
        return !IsRunning || Ui.Confirm(FindForm(), $"Queries are still running on {Session.DisplayName}. Cancel them and close the connection?");
    }

    /// <summary>Stops the queries and saves the editors; the caller disposes the view and the session.</summary>
    public void Shutdown()
    {
        foreach (var t in _editors) t.Stop();
        SaveEditors(); // what you typed is there next time, no "save?" prompt
        _processes?.Pause();
        _data.Close();
    }
}
