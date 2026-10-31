using System.Diagnostics;
using MyFront.Core.Accounts;
using MyFront.Core.Db;

namespace MyFront.UI.Dialogs;

/// <summary>Add / edit one connection (with an optional SSH tunnel), with "Test connection".</summary>
internal sealed class AccountEditForm : DialogForm
{
    private readonly Account _account;
    private readonly IReadOnlyCollection<string> _otherNames;
    private const string AllDatabases = "(all databases)";
    private readonly TextBox _name, _host, _user, _password, _caFile;
    private readonly NumericUpDown _port, _timeout;
    private readonly CheckBox _savePassword, _readOnly;
    private readonly ComboBox _tls, _database;
    /// <summary>The Database list holds the server's databases (filled by a successful connect).</summary>
    private bool _databasesListed;
    private readonly Label _testResult;
    private readonly Button _test;
    // SSH
    private readonly CheckBox _sshOn, _sshSave;
    private readonly TextBox _sshHost, _sshUser, _sshPassword, _sshKey, _sshPassphrase, _sshDbHost, _sshDbPort;
    private readonly NumericUpDown _sshPort;
    private readonly RadioButton _sshByPassword, _sshByKey;
    private readonly Label _sshHostKey;
    private readonly TableLayoutPanel _sshFields;
    private CancellationTokenSource? _testCts;
    /// <summary>"host:port" the stored host key belongs to; a key never carries over to another SSH server.</summary>
    private string? _hostKeyFor;

    public Account Result => _account;

