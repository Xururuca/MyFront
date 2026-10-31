using System.Diagnostics;
using System.Text;
using MyFront.Core.Db;
using MyFront.Core.History;
using MySqlConnector;

namespace MyFront.UI.Dialogs;

/// <summary>Streams a .sql file statement by statement (memory stays flat for big dumps), with progress and cancel.</summary>
internal sealed class RunSqlFileForm : DialogForm
{
    private readonly ServerSession _session;
    private readonly TextBox _path, _database, _log;
    private readonly ComboBox _encoding;
    private readonly CheckBox _stopOnError;
    private readonly ProgressBar _progress;
    private readonly Label _state;
    private readonly Button _start, _close;
    private CancellationTokenSource? _cts;

    static RunSqlFileForm() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <param name="title">Window title; default "Run SQL file — connection".</param>
    public RunSqlFileForm(ServerSession session, string? database, string? path, string? title = null)
        : base(title ?? "Run SQL file — " + session.DisplayName)
    {
        _session = session;
        var browse = new Button { Text = "…", Width = W(30), Height = W(24) };
        _path = Row("File", new TextBox { Width = W(380), Text = path ?? "" }, browse);
        browse.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Filter = "SQL files (*.sql)|*.sql|All files (*.*)|*.*" };
            if (dlg.ShowDialog(this) == DialogResult.OK) _path.Text = dlg.FileName;
        };
        _encoding = Row("Encoding", new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = W(220) });
        _encoding.Items.AddRange(new object[] { "UTF-8", "Windows-1254 (Turkish)", "ISO-8859-9 (Turkish)", "Windows-1252" });
        _encoding.SelectedIndex = 0;
        _database = Row("Database", new TextBox { Width = W(220), Text = database ?? "", PlaceholderText = "(none — the file uses USE)" });
        _stopOnError = Row("", new CheckBox { Text = "Stop at the first error", AutoSize = true, Checked = true });
        if (session.ReadOnly)
            Row("", new Label { AutoSize = true, ForeColor = ThemeManager.Current.Error, Text = "Read-only connection: statements that write are refused (not run)." });
        _progress = Row("", new ProgressBar { Width = W(380), Height = W(16), Maximum = 1000 });
        _state = Row("", new Label { AutoSize = true, Text = "", ForeColor = SystemColors.GrayText });
        _log = Row("Errors", new TextBox { Width = W(380), Height = W(110), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = Ui.Mono(9) });

        _close = AddButton("Close", DialogResult.None, (_, _) => { if (_cts is not null) _cts.Cancel(); else Close(); });
        _start = AddButton("Run", DialogResult.None, (_, _) => Ui.Fire(this, RunAsync));
        AcceptButton = _start;
        FormClosing += (_, e) =>
        {
            if (_cts is null) return;
            e.Cancel = true;
            _cts.Cancel();
        };
    }

    private Encoding SelectedEncoding => _encoding.SelectedIndex switch
    {
        1 => Encoding.GetEncoding(1254),
        2 => Encoding.GetEncoding(28599),
        3 => Encoding.GetEncoding(1252),
        _ => new UTF8Encoding(false),
    };

    private async Task RunAsync()
    {
        var path = _path.Text.Trim();
        if (!File.Exists(path)) { Ui.Info(this, "Choose an existing .sql file."); return; }
        _start.Enabled = false;
        _close.Text = "Cancel";
        _log.Clear();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var db = _database.Text.Trim();
        var encoding = SelectedEncoding;
        bool stopOnError = _stopOnError.Checked;
        var sw = Stopwatch.StartNew();
        int executed = 0, failed = 0;
        bool cancelled = false;
        var progress = new Progress<(long Pos, long Len, int Done, int Failed)>(p =>
        {
            _progress.Value = p.Len == 0 ? 0 : (int)Math.Min(1000, p.Pos * 1000 / p.Len);
            _state.Text = $"{p.Done:N0} statement(s) · {p.Failed:N0} error(s) · {CellFormat.FormatBytes(p.Pos)} of {CellFormat.FormatBytes(p.Len)} · {CellFormat.FormatDuration(sw.Elapsed)}";
        });
        var errors = new Progress<string>(line => _log.AppendText(line + Environment.NewLine));

        try
        {
            await using var conn = await _session.OpenDedicatedAsync(db.Length == 0 ? null : db, ct);
            await Task.Run(() =>
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                using var reader = new StreamReader(fs, encoding, detectEncodingFromByteOrderMarks: true, 1 << 16);
                long len = fs.Length;
                var lastReport = Stopwatch.StartNew();
                foreach (var stmt in SqlSplitter.Split(reader))
                {
                    if (ct.IsCancellationRequested) { cancelled = true; break; }
                    try
                    {
                        _session.EnsureAllowed(stmt.Text);
                        using var cmd = new MySqlCommand(stmt.Text, conn) { CommandTimeout = 0 };
                        using var reg = ct.Register(static c => { try { ((MySqlCommand)c!).Cancel(); } catch { } }, cmd);
                        cmd.ExecuteNonQuery();
                        executed++;
                    }
                    catch (Exception ex) when (ct.IsCancellationRequested)
                    {
                        _ = ex;
                        cancelled = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        var head = stmt.Text.Length > 120 ? stmt.Text[..120] + "…" : stmt.Text;
                        ((IProgress<string>)errors).Report($"line {stmt.Line}: {Ui.Describe(ex)}\r\n    {head.Replace('\n', ' ').Replace("\r", "")}");
                        if (stopOnError) break;
                    }
                    if (lastReport.ElapsedMilliseconds > 100)
                    {
                        ((IProgress<(long, long, int, int)>)progress).Report((fs.Position, len, executed, failed));
                        lastReport.Restart();
                    }
                }
                ((IProgress<(long, long, int, int)>)progress).Report((cancelled || (failed > 0 && stopOnError) ? fs.Position : len, len, executed, failed));
            }, CancellationToken.None);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.AppendText(Ui.Describe(ex) + Environment.NewLine);
            failed++;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _start.Enabled = true;
            _close.Text = "Close";
        }
        var summary = $"{executed:N0} statement(s) ok, {failed:N0} error(s){(cancelled ? ", cancelled" : "")} in {CellFormat.FormatDuration(sw.Elapsed)}";
        _state.Text = summary;
        SqlHistory.Append(_session.Account.Id, db, $"-- Run SQL file: {path}", summary);
    }
}
