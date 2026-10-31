using System.Globalization;
using MyFront.Core.Quick;

namespace MyFront.UI.Dialogs;

/// <summary>Asks for {{placeholder}} values before a query runs. Values are bound as parameters, never pasted into the SQL.</summary>
internal sealed class ParameterDialog : DialogForm
{
    /// <summary>Last value per placeholder name, for this session.</summary>
    private static readonly Dictionary<string, string> LastValues = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<(Placeholder P, Control Input)> _inputs = new();
    private readonly Label _error;

    public Dictionary<string, object?> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ParameterDialog(string title, string sql, IReadOnlyList<Placeholder> placeholders) : base("Parameters — " + title)
    {
        var preview = sql.Replace("\r", "").Trim();
        if (preview.Length > 400) preview = preview[..400] + "…";
        Row("", new Label { Text = preview, AutoSize = true, MaximumSize = new Size(W(460), 0), Font = Ui.Mono(9), ForeColor = SystemColors.GrayText });
        foreach (var p in placeholders)
        {
            LastValues.TryGetValue(p.Name, out var last);
            Control input;
            if (p.Type is ParamType.Date or ParamType.DateTime)
            {
                var picker = new DateTimePicker
                {
                    Format = DateTimePickerFormat.Custom,
                    CustomFormat = p.Type == ParamType.Date ? "dd.MM.yyyy" : "dd.MM.yyyy HH:mm",
                    Width = W(180),
                };
                if (last is not null && DateTime.TryParse(last, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) picker.Value = d;
                input = picker;
            }
            else
            {
                input = new TextBox { Width = W(300), Text = last ?? "" };
            }
            var hint = p.Type switch
            {
                ParamType.Int => "whole number",
                ParamType.Number => "number, e.g. 1.234,56",
                ParamType.Date => "date",
                ParamType.DateTime => "date and time",
                _ => "text",
            };
            Row($"{{{{{p.Name}}}}}", input, new Label { Text = hint, AutoSize = true, ForeColor = SystemColors.GrayText });
            _inputs.Add((p, input));
        }
        _error = Row("", new Label { AutoSize = true, ForeColor = Theme.Light.Error, MaximumSize = new Size(W(460), 0) });
        CancelButton = AddButton("Cancel", DialogResult.Cancel);
        AcceptButton = AddButton("Run", DialogResult.None, (_, _) => Accept());
    }

    private void Accept()
    {
        Values.Clear();
        foreach (var (p, input) in _inputs)
        {
            try
            {
                object? value;
                string remember;
                if (input is DateTimePicker picker)
                {
                    value = p.Type == ParamType.Date ? picker.Value.Date : picker.Value;
                    remember = picker.Value.ToString("o", CultureInfo.InvariantCulture);
                }
                else
                {
                    value = ParameterParser.ConvertInput(input.Text, p.Type);
                    remember = input.Text;
                }
                Values[p.Name] = value;
                LastValues[p.Name] = remember;
            }
            catch (ParameterException ex)
            {
                _error.ForeColor = ThemeManager.Current.Error;
                _error.Text = $"{{{{{p.Name}}}}}: {ex.Message}";
                input.Focus();
                return;
            }
        }
        DialogResult = DialogResult.OK;
    }

    /// <summary>Self-test hook: supplies values instead of the dialog (null = Cancel).</summary>
    internal static Func<IReadOnlyList<Placeholder>, Dictionary<string, object?>?>? Override;

    /// <summary>Values for every placeholder in the SQL; an empty dictionary when there are none; null if the user cancelled.</summary>
    public static Dictionary<string, object?>? Ask(IWin32Window? owner, string title, string sql)
    {
        var placeholders = ParameterParser.Find(sql);
        if (placeholders.Count == 0) return new Dictionary<string, object?>();
        if (Override is not null) return Override(placeholders);
        using var dlg = new ParameterDialog(title, sql, placeholders);
        return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.Values : null;
    }
}

/// <summary>Small "working…" window with Cancel for background jobs (straight-to-Excel quick queries).</summary>
internal sealed class ProgressForm : DialogForm
{
    private readonly Label _text;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 200 };
    private readonly CancellationTokenSource _cts = new();

    public CancellationToken Token => _cts.Token;

    public ProgressForm(string title, Func<string> status) : base(title)
    {
        Row("", new ProgressBar { Style = ProgressBarStyle.Marquee, Width = W(340), Height = W(16) });
        _text = Row("", new Label { AutoSize = true, Text = "Starting…", MinimumSize = new Size(W(340), 0) });
        AddButton("Cancel", DialogResult.None, (_, _) => { _cts.Cancel(); _text.Text = "Cancelling…"; });
        _timer.Tick += (_, _) => _text.Text = status();
        _timer.Start();
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing && !_done) { e.Cancel = true; _cts.Cancel(); } };
    }

    private bool _done;

    public void Finish()
    {
        _done = true;
        _timer.Stop();
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _cts.Dispose(); }
        base.Dispose(disposing);
    }
}
