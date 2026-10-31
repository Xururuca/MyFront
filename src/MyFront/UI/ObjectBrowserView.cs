using System.Globalization;
using MyFront.Core.Db;
using MySqlConnector;

namespace MyFront.UI;

/// <summary>The "Object Browser": lists the children of the selected navigator node.</summary>
internal sealed class ObjectBrowserView : UserControl
{
    private readonly ListView _list;
    private readonly ListView _indexes;
    private readonly SplitContainer _split;
    private readonly TextBox _definition;
    private readonly Label _status;
    private int _version;
    private NodeInfo? _node;
    /// <summary><see cref="_list"/> lists the fields of a table or view (not databases or tables).</summary>
    private bool _fieldsShown;

    /// <summary>Double-click on a table row in the database view.</summary>
    public event Action<NodeInfo, string>? OpenObjectRequested;
    /// <summary>Right-click (or double-click) a field of a table → Edit field….</summary>
    public event Action<NodeInfo, string>? EditFieldRequested;
    /// <summary>Right-click the field or index list of a table → Add key….</summary>
    public event Action<NodeInfo>? AddKeyRequested;
    /// <summary>Right-click (or double-click) a key in the index list → Edit key….</summary>
    public event Action<NodeInfo, string>? EditKeyRequested;

    public ObjectBrowserView()
    {
        _list = NewList();
        _indexes = NewList();
        _list.DoubleClick += (_, _) =>
        {
            if (_list.SelectedItems.Count != 1) return;
            if (_node is { Kind: NodeKind.Database or NodeKind.Server })
                OpenObjectRequested?.Invoke(_node, _list.SelectedItems[0].Text);
            else if (_node is { Kind: NodeKind.Table } && _fieldsShown)
                EditFieldRequested?.Invoke(_node, _list.SelectedItems[0].Text);
        };
        _indexes.DoubleClick += (_, _) =>
        {
            if (_indexes.SelectedItems.Count == 1 && _node is { Kind: NodeKind.Table } && _fieldsShown)
                EditKeyRequested?.Invoke(_node, _indexes.SelectedItems[0].Text);
        };
        _list.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) ShowTableMenu(_list, e.Location); };
        _indexes.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) ShowTableMenu(_indexes, e.Location); };
        _split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Panel2Collapsed = true, BorderStyle = BorderStyle.None };
        _split.Panel1.Controls.Add(_list);
        _split.Panel2.Controls.Add(_indexes);
        _definition = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
            Font = Ui.Mono(10), BackColor = SystemColors.Window, Visible = false, BorderStyle = BorderStyle.None,
        };
        _status = new Label { Dock = DockStyle.Bottom, Height = LogicalToDeviceUnits(20), ForeColor = SystemColors.GrayText, TextAlign = ContentAlignment.MiddleLeft };
        Controls.Add(_split);
        Controls.Add(_definition);
        Controls.Add(_status);
    }

    private ListView NewList()
    {
        var lv = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false,
            BorderStyle = BorderStyle.None, SmallImageList = Icons.TreeImages(LogicalToDeviceUnits(16)),
        };
        lv.ColumnClick += (_, e) => SortList(lv, e.Column);
        return lv;
    }

    private void ShowTableMenu(ListView list, Point location)
    {
        var m = BuildTableMenu(list, location);
        if (m is null) return;
        m.Closed += (_, _) => BeginInvoke(m.Dispose);
        m.Show(list, location);
    }

    /// <summary>
    /// Right-click in the field or index list of a table or view: Edit field… on a field, Edit key… on a key, Add key…
    /// anywhere (also on an empty area); null = no menu there.
    /// </summary>
    internal ContextMenuStrip? BuildTableMenu(ListView list, Point location)
    {
        if (_node is not { IsTableLike: true } node || !_fieldsShown) return null;
        var hit = list.HitTest(location).Item;
        if (hit is not null) hit.Selected = true;
        var m = new ContextMenuStrip();
        if (hit is not null && list == _list)
        {
            m.Items.Add(new ToolStripMenuItem("Edit field…", null, (_, _) => EditFieldRequested?.Invoke(node, hit.Text)) { ShortcutKeyDisplayString = "double-click" });
            m.Items.Add(new ToolStripSeparator());
        }
        if (hit is not null && list == _indexes)
        {
            m.Items.Add(new ToolStripMenuItem("Edit key…", null, (_, _) => EditKeyRequested?.Invoke(node, hit.Text)) { ShortcutKeyDisplayString = "double-click" });
            m.Items.Add(new ToolStripSeparator());
        }
        m.Items.Add(new ToolStripMenuItem("Add key…", null, (_, _) => AddKeyRequested?.Invoke(node)));
        return m;
    }

    public void ShowNode(NodeInfo? node) => Ui.Fire(this, () => ShowNodeAsync(node));

    /// <summary>Shows a node's children; a key or field node shows its table with that key or field selected.</summary>
    public async Task ShowNodeAsync(NodeInfo? node)
    {
        int version = ++_version;
        var child = node?.Kind is NodeKind.Key or NodeKind.Field ? node : null;
        node = node?.Owner;
        _node = node;
        _status.Text = node is null ? "" : "Loading…";
        try
        {
            switch (node?.Kind)
            {
                case NodeKind.Server: await ShowDatabasesAsync(node, version); break;
                case NodeKind.Database: await ShowTablesAsync(node, version); break;
                case NodeKind.Table or NodeKind.View:
                    await ShowColumnsAsync(node, version);
                    if (child is not null && version == _version) SelectItem(child.Kind == NodeKind.Field ? _list : _indexes, child.Child!);
                    break;
                case NodeKind.Procedure or NodeKind.Function: await ShowDefinitionAsync(node, version); break;
                default:
                    Reset(Array.Empty<(string, int, bool)>());
                    _status.Text = "";
                    break;
            }
        }
        catch when (version != _version) { /* selection moved on */ }
    }

    public Task ReloadAsync() => ShowNodeAsync(_node);

    private void SelectItem(ListView list, string name)
    {
        _list.SelectedItems.Clear();
        _indexes.SelectedItems.Clear();
        if (list.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Text == name) is not { } item) return;
        item.Selected = true;
        item.Focused = true;
        item.EnsureVisible();
    }

    private void Reset((string Title, int Width, bool Right)[] columns, ListView? list = null)
    {
        list ??= _list;
        _definition.Visible = false;
        _split.Visible = true;
        list.BeginUpdate();
        list.Items.Clear();
        list.Columns.Clear();
        list.ListViewItemSorter = null;
        if (list == _list) _fieldsShown = false;
        foreach (var (title, width, right) in columns)
            list.Columns.Add(title, LogicalToDeviceUnits(width), right ? HorizontalAlignment.Right : HorizontalAlignment.Left);
        list.EndUpdate();
    }

    private async Task ShowDatabasesAsync(NodeInfo node, int version)
    {
        const string sql = """
            SELECT s.SCHEMA_NAME, s.DEFAULT_CHARACTER_SET_NAME, s.DEFAULT_COLLATION_NAME,
                   COUNT(t.TABLE_NAME), SUM(t.DATA_LENGTH + t.INDEX_LENGTH)
            FROM information_schema.SCHEMATA s
            LEFT JOIN information_schema.TABLES t ON t.TABLE_SCHEMA = s.SCHEMA_NAME
            GROUP BY s.SCHEMA_NAME, s.DEFAULT_CHARACTER_SET_NAME, s.DEFAULT_COLLATION_NAME
            ORDER BY s.SCHEMA_NAME
            """;
        var rows = new List<object?[]>();
        await using (var conn = await node.Session.OpenAsync(null))
        await using (var cmd = new MySqlCommand(sql, conn))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                var v = new object?[5];
                r.GetValues(v!);
                if (node.Session.Account.ShowsDatabase(Convert.ToString(v[0]) ?? "")) rows.Add(v.Select(CellFormat.Normalize).ToArray());
            }
        }
        if (version != _version) return;
        _split.Panel2Collapsed = true;
        Reset(new[] { ("Database", 220, false), ("Tables", 70, true), ("Size", 90, true), ("Charset", 90, false), ("Collation", 170, false) });
        _list.BeginUpdate();
        foreach (var v in rows)
        {
            var size = v[4] is null ? (long?)null : Convert.ToInt64(v[4], CultureInfo.InvariantCulture);
            AddItem(_list, "database", new object?[] { v[0], v[3], size, v[1], v[2] },
                new[] { Convert.ToString(v[0]) ?? "", Convert.ToString(v[3]) ?? "", CellFormat.FormatBytes(size), Convert.ToString(v[1]) ?? "", Convert.ToString(v[2]) ?? "" });
        }
        _list.EndUpdate();
        _status.Text = $"{rows.Count} database(s) · {node.Session.Summary}";
    }

    private async Task ShowTablesAsync(NodeInfo node, int version)
    {
        var tables = await node.Session.Schema.GetTableStatusAsync(node.Database!);
        if (version != _version) return;
        _split.Panel2Collapsed = true;
        Reset(new[]
        {
            ("Name", 220, false), ("Type", 70, false), ("Rows", 90, true), ("Data size", 85, true), ("Index size", 85, true),
            ("Engine", 70, false), ("Auto inc.", 80, true), ("Created", 130, false), ("Last update", 130, false),
            ("Collation", 150, false), ("Comment", 200, false),
        });
        _list.BeginUpdate();
        foreach (var t in tables)
        {
            AddItem(_list, t.IsView ? "view" : "table",
                new object?[] { t.Name, t.IsView ? "View" : "Table", t.Rows, t.DataLength, t.IndexLength, t.Engine, t.AutoIncrement, t.CreateTime, t.UpdateTime, t.Collation, t.Comment },
                new[]
                {
                    t.Name, t.IsView ? "View" : "Table", t.Rows?.ToString("N0") ?? "", CellFormat.FormatBytes(t.DataLength), CellFormat.FormatBytes(t.IndexLength),
                    t.Engine ?? "", t.AutoIncrement?.ToString("N0") ?? "", Date(t.CreateTime), Date(t.UpdateTime), t.Collation ?? "", t.Comment,
                });
        }
        _list.EndUpdate();
        long totalRows = tables.Sum(t => t.Rows ?? 0), totalSize = tables.Sum(t => (t.DataLength ?? 0) + (t.IndexLength ?? 0));
        _status.Text = $"{tables.Count} object(s) · ≈{totalRows:N0} rows · {CellFormat.FormatBytes(totalSize)}  (InnoDB row counts are estimates)";
    }

    private async Task ShowColumnsAsync(NodeInfo node, int version)
    {
        var info = await node.Session.Schema.GetTableAsync(node.Database!, node.Name!);
        if (version != _version) return;
        Reset(new[] { ("Field", 180, false), ("Type", 160, false), ("Null", 50, false), ("Key", 50, false), ("Default", 140, false), ("Extra", 150, false), ("Collation", 150, false), ("Comment", 200, false) });
        _fieldsShown = true;
        _list.BeginUpdate();
        foreach (var c in info.Columns)
        {
            var values = new[] { c.Name, c.Type, c.Nullable ? "YES" : "NO", c.Key, c.Default ?? (c.Nullable ? "NULL" : ""), c.Extra, c.Collation ?? "", c.Comment };
            AddItem(_list, null, values.Cast<object?>().ToArray(), values);
        }
        _list.EndUpdate();

        Reset(new[] { ("Index", 180, false), ("Columns", 300, false), ("Unique", 60, false), ("Type", 80, false) }, _indexes);
        _indexes.BeginUpdate();
        foreach (var i in info.Indexes)
        {
            var values = new[] { i.Name, string.Join(", ", i.Columns), i.Unique ? "YES" : "NO", i.IndexType };
            AddItem(_indexes, null, values.Cast<object?>().ToArray(), values);
        }
        _indexes.EndUpdate();
        _split.Panel2Collapsed = info.Indexes.Count == 0;
        if (!_split.Panel2Collapsed) _split.SplitterDistance = Math.Max(LogicalToDeviceUnits(80), _split.Height * 2 / 3);

        var key = info.KeyColumns;
        _status.Text = $"{info.Columns.Count} column(s) · {info.Indexes.Count} index(es)" +
                       (node.Kind == NodeKind.View ? " · view (read-only in Data Browser)" :
                        key is null ? " · no primary/unique key: the Data Browser finds rows by all their values" : $" · key: {string.Join(", ", key)}");
    }

    private async Task ShowDefinitionAsync(NodeInfo node, int version)
    {
        var sql = await node.Session.Schema.GetCreateStatementAsync(node.Database!, node.Name, node.ObjectType);
        if (version != _version) return;
        _split.Visible = false;
        _definition.Visible = true;
        _definition.Text = sql.Replace("\r\n", "\n").Replace("\n", "\r\n");
        _status.Text = node.Kind == NodeKind.Function ? "Function" : "Procedure";
    }

    private static string Date(DateTime? d) => d?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "";

    private static void AddItem(ListView list, string? image, object?[] raw, string[] texts)
    {
        var item = new ListViewItem(texts) { Tag = raw };
        if (image is not null) item.ImageKey = image;
        list.Items.Add(item);
    }

    private static void SortList(ListView list, int column)
    {
        var current = list.ListViewItemSorter as ItemComparer;
        bool desc = current is not null && current.Column == column && !current.Descending;
        list.ListViewItemSorter = new ItemComparer(column, desc);
    }

    private sealed class ItemComparer : System.Collections.IComparer
    {
        public ItemComparer(int column, bool descending) { Column = column; Descending = descending; }
        public int Column { get; }
        public bool Descending { get; }

        public int Compare(object? x, object? y)
        {
            var a = (x as ListViewItem)?.Tag is object?[] ra && Column < ra.Length ? ra[Column] : null;
            var b = (y as ListViewItem)?.Tag is object?[] rb && Column < rb.Length ? rb[Column] : null;
            int c = ValueComparer.Instance.Compare(a, b);
            return Descending ? -c : c;
        }
    }
}
