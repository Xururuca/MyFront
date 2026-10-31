using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyFront.Core;
using MyFront.Core.Accounts;
using MyFront.Core.Db;
using MySqlConnector;

namespace MyFront.SelfTest;

/// <summary>
/// SSH tunnel tests that need no SSH server: settings, secrets, host-key decisions, key files
/// and the error messages for servers that refuse or don't speak SSH.
/// </summary>
internal static class SshTests
{
    private sealed class FakePrompts : ISshPrompts
    {
        public readonly List<string> Asked = new();
        public string? Answer;
        public bool Trust;
        public string? Presented;
        public bool TrustNewHostKey(string server, string keyType, string fingerprint)
        {
            Asked.Add("trust " + server);
            Presented = $"{keyType} {fingerprint}";
            return Trust;
        }
        public string? AskSecret(string prompt) { Asked.Add(prompt); return Answer; }
    }

    public static async Task RunAsync(TestRunner t)
    {
        await t.Check("ssh: host key trusted on first use, refused when it changes", () =>
        {
            var key = SshTunnel.FormatKey("ssh-ed25519", "abc+def/123");
            Assert.Equal("ssh-ed25519 SHA256:abc+def/123", key, "format");
            Assert.Equal(HostKeyDecision.New, SshTunnel.Check(null, key), "nothing stored");
            Assert.Equal(HostKeyDecision.New, SshTunnel.Check("", key), "empty stored");
            Assert.Equal(HostKeyDecision.Trusted, SshTunnel.Check(key + " ", key), "same key");
            Assert.Equal(HostKeyDecision.Changed, SshTunnel.Check(key, SshTunnel.FormatKey("ssh-ed25519", "abc+def/124")), "other fingerprint");
            Assert.Equal(HostKeyDecision.Changed, SshTunnel.Check(key, SshTunnel.FormatKey("ssh-rsa", "abc+def/123")), "other key type");
            Assert.Equal(HostKeyDecision.Changed, SshTunnel.Check(key, SshTunnel.FormatKey("ssh-ed25519", "ABC+def/123")), "case matters (base64)");
        });

        await t.Check("ssh: settings saved with the account, secrets DPAPI only, typed secrets never written", () =>
        {
            var a = new Account { Name = "ssh-test", Host = "db.internal", Port = 3307 };
            a.Ssh = new SshSettings { Enabled = true, Host = "jump.example.com", User = "deploy", DbHost = "10.0.0.5", DbPort = 3306, HostKey = "ssh-ed25519 SHA256:x" };
            a.Ssh.SetPassword("Ssh-Şifre-1");
            a.Ssh.SetPassphrase("pass phrase");
            a.Ssh.PasswordOverride = "typed-only-secret";
            var json = JsonSerializer.Serialize(new List<Account> { a }, CoreJson.Default.ListAccount);
            Assert.True(!json.Contains("Ssh-Şifre-1") && !json.Contains("Ssh-\\u015E") && !json.Contains("pass phrase"), "no plaintext secret");
            Assert.True(!json.Contains("typed-only-secret") && !json.Contains("Override"), "override not serialized");
            var back = JsonSerializer.Deserialize(json, CoreJson.Default.ListAccount)!.Single();
            Assert.True(back.UsesSsh, "enabled");
            Assert.Equal("jump.example.com", back.Ssh!.Host, "host");
            Assert.Equal(22, back.Ssh.Port, "default port");
            Assert.Equal("10.0.0.5", back.Ssh.DbHost, "db host");
            Assert.Equal(3306, back.Ssh.DbPort, "db port");
            Assert.Equal("Ssh-Şifre-1", back.Ssh.Password, "password decrypts");
            Assert.Equal("pass phrase", back.Ssh.Passphrase, "passphrase decrypts");
            Assert.True(back.Ssh.PasswordEncrypted!.StartsWith("dpapi1:"), "dpapi prefix");

            a.Ssh.SaveSecret = false;
            a.Ssh.SetPassword("not saved");
            Assert.True(a.Ssh.PasswordEncrypted is null, "SaveSecret off clears the stored password");
            Assert.Equal("typed-only-secret", a.Ssh.Password, "override still usable now");

            var plain = JsonSerializer.Serialize(new List<Account> { new() { Name = "direct" } }, CoreJson.Default.ListAccount);
            var direct = JsonSerializer.Deserialize(plain, CoreJson.Default.ListAccount)!.Single();
            Assert.True(!direct.UsesSsh && direct.Ssh is null, "accounts without SSH stay direct");
        });

        await t.Check("ssh: Clone copies the SSH settings (editing a copy leaves the saved account alone)", () =>
        {
            var a = new Account { Ssh = new SshSettings { Enabled = true, Host = "a", HostKey = "k1" } };
            var c = a.Clone();
            c.Ssh!.Host = "b";
            c.Ssh.HostKey = null;
            Assert.Equal("a", a.Ssh.Host, "original host");
            Assert.Equal("k1", a.Ssh.HostKey, "original host key");
        });

        await t.Check("ssh: Verify Full is checked as Verify CA through the tunnel (host name is 127.0.0.1)", () =>
        {
            var ca = Path.Combine(Path.GetTempPath(), "MyFront.SelfTest", "dummy-ca.pem");
            Directory.CreateDirectory(Path.GetDirectoryName(ca)!);
            File.WriteAllText(ca, "-----BEGIN CERTIFICATE-----\n-----END CERTIFICATE-----\n");
            var a = new Account { Host = "db.example.com", Port = 3306, Tls = TlsMode.VerifyFull, CaFile = ca };
            var direct = ConnectionFactory.Build(a, "x", null, ConnectionPurpose.Pooled);
            var tunneled = ConnectionFactory.Build(a, "x", null, ConnectionPurpose.Pooled, "127.0.0.1", 50123);
            Assert.Equal(MySqlSslMode.VerifyFull, direct.SslMode, "direct");
            Assert.Equal(MySqlSslMode.VerifyCA, tunneled.SslMode, "tunneled");
            Assert.Equal("127.0.0.1", tunneled.Server, "tunnel host");
            Assert.Equal(50123u, tunneled.Port, "tunnel port");
            Assert.Equal(ca, tunneled.SslCa, "CA still checked");
        });

        var closedPort = ClosedPort();

        await t.Check("ssh: refused port → clear error, no database connection attempted", async () =>
        {
            var a = SshAccount(closedPort);
            a.Ssh!.SetPassword("pw");
            var ex = await Assert.Throws(() => ServerSession.ConnectAsync(a, "dbpw"), "connect");
            Assert.True(ex is SshTunnelException, ex.GetType().Name + ": " + ex.Message);
            Assert.True(ex.Message.Contains("not reachable") || ex.Message.Contains("failed"), ex.Message);
            Assert.True(ex.Message.Contains($"tester@127.0.0.1:{closedPort}"), "names the SSH server: " + ex.Message);
            return ex.Message;
        });

        await t.Check("ssh: a server that doesn't speak SSH → clear error", async () =>
        {
            using var junk = new TcpListener(IPAddress.Loopback, 0);
            junk.Start();
            var port = ((IPEndPoint)junk.LocalEndpoint).Port;
            var serve = Task.Run(async () =>
            {
                using var c = await junk.AcceptTcpClientAsync();
                var bytes = Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\n\r\n");
                await c.GetStream().WriteAsync(bytes);
                await Task.Delay(200);
            });
            var a = SshAccount(port);
            a.Ssh!.SetPassword("pw");
            var ex = await Assert.Throws(() => ServerSession.ConnectAsync(a, "dbpw"), "connect");
            await serve;
            Assert.True(ex is SshTunnelException, ex.GetType().Name + ": " + ex.Message);
            return ex.Message;
        });

        await t.Check("ssh: Cancel stops a connect to a server that never answers", async () =>
        {
            using var silent = new TcpListener(IPAddress.Loopback, 0);
            silent.Start(); // accepts (backlog) but never sends an SSH banner
            var a = SshAccount(((IPEndPoint)silent.LocalEndpoint).Port);
            a.ConnectTimeoutSeconds = 60;
            a.Ssh!.PasswordOverride = "pw";
            using var cts = new CancellationTokenSource(400);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ex = await Assert.Throws(() => ServerSession.ConnectAsync(a, "dbpw", cts.Token), "cancelled");
            Assert.True(ex is OperationCanceledException, ex.GetType().Name + ": " + ex.Message);
            Assert.True(sw.ElapsedMilliseconds < 3000, $"stopped after {sw.ElapsedMilliseconds} ms");
            return $"stopped after {sw.ElapsedMilliseconds} ms";
        });

        await t.Check("ssh: unsaved SSH password isn't asked for a server that can't be reached", async () =>
        {
            var a = SshAccount(closedPort);
            var prompts = new FakePrompts { Answer = "x" };
            var ex = await Assert.Throws(() => ServerSession.ConnectAsync(a, "dbpw", default, prompts), "unreachable");
            Assert.Equal(0, prompts.Asked.Count, "nothing asked: " + string.Join(", ", prompts.Asked));
            Assert.True(ex is SshTunnelException && ex.Message.Contains("not reachable"), ex.Message);
        });

        var dir = Path.Combine(Path.GetTempPath(), "MyFront.SelfTest", "ssh");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        using var rsa = RSA.Create(2048);
        var plainKey = Path.Combine(dir, "id_rsa_plain");
        File.WriteAllText(plainKey, rsa.ExportRSAPrivateKeyPem());
        var junkKey = Path.Combine(dir, "id_junk");
        File.WriteAllText(junkKey, "this is not a key");

        await t.Check("ssh: private key file — plain key loads, missing / junk file gives a clear error", async () =>
        {
            var ex = await Assert.Throws(() => ServerSession.ConnectAsync(SshAccount(closedPort, plainKey), "dbpw"), "plain key, closed port");
            Assert.True(ex.Message.Contains("not reachable"), "got past the key: " + ex.Message);

            ex = await Assert.Throws(() => ServerSession.ConnectAsync(SshAccount(closedPort, Path.Combine(dir, "nope")), "dbpw"), "missing");
            Assert.True(ex is SshTunnelException && ex.Message.Contains("not found"), ex.Message);

            ex = await Assert.Throws(() => ServerSession.ConnectAsync(SshAccount(closedPort, junkKey), "dbpw"), "junk");
            Assert.True(ex is SshTunnelException && ex.Message.Contains("id_junk"), ex.GetType().Name + ": " + ex.Message);
            return ex.Message;
        });

        // encrypted keys in the formats people actually have: ssh-keygen's OpenSSH format (Windows ships ssh-keygen)
        // and the older OpenSSL PEM format ("Proc-Type: 4,ENCRYPTED")
        const string pass = "Anahtar-Şifre 1";
        var keys = new List<(string Label, string Path)>();
        var keygen = Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh-keygen.exe");
        foreach (var type in new[] { "ed25519", "rsa", "ecdsa" })
        {
            if (!File.Exists(keygen)) break;
            var file = Path.Combine(dir, "id_" + type);
            var psi = new System.Diagnostics.ProcessStartInfo(keygen) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-q", "-t", type, "-N", pass, "-C", "selftest", "-f", file }) psi.ArgumentList.Add(arg);
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit(20000);
            if (p.ExitCode == 0 && File.Exists(file)) keys.Add(($"OpenSSH {type}", file));
            else Console.WriteLine($"      ssh-keygen {type} failed: {p.StandardError.ReadToEnd().Trim()}");
        }
        var legacy = Path.Combine(dir, "id_rsa_legacy");
        File.WriteAllText(legacy, LegacyEncryptedPem(rsa, pass));
        keys.Add(("PEM AES-128-CBC", legacy));

        foreach (var (label, file) in keys)
        {
            var name = Path.GetFileName(file);
            await t.Check($"ssh: encrypted key ({label}) — saved passphrase used, else asked; wrong one reported", async () =>
            {
                var a = SshAccount(closedPort, file);
                a.Ssh!.SetPassphrase(pass);
                var prompts = new FakePrompts();
                var ex = await Assert.Throws(() => ServerSession.ConnectAsync(a, "dbpw", default, prompts), "saved passphrase");
                Assert.Equal(0, prompts.Asked.Count, "not asked: " + string.Join(", ", prompts.Asked));
                Assert.True(ex.Message.Contains("not reachable"), "key opened: " + ex.Message);

                prompts = new FakePrompts { Answer = pass };
                ex = await Assert.Throws(() => ServerSession.ConnectAsync(SshAccount(closedPort, file), "dbpw", default, prompts), "asked passphrase");
                Assert.True(prompts.Asked.Count == 1 && prompts.Asked[0] == "Passphrase for " + name, string.Join(", ", prompts.Asked));
                Assert.True(ex.Message.Contains("not reachable"), "key opened: " + ex.Message);

                a = SshAccount(closedPort, file);
                a.Ssh!.SetPassphrase("wrong");
                ex = await Assert.Throws(() => ServerSession.ConnectAsync(a, "dbpw"), "wrong passphrase");
                Assert.True(ex is SshTunnelException && ex.Message.Contains(name), ex.GetType().Name + ": " + ex.Message);
                return ex.Message;
            });
        }
    }

