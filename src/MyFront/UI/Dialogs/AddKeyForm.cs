using MyFront.Core.Db;

namespace MyFront.UI.Dialogs;

/// <summary>
/// Object Browser → right-click → Add key…: the primary key or a named key, its fields
/// picked from the table's fields by drag &amp; drop (or the arrow buttons) and ordered in the Key Fields list, prefix
/// lengths, comment, Unique / Fulltext. Runs ALTER TABLE … ADD … after showing it.
/// Edit key… opens the same dialog filled with an existing key and runs ALTER TABLE … DROP INDEX …, ADD … (one statement).
/// </summary>
internal sealed class AddKeyForm : DialogForm
{
    /// <summary>A field of the table; it sits in either list, with its prefix length while it is a key field.</summary>
    internal sealed class KeyField
    {
        public KeyField(TableColumn column, int order) { Column = column; Order = order; }
        public TableColumn Column { get; }
        /// <summary>Position in the table (Available Fields keeps this order).</summary>
        public int Order { get; }
        /// <summary>Prefix length; 0 = the whole field.</summary>
        public int Length { get; set; }
        /// <summary>DESC in the key (kept from the key being edited; the dialog doesn't set it).</summary>
        public bool Descending { get; set; }
        public override string ToString() => (Length > 0 ? $"{Column.Name}({Length})" : Column.Name) + (Descending ? " DESC" : "");
    }

    private sealed record DragFields(ListBox Source, KeyField[] Fields);

    private readonly ServerSession _session;
    private readonly string _database, _table;
    /// <summary>The name of the key being edited; null = adding a new one.</summary>
    private readonly string? _editName;
    /// <summary>The key being edited, as read when the dialog opened.</summary>
    private TableIndex? _editing;
    /// <summary>Why the key can't be changed here (SPATIAL, an expression, gone); null = it can.</summary>
    private string? _blocked;
    /// <summary>Foreign keys of the table whose fields lead this key (the server may refuse to drop it).</summary>
    private string? _foreignKeyNote;
    private readonly RadioButton _primary, _named;
    private readonly TextBox _name, _comment, _preview;
    private readonly ListBox _keys, _available;
    private readonly NumericUpDown _length;
    private readonly CheckBox _unique, _fulltext;
    private readonly Label _state;
    private readonly Button _ok;
    private readonly CancellationTokenSource _cts = new();
    private readonly ToolTip _tips = new();
    private TableDdlContext? _ctx;
    /// <summary>The name this dialog filled in; it follows the first key field until the user types another.</summary>
    private string? _suggested;
    private bool _filling = true, _syncingLength;
    /// <summary>Fields are being moved: the lists are half done, so the state is updated once at the end.</summary>
    private bool _moving;
    private DragFields? _drag;
    private ListBox? _pressed;
    private Point _pressedAt;

    /// <summary>Completes when the table's fields have been read (self-test).</summary>
    internal Task Loaded { get; private set; } = Task.CompletedTask;
    /// <summary>The statement OK runs (empty while the key is incomplete).</summary>
    internal string Sql => _preview.Text;
    internal ListBox KeyList => _keys;
    internal ListBox AvailableList => _available;

    /// <param name="editKey">An existing key to change (Edit key…); null = add a new key.</param>
    public AddKeyForm(ServerSession session, string database, string table, string? editKey = null)
        : base(editKey is null ? "Add key — " + table : editKey == "PRIMARY" ? "Edit primary key — " + table : $"Edit key {editKey} — {table}")
    {
        _session = session;
        _database = database;
        _table = table;
        _editName = editKey;

        var names = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        _primary = new RadioButton { Text = "Primary Key", AutoSize = true, Margin = new Padding(0, W(3), W(16), 0) };
        _named = new RadioButton { Text = "Name:", AutoSize = true, Checked = true, Margin = new Padding(0, W(3), W(4), 0) };
        _name = new TextBox { Width = W(240) };
        names.Controls.AddRange(new Control[] { _primary, _named, _name });
        Row("Key", names);

        var lists = new TableLayoutPanel { ColumnCount = 3, RowCount = 2, AutoSize = true, Margin = Padding.Empty };
        lists.Controls.Add(Bold("Key Fields"), 0, 0);
        lists.Controls.Add(Bold("Available Fields"), 2, 0);
        _keys = NewList();
        _available = NewList();
        var moves = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Anchor = AnchorStyles.None, Margin = new Padding(W(6), 0, W(6), 0) };
        MoveButton(moves, "◀", "Add the selected fields to the key", AddSelected);
        MoveButton(moves, "◀◀", "Add all fields", AddAll);
        MoveButton(moves, "▲", "Move the selected key fields up", () => MoveSelected(-1));
        MoveButton(moves, "▼", "Move the selected key fields down", () => MoveSelected(1));
        MoveButton(moves, "▶", "Remove the selected fields from the key", RemoveSelected);
        MoveButton(moves, "▶▶", "Remove all fields from the key", RemoveAll);
        lists.Controls.Add(_keys, 0, 1);
        lists.Controls.Add(moves, 1, 1);
        lists.Controls.Add(_available, 2, 1);
        Row("Fields", lists);

