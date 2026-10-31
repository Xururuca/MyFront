using System.Diagnostics;
using System.Globalization;
using MyFront.Core;
using MyFront.Core.Export;

namespace MyFront.UI.Dialogs;

/// <summary>What the user is exporting. Several items = database export (one sheet / file per item).</summary>
internal sealed class ExportRequest
{
    public required string Title { get; init; }
    public required string BaseName { get; init; }
    public required List<ExportItem> Items { get; init; }
    public bool IsDatabase { get; init; }
    public string Format { get; init; } = "xlsx";
}

internal sealed record ExportItem(ExportTable Table, bool IsView, bool Selected);

/// <summary>Export to Excel / CSV with progress and cancel.</summary>
internal sealed class ExportForm : DialogForm
{
    private readonly ExportRequest _request;
    private readonly RadioButton _xlsx, _csv;
    private readonly TextBox _path;
    private readonly CheckedListBox? _items;
    private readonly ComboBox _delimiter, _decimal, _encoding;
    private readonly CheckBox _header, _open;
    private readonly FlowLayoutPanel _csvOptions;
    private readonly ProgressBar _bar;
    private readonly Label _state;
    private readonly Button _export, _close;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 200 };
    private CancellationTokenSource? _cts;
    private ExportProgress? _progress;
    private Stopwatch _sw = new();

    public ExportResult? Result { get; private set; }

    public ExportForm(ExportRequest request) : base("Export")
    {
        _request = request;
        var s = AppSettings.Current;

        Row("What", new Label { Text = request.Title, AutoSize = true, MaximumSize = new Size(W(420), 0) });

        var formats = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        _xlsx = new RadioButton { Text = "Excel (.xlsx)", AutoSize = true, Checked = request.Format != "csv" };
        _csv = new RadioButton { Text = "CSV", AutoSize = true, Checked = request.Format == "csv" };
        formats.Controls.Add(_xlsx);
        formats.Controls.Add(_csv);
        Row("Format", formats);

        var browse = new Button { Text = "…", Width = W(30), Height = W(24) };
        _path = Row(request.IsDatabase ? "Target" : "File", new TextBox { Width = W(420) }, browse);
        browse.Click += (_, _) => Browse();

        if (request.IsDatabase)
        {
            _items = Row("Tables", new CheckedListBox { Width = W(420), Height = W(150), CheckOnClick = true, IntegralHeight = false });
            foreach (var item in request.Items)
                _items.Items.Add(item.Table.Name + (item.IsView ? "  (view)" : ""), item.Selected);
            var all = new LinkLabel { Text = "all / none", AutoSize = true };
            all.LinkClicked += (_, _) =>
            {
                bool check = _items.CheckedItems.Count < _items.Items.Count;
                for (int i = 0; i < _items.Items.Count; i++) _items.SetItemChecked(i, check);
            };
            Row("", all);
        }

        _csvOptions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        _delimiter = Combo(_csvOptions, "Delimiter", new[] { "; (semicolon)", ", (comma)", "Tab" },
            s.CsvDelimiter switch { "," => 1, "\t" => 2, _ => 0 });
        _decimal = Combo(_csvOptions, "Decimal", new[] { ", (1234,56)", ". (1234.56)" }, s.CsvDecimal == "." ? 1 : 0);
        _encoding = Combo(_csvOptions, "Encoding", new[] { "UTF-8 with BOM", "UTF-8", "Windows-1254 (Turkish)" },
            s.CsvEncoding switch { "utf-8" => 1, "windows-1254" => 2, _ => 0 });
        _header = new CheckBox { Text = "Header row", AutoSize = true, Checked = s.CsvHeader, Margin = new Padding(W(8), W(4), 0, 0) };
        _csvOptions.Controls.Add(_header);
        Row("CSV", _csvOptions);

        _open = Row("", new CheckBox { Text = "Open the file when done", AutoSize = true, Checked = s.OpenAfterExport });
        _bar = Row("", new ProgressBar { Width = W(420), Height = W(16), Maximum = 1000 });
        _state = Row("", new Label { AutoSize = true, MaximumSize = new Size(W(420), 0), ForeColor = SystemColors.GrayText, Text = "" });

        _close = AddButton("Close", DialogResult.None, (_, _) => { if (_cts is not null) _cts.Cancel(); else Close(); });
        _export = AddButton("Export", DialogResult.None, (_, _) => Ui.Fire(this, ExportAsync));
        AcceptButton = _export;

        _xlsx.CheckedChanged += (_, _) => OnFormatChanged();
        _timer.Tick += (_, _) => UpdateProgress();
        FormClosing += (_, e) =>
        {
            if (_cts is null) return;
            e.Cancel = true; // cancel first, the dialog closes itself after
            _cts.Cancel();
        };

        _path.Text = DefaultTarget();
        OnFormatChanged();
    }

    private ComboBox Combo(FlowLayoutPanel host, string label, string[] items, int selected)
    {
        host.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(host.Controls.Count == 0 ? 0 : W(8), W(6), W(4), 0) });
        var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = W(label == "Encoding" ? 150 : 95) };
        cb.Items.AddRange(items);
        cb.SelectedIndex = selected;
        host.Controls.Add(cb);
        return cb;
    }

    private bool IsCsv => _csv.Checked;
    private bool TargetIsFolder => IsCsv && _request.IsDatabase && SelectedTables().Count != 1;

    private string DefaultTarget()
    {
        var folder = AppSettings.Current.ResolvedExportFolder;
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        return Path.Combine(folder, $"{ExportValues.FileName(_request.BaseName)}_{stamp}");
    }

    private void OnFormatChanged()
    {
        _csvOptions.Enabled = IsCsv;
        var p = _path.Text.Trim();
        var ext = Path.GetExtension(p);
        if (ext.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) || ext.Equals(".csv", StringComparison.OrdinalIgnoreCase))
            p = p[..^ext.Length];
        _path.Text = TargetIsFolder ? p : p + (IsCsv ? ".csv" : ".xlsx");
    }

    private void Browse()
    {
        if (TargetIsFolder)
        {
            using var fb = new FolderBrowserDialog { InitialDirectory = Path.GetDirectoryName(_path.Text) ?? "" };
            if (fb.ShowDialog(this) == DialogResult.OK)
                _path.Text = Path.Combine(fb.SelectedPath, Path.GetFileName(_path.Text));
            return;
        }
        using var dlg = new SaveFileDialog
        {
            Filter = IsCsv ? "CSV files (*.csv)|*.csv" : "Excel workbook (*.xlsx)|*.xlsx",
            FileName = Path.GetFileName(_path.Text),
            InitialDirectory = Path.GetDirectoryName(_path.Text) ?? "",
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) _path.Text = dlg.FileName;
    }

    private List<ExportTable> SelectedTables()
    {
        if (_items is null) return _request.Items.Select(i => i.Table).ToList();
        var list = new List<ExportTable>();
        for (int i = 0; i < _items.Items.Count; i++)
            if (_items.GetItemChecked(i)) list.Add(_request.Items[i].Table);
        return list;
    }

    private CsvOptions CsvOptionsFromUi() => new()
    {
        Delimiter = _delimiter.SelectedIndex switch { 1 => ',', 2 => '\t', _ => ';' },
        DecimalSeparator = _decimal.SelectedIndex == 1 ? '.' : ',',
        Encoding = _encoding.SelectedIndex switch { 1 => "utf-8", 2 => "windows-1254", _ => "utf-8-bom" },
        Header = _header.Checked,
    };

    private void SaveSettings(string target)
    {
        var s = AppSettings.Current;
        var o = CsvOptionsFromUi();
        s.ExportFolder = TargetIsFolder ? Path.GetDirectoryName(target) : Path.GetDirectoryName(Path.GetFullPath(target));
        s.OpenAfterExport = _open.Checked;
        s.CsvDelimiter = o.Delimiter.ToString();
        s.CsvDecimal = o.DecimalSeparator.ToString();
        s.CsvEncoding = o.Encoding;
        s.CsvHeader = o.Header;
        s.Save();
    }

    public async Task ExportAsync()
    {
        var tables = SelectedTables();
        if (tables.Count == 0) { Ui.Info(this, "Choose at least one table."); return; }
        if (!TargetIsFolder && IsCsv && tables.Count == 1 && _request.IsDatabase && !_path.Text.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            _path.Text += ".csv";
        var target = _path.Text.Trim();
        if (target.Length == 0) { Ui.Info(this, "Choose a file name."); return; }
        if (!TargetIsFolder && File.Exists(target) && !Ui.Confirm(this, $"{Path.GetFileName(target)} exists. Overwrite it?")) return;
        SaveSettings(target);

        _progress = new ExportProgress { TotalRowsEstimate = tables.All(t => t.EstimatedRows is not null) ? tables.Sum(t => t.EstimatedRows) : null };
        _cts = new CancellationTokenSource();
        SetBusy(true);
        _sw = Stopwatch.StartNew();
        _timer.Start();
        try
        {
            Result = IsCsv
                ? await CsvExporter.ExportAsync(target, tables, CsvOptionsFromUi(), _progress, _cts.Token)
                : await ExcelExporter.ExportAsync(target, tables, _progress, _cts.Token);
            _bar.Style = ProgressBarStyle.Continuous;
            _bar.Value = _bar.Maximum;
            _bar.Value = _bar.Maximum - 1; // stepping back skips the slow fill animation
            _bar.Value = _bar.Maximum;
            _state.ForeColor = ThemeManager.Current.Ok;
            _state.Text = $"Done: {Result.Rows:N0} rows" + (IsCsv ? $" · {Result.Files.Count} file(s)" : $" · {Result.Sheets} sheet(s)") +
                          $" · {Duration(Result.Elapsed)}\n{target}";
            if (_open.Checked) Ui.OpenPath(TargetIsFolder ? target : Result.Files[0]);
        }
        catch (Exception ex) when (_cts.IsCancellationRequested)
        {
            _ = ex;
            _state.ForeColor = ThemeManager.Current.GrayText;
            _state.Text = $"Cancelled after {_progress.Rows:N0} rows. Nothing was written.";
        }
        catch (Exception ex)
        {
            _state.ForeColor = ThemeManager.Current.Error;
            _state.Text = Ui.Describe(ex);
        }
        finally
        {
            _timer.Stop();
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    private static string Duration(TimeSpan t) => Core.Db.CellFormat.FormatDuration(t);

    private void SetBusy(bool busy)
    {
        _export.Enabled = !busy;
        _xlsx.Enabled = _csv.Enabled = _path.Enabled = !busy;
        if (_items is not null) _items.Enabled = !busy;
        _csvOptions.Enabled = !busy && IsCsv;
        _close.Text = busy ? "Cancel" : "Close";
        if (busy)
        {
            _state.ForeColor = ThemeManager.Current.GrayText;
            _bar.Value = 0;
            _bar.Style = _progress?.TotalRowsEstimate is > 0 ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
        }
    }

    private void UpdateProgress()
    {
        if (_progress is null) return;
        long rows = _progress.Rows;
        if (_progress.TotalRowsEstimate is > 0 and var total)
            _bar.Value = (int)Math.Min(_bar.Maximum, rows * _bar.Maximum / total);
        var where = _progress.CurrentTable is { } t && _request.Items.Count > 1 ? $" · {t}" : "";
        var sheets = !IsCsv && _progress.Sheets > 1 ? $" · sheet {_progress.Sheets}" : "";
        _state.Text = $"{rows:N0} rows{sheets}{where} · {Duration(_sw.Elapsed)}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
