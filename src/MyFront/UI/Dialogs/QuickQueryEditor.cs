using MyFront.Core.Accounts;
using MyFront.Core.Db;
using MyFront.Core.Quick;

namespace MyFront.UI.Dialogs;

/// <summary>Add / edit / reorder / delete quick queries; import and export quickqueries.json.</summary>
internal sealed class QuickQueryEditor : Form
{
    private const string AnyConnection = "(every connection)";
    private readonly List<QuickQuery> _items;
    private readonly IReadOnlyList<Account> _accounts;
    /// <summary>The connection in front: new buttons belong to it.</summary>
    private readonly string? _defaultAccountId;
    private readonly Func<string?, Task<List<string>>> _databasesFor;
    private readonly ListBox _list;
    private readonly TextBox _name;
    private readonly Button _color;
    private readonly ComboBox _icon, _account, _database, _hotkey;
    private readonly CheckBox _toExcel, _confirm;
    private readonly SqlEditor _sql;
    private readonly Label _params, _danger, _status;
    private readonly Panel _details;
    private bool _loading;

    public List<QuickQuery> Result => _items;

    public QuickQueryEditor(IEnumerable<QuickQuery> current, IReadOnlyList<Account> accounts,
        Func<string?, Task<List<string>>> databasesFor, string? selectId = null, bool addNew = false, string? defaultAccountId = null)
    {
        _items = current.Select(q => q.Clone()).ToList();
        _accounts = accounts;
        _defaultAccountId = defaultAccountId;
        _databasesFor = databasesFor;
        Text = "Quick queries";
        Icon = Ui.AppIcon;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        Size = new Size(W(980), W(640));
        MinimumSize = new Size(W(760), W(480));

        // ---- left: list -------------------------------------------------------------------------
        _list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle, FormattingEnabled = true };
        _list.Format += (_, e) => { if (e.ListItem is QuickQuery q) e.Value = $"{q.Name}  ·  {ConnectionName(q)}"; };
        // not while a list entry is only being renamed: re-inserting it fires this, and a reload would rewrite
        // the Name box under the caret (and briefly disable it, dropping the focus) on every keystroke
        _list.SelectedIndexChanged += (_, _) => { if (!_loading) LoadSelected(); };
        var listButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, W(4), 0, 0) };
        Button Small(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(W(34), W(26)) };
            b.Click += click;
            listButtons.Controls.Add(b);
            return b;
        }
        Small("New", (_, _) => AddNew());
        Small("Duplicate", (_, _) => Duplicate());
        Small("▲", (_, _) => MoveSelected(-1));
        Small("▼", (_, _) => MoveSelected(1));
        Small("Delete", (_, _) => DeleteSelected());
        var left = new Panel { Dock = DockStyle.Left, Width = W(250), Padding = new Padding(W(8)) };
        left.Controls.Add(_list);
        left.Controls.Add(listButtons);

        // ---- right: details ---------------------------------------------------------------------
        _details = new Panel { Dock = DockStyle.Fill, Padding = new Padding(W(8)) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, W(110)));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        T Row<T>(string label, T c) where T : Control
        {
            int r = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, W(7), W(6), 0) }, 0, r);
            c.Margin = new Padding(0, W(3), 0, W(3));
            grid.Controls.Add(c, 1, r);
            return c;
        }
        _name = Row("Name", new TextBox { Width = W(300) });
        var look = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        _color = new Button { Text = "Colour…", AutoSize = true };
        var noColor = new LinkLabel { Text = "none", AutoSize = true, Margin = new Padding(W(4), W(7), W(16), 0) };
        _icon = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = W(150) };
        _icon.Items.Add("(no icon)");
        foreach (var i in QuickQueryIcons.All) _icon.Items.Add(i.Label);
        look.Controls.AddRange(new Control[] { _color, noColor, new Label { Text = "Icon", AutoSize = true, Margin = new Padding(0, W(7), W(4), 0) }, _icon });
        Row("Look", look);
        _account = Row("Connection", new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = W(360) });
        _account.Items.Add(AnyConnection);
        foreach (var a in accounts) _account.Items.Add(a.Name);
        _database = Row("Database", new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = W(260) });
        _hotkey = Row("Hotkey", new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = W(120) });
        _hotkey.Items.Add("none");
        for (int k = 1; k <= 9; k++) _hotkey.Items.Add($"Ctrl+{k}");
        _toExcel = Row("", new CheckBox { Text = "Send the result straight to Excel (opens the .xlsx instead of a result tab)", AutoSize = true });
        _confirm = Row("", new CheckBox { Text = "Ask before running if the SQL contains UPDATE / DELETE / DROP / TRUNCATE / ALTER", AutoSize = true, Checked = true });
        _params = Row("", new Label { AutoSize = true, MaximumSize = new Size(W(620), 0), ForeColor = SystemColors.GrayText });
        _danger = Row("", new Label { AutoSize = true, MaximumSize = new Size(W(620), 0) });
        var sqlLabel = new Label { Dock = DockStyle.Top, Height = W(22), Text = "SQL   — placeholders: {{name}}, {{day:date}}, {{n:int}}, {{price:number}}; e.g.  WHERE city = {{city}} AND d >= {{from:date}}", ForeColor = SystemColors.GrayText, TextAlign = ContentAlignment.BottomLeft };
        ScintillaLoader.Ensure();
        _sql = new SqlEditor { Dock = DockStyle.Fill };
        var sqlHost = new Panel { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
        sqlHost.Controls.Add(_sql);
        _details.Controls.Add(sqlHost);
        _details.Controls.Add(sqlLabel);
        _details.Controls.Add(grid);

        // ---- bottom -----------------------------------------------------------------------------
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = W(44), Padding = new Padding(W(8)) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(W(90), W(28)) };
        var ok = new Button { Text = "Save", AutoSize = true, MinimumSize = new Size(W(90), W(28)) };
        ok.Click += (_, _) => OnSave();
        var export = new Button { Text = "Export…", AutoSize = true, MinimumSize = new Size(W(90), W(28)) };
        export.Click += (_, _) => ExportFile();
        var import = new Button { Text = "Import…", AutoSize = true, MinimumSize = new Size(W(90), W(28)) };
        import.Click += (_, _) => ImportFile();
        _status = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(W(20), W(8), 0, 0) };
        bottom.Controls.AddRange(new Control[] { cancel, ok, _status, export, import });
        CancelButton = cancel;

        Controls.Add(_details);
        Controls.Add(left);
        Controls.Add(bottom);

        // ---- field → item -----------------------------------------------------------------------
        _name.TextChanged += (_, _) => Edit(q => { q.Name = _name.Text.Trim().Length == 0 ? "Query" : _name.Text.Trim(); RefreshListText(); });
        _color.Click += (_, _) => PickColor();
        noColor.LinkClicked += (_, _) => Edit(q => { q.Color = null; ShowColor(q); });
        _icon.SelectedIndexChanged += (_, _) => Edit(q => q.Icon = _icon.SelectedIndex <= 0 ? null : QuickQueryIcons.All[_icon.SelectedIndex - 1].Key);
        _account.SelectedIndexChanged += (_, _) => Edit(q =>
        {
            var a = _account.SelectedIndex <= 0 ? null : _accounts[_account.SelectedIndex - 1];
            q.AccountId = a?.Id;
            q.AccountName = a?.Name;
            RefreshListText();
            Ui.Fire(this, () => FillDatabasesAsync(a?.Id));
        });
        _database.TextChanged += (_, _) => Edit(q => q.Database = _database.Text.Trim().Length == 0 ? null : _database.Text.Trim());
        _hotkey.SelectedIndexChanged += (_, _) => Edit(SetHotkey);
        _toExcel.CheckedChanged += (_, _) => Edit(q => q.ToExcel = _toExcel.Checked);
        _confirm.CheckedChanged += (_, _) => Edit(q => { q.ConfirmDangerous = _confirm.Checked; ShowAnalysis(q); });
        _sql.TextChanged += (_, _) => Edit(q => { q.Sql = _sql.Text; ShowAnalysis(q); });

        foreach (var q in _items) _list.Items.Add(q);
        Load += (_, _) =>
        {
            ThemeManager.Apply(this);
            if (addNew) AddNew();
            else
            {
                int i = selectId is null ? 0 : _items.FindIndex(q => q.Id == selectId);
                if (_items.Count > 0) _list.SelectedIndex = Math.Max(0, i);
                else LoadSelected();
            }
        };
    }

    private int W(int logical) => LogicalToDeviceUnits(logical);

    private QuickQuery? Current => _list.SelectedItem as QuickQuery;

    private string ConnectionName(QuickQuery q) => q.AccountId is null ? "every connection"
        : _accounts.FirstOrDefault(a => a.Id == q.AccountId)?.Name ?? (q.AccountName ?? "?") + " (missing)";

    private void Edit(Action<QuickQuery> change)
    {
        if (_loading || Current is not { } q) return;
        change(q);
    }

    private void RefreshListText()
    {
        int i = _list.SelectedIndex;
        if (i < 0) return;
        _loading = true;
        _list.Items[i] = _list.Items[i]; // repaints with the new ToString()
        _loading = false;
    }

    private void LoadSelected()
    {
        var q = Current;
        _details.Enabled = q is not null;
        _loading = true;
        try
        {
            _name.Text = q?.Name ?? "";
            ShowColor(q);
            _icon.SelectedIndex = q?.Icon is { } key ? Math.Max(0, Array.FindIndex(QuickQueryIcons.All, i => i.Key == key) + 1) : 0;
            int a = q?.AccountId is { } id ? _accounts.ToList().FindIndex(x => x.Id == id) : -1;
            _account.SelectedIndex = a + 1;
            _database.Text = q?.Database ?? "";
            _hotkey.SelectedIndex = q?.Hotkey ?? 0;
            _toExcel.Checked = q?.ToExcel ?? false;
            _confirm.Checked = q?.ConfirmDangerous ?? true;
            _sql.Text = q?.Sql ?? "";
            _sql.EmptyUndoBuffer();
        }
        finally { _loading = false; }
        if (q is not null)
        {
            ShowAnalysis(q);
            if (q.AccountId is not null && _accounts.All(x => x.Id != q.AccountId))
                _status.Text = string.IsNullOrEmpty(q.AccountName)
                    ? "This button's connection doesn't exist on this PC — choose one."
                    : $"The connection \"{q.AccountName}\" doesn't exist on this PC — choose one.";
            else _status.Text = "";
            Ui.Fire(this, () => FillDatabasesAsync(q.AccountId));
        }
    }

    private async Task FillDatabasesAsync(string? accountId)
    {
        var dbs = await _databasesFor(accountId);
        var text = _database.Text;
        _loading = true;
        try
        {
            _database.Items.Clear();
            _database.Items.AddRange(dbs.Cast<object>().ToArray());
            _database.Text = text;
        }
        finally { _loading = false; }
    }

    private void ShowColor(QuickQuery? q)
    {
        var c = QuickQueryIcons.ParseColor(q?.Color);
        _color.Image?.Dispose();
        _color.Image = null;
        if (c is { } color)
        {
            var bmp = new Bitmap(W(14), W(14));
            using (var g = Graphics.FromImage(bmp)) g.Clear(color);
            _color.Image = bmp;
            _color.TextImageRelation = TextImageRelation.ImageBeforeText;
        }
    }

    private void PickColor()
    {
        if (Current is not { } q) return;
        using var dlg = new ColorDialog { FullOpen = true, AnyColor = true, Color = QuickQueryIcons.ParseColor(q.Color) ?? Color.FromArgb(0, 120, 212) };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        q.Color = QuickQueryIcons.ToHex(dlg.Color);
        ShowColor(q);
    }

    /// <summary>
    /// A hotkey belongs to one button per connection: giving it to this one takes it from another button on the same
    /// toolbar (another connection's buttons keep theirs).
    /// </summary>
    private void SetHotkey(QuickQuery q)
    {
        int? key = _hotkey.SelectedIndex <= 0 ? null : _hotkey.SelectedIndex;
        q.Hotkey = key;
        if (key is null) return;
        foreach (var other in _items.Where(o => o != q && o.Hotkey == key && o.SharesToolbarWith(q)))
        {
            other.Hotkey = null;
            _status.Text = $"Ctrl+{key} moved from \"{other.Name}\" to \"{q.Name}\".";
        }
    }

    private void ShowAnalysis(QuickQuery q)
    {
        try
        {
            var ph = ParameterParser.Find(q.Sql);
            ParameterParser.Bind(q.Sql, ph.ToDictionary(p => p.Name, p => (object?)null, StringComparer.OrdinalIgnoreCase));
            _params.ForeColor = ThemeManager.Current.GrayText;
            _params.Text = ph.Count == 0 ? "No parameters."
                : "Asks for: " + string.Join(", ", ph.Select(p => $"{{{{{p.Name}}}}} ({p.Type.ToString().ToLowerInvariant()})")) + " — passed as query parameters.";
        }
        catch (ParameterException ex)
        {
            _params.ForeColor = ThemeManager.Current.Error;
            _params.Text = ex.Message;
        }
        bool dangerous = SqlClassifier.ContainsDangerous(q.Sql);
        _danger.ForeColor = dangerous ? ThemeManager.Current.Error : ThemeManager.Current.GrayText;
        _danger.Text = !dangerous ? "" : q.ConfirmDangerous
            ? "This SQL changes data or structure — you'll be asked to confirm each run."
            : "This SQL changes data or structure and will run WITHOUT asking.";
    }

    // ---- list actions -----------------------------------------------------------------------------

    private void AddNew()
    {
        var a = _accounts.FirstOrDefault(x => x.Id == _defaultAccountId);
        var q = new QuickQuery { Name = "New query", Sql = "SELECT * FROM ", AccountId = a?.Id, AccountName = a?.Name };
        _items.Add(q);
        _list.Items.Add(q);
        _list.SelectedItem = q;
        _name.Focus();
        _name.SelectAll();
    }

    private void Duplicate()
    {
        if (Current is not { } q) return;
        var copy = q.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = q.Name + " (copy)";
        copy.Hotkey = null;
        int i = _items.IndexOf(q) + 1;
        _items.Insert(i, copy);
        _list.Items.Insert(i, copy);
        _list.SelectedIndex = i;
    }

    internal void MoveSelected(int delta)
    {
        int i = _list.SelectedIndex, j = i + delta;
        if (i < 0 || j < 0 || j >= _items.Count) return;
        (_items[i], _items[j]) = (_items[j], _items[i]);
        _loading = true;
        _list.Items[i] = _items[i];
        _list.Items[j] = _items[j];
        _loading = false;
        _list.SelectedIndex = j;
    }

    private void DeleteSelected()
    {
        if (Current is not { } q || !Ui.Confirm(this, $"Delete the quick query \"{q.Name}\"?", "Delete", warning: true)) return;
        int i = _list.SelectedIndex;
        _items.RemoveAt(i);
        _list.Items.RemoveAt(i);
        if (_items.Count > 0) _list.SelectedIndex = Math.Min(i, _items.Count - 1);
        else LoadSelected();
    }

    // ---- file -------------------------------------------------------------------------------------

    private void ExportFile()
    {
        using var dlg = new SaveFileDialog { Filter = "Quick queries (*.json)|*.json", FileName = "quickqueries.json" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        ExportTo(dlg.FileName);
    }

    internal void ExportTo(string path)
    {
        QuickQueryStore.Export(path, _items);
        _status.Text = $"Exported {_items.Count} quick quer{(_items.Count == 1 ? "y" : "ies")} to {Path.GetFileName(path)}";
    }

    private void ImportFile()
    {
        using var dlg = new OpenFileDialog { Filter = "Quick queries (*.json)|*.json|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        List<QuickQuery> imported;
        try { imported = QuickQueryStore.Import(dlg.FileName, _accounts); }
        catch (InvalidDataException ex) { Ui.Error(this, ex); return; }
        var answer = MessageBox.Show(this,
            $"{imported.Count} quick quer{(imported.Count == 1 ? "y" : "ies")} in {Path.GetFileName(dlg.FileName)}.\n\n" +
            "Yes = add them to your list (ones you already have are updated)\nNo = replace your whole list",
            "Import quick queries", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.Cancel) return;
        ImportList(imported, replace: answer == DialogResult.No);
    }

    internal void ImportList(List<QuickQuery> imported, bool replace)
    {
        if (replace) _items.Clear();
        foreach (var q in imported)
        {
            int i = _items.FindIndex(x => x.Id == q.Id);
            if (q.Hotkey is { } k && _items.Any(x => x.Id != q.Id && x.Hotkey == k && x.SharesToolbarWith(q))) q.Hotkey = null; // keep your hotkeys
            if (i >= 0) _items[i] = q; else _items.Add(q);
        }
        _list.Items.Clear();
        foreach (var q in _items) _list.Items.Add(q);
        if (_items.Count > 0) _list.SelectedIndex = 0;
        _status.Text = $"Imported {imported.Count}; {_items.Count} in your list. Click Save to keep them.";
    }

    private void OnSave()
    {
        var empty = _items.FirstOrDefault(q => q.Sql.Trim().Length == 0);
        if (empty is not null)
        {
            _list.SelectedItem = empty;
            Ui.Info(this, $"\"{empty.Name}\" has no SQL.");
            return;
        }
        DialogResult = DialogResult.OK;
    }
}