    /// <summary>OpenSSL's traditional encrypted PEM (what "ssh-keygen -m PEM" and old tools write).</summary>
    private static string LegacyEncryptedPem(RSA rsa, string pass)
    {
        var iv = RandomNumberGenerator.GetBytes(16);
        // EVP_BytesToKey(MD5, salt = first 8 IV bytes, 1 round): 16 bytes = one MD5 block
        var key = MD5.HashData(Encoding.UTF8.GetBytes(pass).Concat(iv[..8]).ToArray());
        using var aes = Aes.Create();
        aes.Key = key;
        var enc = aes.EncryptCbc(rsa.ExportRSAPrivateKey(), iv);
        return "-----BEGIN RSA PRIVATE KEY-----\nProc-Type: 4,ENCRYPTED\nDEK-Info: AES-128-CBC," + Convert.ToHexString(iv) + "\n\n" +
               Convert.ToBase64String(enc, Base64FormattingOptions.InsertLineBreaks).Replace("\r\n", "\n") + "\n-----END RSA PRIVATE KEY-----\n";
    }

    // ---- live: through the SSH server in testdb.local.json -------------------------------------

    /// <summary>
    /// Runs against the real SSH server. Only one login per run is allowed to fail: repeated failed SSH logins can get
    /// this PC banned (fail2ban), which would block the database too — so no wrong-password tests here.
    /// </summary>
    public static async Task RunLiveAsync(TestRunner t, TestDb db)
    {
        if (db.Ssh is null) { Console.WriteLine("      (no \"ssh\" in testdb.local.json — live SSH tests skipped)"); return; }
        var expectedKey = db.Ssh.HostKey?.StartsWith("ssh-") == true ? db.Ssh.HostKey : null;
        string? hostKey = null;

        // these two stop at the host key, before any login: safe to run whatever the credentials
        await t.Check("ssh live: unknown host key → asked; \"No\" refuses before logging in", async () =>
        {
            var prompts = new FakePrompts { Trust = false };
            var ex = await Assert.Throws(() => ServerSession.ConnectAsync(db.ToSshAccount(), db.Password, default, prompts), "refused");
            Assert.True(ex is SshTunnelException && ex.Message.Contains("not confirmed"), ex.GetType().Name + ": " + ex.Message);
            Assert.Equal(1, prompts.Asked.Count, "asked once");
            Assert.Equal($"trust {db.Ssh.Host}:{db.Ssh.Port}", prompts.Asked[0], "question");
            hostKey = prompts.Presented!;
            if (expectedKey is not null) Assert.Equal(expectedKey, hostKey, "same fingerprint as OpenSSH's ssh -v");
            return hostKey;
        });

        await t.Check("ssh live: changed host key → refused with a man-in-the-middle warning, nothing asked", async () =>
        {
            var prompts = new FakePrompts { Trust = true };
            var fake = "ssh-ed25519 SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
            var ex = await Assert.Throws(() => ServerSession.ConnectAsync(db.ToSshAccount(trustedHostKey: fake), db.Password, default, prompts), "refused");
            Assert.True(ex is SshTunnelException && ex.Message.Contains("CHANGED") && ex.Message.Contains(fake) && ex.Message.Contains(hostKey!),
                ex.GetType().Name + ": " + ex.Message);
            Assert.Equal(0, prompts.Asked.Count, "never asks to accept a changed key");
        });

        await t.Check("ssh live: unsaved password is asked only after the host key is confirmed; Cancel sends nothing", async () =>
        {
            var prompts = new FakePrompts { Trust = true, Answer = null };
            var a = db.ToSshAccount();
            a.Ssh!.Auth = SshAuth.Password; // whatever the config uses: no password saved, Cancel at the prompt
            a.Ssh.KeyFile = null;
            a.Ssh.PasswordOverride = null;
            await Assert.Throws(() => ServerSession.ConnectAsync(a, db.Password, default, prompts), "cancelled");
            Assert.Equal($"trust {db.Ssh.Host}:{db.Ssh.Port} | SSH password for {db.Ssh.User}@{db.Ssh.Host}:{db.Ssh.Port}",
                string.Join(" | ", prompts.Asked), "order");
        });

        if (string.IsNullOrEmpty(db.Ssh.Password) && string.IsNullOrEmpty(db.Ssh.KeyFile))
        {
            Console.WriteLine("      (no SSH password / keyFile in testdb.local.json — login tests skipped)");
            return;
        }

        bool loggedIn = false;
        await t.Check("ssh live: trusted key → no question; the database answers through the tunnel (TLS inside)", async () =>
        {
            var prompts = new FakePrompts();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var s = await ServerSession.ConnectAsync(db.ToSshAccount(trustedHostKey: hostKey), db.Password, default, prompts);
            loggedIn = true;
            var connectMs = sw.ElapsedMilliseconds;
            Assert.Equal(0, prompts.Asked.Count, "nothing asked: " + string.Join(", ", prompts.Asked));
            Assert.True(s.Tunnel!.NewHostKey is null, "no new key");
            await using var conn = await s.OpenAsync(db.Database);
            await using var cmd = new MySqlCommand("SELECT CURRENT_USER(), DATABASE(), @@port", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            await r.ReadAsync();
            var who = $"{r.GetString(0)} db={r.GetString(1)} port={r.GetValue(2)}";
            Assert.Equal(db.Database, r.GetString(1), "database");
            Assert.True(s.Summary.Contains("via SSH"), s.Summary);
            Assert.True(s.TlsVersion is not null, "TLS inside the tunnel (Verify Full checked as Verify CA)");
            return $"{connectMs} ms · {s.Tunnel.Description} · {who} · {s.TlsVersion}";
        });
        if (!loggedIn) { Console.WriteLine("      (SSH or DB login failed — remaining live SSH tests skipped, no retries)"); return; }

        using var session = await ServerSession.ConnectAsync(db.ToSshAccount(trustedHostKey: hostKey), db.Password);

        await t.Check("ssh live: 200k rows through the tunnel vs direct", async () =>
        {
            async Task<(long Ms, int Rows)> Read(ServerSession s)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await using var conn = await s.OpenAsync(db.Database);
                await using var cmd = new MySqlCommand("SELECT * FROM prices", conn);
                await using var r = await cmd.ExecuteReaderAsync();
                int n = 0;
                var values = new object[r.FieldCount];
                while (await r.ReadAsync()) { r.GetValues(values); n++; }
                return (sw.ElapsedMilliseconds, n);
            }
            using var direct = await ServerSession.ConnectAsync(db.ToAccount(TlsMode.VerifyFull), db.Password);
            var d = await Read(direct);
            var viaSsh = await Read(session);
            Assert.Equal(d.Rows, viaSsh.Rows, "same rows");
            return $"{viaSsh.Rows:N0} rows: SSH {viaSsh.Ms} ms, direct {d.Ms} ms";
        });

        await t.Check("ssh live: several connections at once share the one tunnel", async () =>
        {
            var tasks = Enumerable.Range(0, 4).Select(async i =>
            {
                await using var conn = await session.OpenDedicatedAsync(db.Database);
                await using var cmd = new MySqlCommand($"SELECT SLEEP(0.3) + {i}", conn);
                return Convert.ToInt32(await cmd.ExecuteScalarAsync());
            }).ToArray();
            var results = await Task.WhenAll(tasks);
            Assert.Equal("0,1,2,3", string.Join(",", results), "all answered");
        });

        await t.Check("ssh live: a dropped SSH session reconnects on the next query", async () =>
        {
            var client = (Renci.SshNet.SshClient)typeof(SshTunnel)
                .GetField("_client", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(session.Tunnel)!;
            client.Disconnect();
            Assert.True(!session.Tunnel!.IsConnected, "dropped");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await using var conn = await session.OpenAsync(db.Database);
            await using var cmd = new MySqlCommand("SELECT COUNT(*) FROM products", conn);
            var n = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            Assert.True(session.Tunnel.IsConnected && n > 0, "reconnected");
            return $"reconnected + query in {sw.ElapsedMilliseconds} ms";
        });
    }

    private static Account SshAccount(int sshPort, string? keyFile = null) => new()
    {
        Name = "ssh-test", Host = "db.internal", Port = 3306, User = "u", ConnectTimeoutSeconds = 5,
        Ssh = new SshSettings
        {
            Enabled = true, Host = "127.0.0.1", Port = sshPort, User = "tester",
            Auth = keyFile is null ? SshAuth.Password : SshAuth.PrivateKey, KeyFile = keyFile,
        },
    };

    /// <summary>A localhost port nothing listens on (connect is refused at once).</summary>
    private static int ClosedPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
