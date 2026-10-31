using System.Diagnostics;
using MyFront.Core;
using MyFront.Core.Db;
using MyFront.Core.Export;

namespace MyFront.UI.Dialogs;

/// <summary>Tree → right-click a table or view → Export SQL…: a mysqldump-like .sql file, streamed, with progress and cancel.</summary>
internal sealed class SqlExportForm : DialogForm
{
    private readonly ServerSession _session;
    private readonly string _database, _name;
    private readonly bool _isView;
    private readonly long? _rows;
    private readonly TextBox _path;
    private readonly RadioButton _both, _structure, _data;
    private readonly CheckBox _definer;
    private readonly ProgressBar _bar;
    private readonly Label _state;
    private readonly Button _export, _close;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 200 };
    private CancellationTokenSource? _cts;
    private ExportProgress? _progress;
    private Stopwatch _sw = new();

    public ExportResult? Result { get; private set; }

    /// <param name="rows">Row estimate for the progress bar (null for views / unknown).</param>
    public SqlExportForm(ServerSession session, string database, string name, bool isView, long? rows) : base("Export SQL — " + name)
    {
        _session = session;
        _database = database;
        _name = name;
        _isView = isView;
        _rows = rows;
        Row("What", new Label
        {
            AutoSize = true, MaximumSize = new Size(W(420), 0),
            Text = isView ? $"View {database}.{name}: DROP VIEW IF EXISTS + CREATE VIEW"
                : $"Table {database}.{name}{(rows is null ? "" : $" (≈{rows:N0} rows)")}: DROP TABLE IF EXISTS, CREATE TABLE with its keys and foreign keys, the rows as INSERTs, the triggers",
        });
        var browse = new Button { Text = "…", Width = W(30), Height = W(24) };
        _path = Row("File", new TextBox { Width = W(420), Text = Path.Combine(AppSettings.Current.ResolvedExportFolder, ExportValues.FileName(name) + ".sql") }, browse);
        browse.Click += (_, _) => Browse();
        var contents = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        _both = new RadioButton { Text = "Structure and data", AutoSize = true, Checked = true };
        _structure = new RadioButton { Text = "Structure only", AutoSize = true };
        _data = new RadioButton { Text = "Data only", AutoSize = true };
        contents.Controls.AddRange(new Control[] { _both, _structure, _data });
        contents.Enabled = !isView; // a view has no rows of its own
        Row("Contents", contents);
        _definer = Row("", new CheckBox { Text = "Leave out DEFINER (the importing user owns triggers and views)", AutoSize = true, Checked = true });
        _bar = Row("", new ProgressBar { Width = W(420), Height = W(16), Maximum = 1000 });
        _state = Row("", new Label { AutoSize = true, MaximumSize = new Size(W(420), 0), ForeColor = SystemColors.GrayText, Text = "UTF-8 without BOM" });
        _close = AddButton("Close", DialogResult.None, (_, _) => { if (_cts is not null) _cts.Cancel(); else Close(); });
        _export = AddButton("Export", DialogResult.None, (_, _) => Ui.Fire(this, ExportAsync));
        AcceptButton = _export;
        _timer.Tick += (_, _) => UpdateProgress();
        FormClosing += (_, e) =>
        {
            if (_cts is null) return;
            e.Cancel = true; // cancel first, the dialog closes itself after
            _cts.Cancel();
        };
    }

    private void Browse()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "SQL files (*.sql)|*.sql|All files (*.*)|*.*",
            FileName = Path.GetFileName(_path.Text),
            InitialDirectory = Path.GetDirectoryName(_path.Text) ?? "",
            OverwritePrompt = false, // asked on Export
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) _path.Text = dlg.FileName;
    }

    internal SqlDumpOptions Options => new()
    {
        Structure = _isView || !_data.Checked,
        Data = !_isView && !_structure.Checked,
        StripDefiner = _definer.Checked,
    };

    internal async Task ExportAsync()
    {
        var target = _path.Text.Trim();
        if (target.Length == 0) { Ui.Info(this, "Choose a file name."); return; }
        if (File.Exists(target) && !Ui.Confirm(this, $"{Path.GetFileName(target)} exists. Overwrite it?")) return;
        _progress = new ExportProgress { TotalRowsEstimate = _isView || _structure.Checked ? null : _rows };
        _cts = new CancellationTokenSource();
        SetBusy(true);
        _sw = Stopwatch.StartNew();
        _timer.Start();
        try
        {
            Result = await SqlDump.WriteAsync(_session, _database, new[] { new SqlDumpObject(_name, _isView) }, target, Options, _progress, _cts.Token);
            _bar.Style = ProgressBarStyle.Continuous;
            _bar.Value = _bar.Maximum;
            _bar.Value = _bar.Maximum - 1; // stepping back skips the slow fill animation
            _bar.Value = _bar.Maximum;
            _state.ForeColor = ThemeManager.Current.Ok;
            _state.Text = $"Done: {Result.Rows:N0} rows · {CellFormat.FormatDuration(Result.Elapsed)}\n{target}";
            var folder = Path.GetDirectoryName(Path.GetFullPath(target));
            if (folder is not null)
            {
                AppSettings.Current.ExportFolder = folder;
                AppSettings.Current.Save();
            }
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

    private void SetBusy(bool busy)
    {
        _export.Enabled = _path.Enabled = _definer.Enabled = !busy;
        _both.Enabled = _structure.Enabled = _data.Enabled = !busy;
        _close.Text = busy ? "Cancel" : "Close";
        if (!busy) return;
        _state.ForeColor = ThemeManager.Current.GrayText;
        _bar.Value = 0;
        _bar.Style = _progress?.TotalRowsEstimate is > 0 ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
    }

    private void UpdateProgress()
    {
        if (_progress is null) return;
        long rows = _progress.Rows;
        if (_progress.TotalRowsEstimate is > 0 and var total)
            _bar.Value = (int)Math.Min(_bar.Maximum, rows * _bar.Maximum / total);
        _state.Text = $"{rows:N0} rows · {CellFormat.FormatDuration(_sw.Elapsed)}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
