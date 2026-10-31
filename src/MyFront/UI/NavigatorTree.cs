using MyFront.Core.Db;

namespace MyFront.UI;

internal enum NodeKind { Server, Database, Table, View, Procedure, Function, Processes, Variables, Loading, Key, Field, Hidden }

/// <param name="Name">The table, view or routine (for a key or field node: its table or view).</param>
/// <param name="Child">A key or field node: the key's name (PRIMARY for the primary key) or the field's name.</param>
/// <param name="InView">A field node of a view.</param>
internal sealed record NodeInfo(NodeKind Kind, ServerSession Session, string? Database = null, string? Name = null, string? Child = null, bool InView = false)
{
    public bool IsTableLike => Kind is NodeKind.Table or NodeKind.View;

    /// <summary>The table or view a key / field node belongs to; any other node itself.</summary>
    public NodeInfo Owner => Kind is NodeKind.Key or NodeKind.Field ? new NodeInfo(InView ? NodeKind.View : NodeKind.Table, Session, Database, Name) : this;

    /// <summary>The object's own name (what Copy name copies); null for nodes without one.</summary>
    public string? PlainName => Kind switch
    {
        NodeKind.Database => Database,
        NodeKind.Table or NodeKind.View or NodeKind.Procedure or NodeKind.Function => Name,
        NodeKind.Key or NodeKind.Field => Child,
        _ => null,
    };

    public DbObjectType? ObjectType => Kind switch
    {
        NodeKind.Table => DbObjectType.Table,
        NodeKind.View => DbObjectType.View,
        NodeKind.Procedure => DbObjectType.Procedure,
        NodeKind.Function => DbObjectType.Function,
        _ => null,
    };
}

/// <summary>
/// Server → Databases → Tables / Views / Routines → (tables) keys, then fields. Children load on first expand, off the
/// UI thread. Tables and views the user hid (<see cref="Core.Accounts.Account.IsHidden"/>) sit in a closed "Hidden" folder at the bottom
/// of their database.
/// </summary>
internal sealed class NavigatorTree : TreeView
{
    private readonly Dictionary<TreeNode, Task> _loading = new();
    private Font? _bold;

    public event Action<NodeInfo?>? NodeSelected;
    /// <summary>The user clicked a node or moved to it with the keyboard; not raised when code selects a node (USE, quick queries …).</summary>
    public event Action<NodeInfo>? NodeActivated;
    public event Action<NodeInfo, string>? CommandRequested;

    public NavigatorTree()
    {
        HideSelection = false;
        BorderStyle = BorderStyle.None;
        ShowLines = true;
        ShowRootLines = true;
        FullRowSelect = false;
        ItemHeight = LogicalToDeviceUnits(20);
        ImageList = Icons.TreeImages(LogicalToDeviceUnits(16));
    }

    public static NodeInfo? Info(TreeNode? node) => node?.Tag as NodeInfo;

    public NodeInfo? SelectedInfo => Info(SelectedNode);

    public TreeNode AddSession(ServerSession session)
    {
        var node = MakeNode(session.DisplayName + (session.ReadOnly ? "  (read-only)" : ""), new NodeInfo(NodeKind.Server, session), "server", lazy: true);
        node.ToolTipText = session.Summary;
        Nodes.Add(node);
        return node;
    }

    public void RemoveSession(ServerSession session)
    {
        var node = FindServer(session);
        node?.Remove();
    }

    public TreeNode? FindServer(ServerSession session) =>
        Nodes.Cast<TreeNode>().FirstOrDefault(n => Info(n)?.Session == session);

    private static TreeNode MakeNode(string text, NodeInfo info, string image, bool lazy)
    {
        var n = new TreeNode(text) { Tag = info, ImageKey = image, SelectedImageKey = image };
        if (lazy) n.Nodes.Add(new TreeNode("Loading…") { Tag = new NodeInfo(NodeKind.Loading, info.Session), ImageKey = "loading", SelectedImageKey = "loading" });
        return n;
    }

