using MyFront.Core.Db;

namespace MyFront.UI.Dialogs;

/// <summary>
/// Tree → right-click a table or view → Rename…: RENAME TABLE; a field → Rename…: ALTER TABLE … CHANGE COLUMN with the
/// field's whole definition from SHOW CREATE TABLE, only the name changed. Shown before it runs; a server error keeps
/// the dialog open.
/// </summary>
internal sealed class RenameForm : DialogForm
{
    private readonly ServerSession _session;
    private readonly string _database, _oldName;
    /// <summary>A field: its table; null when a table or view is renamed.</summary>
    private readonly string? _table;
    private readonly TextBox _name;
    private readonly Label _preview, _state;
    private readonly Button _ok;
    private readonly CancellationTokenSource _cts = new();
    /// <summary>A field: its definition (null until read) and how literals are written.</summary>
    private ColumnDefinition? _field;
    private bool _noBackslashEscapes;

    public string NewName => _name.Text.Trim();
    internal string Sql => _preview.Text;
    /// <summary>Completes when the field's definition has been read (self-test).</summary>
    internal Task Loaded { get; private set; } = Task.CompletedTask;

    public RenameForm(ServerSession session, string database, string name, bool isView)
        : this(session, database, name, null, $"Rename {(isView ? "view" : "table")} {name}") { }

    /// <summary>Renames a field of <paramref name="table"/>.</summary>
    public RenameForm(ServerSession session, string database, string table, string column)
        : this(session, database, column, table, $"Rename field {column} — {table}") { }

    private RenameForm(ServerSession session, string database, string name, string? table, string title) : base(title)
    {
        _session = session;
        _database = database;
        _oldName = name;
        _table = table;
        _name = Row("New name", new TextBox { Width = W(320), Text = name });
        _preview = Row("SQL", new Label { AutoSize = true, MaximumSize = new Size(W(420), 0), Font = Ui.Mono(9) });
        _state = Row("", new Label { AutoSize = true, MaximumSize = new Size(W(420), 0), ForeColor = SystemColors.GrayText, Text = table is null ? "" : "Loading…" });
        CancelButton = AddButton("Cancel", DialogResult.Cancel);
        _ok = AddButton("Rename", DialogResult.None, (_, _) => Ui.Fire(this, ApplyAsync));
        AcceptButton = _ok;
        _name.TextChanged += (_, _) => UpdatePreview();
        FormClosed += (_, _) => _cts.Cancel();
        UpdatePreview();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (_table is not null) Loaded = LoadFieldAsync();
    }

    private async Task LoadFieldAsync()
    {
        try
        {
            var ctx = await TableDdlContext.LoadAsync(_session, _database, _table!, _cts.Token);
            _field = ColumnDefinition.FromCreateTable(ctx.CreateTable, _oldName);
            _noBackslashEscapes = ctx.NoBackslashEscapes;
        }
        catch (Exception ex) when (!IsDisposed && ex is not OperationCanceledException)
        {
            _state.ForeColor = ThemeManager.Current.Error;
            _state.Text = Ui.Describe(ex);
            return;
        }
        if (IsDisposed) return;
        _state.Text = "";
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        bool changed = NewName.Length > 0 && NewName != _oldName;
        if (_table is null)
            _preview.Text = changed ? TableDdl.Rename(_database, _oldName, NewName) : "";
        else if (_field is not null && changed)
        {
            _field.Name = NewName;
            _preview.Text = TableDdl.ChangeColumn(_database, _table, _oldName, _field, null, _noBackslashEscapes);
        }
        else _preview.Text = "";
        _ok.Enabled = _preview.Text.Length > 0;
    }

    internal async Task ApplyAsync()
    {
        if (!_ok.Enabled) return;
        _ok.Enabled = false;
        _state.ForeColor = ThemeManager.Current.GrayText;
        _state.Text = "Renaming…";
        try
        {
            await TableDdl.ExecuteAsync(_session, _database, Sql, _table is null ? $"rename {_oldName}" : $"rename field {_table}.{_oldName}", _cts.Token);
            DialogResult = DialogResult.OK;
        }
        catch (Exception ex) when (!IsDisposed && ex is not OperationCanceledException)
        {
            _state.ForeColor = ThemeManager.Current.Error;
            _state.Text = Ui.Describe(ex);
            _ok.Enabled = true;
        }
    }
}
