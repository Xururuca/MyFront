using MyFront.Core.Accounts;
using MyFront.Core.Editors;
using MyFront.Core.Quick;

namespace MyFront.SelfTest;

/// <summary>Offline: a connection exported to .json (no passwords) and imported as a new connection with its editors and buttons.</summary>
internal static class ConnectionFileTests
{
    public static void Offline(TestRunner t)
    {
        t.Check("connection file: export has the settings, editor texts and its own buttons — never a password; import adds it as a new connection", () =>
        {
            var a = new Account
            {
                Name = "move me", Host = "db.example", Port = 3307, User = "u", DefaultDatabase = "shop", Tls = TlsMode.Required, CaFile = @"C:\certs\ca.pem",
                Ssh = new SshSettings { Enabled = true, Host = "ssh.example", User = "root", Auth = SshAuth.PrivateKey, KeyFile = @"C:\keys\id", HostKey = "ssh-ed25519 SHA256:abc" },
            };
            a.SetPassword("db-secret-123");
            a.Ssh.SetPassphrase("key-secret-456");
            a.LastLogin = DateTime.Now;
            EditorStore.WriteText(a.Id, 1, "select 1;");
            EditorStore.WriteText(a.Id, 3, "select 'İş' -- 3");
            EditorStore.WriteText(a.Id, 7, "extra tab");
            var other = new Account { Name = "other" };
            var quick = new List<QuickQuery>
            {
                new() { Name = "Mine", Sql = "SELECT 1", AccountId = a.Id, AccountName = a.Name, Hotkey = 1, Color = "#c00000" },
                new() { Name = "Other's", Sql = "SELECT 2", AccountId = other.Id, AccountName = other.Name },
                new() { Name = "Everywhere", Sql = "SELECT 3" },
            };
            var path = Path.Combine(Path.GetTempPath(), $"myfront-connection-{Guid.NewGuid():N}.json");
            try
            {
                var file = ConnectionFile.Export(path, a, quick);
                var json = File.ReadAllText(path);
                Assert.True(!json.Contains("dpapi") && !json.Contains("PasswordEncrypted") && !json.Contains("PassphraseEncrypted")
                            && !json.Contains("secret") && !json.Contains("LastLogin"), "no password in the file:\n" + json);
                Assert.True(json.Contains("\"Format\": \"MyFront connection\"") && json.Contains("db.example") && json.Contains("SHA256:abc"), "settings in the file");
                Assert.Equal("1,3,7", string.Join(",", file.Editors.Select(e => e.Slot)), "editor tabs with text");
                Assert.Equal("Mine", string.Join(",", file.QuickQueries.Select(q => q.Name)), "only its own buttons");
                Assert.True(a.HasSavedPassword && a.Ssh.PassphraseEncrypted is not null, "the connection here keeps its passwords");

                // on another PC: the name is taken, and a button for every connection already has Ctrl+1
                var accounts = new List<Account> { new() { Name = "Move me" } };
                var there = new List<QuickQuery> { new() { Name = "Theirs", Sql = "x", Hotkey = 1 } };
                var added = ConnectionFile.Read(path).AddTo(accounts, there);
                Assert.True(added.Id != a.Id && added.Name == "move me (2)" && accounts.Contains(added), "a new connection: " + added.Name);
                Assert.True(added is { Host: "db.example", Port: 3307, User: "u", DefaultDatabase: "shop", Tls: TlsMode.Required, CaFile: @"C:\certs\ca.pem" }, "settings");
                Assert.True(added.Ssh is { Enabled: true, Host: "ssh.example", Auth: SshAuth.PrivateKey, KeyFile: @"C:\keys\id", HostKey: "ssh-ed25519 SHA256:abc" }, "ssh");
                Assert.True(!added.HasSavedPassword && added.Ssh!.PassphraseEncrypted is null && added.LastLogin is null, "no password");
                Assert.Equal("select 1;|select 'İş' -- 3|extra tab", string.Join("|", new[] { 1, 3, 7 }.Select(s => EditorStore.ReadText(added.Id, s))), "editor texts");
                Assert.Equal("1,2,3,4,5,7", string.Join(",", EditorStore.Load(added.Id).Tabs.Select(x => x.Slot)), "its tabs when it opens");
                var mine = there.Single(q => q.AccountId == added.Id);
                Assert.True(mine is { Name: "Mine", Sql: "SELECT 1", Color: "#c00000", Hotkey: null } && mine.AccountName == added.Name && mine.Id != quick[0].Id,
                    "its button (Ctrl+1 is taken there)");
                Assert.Equal(2, there.Count, "buttons there");

                // importing the same file again: one more copy, nothing replaced
                var again = ConnectionFile.Read(path).AddTo(accounts, there);
                Assert.True(again.Name == "move me (3)" && again.Id != added.Id && there.Count == 3, "again: " + again.Name);
                return $"{file.Editors.Count} editors, {file.QuickQueries.Count} button, {json.Length} bytes";
            }
            finally
            {
                File.Delete(path);
            }
        });

        t.Check("connection file: export → delete the connection → import shows each button once (the one left behind is taken over)", () =>
        {
            var a = new Account { Name = "SALES", Host = "db.example" };
            var other = new Account { Name = "WAREHOUSE" };
            var mine = new QuickQuery { Name = "CLEAN UP", Sql = "DELETE FROM t", AccountId = a.Id, AccountName = a.Name, Hotkey = 1 };
            var quick = new List<QuickQuery>
            {
                new() { Name = "Last 100", Sql = "SELECT 1", AccountId = other.Id, AccountName = other.Name, Hotkey = 1 },
                mine,
                new() { Name = "Everywhere", Sql = "SELECT 3" },
            };
            var path = Path.Combine(Path.GetTempPath(), $"myfront-connection-{Guid.NewGuid():N}.json");
            try
            {
                ConnectionFile.Export(path, a, quick);
                var accounts = new List<Account> { other }; // SALES deleted; its button stays in quickqueries.json
                var added = ConnectionFile.Read(path).AddTo(accounts, quick);
                QuickQueryStore.Relink(quick, accounts);
                var shown = quick.Where(q => q.ShowsOn(added.Id) && q.AccountId is not null).ToList();
                Assert.True(added.Name == "SALES" && quick.Count == 3, $"no second button: {quick.Count} buttons");
                Assert.True(shown.Count == 1 && shown[0] is { Name: "CLEAN UP", Hotkey: 1 } && shown[0].Id == mine.Id && quick[1] == shown[0],
                    "the same button, in its place, with its Ctrl+1: " + string.Join(", ", shown.Select(q => q.Name)));

                // the same file again, the first import still here: that one gets its own copy
                var again = ConnectionFile.Read(path).AddTo(accounts, quick);
                Assert.True(again.Name == "SALES (2)" && quick.Count == 4 && quick.Count(q => q.AccountId == added.Id) == 1
                            && quick.Single(q => q.AccountId == again.Id) is { Hotkey: 1 } copy && copy.Id != mine.Id, "again: a copy for the new one");
                return $"{quick.Count(q => q.Name == "CLEAN UP")} CLEAN UP buttons for 2 connections";
            }
            finally
            {
                File.Delete(path);
            }
        });

        t.Check("connection file: another .json (e.g. exported quick queries) is refused", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"myfront-not-connection-{Guid.NewGuid():N}.json");
            try
            {
                QuickQueryStore.Export(path, new[] { new QuickQuery { Name = "q" } });
                try
                {
                    ConnectionFile.Read(path);
                    throw new Exception("read a quick query file as a connection");
                }
                catch (InvalidDataException ex)
                {
                    return ex.Message;
                }
            }
            finally
            {
                File.Delete(path);
            }
        });
    }
}
