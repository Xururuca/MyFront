using System.Text.Json;
using MyFront.Core.Accounts;

namespace MyFront.SelfTest;

/// <summary>Reads the git-ignored testdb.local.json from the repository root.</summary>
internal sealed class TestDb
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 3306;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string Database { get; set; } = "";
    public string? CaFile { get; set; }
    /// <summary>Optional real-world Excel files for the import tests (kept out of the repository).</summary>
    public string[] ImportSamples { get; set; } = Array.Empty<string>();
    /// <summary>Optional SSH server in front of the database, for the live tunnel tests.</summary>
    public SshTestConfig? Ssh { get; set; }

    public static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MyFront.slnx"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
    }

    public static TestDb Load()
    {
        var path = Path.Combine(RepoRoot, "testdb.local.json");
        if (!File.Exists(path))
            throw new FileNotFoundException("Create testdb.local.json (see testdb.local.example.json).", path);
        return JsonSerializer.Deserialize<TestDb>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    public Account ToAccount(TlsMode tls = TlsMode.Required, bool readOnly = false) => new()
    {
        Name = "selftest",
        Host = Host,
        Port = Port,
        User = User,
        DefaultDatabase = Database,
        Tls = tls,
        CaFile = CaFile,
        ReadOnly = readOnly,
        SavePassword = false,
    };

    /// <summary>The test account reached through the SSH server (secrets kept in memory only).</summary>
    public Account ToSshAccount(TlsMode tls = TlsMode.VerifyFull, string? trustedHostKey = null)
    {
        var a = ToAccount(tls);
        var c = Ssh ?? throw new InvalidOperationException("no \"ssh\" section in testdb.local.json");
        a.Name = "selftest via SSH";
        a.Ssh = new SshSettings
        {
            Enabled = true, Host = c.Host, Port = c.Port, User = c.User,
            Auth = string.IsNullOrEmpty(c.KeyFile) ? SshAuth.Password : SshAuth.PrivateKey, KeyFile = c.KeyFile,
            PasswordOverride = c.Password, PassphraseOverride = c.Passphrase,
            DbHost = c.DbHost, DbPort = c.DbPort, HostKey = trustedHostKey,
        };
        return a;
    }
}

internal sealed class SshTestConfig
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string User { get; set; } = "";
    public string? Password { get; set; }
    public string? KeyFile { get; set; }
    public string? Passphrase { get; set; }
    public string DbHost { get; set; } = "127.0.0.1";
    public int? DbPort { get; set; }
    /// <summary>Expected host key ("ssh-ed25519 SHA256:…"), checked when given.</summary>
    public string? HostKey { get; set; }
}
