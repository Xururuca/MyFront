using System.Globalization;
using MyFront.Core.Db;
using MySqlConnector;

namespace MyFront.UI;

/// <summary>SHOW FULL PROCESSLIST with refresh, auto-refresh and kill.</summary>
internal sealed class ProcessesView : UserControl
{
    private readonly ResultGrid _grid = new() { Dock = DockStyle.Fill };
    private readonly ToolStripButton _kill, _auto;
    private readonly ToolStripLabel _info = new("") { ForeColor = SystemColors.GrayText };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 3000 };
    private ServerSession? _session;
    private bool _loading;

    public ProcessesView()
    {
        var bar = Ui.NewToolStrip(this);
        Ui.Button(bar, "Refresh", Icons.Refresh, Icons.Green, (_, _) => Ui.Fire(this, LoadAsync), "Refresh (F5)");
        _auto = Ui.Button(bar, "Auto refresh", Icons.History, Icons.Blue, (_, _) => { }, "Refresh every 3 seconds", showText: true);
        _auto.CheckOnClick = true;
        _auto.CheckedChanged += (_, _) => _timer.Enabled = _auto.Checked;
        _kill = Ui.Button(bar, "Kill", Icons.Stop, Icons.Red, (_, _) => Ui.Fire(this, KillAsync), "Kill the selected connection", showText: true);
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add(_info);
        _timer.Tick += (_, _) => Ui.Fire(this, LoadAsync);
        Controls.Add(_grid);
        Controls.Add(bar);
    }

    public async Task ShowAsync(ServerSession session)
    {
        _session = session;
        _kill.Enabled = !session.ReadOnly;
        await LoadAsync();
    }

    public void Pause() => _auto.Checked = false;

    public async Task LoadAsync()
    {
        if (_session is null || _loading) return;
        _loading = true;
        try
        {
            var store = new RowStore();
            ColumnInfo[] cols;
            await using (var conn = await _session.OpenAsync(null))
            await using (var cmd = new MySqlCommand("SHOW FULL PROCESSLIST", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
            {
                cols = ColumnInfo.FromReader(r);
                while (await r.ReadAsync())
                {
                    var v = new object?[r.FieldCount];
                    r.GetValues(v!);
                    for (int i = 0; i < v.Length; i++) v[i] = CellFormat.Normalize(v[i]);
                    store.Add(v);
                }
            }
            int keepRow = _grid.CurrentCell?.RowIndex ?? -1;
            var widths = _grid.Columns.Count == cols.Length ? _grid.Columns.Cast<DataGridViewColumn>().Select(c => c.Width).ToArray() : null;
            _grid.Bind(cols, store);
            if (widths is null) _grid.AutoFitColumns();
            else for (int i = 0; i < widths.Length; i++) _grid.Columns[i].Width = widths[i];
            if (keepRow >= 0 && keepRow < _grid.RowCount) _grid.CurrentCell = _grid[0, keepRow];
            _info.Text = $"{store.Count} connection(s) · {DateTime.Now:HH:mm:ss}";
        }
        finally { _loading = false; }
    }

    private async Task KillAsync()
    {
        if (_session is null || _grid.CurrentCell is null || _grid.Source is null) return;
        var row = _grid.Source.GetRow(_grid.CurrentCell.RowIndex);
        if (row.Length == 0 || row[0] is null) return;
        long id = Convert.ToInt64(row[0], CultureInfo.InvariantCulture);
        var info = row.Length > 7 ? Convert.ToString(row[7]) : null;
        if (!Ui.Confirm(FindForm(), $"Kill connection {id} ({row[1]}@{row[2]})?\n\n{info}", "Kill process", warning: true)) return;
        var sql = $"KILL {id.ToString(CultureInfo.InvariantCulture)}";
        _session.EnsureAllowed(sql);
        await using (var conn = await _session.OpenAsync(null))
        await using (var cmd = new MySqlCommand(sql, conn))
            await cmd.ExecuteNonQueryAsync();
        await LoadAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Session and global server variables side by side, with a name filter.</summary>
internal sealed class VariablesView : UserControl
{
    private readonly ResultGrid _grid = new() { Dock = DockStyle.Fill };
    private readonly ToolStripTextBox _filter;
    private readonly ToolStripLabel _info = new("") { ForeColor = SystemColors.GrayText };
    private ServerSession? _session;
    private List<object?[]> _all = new();

    private static readonly ColumnInfo[] Columns =
    {
        new() { Name = "Variable", DataTypeName = "VARCHAR" },
        new() { Name = "Session value", DataTypeName = "VARCHAR" },
        new() { Name = "Global value", DataTypeName = "VARCHAR" },
    };

    public VariablesView()
    {
        var bar = Ui.NewToolStrip(this);
        Ui.Button(bar, "Refresh", Icons.Refresh, Icons.Green, (_, _) => Ui.Fire(this, LoadAsync), "Refresh (F5)");
        bar.Items.Add(new ToolStripLabel("Filter"));
        _filter = new ToolStripTextBox { Width = LogicalToDeviceUnits(200) };
        _filter.TextChanged += (_, _) => ApplyFilter();
        bar.Items.Add(_filter);
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add(_info);
        Controls.Add(_grid);
        Controls.Add(bar);
    }

    public async Task ShowAsync(ServerSession session)
    {
        _session = session;
        await LoadAsync();
    }

    public async Task LoadAsync()
    {
        if (_session is null) return;
        var session = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var global = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        await using (var conn = await _session.OpenAsync(null))
        {
            foreach (var (sql, target) in new[] { ("SHOW SESSION VARIABLES", session), ("SHOW GLOBAL VARIABLES", global) })
            {
                await using var cmd = new MySqlCommand(sql, conn);
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) target[r.GetString(0)] = r.IsDBNull(1) ? null : r.GetString(1);
            }
        }
        _all = session.Keys.Union(global.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Select(k => new object?[] { k, session.GetValueOrDefault(k), global.GetValueOrDefault(k) })
            .ToList();
        ApplyFilter();
        _grid.AutoFitColumns(400);
    }

    private void ApplyFilter()
    {
        var f = _filter.Text.Trim();
        var store = new RowStore();
        foreach (var row in _all)
            if (f.Length == 0 || ((string)row[0]!).Contains(f, StringComparison.OrdinalIgnoreCase))
                store.Add(row);
        var widths = _grid.Columns.Count == 3 ? _grid.Columns.Cast<DataGridViewColumn>().Select(c => c.Width).ToArray() : null;
        _grid.Bind(Columns, store);
        if (widths is not null) for (int i = 0; i < 3; i++) _grid.Columns[i].Width = widths[i];
        _info.Text = $"{store.Count} of {_all.Count} variable(s)";
    }
}
