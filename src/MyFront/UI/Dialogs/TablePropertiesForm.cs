using MyFront.Core.Db;

namespace MyFront.UI.Dialogs;

/// <summary>"Properties of …" for a table or view: General, Information, Fields, Keys, Foreign Keys, Triggers, Partitions, Dependencies, Source Code.</summary>
internal sealed class TablePropertiesForm : Form
{
    private readonly ServerSession _session;
    private readonly string _database, _table;
    private readonly ThemedTabControl _tabs;
    private readonly Label _state;
    private readonly CancellationTokenSource _cts = new();
    private TableProperties? _props;
    private TabPage? _dependencyPage;
    private ListView? _dependencies;
    private bool _dependenciesLoaded;

    internal TableProperties? Properties => _props;
    internal ThemedTabControl Tabs => _tabs;
    /// <summary>Completes when the first load has finished (self-test).</summary>
    internal Task Loaded { get; private set; } = Task.CompletedTask;
    internal Task DependenciesLoaded { get; private set; } = Task.CompletedTask;

    public TablePropertiesForm(ServerSession session, string database, string table)
    {
        _session = session;
        _database = database;
        _table = table;
        Text = "Properties of " + table;
        Icon = Ui.AppIcon;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.None;
        Size = new Size(W(720), W(560));
        MinimumSize = new Size(W(480), W(360));
        KeyPreview = true;

        _tabs = new ThemedTabControl { Dock = DockStyle.Fill };
        _state = new Label { Dock = DockStyle.Top, AutoSize = false, Height = W(22), Text = "Loading…", ForeColor = SystemColors.GrayText, TextAlign = ContentAlignment.MiddleLeft };
        var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(W(84), W(26)) };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(W(6)),
        };
        buttons.Controls.Add(close);
        CancelButton = close;
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(W(8), W(4), W(8), 0) };
        body.Controls.Add(_tabs);
        body.Controls.Add(_state);
        Controls.Add(body);
        Controls.Add(buttons);
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            if (_tabs.SelectedTab == _dependencyPage && !_dependenciesLoaded) DependenciesLoaded = LoadDependenciesAsync();
        };
        FormClosed += (_, _) => _cts.Cancel();
    }

    private int W(int logical) => LogicalToDeviceUnits(logical);

    protected override void OnLoad(EventArgs e)
    {
        ThemeManager.Apply(this);
        base.OnLoad(e);
        Loaded = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _props = await TableProperties.LoadAsync(_session, _database, _table, _cts.Token);
        }
        catch (Exception ex) when (!IsDisposed)
        {
            if (ex is OperationCanceledException) return;
            _state.ForeColor = ThemeManager.Current.Error;
            _state.Text = Ui.Describe(ex).Replace('\n', ' ');
            return;
        }
        if (IsDisposed) return;
        var p = _props;
        _state.Visible = false;
        Text = $"Properties of {(p.IsView ? "view" : "table")} {p.Name}";
        _tabs.SuspendLayout();
        _tabs.TabPages.Add(GeneralPage(p));
        if (!p.IsView) _tabs.TabPages.Add(InformationPage(p));
        _tabs.TabPages.Add(ListPage("Fields", new[] { ("#", 34), ("Name", 150), ("Type", 150), ("Null", 44), ("Default", 110), ("Extra", 110), ("Key", 44), ("Collation", 140), ("Comment", 200) },
            p.Fields.Select((f, i) => new[] { (i + 1).ToString(), f.Name, f.Type, f.Nullable ? "YES" : "NO", f.Default ?? (f.Nullable ? "NULL" : ""), f.Extra, f.Key, f.Collation ?? "", f.Comment }),
            "no fields"));
        if (!p.IsView)
        {
            _tabs.TabPages.Add(ListPage("Keys", new[] { ("Name", 150), ("Columns", 240), ("Unique", 56), ("Type", 80), ("Comment", 160) },
                p.Keys.Select(k => new[] { k.Name, k.Columns, k.Unique ? "yes" : "", k.Type, k.Comment }), "no keys (indexes)"));
            _tabs.TabPages.Add(ListPage("Foreign Keys", new[] { ("Name", 160), ("Columns", 150), ("References", 220), ("On delete", 90), ("On update", 90) },
                p.ForeignKeys.Select(f => new[] { f.Name, f.Columns, f.References, f.OnDelete, f.OnUpdate }), "no foreign keys"));
            _tabs.TabPages.Add(ListPage("Triggers", new[] { ("Name", 150), ("Timing", 64), ("Event", 64), ("Statement", 330), ("Definer", 130) },
                p.Triggers.Select(t => new[] { t.Name, t.Timing, t.Event, OneLine(t.Statement), t.Definer }), "no triggers"));
            _tabs.TabPages.Add(ListPage("Partitions", new[] { ("Name", 120), ("Method", 90), ("Expression", 140), ("Value", 120), ("Rows", 80), ("Data size", 80), ("Comment", 140) },
                p.Partitions.Select(x => new[] { x.Name, x.Method, x.Expression, x.Value, x.Rows?.ToString("N0") ?? "", CellFormat.FormatBytes(x.DataLength), x.Comment }),
                "not partitioned"));
            _dependencies = NewList(new[] { ("Type", 170), ("Object", 200), ("Detail", 300) });
            _dependencyPage = Page("Dependencies", _dependencies);
            _tabs.TabPages.Add(_dependencyPage);
        }
        _tabs.TabPages.Add(SourcePage(p.CreateStatement));
        _tabs.ResumeLayout();
        ThemeManager.Apply(_tabs);
    }

    private async Task LoadDependenciesAsync()
    {
        if (_props is null || _dependencies is null) return;
        _dependenciesLoaded = true;
        _dependencies.Items.Add(new ListViewItem("Loading…") { ForeColor = ThemeManager.Current.GrayText });
        try
        {
            var deps = await _props.LoadDependenciesAsync(_session, _cts.Token);
            if (IsDisposed) return;
            Fill(_dependencies, deps.Select(d => new[] { d.Kind, d.Object, d.Detail }), "nothing in this database uses it");
        }
        catch (Exception ex) when (!IsDisposed && ex is not OperationCanceledException)
        {
            _dependencies.Items.Clear();
            _dependencies.Items.Add(new ListViewItem(Ui.Describe(ex)) { ForeColor = ThemeManager.Current.Error });
            _dependenciesLoaded = false;
        }
    }

    private static string OneLine(string s) => string.Join(" ", s.Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();

    private TabPage Page(string title, Control content)
    {
        var page = new TabPage(title) { UseVisualStyleBackColor = true, Padding = new Padding(W(6)) };
        content.Dock = DockStyle.Fill;
        page.Controls.Add(content);
        return page;
    }

    // ---- General / Information: label + read-only (selectable) value rows ----------------------

    private TableLayoutPanel Grid()
    {
        var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(W(4)) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    private void Section(TableLayoutPanel t, string title)
    {
        var r = t.RowCount++;
        var l = new Label { Text = title, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, r == 0 ? W(2) : W(12), 0, W(4)) };
        t.Controls.Add(l, 0, r);
        t.SetColumnSpan(l, 2);
    }

    private void Value(TableLayoutPanel t, string label, string? value)
    {
        var r = t.RowCount++;
        t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(W(12), W(5), W(12), 0) }, 0, r);
        t.Controls.Add(new TextBox { Text = value ?? "", ReadOnly = true, Width = W(320), Anchor = AnchorStyles.Left, Margin = new Padding(0, W(2), 0, W(2)) }, 1, r);
    }

    private TabPage GeneralPage(TableProperties p)
    {
        var t = Grid();
        Section(t, "Description");
        Value(t, "Name", p.Name);
        Value(t, "Database", p.Database);
        if (p.IsView)
        {
            Value(t, "Definer", p.Definer);
            Value(t, "SQL security", p.SecurityType);
            Value(t, "Check option", p.CheckOption);
            Value(t, "Updatable", p.Updatable);
            if (p.Comment.Length > 0) Value(t, "Comment", p.Comment);
        }
        else
        {
            Value(t, "Engine", p.Engine);
            Value(t, "Character set", p.CharacterSet);
            Value(t, "Collation", p.Collation);
            Value(t, "Comment", p.Comment);
            Section(t, "Rows");
            Value(t, "Row format", p.RowFormat);
            Value(t, "Auto increment", p.AutoIncrement?.ToString("N0"));
        }
        return Page("General", Scrollable(t));
    }

    private TabPage InformationPage(TableProperties p)
    {
        var t = Grid();
        Section(t, "Dates");
        Value(t, "Created", Date(p.Created));
        Value(t, "Updated", Date(p.Updated));
        Value(t, "Checked", Date(p.Checked));
        Section(t, "Size");
        var estimate = p.Engine?.Equals("InnoDB", StringComparison.OrdinalIgnoreCase) == true;
        Value(t, "Rows", p.Rows is null ? "" : (estimate ? "≈ " : "") + p.Rows.Value.ToString("N0") + (estimate ? "  (InnoDB estimate)" : ""));
        Value(t, "Average row length", CellFormat.FormatBytes(p.AvgRowLength));
        Value(t, "Data", CellFormat.FormatBytes(p.DataLength));
        Value(t, "Indexes", CellFormat.FormatBytes(p.IndexLength));
        Value(t, "Total", CellFormat.FormatBytes(p.DataLength + p.IndexLength));
        Value(t, "Free (reclaimable)", CellFormat.FormatBytes(p.DataFree));
        Section(t, "Other");
        Value(t, "Table type", p.TableType);
        Value(t, "Create options", p.CreateOptions);
        return Page("Information", Scrollable(t));
    }

    private static string Date(DateTime? d) => d?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";

    private static Panel Scrollable(Control inner)
    {
        var p = new Panel { AutoScroll = true };
        p.Controls.Add(inner);
        return p;
    }

    // ---- lists -----------------------------------------------------------------------------------

    private ListView NewList((string Title, int Width)[] columns)
    {
        var lv = new ListView
        {
            View = View.Details, FullRowSelect = true, HideSelection = false, BorderStyle = BorderStyle.FixedSingle,
            ShowItemToolTips = true, GridLines = false,
        };
        foreach (var (title, width) in columns) lv.Columns.Add(title, W(width));
        lv.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.C && e.Control && lv.SelectedItems.Count > 0)
            {
                Clipboard.SetText(string.Join(Environment.NewLine, lv.SelectedItems.Cast<ListViewItem>()
                    .Select(i => string.Join("\t", i.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text)))));
                e.Handled = true;
            }
        };
        return lv;
    }

    private static void Fill(ListView lv, IEnumerable<string[]> rows, string empty)
    {
        lv.BeginUpdate();
        lv.Items.Clear();
        foreach (var row in rows) lv.Items.Add(new ListViewItem(row) { ToolTipText = string.Join("  ·  ", row.Where(x => x.Length > 0)) });
        if (lv.Items.Count == 0) lv.Items.Add(new ListViewItem("(" + empty + ")") { ForeColor = ThemeManager.Current.GrayText });
        lv.EndUpdate();
    }

    private TabPage ListPage(string title, (string, int)[] columns, IEnumerable<string[]> rows, string empty)
    {
        var lv = NewList(columns);
        Fill(lv, rows, empty);
        var page = Page(title, lv);
        page.Tag = lv;
        return page;
    }

    private TabPage SourcePage(string sql)
    {
        ScintillaLoader.Ensure();
        var editor = new SqlEditor { Text = sql, ReadOnly = true };
        return Page("Source Code", editor);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Ctrl+Tab / Ctrl+Shift+Tab move between the tabs like in other Windows dialogs
        if (keyData is (Keys.Control | Keys.Tab) or (Keys.Control | Keys.Shift | Keys.Tab) && _tabs.TabCount > 0)
        {
            var step = keyData.HasFlag(Keys.Shift) ? _tabs.TabCount - 1 : 1;
            _tabs.SelectedIndex = (_tabs.SelectedIndex + step) % _tabs.TabCount;
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