    /// <summary>
    /// Bold text (the primary key and its fields). The tree measures labels in its own font, so a bold label gets a
    /// few trailing spaces to keep its end from being cut off.
    /// </summary>
    private TreeNode Bold(TreeNode node)
    {
        _bold ??= new Font(Font, FontStyle.Bold);
        node.NodeFont = _bold;
        int extra = TextRenderer.MeasureText(node.Text, _bold).Width - TextRenderer.MeasureText(node.Text, Font).Width;
        int space = Math.Max(1, TextRenderer.MeasureText("x x", Font).Width - TextRenderer.MeasureText("xx", Font).Width);
        if (extra > 0) node.Text += new string(' ', (extra + space - 1) / space);
        return node;
    }

    /// <summary>Keys (tables only; the primary key first, in bold), then the fields (primary key fields in bold).</summary>
    private async Task<List<TreeNode>> TableChildrenAsync(NodeInfo info)
    {
        var table = await info.Session.Schema.GetTableAsync(info.Database!, info.Name!);
        var nodes = new List<TreeNode>();
        bool view = info.Kind == NodeKind.View;
        var pk = table.Indexes.FirstOrDefault(i => i.IsPrimary);
        if (!view)
        {
            foreach (var key in table.Indexes.OrderBy(i => i.IsPrimary ? 0 : 1))
            {
                var node = MakeNode(key.IsPrimary ? "Primary Key" : key.Name, info with { Kind = NodeKind.Key, Child = key.Name }, "key", lazy: false);
                node.ToolTipText = $"{(key.IsPrimary ? "PRIMARY KEY" : key.IndexType == "FULLTEXT" ? "FULLTEXT" : key.Unique ? "UNIQUE" : "KEY")} " +
                                   $"({string.Join(", ", key.Parts.Select(p => p.Column + (p.Length is { } n ? $"({n})" : "")))})";
                nodes.Add(key.IsPrimary ? Bold(node) : node);
            }
        }
        foreach (var c in table.Columns)
        {
            var node = MakeNode(c.Name, info with { Kind = NodeKind.Field, Child = c.Name, InView = view }, "field", lazy: false);
            node.ToolTipText = c.Type + (c.Nullable ? "" : " NOT NULL") + (c.Extra.Length > 0 ? " " + c.Extra : "");
            nodes.Add(pk?.Columns.Contains(c.Name) == true ? Bold(node) : node);
        }
        return nodes;
    }

    private static bool NeedsLoad(TreeNode node) =>
        node.Nodes.Count == 1 && Info(node.Nodes[0])?.Kind == NodeKind.Loading;

    protected override void OnBeforeExpand(TreeViewCancelEventArgs e)
    {
        base.OnBeforeExpand(e);
        if (e.Node is not null && NeedsLoad(e.Node))
            Ui.Fire(this, () => LoadChildrenAsync(e.Node));
    }

    /// <summary>Loads a node's children; concurrent callers share the same in-flight load.</summary>
    public Task LoadChildrenAsync(TreeNode node)
    {
        if (_loading.TryGetValue(node, out var running)) return running;
        var task = LoadCoreAsync(node);
        if (!task.IsCompleted) _loading[node] = task;
        return task;
    }

