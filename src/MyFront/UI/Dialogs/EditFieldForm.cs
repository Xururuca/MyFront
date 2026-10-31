using MyFront.Core.Db;

namespace MyFront.UI.Dialogs;

/// <summary>
/// Object Browser → right-click a field → Edit field…: name, type, NULL, default, auto increment, collation, comment and
/// position, written as ALTER TABLE … CHANGE COLUMN. Starts from the column's SHOW CREATE TABLE definition, so what the
/// dialog doesn't show (ON UPDATE, generated expression, character set, CHECK, INVISIBLE …) is kept.
/// </summary>
internal sealed class EditFieldForm : DialogForm
{
    private const string TableDefault = "(table default)";
    private const string Unchanged = "(where it is)";

    private static readonly string[] Types =
    {
        "varchar(255)", "char(10)", "text", "mediumtext", "longtext", "tinyint", "smallint", "int", "int unsigned", "bigint",
        "bigint unsigned", "decimal(10,2)", "float", "double", "date", "datetime", "timestamp", "time", "year", "blob", "json",
    };

    private readonly ServerSession _session;
    private readonly string _database, _table, _column;
    private readonly TextBox _name, _value, _comment, _preview;
    private readonly ComboBox _type, _default, _collation, _position;
    private readonly CheckBox _null, _autoInc;
    private readonly Label _state;
    private readonly Button _ok;
    private readonly CancellationTokenSource _cts = new();
    private TableDdlContext? _ctx;
    private ColumnDefinition? _def;
    private bool _filling = true;

    /// <summary>Completes when the definition has been read (self-test).</summary>
    internal Task Loaded { get; private set; } = Task.CompletedTask;
    /// <summary>The statement OK runs (empty while the input is incomplete).</summary>
    internal string Sql => _preview.Text;

    public EditFieldForm(ServerSession session, string database, string table, string column) : base($"Edit field {column} — {table}")
    {
        _session = session;
        _database = database;
        _table = table;
        _column = column;

        _name = Row("Name", new TextBox { Width = W(280) });
        _type = Row("Type", new ComboBox { Width = W(280), DropDownStyle = ComboBoxStyle.DropDown });
        _type.Items.AddRange(Types);
        _null = Row("", new CheckBox { Text = "NULL allowed", AutoSize = true });
        var defaults = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        _default = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = W(150), Margin = new Padding(0, 0, W(6), 0) };
        _default.Items.AddRange(new object[] { "none", "NULL", "value", "CURRENT_TIMESTAMP", "expression" });
        _value = new TextBox { Width = W(220), Margin = Padding.Empty };
        defaults.Controls.Add(_default);
        defaults.Controls.Add(_value);
        Row("Default", defaults);
        _autoInc = Row("", new CheckBox { Text = "Auto increment", AutoSize = true });
        _collation = Row("Collation", new ComboBox { Width = W(280), DropDownStyle = ComboBoxStyle.DropDown });
        _comment = Row("Comment", new TextBox { Width = W(380) });
        _position = Row("Position", new ComboBox { Width = W(280), DropDownStyle = ComboBoxStyle.DropDownList });
        Row("", new Label { Text = "SQL", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, W(8), 0, 0) });
        _preview = Row("", new TextBox
        {
            Width = W(460), Height = W(78), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = Ui.Mono(9),
        });
        _state = Row("", new Label { AutoSize = true, MaximumSize = new Size(W(460), 0), ForeColor = SystemColors.GrayText, Text = "Loading…" });

        CancelButton = AddButton("Cancel", DialogResult.Cancel);
        _ok = AddButton("OK", DialogResult.None, (_, _) => Ui.Fire(this, ApplyAsync));
        _ok.Enabled = false;
        AcceptButton = _ok;

        foreach (var tb in new Control[] { _name, _value, _comment, _type, _collation })
            tb.TextChanged += (_, _) => UpdatePreview();
        foreach (var cb in new[] { _default, _position, _type, _collation })
            cb.SelectedIndexChanged += (_, _) => UpdatePreview();
        _null.CheckedChanged += (_, _) => UpdatePreview();
        _autoInc.CheckedChanged += (_, _) => UpdatePreview();
        FormClosed += (_, _) => _cts.Cancel();
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
            _def = ColumnDefinition.FromCreateTable(_ctx.CreateTable, _column);
        }
        catch (Exception ex) when (!IsDisposed && ex is not OperationCanceledException)
        {
            SetState(Ui.Describe(ex), error: true);
            return;
        }
        if (IsDisposed) return;
        var d = _def;
        _name.Text = d.Name;
        _type.Text = d.Type;
        _null.Checked = d.Nullable;
        _default.SelectedIndex = (int)d.DefaultKind;
        _value.Text = d.Default ?? "";
        _autoInc.Checked = d.AutoIncrement;
        _collation.Items.Add(TableDefault);
        _collation.Items.AddRange(_ctx.Collations.Cast<object>().ToArray());
        _collation.Text = d.Collation.Length == 0 ? TableDefault : d.Collation;
        _comment.Text = d.Comment;
        _position.Items.Add(Unchanged);
        _position.Items.Add("first");
        foreach (var c in _ctx.Table.Columns.Where(c => c.Name != _column)) _position.Items.Add("after " + c.Name);
        _position.SelectedIndex = 0;
        if (d.IsGenerated)
        {
            // a generated column has no default or auto increment; its expression is kept
            _default.Enabled = _value.Enabled = _autoInc.Enabled = false;
        }
        _filling = false;
        UpdatePreview();
    }

    private void SetState(string text, bool error)
    {
        _state.ForeColor = error ? ThemeManager.Current.Error : ThemeManager.Current.GrayText;
        _state.Text = text;
    }

    private ColumnPosition? SelectedPosition() => _position.SelectedIndex switch
    {
        <= 0 => null,
        1 => ColumnPosition.AtFirst,
        var i => ColumnPosition.AfterColumn(_ctx!.Table.Columns.Where(c => c.Name != _column).ElementAt(i - 2).Name),
    };

    private void UpdatePreview()
    {
        if (_filling || _def is null || _ctx is null) return;
        var kind = (ColumnDefaultKind)Math.Max(0, _default.SelectedIndex);
        if (!_def.IsGenerated) _value.Enabled = kind is ColumnDefaultKind.Value or ColumnDefaultKind.Expression;
        _def.Name = _name.Text;
        _def.Type = _type.Text;
        _def.Nullable = _null.Checked;
        _def.DefaultKind = kind;
        _def.Default = _value.Text;
        _def.AutoIncrement = _autoInc.Checked;
        _def.Collation = _collation.Text.Trim() is TableDefault or "" ? "" : _collation.Text.Trim();
        _def.Comment = _comment.Text;
        try
        {
            _preview.Text = TableDdl.ChangeColumn(_database, _table, _column, _def, SelectedPosition(), _ctx.NoBackslashEscapes);
            SetState(_def.IsGenerated ? "Generated column: its expression is kept." : "", error: false);
            _ok.Enabled = true;
        }
        catch (ArgumentException ex)
        {
            _preview.Text = "";
            SetState(ex.Message, error: true);
            _ok.Enabled = false;
        }
    }

    /// <summary>Runs the ALTER; on an error the dialog stays open with the server's message.</summary>
    internal async Task ApplyAsync()
    {
        var sql = Sql;
        if (sql.Length == 0) return;
        _ok.Enabled = false;
        SetState("Saving…", error: false);
        UseWaitCursor = true;
        try
        {
            await TableDdl.ExecuteAsync(_session, _database, sql, $"edit field {_table}.{_column}", _cts.Token);
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
