using MyFront.Core.Accounts;
using MyFront.Core.Db;
using MyFront.Core.Quick;
using MyFront.UI;
using MyFront.UI.Dialogs;

namespace MyFront.SelfTest;

/// <summary>Connection scope: quick queries per connection, the Database list in the connection dialog, the tree limited to that database.</summary>
internal static partial class UiTests
{
    private static async Task ConnectionScopeUi(TestRunner t, TestDb db, MainForm form)
    {
        form.WindowState = FormWindowState.Normal;
        form.Size = new Size(form.LogicalToDeviceUnits(1280), form.LogicalToDeviceUnits(800));
        var accountA = db.ToAccount(TlsMode.Required);
        accountA.Name = "scope A";
        var accountB = db.ToAccount(TlsMode.Required);
        accountB.Name = "scope B";
        var a = await ServerSession.ConnectAsync(accountA, db.Password);
        await form.AddSessionAsync(a);
        var b = await ServerSession.ConnectAsync(accountB, db.Password);
        await form.AddSessionAsync(b);
        var qa = new QuickQuery { Name = "Only A", Sql = "SELECT 'a' AS who", Hotkey = 1, AccountId = accountA.Id, AccountName = accountA.Name };
        var qb = new QuickQuery { Name = "Only B", Sql = "SELECT 'b' AS who", Hotkey = 1, AccountId = accountB.Id, AccountName = accountB.Name };
        var every = new QuickQuery { Name = "Everywhere", Sql = "SELECT 2 AS n" };
        string Buttons() => string.Join("|", form.QuickBar.QueryButtons.Select(x => x.Text));
        string? QuickResult(ServerSession s) =>
            form.ViewOf(s)!.Editors.FirstOrDefault(x => x.IsQuickPage) is { IsRunning: false, ResultTabs.Count: > 0 } page
                ? page.ResultTabs[0].Data.Rows.GetRow(0)[0]?.ToString() : null;
        try
        {
            await t.Check("ui: quick query buttons belong to their connection — shown (and Ctrl+1 run) only while it is in front", async () =>
            {
                form.SetQuickQueries(new List<QuickQuery> { qa, qb, every }, save: false);
                form.ShowConnection(form.ViewOf(a)!);
                await Task.Delay(50);
                Assert.Equal("Only A C+1|Everywhere", Buttons(), "A in front");
                Assert.True(PressKeys(form, Keys.Control | Keys.D1), "Ctrl+1 handled");
                await Until(() => QuickResult(a) is not null, "Ctrl+1 ran on A");
                Assert.Equal("a", QuickResult(a), "A's own button ran on A");
                Assert.True(form.ViewOf(b)!.Editors.All(x => !x.IsQuickPage), "nothing ran on B");
                await Task.Delay(100);
                Shot(form, "90-quick-of-connection-a");

                form.ShowConnection(form.ViewOf(b)!);
                await Task.Delay(50);
                Assert.Equal("Only B C+1|Everywhere", Buttons(), "B in front");
                Assert.True(PressKeys(form, Keys.Control | Keys.D1), "Ctrl+1 handled on B");
                await Until(() => QuickResult(b) is not null, "Ctrl+1 ran on B");
                Assert.Equal("b", QuickResult(b), "B's own button ran on B");
                Assert.Equal("a", QuickResult(a), "A's page untouched");
                await Task.Delay(100);
                Shot(form, "91-quick-of-connection-b");
                return Buttons();
            });

            await t.Check("ui: quick query editor — a new button belongs to the connection in front; Ctrl+1 is per connection; the list names the connection", async () =>
            {
                using var ed = new QuickQueryEditor(form.QuickQueries, new[] { accountA, accountB },
                    _ => Task.FromResult(new List<string> { db.Database }), null, addNew: false, defaultAccountId: accountB.Id);
                ed.Show(form);
                await Task.Delay(150);
                Call(ed, "AddNew");
                var added = ed.Result[^1];
                Assert.True(added.AccountId == accountB.Id && added.AccountName == accountB.Name, "new button on B: " + added.AccountName);
                Assert.Equal(accountB.Name, Field<ComboBox>(ed, "_account").Text, "Connection box");
                var list = Field<ListBox>(ed, "_list");
                Assert.Equal("New query  ·  scope B", list.GetItemText(list.SelectedItem), "list entry");
                Assert.Equal("Everywhere  ·  every connection", list.GetItemText(list.Items[2]), "every connection entry");
                Field<ComboBox>(ed, "_hotkey").SelectedIndex = 1; // Ctrl+1: taken from "Only B", "Only A" keeps it
                var r = ed.Result;
                Assert.True(r.Single(q => q.Name == "Only B").Hotkey is null && r.Single(q => q.Name == "Only A").Hotkey == 1 && added.Hotkey == 1,
                    "hotkeys: " + string.Join(", ", r.Select(q => $"{q.Name}={q.Hotkey}")));
                await Task.Delay(100);
                Shot(ed, "92-quick-editor-connection");
                ed.Close();
                return string.Join(", ", r.Select(q => list.GetItemText(q) + (q.Hotkey is { } k ? $" [Ctrl+{k}]" : "")));
            });

            await t.Check("ui: no connection in front → no quick query buttons", async () =>
            {
                form.CloseSession(b);
                form.CloseSession(a);
                await Task.Delay(50);
                Assert.Equal("", Buttons(), "start page");
                a = await ServerSession.ConnectAsync(accountA, db.Password);
                await form.AddSessionAsync(a);
                await Task.Delay(50);
                Assert.Equal("Only A C+1|Everywhere", Buttons(), "back with A");
            });

            await t.Check("ui: connection dialog — Test connection fills the Database list; (all databases) clears it", async () =>
            {
                using var edit = new AccountEditForm(db.ToAccount(TlsMode.Required), Array.Empty<string>(), isNew: false);
                edit.Show(form);
                var combo = Field<ComboBox>(edit, "_database");
                Assert.True(combo.Text == db.Database && combo.Items.Count == 0, "the account's database, nothing listed before connecting");
                Field<TextBox>(edit, "_password").Text = db.Password;
                await (Task)Call(edit, "TestAsync")!;
                var result = Field<Label>(edit, "_testResult").Text;
                Assert.True(result.StartsWith("Connected") && result.Contains("database(s) in the Database list"), result);
                var items = combo.Items.Cast<object>().Select(o => o.ToString()!).ToList();
                Assert.True(items[0] == "(all databases)" && items.Contains(db.Database) && items.Contains("information_schema"), string.Join(", ", items));
                Assert.Equal(db.Database, combo.Text, "still the account's database");
                await Task.Delay(100);
                Shot(edit, "93-connection-database-list");
                combo.SelectedIndex = 0;
                Call(edit, "OnOk");
                Assert.True(edit.DialogResult == DialogResult.OK && edit.Result.DefaultDatabase is null, "(all databases) → none: " + edit.Result.DefaultDatabase);
                edit.Close();
                return string.Join(", ", items);
            });

            await t.Check("ui: connection dialog — opening the Database list connects and lists them, even with a database that doesn't exist typed", async () =>
            {
                using var edit = new AccountEditForm(db.ToAccount(TlsMode.Required), Array.Empty<string>(), isNew: false);
                edit.Show(form);
                var combo = Field<ComboBox>(edit, "_database");
                Field<TextBox>(edit, "_password").Text = db.Password;
                combo.Text = "no_such_db";
                Call(combo, "OnDropDown", EventArgs.Empty);
                await Until(() => Field<bool>(edit, "_databasesListed"), "databases listed");
                Assert.True(combo.Items.Contains(db.Database), "listed");
                Assert.Equal("no_such_db", combo.Text, "typed text kept");
                combo.SelectedItem = db.Database;
                Call(edit, "OnOk");
                Assert.Equal(db.Database, edit.Result.DefaultDatabase, "picked from the list");
                edit.Close();
                return $"{combo.Items.Count - 1} databases";
            });

            await t.Check("ui: a connection with a database shows only that one in the tree and the Object Browser; cleared → all, live", async () =>
            {
                var view = form.ViewOf(a)!;
                var nav = view.Navigator;
                var server = nav.FindServer(a)!;
                string Dbs() => string.Join(",", server.Nodes.Cast<TreeNode>().Where(n => NavigatorTree.Info(n)?.Kind == NodeKind.Database).Select(n => n.Text));
                var list = form.ObjectBrowser.Controls.OfType<SplitContainer>().First().Panel1.Controls.OfType<ListView>().First();
                string Listed() => string.Join(",", list.Items.Cast<ListViewItem>().Select(i => i.Text));
                Assert.Equal(db.Database, Dbs(), "tree: only the account's database");
                Assert.True(server.Nodes.Cast<TreeNode>().Count(n => n.Text is "Processes" or "Variables") == 2, "Processes and Variables stay");
                form.Tabs.SelectedTab = form.ObjectPage;
                nav.SelectedNode = server;
                await Until(() => list.Columns.Count > 0 && list.Columns[0].Text == "Database" && list.Items.Count > 0, "database list");
                Assert.Equal(db.Database, Listed(), "Object Browser: only it");
                await Task.Delay(100);
                Shot(form, "94-tree-one-database");

                await view.SetDefaultDatabaseAsync(null); // what clearing it in the connection dialog does
                Assert.True(Dbs().Split(',').Contains("information_schema") && Dbs().Split(',').Contains(db.Database), "tree: all again: " + Dbs());
                await Until(() => list.Items.Count > 1, "Object Browser: all again");
                await view.SetDefaultDatabaseAsync(db.Database);
                Assert.Equal(db.Database, Dbs(), "only it again");
                Assert.True(nav.SelectedInfo is { Kind: NodeKind.Database } i && i.Database == db.Database, "and selected");
                return Dbs();
            });
        }
        finally
        {
            form.SetQuickQueries(new List<QuickQuery>(), save: false);
            foreach (var s in new[] { a, b }) if (form.ViewOf(s) is not null) form.CloseSession(s);
        }
    }
}