    private async Task LoadCoreAsync(TreeNode node)
    {
        try
        {
            var info = Info(node)!;
            var children = new List<TreeNode>();
            switch (info.Kind)
            {
                case NodeKind.Server:
                    foreach (var db in (await info.Session.Schema.GetDatabasesAsync()).Where(info.Session.Account.ShowsDatabase))
                        children.Add(MakeNode(db, new NodeInfo(NodeKind.Database, info.Session, db), "database", lazy: true));
                    children.Add(MakeNode("Processes", new NodeInfo(NodeKind.Processes, info.Session), "processes", lazy: false));
                    children.Add(MakeNode("Variables", new NodeInfo(NodeKind.Variables, info.Session), "variables", lazy: false));
                    break;
                case NodeKind.Database:
                    var hidden = new List<TreeNode>();
                    foreach (var o in await info.Session.Schema.GetObjectsAsync(info.Database!))
                    {
                        var (kind, image) = o.Type switch
                        {
                            DbObjectType.View => (NodeKind.View, "view"),
                            DbObjectType.Procedure => (NodeKind.Procedure, "procedure"),
                            DbObjectType.Function => (NodeKind.Function, "function"),
                            _ => (NodeKind.Table, "table"),
                        };
                        var child = MakeNode(o.Name, new NodeInfo(kind, info.Session, o.Database, o.Name), image, lazy: kind is NodeKind.Table or NodeKind.View);
                        if (kind is NodeKind.Table or NodeKind.View && info.Session.Account.IsHidden(o.Database, o.Name)) hidden.Add(child);
                        else children.Add(child);
                    }
                    if (hidden.Count > 0)
                    {
                        var folder = MakeHiddenFolder(info);
                        folder.Nodes.AddRange(hidden.ToArray());
                        SetHiddenCount(folder);
                        children.Add(folder);
                    }
                    break;
                case NodeKind.Table or NodeKind.View:
                    children.AddRange(await TableChildrenAsync(info));
                    break;
            }
            BeginUpdate();
            try
            {
                node.Nodes.Clear();
                node.Nodes.AddRange(children.ToArray());
            }
            finally { EndUpdate(); }
        }
        catch
        {
            node.Collapse();
            throw;
        }
        finally
        {
            _loading.Remove(node);
        }
    }

    /// <summary>
    /// Reloads a node's children (F5; a table, key or field reloads its database). What was expanded below it comes back
    /// expanded with fresh children (a table shows its keys and fields again), and the selected node is selected again.
    /// </summary>
    public async Task RefreshNodeAsync(TreeNode node)
    {
        var info = Info(node);
        if (info is null) return;
        if (info.Kind is not (NodeKind.Server or NodeKind.Database))
        {
            if (node.Parent is not null) await RefreshNodeAsync(node.Parent);
            return;
        }
        info.Session.Schema.Invalidate(info.Database);
        bool expanded = node.IsExpanded;
        var open = Descendants(node).Where(n => n.IsExpanded && !NeedsLoad(n)).Select(n => PathOf(n, node)).ToList();
        var selected = SelectedNode is { } sel && IsBelow(sel, node) ? PathOf(sel, node) : null;
        if (selected is not null) SelectedNode = node; // not on a node about to go: no stream of passing selections
        node.Nodes.Clear();
        node.Nodes.Add(new TreeNode("Loading…") { Tag = new NodeInfo(NodeKind.Loading, info.Session), ImageKey = "loading", SelectedImageKey = "loading" });
        await LoadChildrenAsync(node);
        if (expanded) node.Expand();
        foreach (var path in open) // parents come before their children
        {
            if (Find(node, path) is not { } again) continue;
            if (NeedsLoad(again))
            {
                try { await LoadChildrenAsync(again); }
                catch (Exception ex) when (ex is not OperationCanceledException) { continue; } // gone meanwhile
            }
            again.Expand();
        }
        if (selected is not null)
        {
            // the node itself, or the nearest of its parents that is still there
            TreeNode target = node;
            for (int depth = selected.Count; depth > 0; depth--)
                if (Find(node, selected.Take(depth).ToList()) is { } found) { target = found; break; }
            if (SelectedNode != target) SelectedNode = target;
        }
    }

