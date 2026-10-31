using MyFront.Core.Accounts;
using MySqlConnector;

namespace MyFront.Core.Db;

/// <summary>An open connection to one server (one account). Hands out pooled and dedicated connections.</summary>
public sealed class ServerSession : IDisposable
{
    private readonly string _pooled;
    private readonly string _dedicatedNoDb;

    public Account Account { get; }
    public string ServerVersion { get; private set; } = "";
    public bool IsMariaDb { get; private set; }
    public Version Version { get; private set; } = new(0, 0);
    public string? TlsVersion { get; private set; }
    public string? TlsCipher { get; private set; }
    public SchemaCache Schema { get; }
    public bool ReadOnly => Account.ReadOnly;

    public string DisplayName => Account.Name;

    public string Summary
    {
        get
        {
            var tls = TlsVersion is null ? "no TLS" : TlsVersion;
            var ssh = _tunnel is null ? "" : " · via SSH";
            var ro = ReadOnly ? " · READ-ONLY" : "";
            return $"{Account.Name} · {(IsMariaDb ? "MariaDB" : "MySQL")} {Version} · {tls}{ssh}{ro}";
        }
    }

    private readonly SshTunnel? _tunnel;

    /// <summary>The SSH tunnel, when the account uses one.</summary>
    public SshTunnel? Tunnel => _tunnel;

    private ServerSession(Account account, string? password, SshTunnel? tunnel)
    {
        Account = account;
        _tunnel = tunnel;
        string? host = tunnel is null ? null : "127.0.0.1";
        int? port = tunnel?.LocalPort;
        _pooled = ConnectionFactory.Build(account, password, null, ConnectionPurpose.Pooled, host, port).ConnectionString;
        _dedicatedNoDb = ConnectionFactory.Build(account, password, null, ConnectionPurpose.Dedicated, host, port).ConnectionString;
        Schema = new SchemaCache(this);
    }

    /// <param name="ssh">Asks to trust a new SSH host key / for unsaved SSH secrets; null = refuse unknown keys.</param>
    public static async Task<ServerSession> ConnectAsync(Account account, string? password, CancellationToken ct = default, ISshPrompts? ssh = null)
    {
        // Task.Run: TCP connect, TLS handshake and certificate checks partly run synchronously; keep them off the UI thread
        var work = Task.Run(async () =>
        {
            var tunnel = account.UsesSsh ? SshTunnel.Open(account.Ssh!, account.Port, ssh, ct, account.ConnectTimeoutSeconds) : null;
            var s = new ServerSession(account, password, tunnel);
            try
            {
                await using var conn = await s.OpenAsync(null, ct);
                await s.ReadServerInfoAsync(conn, ct);
                return s;
            }
            catch
            {
                s.Dispose();
                throw;
            }
        }, ct);
        try
        {
            // the SSH connect can't be interrupted: on Cancel stop waiting now, let it finish and clean up on its own
            return await work.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && !work.IsCompleted)
        {
            _ = work.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); else _ = t.Exception; }, TaskScheduler.Default);
            throw;
        }
    }

    private async Task ReadServerInfoAsync(MySqlConnection conn, CancellationToken ct)
    {
        await using (var cmd = new MySqlCommand("SELECT VERSION()", conn))
            ServerVersion = Convert.ToString(await cmd.ExecuteScalarAsync(ct)) ?? "";
        IsMariaDb = ServerVersion.Contains("MariaDB", StringComparison.OrdinalIgnoreCase);
        Version = ParseVersion(ServerVersion);

        await using (var cmd = new MySqlCommand("SHOW SESSION STATUS WHERE Variable_name IN ('Ssl_version','Ssl_cipher')", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var value = r.GetString(1);
                if (string.IsNullOrEmpty(value)) continue;
                if (r.GetString(0).Equals("Ssl_version", StringComparison.OrdinalIgnoreCase)) TlsVersion = value;
                else TlsCipher = value;
            }
        }
    }

    internal static Version ParseVersion(string s)
    {
        // "10.11.6-MariaDB-0+deb12u1", "8.0.36", "5.5.5-10.6.12-MariaDB"
        if (s.StartsWith("5.5.5-", StringComparison.Ordinal)) s = s[6..];
        var end = 0;
        while (end < s.Length && (char.IsDigit(s[end]) || s[end] == '.')) end++;
        return System.Version.TryParse(s[..end].TrimEnd('.'), out var v) ? v : new Version(0, 0);
    }

    /// <summary>Opens a pooled connection. Dispose it when done (it goes back to the pool).</summary>
    public Task<MySqlConnection> OpenAsync(string? database, CancellationToken ct = default) =>
        OpenCoreAsync(_pooled, database, ct);

    /// <summary>Opens a non-pooled connection for an editor tab, a file run or an import.</summary>
    /// <param name="compress">zlib protocol compression: big uploads (imports) to remote servers are bandwidth-bound.</param>
    public Task<MySqlConnection> OpenDedicatedAsync(string? database, CancellationToken ct = default, bool compress = false)
    {
        var cs = _dedicatedNoDb;
        if (compress && !IsLocalHost)
            cs = new MySqlConnectionStringBuilder(cs) { UseCompression = true }.ConnectionString;
        return OpenCoreAsync(cs, database, ct);
    }

    private bool IsLocalHost => Account.Host is "localhost" or "127.0.0.1" or "::1" or ".";

    private Task<MySqlConnection> OpenCoreAsync(string connectionString, string? database, CancellationToken ct) =>
        Task.Run(() => OpenOnPoolThreadAsync(connectionString, database, ct), ct);

    private async Task<MySqlConnection> OpenOnPoolThreadAsync(string connectionString, string? database, CancellationToken ct)
    {
        _tunnel?.EnsureAlive();
        var conn = new MySqlConnection(connectionString);
        try
        {
            await conn.OpenAsync(ct);
            if (ReadOnly)
            {
                // server-side guard in addition to the client-side statement check
                await using var ro = new MySqlCommand("SET SESSION TRANSACTION READ ONLY", conn);
                await ro.ExecuteNonQueryAsync(ct);
            }
            if (!string.IsNullOrEmpty(database))
                await conn.ChangeDatabaseAsync(database, ct);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    /// <summary>Throws if the account is read-only and the statement could write.</summary>
    public void EnsureAllowed(string sql)
    {
        if (ReadOnly && !SqlClassifier.IsReadOnlySafe(sql))
            throw new ReadOnlyViolationException(SqlClassifier.FirstKeyword(sql));
    }

    public void Dispose()
    {
        try { MySqlConnection.ClearPool(new MySqlConnection(_pooled)); } catch { /* best effort */ }
        _tunnel?.Dispose();
    }
}

public sealed class ReadOnlyViolationException : Exception
{
    public ReadOnlyViolationException(string keyword)
        : base($"This connection is read-only. \"{(keyword.Length == 0 ? "?" : keyword)}\" statements are blocked.") { }
}
