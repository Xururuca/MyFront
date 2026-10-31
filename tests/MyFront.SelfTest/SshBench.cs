using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MyFront.Core.Accounts;
using MyFront.Core.Db;
using MySqlConnector;
using Renci.SshNet;

namespace MyFront.SelfTest;

/// <summary>Mode "sshbench": the same 200k-row read direct, through MyFront's tunnel per cipher, and through OpenSSH.</summary>
internal static class SshBench
{
    private const string Sql = "SELECT * FROM prices";

    public static async Task RunAsync(TestDb db)
    {
        var cfg = db.Ssh ?? throw new InvalidOperationException("no \"ssh\" in testdb.local.json");
        var baseCs = ConnectionFactory.Build(db.ToAccount(TlsMode.VerifyCA), db.Password, db.Database, ConnectionPurpose.Dedicated);

        var bytes = await ReadAsync(baseCs.ConnectionString, countBytes: true);
        Console.WriteLine($"result size: {bytes.Bytes / 1048576.0:N1} MB on the wire (Bytes_sent)");
        for (int i = 0; i < 2; i++) Console.WriteLine($"direct:                    {(await ReadAsync(baseCs.ConnectionString)).Ms,6} ms");

        foreach (var cipher in new string?[] { null, "aes128-gcm@openssh.com", "aes256-gcm@openssh.com", "aes128-ctr", "chacha20-poly1305@openssh.com" })
        {
            using var client = Client(cfg, cipher);
            client.Connect();
            var ci = client.ConnectionInfo;
            var port = FreePort();
            using var fwd = new ForwardedPortLocal("127.0.0.1", (uint)port, cfg.DbHost, (uint)(cfg.DbPort ?? db.Port));
            client.AddForwardedPort(fwd);
            fwd.Start();
            var cs = new MySqlConnectionStringBuilder(baseCs.ConnectionString) { Server = "127.0.0.1", Port = (uint)port }.ConnectionString;
            var label = $"SSH.NET {ci.CurrentServerEncryption}/{ci.CurrentServerHmacAlgorithm ?? "-"}";
            for (int i = 0; i < 2; i++) Console.WriteLine($"{label,-60} {(await ReadAsync(cs)).Ms,6} ms");
            fwd.Stop();
            client.Disconnect();
        }

        foreach (var (label, keepAlive, async) in new[] { ("SSH.NET + KeepAliveInterval 30 s", true, false), ("SSH.NET + ConnectAsync", false, true) })
        {
            using var client = Client(cfg, null);
            if (keepAlive) client.KeepAliveInterval = TimeSpan.FromSeconds(30);
            if (async) await client.ConnectAsync(default); else client.Connect();
            var port = FreePort();
            using var fwd = new ForwardedPortLocal("127.0.0.1", (uint)port, cfg.DbHost, (uint)(cfg.DbPort ?? db.Port));
            client.AddForwardedPort(fwd);
            fwd.Start();
            var cs = new MySqlConnectionStringBuilder(baseCs.ConnectionString) { Server = "127.0.0.1", Port = (uint)port }.ConnectionString;
            for (int i = 0; i < 2; i++) Console.WriteLine($"{label,-60} {(await ReadAsync(cs)).Ms,6} ms");
        }

        using (var s = await ServerSession.ConnectAsync(db.ToSshAccount(trustedHostKey: cfg.HostKey), db.Password))
        {
            var cs = new MySqlConnectionStringBuilder(baseCs.ConnectionString) { Server = "127.0.0.1", Port = (uint)s.Tunnel!.LocalPort }.ConnectionString;
            for (int i = 0; i < 2; i++) Console.WriteLine($"{"MyFront SshTunnel, dedicated connection",-60} {(await ReadAsync(cs)).Ms,6} ms");
            for (int i = 0; i < 2; i++)
            {
                var sw = Stopwatch.StartNew();
                await using var conn = await s.OpenAsync(db.Database);
                await using var cmd = new MySqlCommand(Sql, conn);
                await using var r = await cmd.ExecuteReaderAsync();
                var values = new object[r.FieldCount];
                while (await r.ReadAsync()) r.GetValues(values);
                Console.WriteLine($"{"MyFront SshTunnel, session.OpenAsync (pooled)",-60} {sw.ElapsedMilliseconds,6} ms");
            }
        }

        var ssh = Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe");
        if (File.Exists(ssh) && !string.IsNullOrEmpty(cfg.KeyFile))
        {
            var port = FreePort();
            var psi = new ProcessStartInfo(ssh) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var a in new[] { "-N", "-o", "BatchMode=yes", "-o", "ExitOnForwardFailure=yes", "-i", cfg.KeyFile, "-p", cfg.Port.ToString(),
                         "-L", $"{port}:{cfg.DbHost}:{cfg.DbPort ?? db.Port}", $"{cfg.User}@{cfg.Host}" })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            try
            {
                var sw = Stopwatch.StartNew();
                while (!CanConnect(port) && sw.ElapsedMilliseconds < 15000 && !p.HasExited) await Task.Delay(100);
                if (p.HasExited) { Console.WriteLine("OpenSSH tunnel failed: " + p.StandardError.ReadToEnd()); return; }
                var cs = new MySqlConnectionStringBuilder(baseCs.ConnectionString) { Server = "127.0.0.1", Port = (uint)port }.ConnectionString;
                for (int i = 0; i < 2; i++) Console.WriteLine($"{"OpenSSH ssh.exe -L (your command)",-60} {(await ReadAsync(cs)).Ms,6} ms");
            }
            finally { p.Kill(); }
        }
    }

    private static SshClient Client(SshTestConfig cfg, string? cipher)
    {
        AuthenticationMethod auth = string.IsNullOrEmpty(cfg.KeyFile)
            ? new PasswordAuthenticationMethod(cfg.User, cfg.Password ?? "")
            : new PrivateKeyAuthenticationMethod(cfg.User, new PrivateKeyFile(cfg.KeyFile, cfg.Passphrase));
        var ci = new ConnectionInfo(cfg.Host, cfg.Port, cfg.User, auth);
        if (cipher is not null)
            foreach (var k in ci.Encryptions.Keys.Where(k => k != cipher).ToList()) ci.Encryptions.Remove(k);
        var c = new SshClient(ci);
        c.HostKeyReceived += (_, e) => e.CanTrust = cfg.HostKey is null || cfg.HostKey == SshTunnel.FormatKey(e.HostKeyName, e.FingerPrintSHA256);
        return c;
    }

    private static async Task<(long Ms, long Bytes)> ReadAsync(string cs, bool countBytes = false)
    {
        await using var conn = new MySqlConnection(cs);
        await conn.OpenAsync();
        long before = countBytes ? await BytesSent(conn) : 0;
        var sw = Stopwatch.StartNew();
        await using (var cmd = new MySqlCommand(Sql, conn))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            var values = new object[r.FieldCount];
            while (await r.ReadAsync()) r.GetValues(values);
        }
        var ms = sw.ElapsedMilliseconds;
        return (ms, countBytes ? await BytesSent(conn) - before : 0);
    }

    private static async Task<long> BytesSent(MySqlConnection conn)
    {
        await using var cmd = new MySqlCommand("SHOW SESSION STATUS LIKE 'Bytes_sent'", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return long.Parse(r.GetString(1));
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    private static bool CanConnect(int port)
    {
        try { using var c = new TcpClient(); c.Connect(IPAddress.Loopback, port); return true; }
        catch (SocketException) { return false; }
    }
}