    private static IEnumerable<TreeNode> Descendants(TreeNode node)
    {
        foreach (TreeNode child in node.Nodes)
        {
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    private static bool IsBelow(TreeNode node, TreeNode ancestor)
    {
        for (var p = node.Parent; p is not null; p = p.Parent)
            if (p == ancestor) return true;
        return false;
    }

    /// <summary>The way down from <paramref name="root"/> to a node: one key per level (kind and names, not the shown text).</summary>
    private static List<string> PathOf(TreeNode node, TreeNode root)
    {
        var path = new List<string>();
        for (var n = node; n is not null && n != root; n = n.Parent) path.Insert(0, KeyOf(n));
        return path;
    }

    private static string KeyOf(TreeNode n) => Info(n) is { } i ? $"{i.Kind}|{i.Database}|{i.Name}|{i.Child}" : n.Text;

    private static TreeNode? Find(TreeNode root, IReadOnlyList<string> path)
    {
        var node = root;
        foreach (var key in path)
        {
            node = node.Nodes.Cast<TreeNode>().FirstOrDefault(n => KeyOf(n) == key);
            if (node is null) return null;
        }
        return node;
    }

    /// <summary>Selects a database node, loading the server's children if needed.</summary>
    public async Task<TreeNode?> SelectDatabaseAsync(ServerSession session, string database, bool expand = false)
    {
        var server = FindServer(session);
        if (server is null) return null;
        if (NeedsLoad(server)) await LoadChildrenAsync(server);
        server.Expand();
        var dbNode = server.Nodes.Cast<TreeNode>().FirstOrDefault(n => Info(n) is { Kind: NodeKind.Database } i && i.Database == database);
        if (dbNode is null) return null;
        if (SelectedNode != dbNode) SelectedNode = dbNode;
        if (expand) dbNode.Expand();
        return dbNode;
    }

    /// <summary>Selects a table, view or routine; a hidden one opens its Hidden folder.</summary>
    public async Task<TreeNode?> SelectObjectAsync(ServerSession session, string database, string name)
    {
        var dbNode = await SelectDatabaseAsync(session, database);
        if (dbNode is null) return null;
        if (NeedsLoad(dbNode)) await LoadChildrenAsync(dbNode);
        dbNode.Expand();
        var node = ObjectNodes(dbNode).FirstOrDefault(n => Info(n)?.Name == name);
        if (node is not null) SelectedNode = node;
        return node;
    }

    // ---- Hide / Unhide --------------------------------------------------------------------------

    /// <summary>A database's objects: its own nodes, then those in its Hidden folder.</summary>
    internal static IEnumerable<TreeNode> ObjectNodes(TreeNode dbNode) =>
        dbNode.Nodes.Cast<TreeNode>().SelectMany(n => Info(n)?.Kind == NodeKind.Hidden ? n.Nodes.Cast<TreeNode>() : new[] { n });

    internal static TreeNode? HiddenFolder(TreeNode dbNode) =>
        dbNode.Nodes.Cast<TreeNode>().FirstOrDefault(n => Info(n)?.Kind == NodeKind.Hidden);

    private static TreeNode MakeHiddenFolder(NodeInfo database)
    {
        return MakeNode("Hidden", new NodeInfo(NodeKind.Hidden, database.Session, database.Database), "hidden", lazy: false);
    }

    private static void SetHiddenCount(TreeNode folder) => folder.Text = $"Hidden ({folder.Nodes.Count})";

    private static DbObject? ObjectOf(TreeNode node) =>
        Info(node) is { ObjectType: { } type, Database: { } db, Name: { } name } ? new DbObject(db, name, type) : null;

    /// <summary>Puts a node where the database's list has it (<see cref="DbObject.Compare"/>), above the Hidden folder.</summary>
    private static void InsertInOrder(TreeNodeCollection nodes, TreeNode node)
    {
        var o = ObjectOf(node)!;
        int at = 0;
        while (at < nodes.Count && Info(nodes[at])?.Kind != NodeKind.Hidden && (ObjectOf(nodes[at]) is not { } other || DbObject.Compare(other, o) < 0)) at++;
        nodes.Insert(at, node);
    }

    /// <summary>
    /// After Hide / Unhide: moves a table or view into its database's Hidden folder (made at the bottom, closed) or back to
    /// its place among the others; what is loaded below it goes with it. An unloaded database gets it right when it loads.
    /// </summary>
    public void MoveHidden(ServerSession session, string database, string name)
    {
        var dbNode = FindServer(session)?.Nodes.Cast<TreeNode>().FirstOrDefault(n => Info(n) is { Kind: NodeKind.Database } i && i.Database == database);
        if (dbNode is null || NeedsLoad(dbNode)) return;
        var folder = HiddenFolder(dbNode);
        var node = ObjectNodes(dbNode).FirstOrDefault(n => Info(n) is { IsTableLike: true } i && i.Name == name);
        bool hide = session.Account.IsHidden(database, name);
        if (node is null || (folder is not null && node.Parent == folder) == hide) return;
        bool wasSelected = SelectedNode is { } sel && (sel == node || IsBelow(sel, node));
        bool folderGoes = !hide && folder?.Nodes.Count == 1;
        if (wasSelected || folderGoes && SelectedNode == folder) SelectedNode = dbNode; // not on a node about to move or go
        BeginUpdate();
        try
        {
            node.Remove();
            if (hide)
            {
                if (folder is null) dbNode.Nodes.Add(folder = MakeHiddenFolder(Info(dbNode)!));
                InsertInOrder(folder.Nodes, node);
            }
            else InsertInOrder(dbNode.Nodes, node);
            if (folder?.Nodes.Count == 0) folder.Remove();
            else if (folder is not null) SetHiddenCount(folder);
        }
        finally { EndUpdate(); }
        if (wasSelected && !hide) SelectedNode = node; // back in sight: stay on it (in the closed folder it would open it)
    }

    protected override void OnAfterSelect(TreeViewEventArgs e)
    {
        base.OnAfterSelect(e);
        var info = Info(e.Node);
        NodeSelected?.Invoke(info?.Kind == NodeKind.Loading ? null : info);
        if (e.Action is TreeViewAction.ByMouse or TreeViewAction.ByKeyboard && info is { Kind: not NodeKind.Loading })
            NodeActivated?.Invoke(info);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyData == (Keys.Alt | Keys.Enter) && SelectedInfo?.Owner is { IsTableLike: true } info)
        {
            e.Handled = e.SuppressKeyPress = true;
            CommandRequested?.Invoke(info, "properties");
        }
    }

    protected override void OnNodeMouseClick(TreeNodeMouseClickEventArgs e)
    {
        base.OnNodeMouseClick(e);
        // a click on the node that is already selected changes nothing in the tree, but is still the user's choice
        if (e.Button == MouseButtons.Left && e.Node == SelectedNode && Info(e.Node) is { Kind: not NodeKind.Loading } clicked
            && HitTest(e.Location).Location is TreeViewHitTestLocations.Label or TreeViewHitTestLocations.Image)
            NodeActivated?.Invoke(clicked);
        if (e.Button != MouseButtons.Right) return;
        SelectedNode = e.Node;
        var info = Info(e.Node);
        if (info is null || info.Kind == NodeKind.Loading) return;
        BuildMenu(info).Show(this, e.Location);
    }

    /// <summary>What dragging a node into an SQL editor inserts: the name of a database, table, view or field, in backticks
    /// only when it needs them (<see cref="Ident.QuoteIfNeeded"/>); null = the node can't be dragged.</summary>
    internal static string? DragText(NodeInfo? info) =>
        info is { Kind: NodeKind.Database or NodeKind.Table or NodeKind.View or NodeKind.Field, PlainName: { } name } ? Ident.QuoteIfNeeded(name) : null;

    /// <summary>Drag a name into an SQL editor: the dragged node decides it, not the selected one. The tree itself takes no drops.</summary>
    protected override void OnItemDrag(ItemDragEventArgs e)
    {
        base.OnItemDrag(e);
        if (e.Button == MouseButtons.Left && DragText(Info(e.Item as TreeNode)) is { } text)
            DoDragDrop(SqlEditor.NameData(text), DragDropEffects.Copy);
    }

    protected override void OnNodeMouseDoubleClick(TreeNodeMouseClickEventArgs e)
    {
        base.OnNodeMouseDoubleClick(e);
        // a table's field or key: Edit field… / Edit key… (views can't be changed)
        if (e.Button == MouseButtons.Left && Info(e.Node) is { Kind: NodeKind.Field or NodeKind.Key, InView: false } info)
            CommandRequested?.Invoke(info, info.Kind == NodeKind.Field ? "editfield" : "editkey");
    }

    private ContextMenuStrip BuildMenu(NodeInfo info)
    {
        var m = new ContextMenuStrip();
        m.Closed += (_, _) => BeginInvoke(m.Dispose);
        ToolStripMenuItem Add(string text, string cmd, Keys keys = Keys.None)
        {
            var item = new ToolStripMenuItem(text, null, (_, _) => CommandRequested?.Invoke(info, cmd));
            if (keys != Keys.None) item.ShortcutKeys = keys;
            m.Items.Add(item);
            return item;
        }
        if (info.Kind is NodeKind.Field or NodeKind.Key)
        {
            if (!info.InView) // a view's fields come from its SELECT: nothing to change here
            {
                if (info.Kind == NodeKind.Field)
                {
                    Add("Edit field…", "editfield").ShortcutKeyDisplayString = "double-click";
                    Add("Rename…", "renamefield");
                }
                else Add("Edit key…", "editkey").ShortcutKeyDisplayString = "double-click";
                m.Items.Add(new ToolStripSeparator());
            }
            Add("Copy name", "copyname");
            return m;
        }
        if (info.IsTableLike) Add("Open in Data Browser", "browse");
        Add("New SQL Editor", "newsql", Keys.Control | Keys.T);
        if (info.Kind is NodeKind.Database or NodeKind.Table or NodeKind.View or NodeKind.Procedure or NodeKind.Function)
            Add("Copy CREATE statement", "copycreate");
        if (info.Kind is NodeKind.Database or NodeKind.Table or NodeKind.View)
            Add("Copy name", "copyname");
        if (info is { IsTableLike: true, Database: { } database, Name: { } name })
        {
            bool hidden = info.Session.Account.IsHidden(database, name);
            Add(hidden ? "Unhide" : "Hide", hidden ? "unhide" : "hide");
        }
        if (info.IsTableLike)
        {
            m.Items.Add(new ToolStripSeparator());
            if (info.Kind == NodeKind.Table) Add("Import Excel / CSV into this table…", "import");
            Add("Export to Excel…", "export-xlsx");
            Add("Export to CSV…", "export-csv");
            Add("Export SQL…", "export-sql");
            m.Items.Add(new ToolStripSeparator());
            Add("Rename…", "rename");
            Add("Delete…", "drop");
        }
        else if (info.Kind == NodeKind.Database)
        {
            m.Items.Add(new ToolStripSeparator());
            Add("Import Excel / CSV…", "import");
            Add("Import SQL…", "import-sql");
            Add("Export database to Excel…", "export-xlsx");
            Add("Export database to CSV…", "export-csv");
        }
        m.Items.Add(new ToolStripSeparator());
        Add("Refresh", "refresh", Keys.F5);
        if (info.Kind == NodeKind.Server)
        {
            m.Items.Add(new ToolStripSeparator());
            Add("Close connection", "close");
        }
        if (info.IsTableLike)
        {
            m.Items.Add(new ToolStripSeparator());
            Add("Properties…", "properties", Keys.Alt | Keys.Enter);
        }
        return m;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _bold?.Dispose();
        base.Dispose(disposing);
    }
}