    public AccountEditForm(Account account, IReadOnlyCollection<string> otherNames, bool isNew) : base(isNew ? "New connection" : "Edit connection")
    {
        _account = account.Clone();
        _otherNames = otherNames;
        var ssh = _account.Ssh ?? new SshSettings();
        _hostKeyFor = ssh.HostKey is null ? null : $"{ssh.Host}:{ssh.Port}";

        // ---- tab 1: connection --------------------------------------------------------------------
        var conn = Table();
        _name = Add(conn, "Name", new TextBox { Width = W(260), Text = account.Name });
        _host = Add(conn, "Host", new TextBox { Width = W(260), Text = account.Host });
        _port = Add(conn, "Port", new NumericUpDown { Minimum = 1, Maximum = 65535, Value = account.Port, Width = W(90) });
        _user = Add(conn, "User", new TextBox { Width = W(260), Text = account.User });
        _password = Add(conn, "Password", new TextBox
        {
            Width = W(260), UseSystemPasswordChar = true,
            PlaceholderText = account.HasSavedPassword ? "(saved — leave empty to keep)" : "",
        });
        _savePassword = Add(conn, "", new CheckBox { Text = "Save password (encrypted with Windows DPAPI)", AutoSize = true, Checked = account.SavePassword });
        // typed or picked; the list fills on a successful Test connection, or connects when opened
        _database = Add(conn, "Database", new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = W(260), Text = account.DefaultDatabase ?? "" });
        _database.HandleCreated += (_, _) => SendMessage(_database.Handle, CB_SETCUEBANNER, IntPtr.Zero, AllDatabases);
        _database.DropDown += (_, _) => { if (!_databasesListed && _test is { Enabled: true }) Ui.Fire(this, () => TestAsync(openList: true)); };
        _tls = Add(conn, "TLS", new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = W(260) });
        _tls.Items.AddRange(new object[] { "Preferred (encrypt if the server supports it)", "Required (encrypt, don't verify)", "Verify CA (check the certificate)", "Verify Full (certificate + host name)" });
        _tls.SelectedIndex = (int)account.Tls;
        var browse = new Button { Text = "…", Width = W(30), Height = W(24) };
        _caFile = Add(conn, "CA file", new TextBox { Width = W(260), Text = account.CaFile ?? "", PlaceholderText = "(PEM file, needed for Verify CA / Full)" }, browse);
        browse.Click += (_, _) => PickFile(_caFile, "Certificates (*.pem;*.crt;*.cer)|*.pem;*.crt;*.cer|All files (*.*)|*.*");
        _readOnly = Add(conn, "", new CheckBox { Text = "Read-only (block statements that write)", AutoSize = true, Checked = account.ReadOnly });
        _timeout = Add(conn, "Timeout (s)", new NumericUpDown { Minimum = 1, Maximum = 300, Value = Math.Clamp(account.ConnectTimeoutSeconds, 1, 300), Width = W(90) });

        // ---- tab 2: SSH tunnel --------------------------------------------------------------------
        var sshTab = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Top };
        _sshOn = new CheckBox { Text = "Connect through an SSH tunnel", AutoSize = true, Checked = ssh.Enabled, Margin = new Padding(0, W(3), 0, W(6)) };
        sshTab.Controls.Add(_sshOn);
        _sshFields = Table();
        _sshFields.Margin = Padding.Empty;
        sshTab.Controls.Add(_sshFields);
        _sshHost = Add(_sshFields, "SSH host", new TextBox { Width = W(260), Text = ssh.Host, PlaceholderText = "ssh.example.com" });
        _sshPort = Add(_sshFields, "SSH port", new NumericUpDown { Minimum = 1, Maximum = 65535, Value = Math.Clamp(ssh.Port, 1, 65535), Width = W(90) });
        _sshUser = Add(_sshFields, "SSH user", new TextBox { Width = W(260), Text = ssh.User });
        var how = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        _sshByPassword = new RadioButton { Text = "Password", AutoSize = true, Checked = ssh.Auth == SshAuth.Password };
        _sshByKey = new RadioButton { Text = "Private key", AutoSize = true, Checked = ssh.Auth == SshAuth.PrivateKey, Margin = new Padding(W(16), W(3), 0, 0) };
        how.Controls.AddRange(new Control[] { _sshByPassword, _sshByKey });
        Add(_sshFields, "Login with", how);
        _sshPassword = Add(_sshFields, "SSH password", new TextBox
        {
            Width = W(260), UseSystemPasswordChar = true,
            PlaceholderText = ssh.PasswordEncrypted is not null ? "(saved — leave empty to keep)" : "",
        });
        var keyBrowse = new Button { Text = "…", Width = W(30), Height = W(24) };
        _sshKey = Add(_sshFields, "Key file", new TextBox { Width = W(260), Text = ssh.KeyFile ?? "", PlaceholderText = "id_ed25519, id_rsa or .ppk" }, keyBrowse);
        keyBrowse.Click += (_, _) => PickFile(_sshKey, "Private keys (id_*;*.pem;*.key;*.ppk)|id_*;*.pem;*.key;*.ppk|All files (*.*)|*.*",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"));
        _sshPassphrase = Add(_sshFields, "Passphrase", new TextBox
        {
            Width = W(260), UseSystemPasswordChar = true,
            PlaceholderText = ssh.PassphraseEncrypted is not null ? "(saved — leave empty to keep)" : "(only if the key has one)",
        });
        _sshSave = Add(_sshFields, "", new CheckBox { Text = "Save SSH password / passphrase (DPAPI)", AutoSize = true, Checked = ssh.SaveSecret });
        _sshDbHost = Add(_sshFields, "DB host from SSH", new TextBox { Width = W(260), Text = ssh.DbHost, PlaceholderText = "127.0.0.1 = MariaDB on the SSH server" });
        _sshDbPort = Add(_sshFields, "DB port from SSH", new TextBox { Width = W(90), Text = ssh.DbPort?.ToString() ?? "", PlaceholderText = "(= Port)" });
        var forget = new Button { Text = "Forget", AutoSize = true };
        _sshHostKey = Add(_sshFields, "Host key", new Label { AutoSize = true, MaximumSize = new Size(W(260), 0), ForeColor = SystemColors.GrayText }, forget);
        forget.Click += (_, _) =>
        {
            if (_account.Ssh is not null) _account.Ssh.HostKey = null;
            ShowHostKey();
        };
        var note = Add(_sshFields, "", new Label
        {
            AutoSize = true, MaximumSize = new Size(W(290), 0), ForeColor = SystemColors.GrayText,
            Text = "The tunnel listens on 127.0.0.1 only. With a tunnel, \"Verify Full\" is checked as \"Verify CA\" " +
                   "(the database host name becomes 127.0.0.1); the SSH host key vouches for the server.",
        });
        _sshFields.SetColumnSpan(note, 2);
        _sshOn.CheckedChanged += (_, _) => UpdateSshEnabled();
        _sshByPassword.CheckedChanged += (_, _) => UpdateSshEnabled();
        UpdateSshEnabled();
        ShowHostKey();

        var tabs = new ThemedTabControl();
        var p1 = new TabPage("Connection") { UseVisualStyleBackColor = true, Padding = new Padding(W(8)) };
        var p2 = new TabPage("SSH tunnel") { UseVisualStyleBackColor = true, Padding = new Padding(W(8)) };
        p1.Controls.Add(conn);
        p2.Controls.Add(sshTab);
        tabs.TabPages.Add(p1);
        tabs.TabPages.Add(p2);
        var s1 = conn.GetPreferredSize(Size.Empty);
        _sshHostKey.Text = "ssh-ed25519 SHA256:" + new string('W', 43) + "\n(for server.example.com:65535)"; // size for the longest
        var s2 = sshTab.GetPreferredSize(Size.Empty);
        ShowHostKey();
        tabs.Size = new Size(Math.Max(s1.Width, s2.Width) + W(30), Math.Max(s1.Height, s2.Height) + W(50));
        Row("", tabs).Margin = Padding.Empty;
        _testResult = Row("", new Label { AutoSize = true, MaximumSize = new Size(tabs.Width, 0), ForeColor = SystemColors.GrayText });

        var cancel = AddButton("Cancel", DialogResult.Cancel);
        var ok = AddButton("OK", DialogResult.None, (_, _) => OnOk());
        _test = AddButton("Test connection", DialogResult.None, (_, _) => Ui.Fire(this, () => TestAsync()));
        AcceptButton = ok;
        CancelButton = cancel;
        FormClosed += (_, _) => _testCts?.Cancel();
    }

    private TableLayoutPanel Table()
    {
        var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Dock = DockStyle.Top };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return t;
    }

    private T Add<T>(TableLayoutPanel t, string label, T control, Control? extra = null) where T : Control
    {
        int r = t.RowCount++;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        if (label.Length > 0)
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, W(6), W(8), 0) }, 0, r);
        control.Margin = new Padding(0, W(3), 0, W(3));
        t.Controls.Add(control, 1, r);
        if (extra is not null)
        {
            extra.Margin = new Padding(W(4), W(2), 0, W(2));
            t.Controls.Add(extra, 2, r);
        }
        return control;
    }

    private void PickFile(TextBox target, string filter, string? folder = null)
    {
        using var dlg = new OpenFileDialog { Filter = filter };
        if (folder is not null && Directory.Exists(folder)) dlg.InitialDirectory = folder;
        if (dlg.ShowDialog(this) == DialogResult.OK) target.Text = dlg.FileName;
    }

    private void UpdateSshEnabled()
    {
        _sshFields.Enabled = _sshOn.Checked;
        _sshPassword.Enabled = _sshByPassword.Checked;
        _sshKey.Enabled = _sshPassphrase.Enabled = _sshByKey.Checked;
    }

    private void ShowHostKey()
    {
        var key = _account.Ssh?.HostKey;
        _sshHostKey.Text = key is null ? "not trusted yet — you'll be asked to confirm it on the first connect" : $"{key}\n(for {_hostKeyFor})";
    }

    private bool Apply(Account a, out string? error)
    {
        error = null;
        var name = _name.Text.Trim();
        if (name.Length == 0) name = $"{_user.Text.Trim()}@{_host.Text.Trim()}";
        if (_otherNames.Contains(name, StringComparer.OrdinalIgnoreCase)) { error = $"A connection named \"{name}\" already exists."; return false; }
        if (_host.Text.Trim().Length == 0) { error = "Host is required."; return false; }
        if (_user.Text.Trim().Length == 0) { error = "User is required."; return false; }
        var tls = (TlsMode)_tls.SelectedIndex;
        var ca = _caFile.Text.Trim();
        if (tls is TlsMode.VerifyCA or TlsMode.VerifyFull && (ca.Length == 0 || !File.Exists(ca)))
        {
            error = "Verify CA / Verify Full needs an existing CA certificate file.";
            return false;
        }
        int? dbPort = null;
        if (_sshDbPort.Text.Trim().Length > 0)
        {
            if (!int.TryParse(_sshDbPort.Text.Trim(), out var p) || p is < 1 or > 65535) { error = "DB port from SSH must be 1–65535 (or empty)."; return false; }
            dbPort = p;
        }
        if (_sshOn.Checked)
        {
            if (_sshHost.Text.Trim().Length == 0 || _sshUser.Text.Trim().Length == 0) { error = "SSH host and SSH user are required."; return false; }
            if (_sshByKey.Checked && !File.Exists(_sshKey.Text.Trim())) { error = "The SSH key file doesn't exist."; return false; }
        }
        a.Name = name;
        a.Host = _host.Text.Trim();
        a.Port = (int)_port.Value;
        a.User = _user.Text.Trim();
        var database = _database.Text.Trim();
        a.DefaultDatabase = database.Length == 0 || database == AllDatabases ? null : database;
        a.Tls = tls;
        a.CaFile = ca.Length == 0 ? null : ca;
        a.ReadOnly = _readOnly.Checked;
        a.ConnectTimeoutSeconds = (int)_timeout.Value;

        var s = a.Ssh ?? new SshSettings();
        s.Enabled = _sshOn.Checked;
        s.Host = _sshHost.Text.Trim();
        s.Port = (int)_sshPort.Value;
        s.User = _sshUser.Text.Trim();
        s.Auth = _sshByKey.Checked ? SshAuth.PrivateKey : SshAuth.Password;
        s.KeyFile = _sshKey.Text.Trim().Length == 0 ? null : _sshKey.Text.Trim();
        s.DbHost = _sshDbHost.Text.Trim().Length == 0 ? "127.0.0.1" : _sshDbHost.Text.Trim();
        s.DbPort = dbPort;
        s.HostKey = _hostKeyFor == $"{s.Host}:{s.Port}" ? _account.Ssh?.HostKey : null;
        // typed secrets are used now even when they are not saved
        s.PasswordOverride = _sshPassword.Text.Length > 0 ? _sshPassword.Text : s.PasswordOverride;
        s.PassphraseOverride = _sshPassphrase.Text.Length > 0 ? _sshPassphrase.Text : s.PassphraseOverride;
        a.Ssh = s.Enabled || a.Ssh is not null ? s : null;
        return true;
    }

    private string? CurrentPassword() =>
        _password.Text.Length > 0 ? _password.Text : _account.GetSavedPassword();

    private void OnOk()
    {
        if (!Apply(_account, out var error))
        {
            Ui.Info(this, error!);
            return;
        }
        var password = CurrentPassword();
        _account.SavePassword = _savePassword.Checked;
        _account.SetPassword(_savePassword.Checked ? password : null);
        if (_account.Ssh is { } s)
        {
            var sshPassword = s.Password;
            var passphrase = s.Passphrase;
            s.SaveSecret = _sshSave.Checked;
            s.SetPassword(s.Auth == SshAuth.Password ? sshPassword : null);
            s.SetPassphrase(s.Auth == SshAuth.PrivateKey ? passphrase : null);
            // unsaved secrets are asked for again on connect
            s.PasswordOverride = s.PassphraseOverride = null;
        }
        _password.Clear();
        _sshPassword.Clear();
        _sshPassphrase.Clear();
        DialogResult = DialogResult.OK;
    }

    /// <summary>Connects with what is typed; on success the Database list gets the server's databases (and opens with openList).</summary>
    private async Task TestAsync(bool openList = false)
    {
        var probe = _account.Clone();
        if (!Apply(probe, out var error))
        {
            ShowTest(false, error!);
            return;
        }
        // the databases are listed from the server, so the database typed doesn't have to exist for that
        if (openList) probe.DefaultDatabase = null;
        _test.Enabled = false;
        ShowTest(null, probe.UsesSsh ? $"Opening the SSH tunnel to {probe.Ssh!.Host}…" : "Connecting…");
        _testCts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        var prompts = new SshPromptsUi(this);
        try
        {
            using var s = await ServerSession.ConnectAsync(probe, CurrentPassword(), _testCts.Token, prompts);
            var ms = sw.ElapsedMilliseconds;
            var databases = await s.Schema.GetDatabasesAsync(_testCts.Token);
            if (IsDisposed) return;
            ListDatabases(databases, openList);
            ShowTest(true, $"Connected in {ms} ms{(s.Tunnel is null ? "" : "\n" + s.Tunnel.Description)}\n{s.ServerVersion}\n{s.TlsVersion ?? "no TLS"} {s.TlsCipher}" +
                           $"\n{databases.Count} database(s) in the Database list — pick one to show only it");
        }
        catch (Exception ex) when (!IsDisposed)
        {
            ShowTest(false, Ui.Describe(ex));
        }
        finally
        {
            if (prompts.AcceptedHostKey is { } key && !IsDisposed)
            {
                (_account.Ssh ??= probe.Ssh!.Clone()).HostKey = key; // remembered when you click OK
                _hostKeyFor = $"{probe.Ssh!.Host}:{probe.Ssh.Port}";
                ShowHostKey();
            }
            if (!IsDisposed) _test.Enabled = true;
        }
    }

    private void ListDatabases(List<string> databases, bool open)
    {
        var text = _database.Text;
        _database.BeginUpdate();
        _database.Items.Clear();
        _database.Items.Add(AllDatabases);
        _database.Items.AddRange(databases.Cast<object>().ToArray());
        _database.EndUpdate();
        _database.Text = text;
        _databasesListed = true;
        if (open && _database.ContainsFocus)
        {
            // re-open so the list is sized for its new items (it opened empty while connecting)
            _database.DroppedDown = false;
            _database.DroppedDown = true;
        }
    }

    private const int CB_SETCUEBANNER = 0x1703;

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    private void ShowTest(bool? ok, string text)
    {
        if (IsDisposed) return;
        _testResult.ForeColor = ok switch { true => ThemeManager.Current.Ok, false => ThemeManager.Current.Error, _ => ThemeManager.Current.GrayText };
        _testResult.Text = text;
    }
}

