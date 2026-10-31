using System.Net;
using System.Net.Sockets;
using MyFront.Core.Accounts;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace MyFront.Core.Db;

/// <summary>What the tunnel needs from the user interface (called on a background thread).</summary>
public interface ISshPrompts
{
    /// <summary>First connection to this SSH server: true = trust and remember the key.</summary>
    /// <param name="fingerprint">"SHA256:…"; the stored form is "{keyType} {fingerprint}".</param>
    bool TrustNewHostKey(string server, string keyType, string fingerprint);
    /// <summary>A password / passphrase that isn't saved; null = cancel.</summary>
    string? AskSecret(string prompt);
}

public enum HostKeyDecision { Trusted, New, Changed }

public sealed class SshTunnelException : Exception
{
    public SshTunnelException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// SSH client + local port forward 127.0.0.1:{free port} → {DbHost}:{DbPort} on the SSH server.
/// Host keys are trusted on first use and must not change afterwards.
/// </summary>
public sealed class SshTunnel : IDisposable
{
    private readonly SshSettings _settings;
    private readonly SshClient _client;
    private readonly string _dbHost;
    private readonly uint _dbPort;
    private ForwardedPortLocal _forward;
    private readonly object _gate = new();

    public int LocalPort { get; }
    /// <summary>Set when a new host key was confirmed during this connect (the caller saves it to the account).</summary>
    public string? NewHostKey { get; private set; }
    public string Description { get; }

    private SshTunnel(SshSettings s, SshClient client, ForwardedPortLocal forward, string dbHost, uint dbPort, int localPort, string? newKey, string description)
    {
        _settings = s;
        _client = client;
        _forward = forward;
        _dbHost = dbHost;
        _dbPort = dbPort;
        LocalPort = localPort;
        NewHostKey = newKey;
        Description = description;
    }

    public static HostKeyDecision Check(string? stored, string presented) =>
        string.IsNullOrEmpty(stored) ? HostKeyDecision.New
        : string.Equals(stored.Trim(), presented, StringComparison.Ordinal) ? HostKeyDecision.Trusted
        : HostKeyDecision.Changed;

    public static string FormatKey(string keyType, string sha256) => $"{keyType} SHA256:{sha256}";

    public static SshTunnel Open(SshSettings s, int defaultDbPort, ISshPrompts? prompts, CancellationToken ct, int timeoutSeconds = 15)
    {
        if (string.IsNullOrWhiteSpace(s.Host) || string.IsNullOrWhiteSpace(s.User))
            throw new SshTunnelException("SSH host and user are required.");
        var server = $"{s.User}@{s.Host}:{s.Port}";
        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 3, 120));
        string? trusted = s.HostKey, newKey = null;
        Exception? keyError = null;

        void CheckHostKey(object? _, HostKeyEventArgs e)
        {
            var presented = FormatKey(e.HostKeyName, e.FingerPrintSHA256);
            switch (Check(trusted, presented))
            {
                case HostKeyDecision.Trusted:
                    e.CanTrust = true;
                    break;
                case HostKeyDecision.New:
                    e.CanTrust = prompts?.TrustNewHostKey($"{s.Host}:{s.Port}", e.HostKeyName, "SHA256:" + e.FingerPrintSHA256) == true;
                    if (e.CanTrust) { newKey = presented; trusted = presented; }
                    else keyError = new SshTunnelException($"The SSH host key of {s.Host} was not confirmed.");
                    break;
                default:
                    e.CanTrust = false;
                    keyError = new SshTunnelException(
                        $"WARNING: the SSH host key of {s.Host}:{s.Port} has CHANGED.\n\nTrusted: {trusted}\nNow:     {presented}\n\n" +
                        "Someone could be intercepting the connection (man-in-the-middle). If the server was really reinstalled, " +
                        "forget the stored key in the connection's SSH settings and connect again.");
                    break;
            }
        }

        // false = the server rejected the login (only expected for the "none" probe)
        bool Connect(SshClient c, bool probe)
        {
            c.HostKeyReceived += CheckHostKey;
            try
            {
                // synchronous on purpose (we're on a pool thread): with SSH.NET 2026.0.0 a session opened by
                // ConnectAsync forwards data 5–6x slower (200k rows: 7.7 s vs 1.3 s). Connect() can't be
                // interrupted; ServerSession.ConnectAsync stops waiting on Cancel and this check drops the result.
                c.Connect();
                ct.ThrowIfCancellationRequested();
                return true;
            }
            catch (OperationCanceledException)
            {
                c.Dispose();
                throw;
            }
            catch (Exception ex) when (keyError is not null)
            {
                c.Dispose();
                throw new SshTunnelException(keyError.Message, ex);
            }
            catch (SshAuthenticationException) when (probe)
            {
                c.Dispose();
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                c.Dispose();
                throw Friendly(ex, server);
            }
        }

