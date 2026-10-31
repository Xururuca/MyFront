using System.Globalization;
using MyFront.Core.Accounts;
using MyFront.Core.Db;
using MyFront.Core.Quick;

namespace MyFront.UI.Dialogs;

/// <summary>Accounts: pick, add, edit, export / import, delete, then open (connect) one.</summary>
internal sealed class AccountsForm : Form
{
    private readonly ListView _list;
    private readonly Button _open, _edit, _export, _delete, _cancel;
    private readonly Label _status;
    private List<Account> _accounts;
    private CancellationTokenSource? _connectCts;
    private readonly Func<string, bool>? _isOpen;

    public ServerSession? Session { get; private set; }
    /// <summary>Set instead of <see cref="Session"/> when the chosen account is already connected (no second connection).</summary>
    public string? OpenAccountId { get; private set; }
    /// <summary>Import… added quick query buttons (the main window reloads them).</summary>
    public bool QuickQueriesChanged { get; private set; }

    /// <param name="isOpen">Tells whether an account id is already connected in the main window.</param>
    public AccountsForm(Func<string, bool>? isOpen = null)
    {
        _isOpen = isOpen;
        Text = "Open connection";
        Icon = Ui.AppIcon;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        Size = new Size(LogicalToDeviceUnits(680), LogicalToDeviceUnits(380));
        MinimumSize = new Size(LogicalToDeviceUnits(500), LogicalToDeviceUnits(260));

        _list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false,
            SmallImageList = Icons.TreeImages(LogicalToDeviceUnits(16)),
        };
        _list.Columns.Add("Name", LogicalToDeviceUnits(170));
        _list.Columns.Add("Host", LogicalToDeviceUnits(150));
        _list.Columns.Add("User", LogicalToDeviceUnits(90));
        _list.Columns.Add("TLS", LogicalToDeviceUnits(80));
        _list.Columns.Add("Last login", LogicalToDeviceUnits(120));
        _list.DoubleClick += (_, _) => Ui.Fire(this, OpenAsync);
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) DeleteSelected(); };

        var side = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(LogicalToDeviceUnits(8), 0, 0, 0) };
        var tips = new ToolTip();
        Button B(string text, EventHandler click, string? tip = null)
        {
            var b = new Button { Text = text, Width = LogicalToDeviceUnits(110), Height = LogicalToDeviceUnits(28) };
            b.Click += click;
            if (tip is not null) tips.SetToolTip(b, tip);
            side.Controls.Add(b);
            return b;
        }
        _open = B("Open", (_, _) => Ui.Fire(this, OpenAsync));
        B("New…", (_, _) => NewAccount());
        _edit = B("Edit…", (_, _) => EditSelected());
        _export = B("Export…", (_, _) => ExportSelected(),
            "Save this connection with its SQL editor texts and quick query buttons to a .json file (without the password)");
        B("Import…", (_, _) => ImportFile(), "Add a connection from a .json file saved with Export…");
        _delete = B("Delete", (_, _) => DeleteSelected());
        _cancel = B("Close", (_, _) => { if (_connectCts is not null) _connectCts.Cancel(); else DialogResult = DialogResult.Cancel; });
        AcceptButton = _open;
        CancelButton = _cancel;

        _status = new Label { Dock = DockStyle.Bottom, Height = LogicalToDeviceUnits(22), TextAlign = ContentAlignment.MiddleLeft, ForeColor = SystemColors.GrayText };
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(LogicalToDeviceUnits(8)) };
        body.Controls.Add(_list);
        body.Controls.Add(side);
        body.Controls.Add(_status);
        Controls.Add(body);

        _accounts = LoadAccounts();
        Fill();
    }

    protected override void OnLoad(EventArgs e)
    {
        ThemeManager.Apply(this);
        base.OnLoad(e);
    }

    private List<Account> LoadAccounts()
    {
        try { return AccountStore.Load(); }
        catch (Exception ex)
        {
            Ui.Error(this, ex);
            return new List<Account>();
        }
    }

    private void Fill(string? selectId = null)
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var a in _accounts.OrderByDescending(a => a.LastLogin ?? DateTime.MinValue).ThenBy(a => a.Name))
        {
            var item = new ListViewItem(new[]
            {
                a.Name, $"{a.Host}:{a.Port}", a.User, a.Tls.ToString(),
                a.LastLogin?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "",
            }) { Tag = a, ImageKey = "server" };
            _list.Items.Add(item);
            if (a.Id == selectId) item.Selected = true;
        }
        if (_list.SelectedItems.Count == 0 && _list.Items.Count > 0) _list.Items[0].Selected = true;
        _list.EndUpdate();
        UpdateButtons();
        _status.Text = _accounts.Count == 0 ? "No connections yet — click New…" : "";
    }

    private Account? Selected => _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as Account : null;

    private void UpdateButtons()
    {
        bool busy = _connectCts is not null;
        _open.Enabled = _edit.Enabled = _export.Enabled = _delete.Enabled = Selected is not null && !busy;
        _cancel.Text = busy ? "Cancel" : "Close";
    }

    private void Save()
    {
        try { AccountStore.Save(_accounts); }
        catch (Exception ex) { Ui.Error(this, ex); }
    }

    private void NewAccount()
    {
        using var dlg = new AccountEditForm(new Account { Name = "" }, _accounts.Select(a => a.Name).ToList(), isNew: true);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _accounts.Add(dlg.Result);
        Save();
        Fill(dlg.Result.Id);
    }

    private void EditSelected()
    {
        var a = Selected;
        if (a is null) return;
        using var dlg = new AccountEditForm(a, _accounts.Where(x => x.Id != a.Id).Select(x => x.Name).ToList(), isNew: false);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _accounts[_accounts.IndexOf(a)] = dlg.Result;
        Save();
        Fill(dlg.Result.Id);
    }

    /// <summary>Saves the selected connection with its editor texts and quick query buttons (never its password) to a .json file.</summary>
    internal void ExportSelected(string? path = null)
    {
        var a = Selected;
        if (a is null) return;
        if (path is null)
        {
            using var dlg = new SaveFileDialog
            {
                Title = "Export connection", Filter = "MyFront connection (*.json)|*.json", DefaultExt = "json",
                FileName = $"MyFront - {string.Concat(a.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c))}.json",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            path = dlg.FileName;
        }
        try
        {
            var file = ConnectionFile.Export(path, a, QuickQueryStore.Load());
            SetStatus($"Exported \"{a.Name}\" with {Count(file.Editors.Count, "SQL editor")} and {Count(file.QuickQueries.Count, "quick query button")} to {Path.GetFileName(path)} — without the password");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Ui.Error(this, ex);
        }
    }

    /// <summary>Adds the connection of an exported .json file as a new one, then opens Edit… for its password (not in the file).</summary>
    internal void ImportFile(string? path = null, bool askPassword = true)
    {
        if (path is null)
        {
            using var dlg = new OpenFileDialog { Title = "Import connection", Filter = "MyFront connection (*.json)|*.json|All files (*.*)|*.*" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            path = dlg.FileName;
        }
        ConnectionFile file;
        Account added;
        try
        {
            file = ConnectionFile.Read(path);
            var quick = QuickQueryStore.Load();
            added = file.AddTo(_accounts, quick);
            if (file.QuickQueries.Count > 0)
            {
                QuickQueryStore.Save(quick);
                QuickQueriesChanged = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Ui.Error(this, ex);
            return;
        }
        Save();
        Fill(added.Id);
        SetStatus($"Imported \"{added.Name}\" with {Count(file.Editors.Count, "SQL editor")} and {Count(file.QuickQueries.Count, "quick query button")} — enter its password (it isn't in the file)");
        if (askPassword) EditSelected();
    }

    private void SetStatus(string text)
    {
        _status.ForeColor = ThemeManager.Current.GrayText;
        _status.Text = text;
    }

    private static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";

    private void DeleteSelected()
    {
        var a = Selected;
        if (a is null || !Ui.Confirm(this, $"Delete connection \"{a.Name}\"?", "Delete", warning: true)) return;
        _accounts.Remove(a);
        Save();
        Fill();
    }

    private async Task OpenAsync()
    {
        var a = Selected;
        if (a is null || _connectCts is not null) return;
        if (_isOpen?.Invoke(a.Id) == true)
        {
            OpenAccountId = a.Id; // the main window switches to its tab
            DialogResult = DialogResult.OK;
            return;
        }
        string? password;
        try { password = a.GetSavedPassword(); }
        catch (Exception ex)
        {
            Ui.Error(this, ex, "Saved password can't be decrypted (different Windows user?)");
            password = null;
        }
        if (password is null)
        {
            using var prompt = new PasswordPrompt($"{a.User}@{a.Host}");
            if (prompt.ShowDialog(this) != DialogResult.OK) return;
            password = prompt.Password;
        }

        _connectCts = new CancellationTokenSource();
        UpdateButtons();
        _status.ForeColor = ThemeManager.Current.GrayText;
        _status.Text = a.UsesSsh ? $"Connecting to {a.Host}:{a.Port} via SSH {a.Ssh!.Host}…" : $"Connecting to {a.Host}:{a.Port}…";
        UseWaitCursor = true;
        var prompts = new SshPromptsUi(this);
        try
        {
            Session = await ServerSession.ConnectAsync(a, password, _connectCts.Token, prompts);
            a.LastLogin = DateTime.Now;
            Save();
            DialogResult = DialogResult.OK;
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Cancelled";
        }
        catch (Exception ex)
        {
            _status.ForeColor = ThemeManager.Current.Error;
            _status.Text = Ui.Describe(ex).Replace('\n', ' ');
        }
        finally
        {
            if (prompts.AcceptedHostKey is { } key)
            {
                a.Ssh!.HostKey = key;
                Save();
            }
            UseWaitCursor = false;
            _connectCts?.Dispose();
            _connectCts = null;
            if (!IsDisposed) UpdateButtons();
        }
    }
}