/// <summary>SSH questions shown on the UI thread (the tunnel opens on a background thread).</summary>
internal sealed class SshPromptsUi : ISshPrompts
{
    private readonly Control _owner;

    /// <summary>Self-test hook: decides host-key trust instead of the dialog.</summary>
    internal static Func<string, string, bool>? TrustOverride;

    /// <summary>The key the user said yes to — saved by the caller even if the login after it failed.</summary>
    public string? AcceptedHostKey { get; private set; }

    public SshPromptsUi(Control owner) => _owner = owner;

    public bool TrustNewHostKey(string server, string keyType, string fingerprint)
    {
        var yes = TrustOverride?.Invoke(server, fingerprint) ?? Ask(server, keyType, fingerprint);
        if (yes) AcceptedHostKey = $"{keyType} {fingerprint}";
        return yes;
    }

    private bool Ask(string server, string keyType, string fingerprint) =>
        OnUi(() => MessageBox.Show(_owner.FindForm(),
            $"First connection to the SSH server {server}.\n\nKey type:     {keyType}\nFingerprint:  {fingerprint}\n\n" +
            "If you can, compare it with the server's own key (on the server: ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub).\n\n" +
            "Trust this server and remember its key?", "SSH host key", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes);

    public string? AskSecret(string prompt) => OnUi(() =>
    {
        var passphrase = prompt.StartsWith("Passphrase", StringComparison.OrdinalIgnoreCase);
        using var p = new PasswordPrompt(prompt, passphrase ? "Passphrase" : "Password", "SSH");
        return p.ShowDialog(_owner.FindForm()) == DialogResult.OK ? p.Password : null;
    });

    private T OnUi<T>(Func<T> f) => _owner.InvokeRequired ? (T)_owner.Invoke(f) : f();
}