        _length = Row("Length", new NumericUpDown { Minimum = 0, Maximum = 3072, Width = W(80), Enabled = false },
            new Label { Text = "of the selected key field (text / binary); 0 = the whole field", AutoSize = true, ForeColor = SystemColors.GrayText });
        _comment = Row("Comment", new TextBox { Width = W(420) });
        var attributes = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        _unique = new CheckBox { Text = "Unique", AutoSize = true, Margin = new Padding(0, W(3), W(16), 0) };
        _fulltext = new CheckBox { Text = "Fulltext", AutoSize = true, Margin = new Padding(0, W(3), 0, 0), Enabled = false };
        attributes.Controls.Add(_unique);
        attributes.Controls.Add(_fulltext);
        Row("Attributes", attributes);
        Row("", Bold("SQL"));
        _preview = Row("", new TextBox { Width = W(460), Height = W(60), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = Ui.Mono(9) });
        _state = Row("", new Label { AutoSize = true, MaximumSize = new Size(W(460), 0), ForeColor = SystemColors.GrayText, Text = "Loading…" });

        CancelButton = AddButton("Cancel", DialogResult.Cancel);
        _ok = AddButton("OK", DialogResult.None, (_, _) => Ui.Fire(this, ApplyAsync));
        _ok.Enabled = false;
        AcceptButton = _ok;