        var methods = new List<AuthenticationMethod>();
        if (s.Auth == SshAuth.PrivateKey)
        {
            if (string.IsNullOrWhiteSpace(s.KeyFile) || !File.Exists(s.KeyFile))
                throw new SshTunnelException($"SSH key file not found: {s.KeyFile}");
            methods.Add(new PrivateKeyAuthenticationMethod(s.User, LoadKey(s, prompts)));
        }
        else
        {
            var password = s.Password;
            if (password is null)
            {
                // like OpenSSH: check the host key before a password is typed for (and sent to) that server
                using var probe = new SshClient(new ConnectionInfo(s.Host, s.Port, s.User, new NoneAuthenticationMethod(s.User)) { Timeout = timeout });
                if (Connect(probe, probe: true)) probe.Disconnect();
                password = prompts?.AskSecret($"SSH password for {server}") ?? throw new OperationCanceledException("No SSH password.");
            }
            methods.Add(new PasswordAuthenticationMethod(s.User, password));
            // many servers only offer keyboard-interactive for passwords (PAM)
            var ki = new KeyboardInteractiveAuthenticationMethod(s.User);
            ki.AuthenticationPrompt += (_, e) => { foreach (var p in e.Prompts) p.Response = password; };
            methods.Add(ki);
        }

        var client = new SshClient(new ConnectionInfo(s.Host, s.Port, s.User, methods.ToArray()) { Timeout = timeout })
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        };
        Connect(client, probe: false);

        int localPort = FreeLocalPort();
        var dbPort = (uint)(s.DbPort ?? defaultDbPort);
        var dbHost = string.IsNullOrWhiteSpace(s.DbHost) ? "127.0.0.1" : s.DbHost.Trim();
        var forward = new ForwardedPortLocal("127.0.0.1", (uint)localPort, dbHost, dbPort);
        try
        {
            client.AddForwardedPort(forward);
            forward.Start();
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw new SshTunnelException($"Could not open the tunnel 127.0.0.1:{localPort} → {dbHost}:{dbPort}: {ex.Message}", ex);
        }
        return new SshTunnel(s, client, forward, dbHost, dbPort, localPort, newKey, $"SSH {server} → {dbHost}:{dbPort}");
    }

    private static PrivateKeyFile LoadKey(SshSettings s, ISshPrompts? prompts)
    {
        try
        {
            return new PrivateKeyFile(s.KeyFile!, s.Passphrase);
        }
        catch (SshPassPhraseNullOrEmptyException)
        {
            var pass = prompts?.AskSecret($"Passphrase for {Path.GetFileName(s.KeyFile)}") ?? throw new OperationCanceledException("No passphrase.");
            try { return new PrivateKeyFile(s.KeyFile!, pass); }
            catch (Exception ex) { throw new SshTunnelException($"Could not open the key {Path.GetFileName(s.KeyFile)} (wrong passphrase?): {ex.Message}", ex); }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not SshTunnelException)
        {
            var hint = s.Passphrase is null ? "" : " (wrong passphrase?)";
            throw new SshTunnelException($"Could not read the SSH key {Path.GetFileName(s.KeyFile)}{hint}: {ex.Message}", ex);
        }
    }

    private static SshTunnelException Friendly(Exception ex, string server) => ex switch
    {
        SshAuthenticationException => new SshTunnelException($"SSH login failed for {server}: {ex.Message}", ex),
        SshOperationTimeoutException => new SshTunnelException($"SSH server {server} did not answer in time.", ex),
        SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted } =>
            new SshTunnelException($"{server} closed the connection — is it an SSH server (right port)?", ex),
        SocketException se => new SshTunnelException($"SSH server {server} not reachable: {se.Message}", ex),
        SshConnectionException => new SshTunnelException($"SSH connection to {server} failed: {ex.Message}", ex),
        _ => new SshTunnelException($"SSH {server}: {ex.Message}", ex),
    };

    private static int FreeLocalPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    /// <summary>Reconnects a dropped SSH session (e.g. after sleep) before the next database connection.</summary>
    public void EnsureAlive()
    {
        lock (_gate)
        {
            if (_client.IsConnected && _forward.IsStarted) return;
            try
            {
                if (!_client.IsConnected) _client.Connect();
                // SSH.NET detaches forwards from a dropped session: add a new one on the same local port
                // (the connection strings point at it)
                try { if (_forward.IsStarted) _forward.Stop(); } catch { }
                try { _client.RemoveForwardedPort(_forward); } catch { }
                _forward.Dispose();
                _forward = new ForwardedPortLocal("127.0.0.1", (uint)LocalPort, _dbHost, _dbPort);
                _client.AddForwardedPort(_forward);
                _forward.Start();
            }
            catch (Exception ex)
            {
                throw Friendly(ex, _settings.ToString());
            }
        }
    }

    public bool IsConnected => _client.IsConnected;

    public void Dispose()
    {
        try { if (_forward.IsStarted) _forward.Stop(); } catch { }
        try { _client.Disconnect(); } catch { }
        _forward.Dispose();
        _client.Dispose();
    }
}
