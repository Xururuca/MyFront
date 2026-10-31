using System.Globalization;
using MyFront.Core.History;

namespace MyFront.UI.Dialogs;

/// <summary>Browses the SQL history of one account; "Insert" puts the statement into the editor.</summary>
internal sealed class HistoryForm : Form
{
    private readonly ListView _list;
    private readonly TextBox _preview, _filter;
    private readonly List<HistoryEntry> _entries;

    public string? SelectedSql { get; private set; }

    public HistoryForm(string accountId, string accountName)
    {
        Text = "SQL history — " + accountName;
        Icon = Ui.AppIcon;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Size = new Size(LogicalToDeviceUnits(900), LogicalToDeviceUnits(560));

        _entries = SqlHistory.Read(accountId);

        _filter = new TextBox { Dock = DockStyle.Top, PlaceholderText = "Filter (text contained in the statement)" };
        _filter.TextChanged += (_, _) => Fill();
        _list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, VirtualMode = false };
        _list.Columns.Add("Time", LogicalToDeviceUnits(130));
        _list.Columns.Add("Database", LogicalToDeviceUnits(100));
        _list.Columns.Add("Result", LogicalToDeviceUnits(160));
        _list.Columns.Add("Statement", LogicalToDeviceUnits(480));
        _list.SelectedIndexChanged += (_, _) => _preview!.Text = Current?.Sql.Replace("\n", "\r\n") ?? "";
        _list.DoubleClick += (_, _) => Insert();
        _preview = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = Ui.Mono(10) };

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        split.Panel1.Controls.Add(_list);
        split.Panel1.Controls.Add(_filter);
        split.Panel2.Controls.Add(_preview);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(LogicalToDeviceUnits(6)) };
        var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, AutoSize = true };
        var insert = new Button { Text = "Insert into editor", AutoSize = true };
        insert.Click += (_, _) => Insert();
        buttons.Controls.Add(close);
        buttons.Controls.Add(insert);
        CancelButton = close;

        Controls.Add(split);
        Controls.Add(buttons);
        Load += (_, _) => split.SplitterDistance = split.Height * 3 / 5;
        Fill();
    }

    protected override void OnLoad(EventArgs e)
    {
        ThemeManager.Apply(this);
        base.OnLoad(e);
    }

    private HistoryEntry? Current => _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as HistoryEntry : null;

    private void Fill()
    {
        var f = _filter.Text.Trim();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var e in _entries)
        {
            if (f.Length > 0 && !e.Sql.Contains(f, StringComparison.CurrentCultureIgnoreCase)) continue;
            var line = e.Sql.Length > 200 ? e.Sql[..200] : e.Sql;
            _list.Items.Add(new ListViewItem(new[]
            {
                e.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), e.Database ?? "", e.Info, line.Replace('\n', ' '),
            }) { Tag = e });
            if (_list.Items.Count >= 2000) break;
        }
        _list.EndUpdate();
    }

    private void Insert()
    {
        if (Current is null) return;
        SelectedSql = Current.Sql.Replace("\n", Environment.NewLine);
        DialogResult = DialogResult.OK;
    }
}
