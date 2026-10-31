namespace MyFront.UI.Dialogs;

/// <summary>Base for small auto-sized dialogs laid out with a TableLayoutPanel.</summary>
internal class DialogForm : Form
{
    protected readonly TableLayoutPanel Grid;
    protected readonly FlowLayoutPanel Buttons;

    protected DialogForm(string title)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.None;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(LogicalToDeviceUnits(10));

        var outer = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        Grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Dock = DockStyle.Fill };
        Grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Buttons = new FlowLayoutPanel
        {
            AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill,
            Margin = new Padding(0, LogicalToDeviceUnits(10), 0, 0),
        };
        outer.Controls.Add(Grid);
        outer.Controls.Add(Buttons);
        Controls.Add(outer);
    }

    protected int W(int logical) => LogicalToDeviceUnits(logical);

    protected override void OnLoad(EventArgs e)
    {
        ThemeManager.Apply(this);
        base.OnLoad(e);
    }

    protected T Row<T>(string label, T control, Control? extra = null) where T : Control
    {
        int r = Grid.RowCount++;
        Grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        if (label.Length > 0)
            Grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, W(6), W(8), 0) }, 0, r);
        control.Margin = new Padding(0, W(3), 0, W(3));
        Grid.Controls.Add(control, 1, r);
        if (extra is not null)
        {
            extra.Margin = new Padding(W(4), W(2), 0, W(2));
            Grid.Controls.Add(extra, 2, r);
        }
        return control;
    }

    protected Button AddButton(string text, DialogResult result = DialogResult.None, EventHandler? click = null)
    {
        var b = new Button { Text = text, DialogResult = result, AutoSize = true, MinimumSize = new Size(W(84), W(26)) };
        if (click is not null) b.Click += click;
        Buttons.Controls.Add(b);
        return b;
    }
}

internal sealed class PasswordPrompt : DialogForm
{
    private readonly TextBox _password;

    public string Password => _password.Text;

    public PasswordPrompt(string target) : this("Password for " + target, "Password", "Password") { }

    /// <param name="message">The full question, e.g. "Passphrase for id_ed25519".</param>
    public PasswordPrompt(string message, string field, string title) : base(title)
    {
        Row("", new Label { Text = message, AutoSize = true });
        _password = Row(field, new TextBox { UseSystemPasswordChar = true, Width = W(240) });
        CancelButton = AddButton("Cancel", DialogResult.Cancel);
        AcceptButton = AddButton("OK", DialogResult.OK);
    }
}