        _primary.CheckedChanged += (_, _) => UpdateState();
        _name.TextChanged += (_, _) =>
        {
            if (_name.Text != _suggested) _suggested = null; // typed by the user: keep it
            UpdateState();
        };
        _comment.TextChanged += (_, _) => UpdateState();
        _unique.CheckedChanged += (_, _) => { if (_unique.Checked) _fulltext.Checked = false; UpdateState(); };
        _fulltext.CheckedChanged += (_, _) => { if (_fulltext.Checked) _unique.Checked = false; UpdateState(); };
        _keys.SelectedIndexChanged += (_, _) => UpdateState();
        _length.ValueChanged += (_, _) =>
        {
            if (_syncingLength || _keys.SelectedItems.Count != 1 || _keys.SelectedItem is not KeyField f) return;
            f.Length = (int)_length.Value;
            _keys.Items[_keys.SelectedIndex] = f; // re-show "name(10)"
            UpdateState();
        };
        _keys.DoubleClick += (_, _) => RemoveSelected();
        _available.DoubleClick += (_, _) => AddSelected();
        FormClosed += (_, _) => _cts.Cancel();
    }

    private Label Bold(string text) => new() { Text = text, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, W(4), 0, W(2)) };

    private void MoveButton(FlowLayoutPanel host, string text, string tip, Action click)
    {
        var b = new Button { Text = text, Width = W(40), Height = W(26), Margin = new Padding(0, W(2), 0, W(2)) };
        _tips.SetToolTip(b, tip);
        b.Click += (_, _) => click();
        host.Controls.Add(b);
    }

    private ListBox NewList()
    {
        var list = new ListBox
        {
            Width = W(190), Height = W(170), SelectionMode = SelectionMode.MultiExtended, IntegralHeight = false, AllowDrop = true,
        };
        list.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { _pressed = list; _pressedAt = e.Location; } };
        list.MouseUp += (_, _) => _pressed = null;
        list.MouseMove += (_, e) =>
        {
            if (_pressed != list || e.Button != MouseButtons.Left) return;
            var drag = SystemInformation.DragSize;
            if (Math.Abs(e.X - _pressedAt.X) < drag.Width && Math.Abs(e.Y - _pressedAt.Y) < drag.Height) return;
            _pressed = null;
            var fields = list.SelectedItems.Cast<KeyField>().ToArray();
            if (fields.Length == 0) return;
            _drag = new DragFields(list, fields); // the fields stay here; the data object only marks the drag
            try { list.DoDragDrop("MyFront key fields", DragDropEffects.Move); }
            finally { _drag = null; }
        };
        list.DragOver += (_, e) => e.Effect = _drag is not null && (list == _keys || _drag.Source == _keys) ? DragDropEffects.Move : DragDropEffects.None;
        list.DragDrop += (_, e) => { if (_drag is { } d) DropFields(d.Source, d.Fields, list, new Point(e.X, e.Y)); };
        return list;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Loaded = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _ctx = await TableDdlContext.LoadAsync(_session, _database, _table, _cts.Token);
        }
        catch (Exception ex) when (!IsDisposed && ex is not OperationCanceledException)
        {
            SetState(Ui.Describe(ex), error: true);
            return;
        }
        if (IsDisposed) return;
        int order = 0;
        foreach (var c in _ctx.Table.Columns) _available.Items.Add(new KeyField(c, order++));
        if (_ctx.Table.Indexes.Any(i => i.Name == "PRIMARY") && _editName != "PRIMARY")
        {
            _primary.Enabled = false;
            _primary.Text = "Primary Key (the table has one)";
        }
        if (_editName is not null) FillFromKey(_editName);
        _filling = false;
        UpdateState();
    }

    /// <summary>Edit key…: name, primary or not, the fields in order with their prefix lengths, comment, Unique / Fulltext.</summary>
    private void FillFromKey(string name)
    {
        _editing = _ctx!.Table.Indexes.FirstOrDefault(i => i.Name == name);
        if (_editing is null)
        {
            _blocked = $"The key {name} doesn't exist any more (changed by someone else?). Close the dialog and refresh.";
            return;
        }
        var kind = TableDdl.KindOf(_editing);
        if (kind is null)
        {
            _blocked = _editing.HasExpression
                ? $"{name} has a part that is an expression, which this dialog can't show: change it in the SQL editor."
                : $"{name} is a {_editing.IndexType} key, which this dialog can't make: change it in the SQL editor.";
            return;
        }
        if (kind == IndexKind.Primary) _primary.Checked = true;
        else
        {
            _named.Checked = true;
            _name.Text = _editing.Name;
        }
        _suggested = null; // the key's own name stays
        _unique.Checked = kind == IndexKind.Unique;
        _fulltext.Checked = kind == IndexKind.Fulltext;
        _comment.Text = _editing.Comment;
        var fields = new List<KeyField>();
        foreach (var part in _editing.Parts)
        {
            var f = _available.Items.Cast<KeyField>().FirstOrDefault(k => k.Column.Name.Equals(part.Column, StringComparison.OrdinalIgnoreCase));
            if (f is null) continue;
            f.Length = part.Length ?? 0;
            f.Descending = part.Descending;
            fields.Add(f);
        }
        AddFields(fields);
        _keys.ClearSelected();
        // a foreign key needs a key that starts with its fields: say so before the server does
        var fks = ForeignKeys(_ctx.CreateTable)
            .Where(fk => fk.Columns.Count <= _editing.Columns.Count
                         && fk.Columns.Select((c, i) => c.Equals(_editing.Columns[i], StringComparison.OrdinalIgnoreCase)).All(x => x))
            .Select(fk => fk.Name).ToList();
        if (fks.Count > 0)
            _foreignKeyNote = $"The foreign key {string.Join(", ", fks)} may need this key: the server refuses the change if no key then starts with its fields.";
    }

    /// <summary>The FOREIGN KEY lines of SHOW CREATE TABLE: constraint name and its fields.</summary>
    private static IEnumerable<(string Name, List<string> Columns)> ForeignKeys(string createTable)
    {
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(createTable,
                     @"CONSTRAINT\s+`((?:[^`]|``)+)`\s+FOREIGN KEY\s*\(([^)]*)\)"))
        {
            var cols = System.Text.RegularExpressions.Regex.Matches(m.Groups[2].Value, "`((?:[^`]|``)+)`")
                .Select(c => c.Groups[1].Value.Replace("``", "`")).ToList();
            yield return (m.Groups[1].Value.Replace("``", "`"), cols);
        }
    }

    /// <summary>Edit key…: nothing differs from the key as it is.</summary>
    private bool Unchanged(IReadOnlyList<IndexPart> parts)
    {
        if (_editing is null) return false;
        var name = Kind == IndexKind.Primary ? "PRIMARY" : _name.Text.Trim();
        return Kind == TableDdl.KindOf(_editing) && name == _editing.Name && _comment.Text == _editing.Comment
               && parts.SequenceEqual(Kind == IndexKind.Fulltext ? _editing.Parts.Select(p => p with { Length = null, Descending = false }) : _editing.Parts);
    }

    private void SetState(string text, bool error)
    {
        _state.ForeColor = error ? ThemeManager.Current.Error : ThemeManager.Current.GrayText;
        _state.Text = text;
    }

    private List<KeyField> KeyFields => _keys.Items.Cast<KeyField>().ToList();

    private IndexKind Kind => _primary.Checked ? IndexKind.Primary : _unique.Checked ? IndexKind.Unique : _fulltext.Checked ? IndexKind.Fulltext : IndexKind.Index;

    /// <summary>Enables what applies, keeps the suggested name in step with the first field, and rebuilds the SQL.</summary>
    private void UpdateState()
    {
        if (_filling || _moving || _ctx is null) return;
        if (_blocked is not null)
        {
            _preview.Text = "";
            SetState(_blocked, error: true);
            _ok.Enabled = false;
            return;
        }
        var keys = KeyFields;
        _name.Enabled = !_primary.Checked;
        _unique.Enabled = !_primary.Checked;
        bool fulltextOk = !_primary.Checked && keys.Count > 0 && keys.All(k => ColumnDefinition.IsFulltextType(k.Column.Type));
        if (!fulltextOk && _fulltext.Checked) _fulltext.Checked = false; // re-enters UpdateState
        _fulltext.Enabled = fulltextOk;
        if (keys.Count > 0 && (_suggested is not null || _name.Text.Length == 0))
        {
            var name = TableDdl.SuggestIndexName(keys[0].Column.Name, _ctx.Table.Indexes.Select(i => i.Name));
            if (name != _name.Text) { _suggested = name; _name.Text = name; } // re-enters UpdateState
        }

        var one = _keys.SelectedItems.Count == 1 ? _keys.SelectedItem as KeyField : null;
        _syncingLength = true;
        _length.Enabled = one is not null && Kind != IndexKind.Fulltext && ColumnDefinition.IsPrefixType(one.Column.Type);
        _length.Value = one?.Length ?? 0;
        _syncingLength = false;

        var parts = keys.Select(k => new IndexPart(k.Column.Name, k.Length > 0 && Kind != IndexKind.Fulltext ? k.Length : null,
            k.Descending && Kind != IndexKind.Fulltext)).ToList();
        try
        {
            _preview.Text = _editing is null
                ? TableDdl.AddIndex(_database, _table, Kind, _name.Text, parts, _comment.Text, _ctx.NoBackslashEscapes)
                : TableDdl.ReplaceIndex(_database, _table, _editing.Name, Kind, _name.Text, parts, _comment.Text, _ctx.NoBackslashEscapes);
        }
        catch (ArgumentException ex)
        {
            _preview.Text = "";
            SetState(ex.Message, error: false);
            _ok.Enabled = false;
            return;
        }
        var bare = Kind == IndexKind.Fulltext ? null : keys.FirstOrDefault(k => k.Length == 0 && ColumnDefinition.NeedsPrefixLength(k.Column.Type));
        bool unchanged = bare is null && Unchanged(parts);
        if (bare is not null)
            SetState($"Give {bare.Column.Name} a length: TEXT and BLOB fields need one in a key (Fulltext keys don't).", error: true);
        else if (_foreignKeyNote is not null)
            SetState(_foreignKeyNote, error: false);
        else if (unchanged)
            SetState("Add or remove fields, or change the name or options; then OK changes the key.", error: false);
        else
            SetState(_primary.Enabled ? "" : "The table already has a primary key.", error: false);
        _ok.Enabled = bare is null && !unchanged;
    }

    // ---- moving fields --------------------------------------------------------------------------

    private void InsertAvailable(KeyField f)
    {
        f.Length = 0;
        f.Descending = false;
        int i = 0;
        while (i < _available.Items.Count && ((KeyField)_available.Items[i]).Order < f.Order) i++;
        _available.Items.Insert(i, f);
    }

    private void AddFields(IReadOnlyList<KeyField> fields, int insertAt = -1)
    {
        if (fields.Count == 0) return;
        _moving = true;
        _keys.BeginUpdate();
        _available.BeginUpdate();
        int at = insertAt < 0 || insertAt > _keys.Items.Count ? _keys.Items.Count : insertAt;
        foreach (var f in fields)
        {
            _available.Items.Remove(f);
            _keys.Items.Insert(at++, f);
        }
        _keys.ClearSelected();
        foreach (var f in fields) _keys.SelectedItems.Add(f);
        _available.EndUpdate();
        _keys.EndUpdate();
        _moving = false;
        UpdateState();
    }

    private void RemoveFields(IReadOnlyList<KeyField> fields)
    {
        if (fields.Count == 0) return;
        _moving = true;
        _available.BeginUpdate();
        foreach (var f in fields)
        {
            _keys.Items.Remove(f);
            InsertAvailable(f);
        }
        _available.EndUpdate();
        _moving = false;
        UpdateState();
    }

    /// <summary>Moves key fields so they start at <paramref name="insertAt"/> (an index in the list as it is now).</summary>
    private void Reorder(IReadOnlyList<KeyField> fields, int insertAt)
    {
        var all = KeyFields;
        int at = insertAt < 0 ? all.Count : insertAt;
        at -= fields.Count(f => all.IndexOf(f) < at); // those above the drop point leave a gap
        var rest = all.Except(fields).ToList();
        rest.InsertRange(Math.Clamp(at, 0, rest.Count), fields);
        _moving = true;
        _keys.BeginUpdate();
        _keys.Items.Clear();
        foreach (var f in rest) _keys.Items.Add(f);
        foreach (var f in fields) _keys.SelectedItems.Add(f);
        _keys.EndUpdate();
        _moving = false;
        UpdateState();
    }

    internal void AddSelected() => AddFields(_available.SelectedItems.Cast<KeyField>().ToList());

    internal void AddAll() => AddFields(_available.Items.Cast<KeyField>().ToList());

    internal void RemoveSelected() => RemoveFields(_keys.SelectedItems.Cast<KeyField>().ToList());

    internal void RemoveAll() => RemoveFields(KeyFields);

    internal void MoveSelected(int direction)
    {
        var selected = _keys.SelectedIndices.Cast<int>().OrderBy(i => i).ToList();
        if (selected.Count == 0) return;
        int first = selected[0], last = selected[^1];
        if (direction < 0 && first == 0 || direction > 0 && last == _keys.Items.Count - 1) return;
        var fields = selected.Select(i => (KeyField)_keys.Items[i]).ToList();
        Reorder(fields, direction < 0 ? first - 1 : last + 2);
    }

    /// <summary>
    /// A drop at a screen point: Available → Key Fields adds them there, within Key Fields reorders them,
    /// Key Fields → Available removes them from the key.
    /// </summary>
    internal void DropFields(ListBox source, IReadOnlyList<KeyField> fields, ListBox target, Point screen)
    {
        int index = target.IndexFromPoint(target.PointToClient(screen));
        if (index == ListBox.NoMatches) index = target.Items.Count;
        if (target == _keys)
        {
            if (source == _keys) Reorder(fields, index);
            else AddFields(fields, index);
        }
        else if (source == _keys) RemoveFields(fields);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>Runs the ALTER; on an error the dialog stays open with the server's message.</summary>
    internal async Task ApplyAsync()
    {
        var sql = Sql;
        if (sql.Length == 0 || !_ok.Enabled) return;
        _ok.Enabled = false;
        SetState("Saving…", error: false);
        UseWaitCursor = true;
        try
        {
            await TableDdl.ExecuteAsync(_session, _database, sql, _editing is null ? $"add key on {_table}" : $"edit key {_editing.Name} on {_table}", _cts.Token);
            DialogResult = DialogResult.OK;
        }
        catch (Exception ex) when (!IsDisposed && ex is not OperationCanceledException)
        {
            SetState(Ui.Describe(ex), error: true);
            _ok.Enabled = true;
        }
        finally
        {
            if (!IsDisposed) UseWaitCursor = false;
        }
    }
}
