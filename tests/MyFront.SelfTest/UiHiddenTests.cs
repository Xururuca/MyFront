using MyFront.Core.Accounts;
using MyFront.Core.Db;
using MyFront.UI;

namespace MyFront.SelfTest;

/// <summary>Hide / Unhide tables in the tree: a closed Hidden folder at the bottom of the database.</summary>
internal static partial class UiTests
{
    private static async Task HiddenTablesUi(TestRunner t, TestDb db, MainForm form)
    {
        form.WindowState = FormWindowState.Normal;
        form.Size = new Size(form.LogicalToDeviceUnits(1280), form.LogicalToDeviceUnits(800));
        var savedAccounts = AccountStore.Load();
        var account = db.ToAccount(TlsMode.Required);
        account.Name = "hide tables";
        AccountStore.Save(savedAccounts.Append(account));
        var session = await ServerSession.ConnectAsync(account, db.Password);
        await Scalar(session, db.Database, """
            DROP VIEW IF EXISTS mf_hide_v;
            DROP TABLE IF EXISTS mf_hide_a, mf_hide_b, mf_hide_c;
            CREATE TABLE mf_hide_a (id INT PRIMARY KEY);
            CREATE TABLE mf_hide_b (id INT PRIMARY KEY);
            CREATE TABLE mf_hide_c (id INT PRIMARY KEY);
            CREATE VIEW mf_hide_v AS SELECT id FROM mf_hide_a;
            """);
        await form.AddSessionAsync(session);
        var nav = form.ViewOf(session)!.Navigator;
        var dbNode = (await nav.SelectDatabaseAsync(session, db.Database, expand: true))!;
        await nav.RefreshNodeAsync(dbNode);
        TreeNode Node(string name) => NavigatorTree.ObjectNodes(dbNode).First(n => NavigatorTree.Info(n)?.Name == name);
        TreeNode? Folder() => NavigatorTree.HiddenFolder(dbNode);
        string Names(TreeNode parent) => string.Join(",", parent.Nodes.Cast<TreeNode>().Select(n => n.Text).Where(n => n.StartsWith("mf_hide_")));
        string Menu(TreeNode n)
        {
            using var m = (ContextMenuStrip)Call(nav, "BuildMenu", NavigatorTree.Info(n)!)!;
            return string.Join("|", m.Items.OfType<ToolStripMenuItem>().Select(x => x.Text));
        }
        void Click(string name, string item)
        {
            nav.SelectedNode = Node(name); // what the right-click does
            using var m = (ContextMenuStrip)Call(nav, "BuildMenu", NavigatorTree.Info(Node(name))!)!;
            m.Items.OfType<ToolStripMenuItem>().Single(x => x.Text == item).PerformClick();
        }
        bool Stored(string name) => AccountStore.Load().Single(x => x.Id == account.Id).IsHidden(db.Database, name);
        var status = Field<ToolStripStatusLabel>(form, "_stMsg");
        try
        {
            await t.Check("ui: Hide (right-click a table or view) moves it into a closed Hidden folder at the bottom of its database; saved with the connection", () =>
            {
                var menu = Menu(Node("mf_hide_b"));
                Assert.True(menu.Contains("Copy name|Hide|Import Excel") && menu.StartsWith("Open in Data Browser") && menu.EndsWith("Properties…"), menu);
                Click("mf_hide_b", "Hide");
                var folder = Folder() ?? throw new Exception("no Hidden folder");
                Assert.True(dbNode.Nodes[^1] == folder && folder.Text == "Hidden (1)" && !folder.IsExpanded, "the folder, last and closed");
                Assert.True(Names(folder) == "mf_hide_b" && !Names(dbNode).Contains("mf_hide_b"), "moved: " + Names(dbNode));
                Assert.True(Stored("mf_hide_b") && session.Account.IsHidden(db.Database, "mf_hide_b"), "saved");
                Assert.True(status.Text?.StartsWith("mf_hide_b hidden") == true, status.Text ?? "");
                Assert.True(nav.SelectedNode == dbNode, "the database selected (the folder stays closed)");

                Click("mf_hide_v", "Hide");
                Click("mf_hide_a", "Hide");
                Assert.True(Names(folder) == "mf_hide_a,mf_hide_b,mf_hide_v" && folder.Text == "Hidden (3)" && !folder.IsExpanded,
                    "in order, tables then views: " + Names(folder));
                Assert.Equal("mf_hide_c", Names(dbNode), "left in the list");
                return folder.Text;
            });

            await t.Check("ui: the Hidden folder stays after F5 and opens only when clicked; a hidden table keeps its menu, with Unhide", async () =>
            {
                await nav.RefreshNodeAsync(dbNode);
                var folder = Folder()!;
                Assert.True(dbNode.Nodes[^1] == folder && folder.Text == "Hidden (3)" && !folder.IsExpanded && Names(folder) == "mf_hide_a,mf_hide_b,mf_hide_v", "after F5");
                Assert.Equal(Menu(Node("mf_hide_c")).Replace("|Hide|", "|Unhide|"), Menu(Node("mf_hide_b")), "the same menu, Unhide for Hide");
                Assert.True(Menu(Node("mf_hide_v")).Contains("|Unhide|"), "a view too");
                folder.Expand();
                await Task.Delay(100);
                Shot(form, "97-tree-hidden-folder");
                folder.Collapse();
                var found = await nav.SelectObjectAsync(session, db.Database, "mf_hide_a");
                Assert.True(found?.Parent == folder && nav.SelectedNode == found && folder.IsExpanded, "found in the folder (Object Browser, rename …)");
            });

            await t.Check("ui: Unhide puts it back in its place and stays on it; the folder goes with the last one", () =>
            {
                Click("mf_hide_b", "Unhide");
                var top = dbNode.Nodes.Cast<TreeNode>().Where(n => NavigatorTree.Info(n)?.Kind is NodeKind.Table or NodeKind.View).ToList();
                var names = top.Select(n => n.Text).ToList();
                var sorted = top.Select(n => new DbObject(db.Database, n.Text, NavigatorTree.Info(n)!.ObjectType!.Value)).ToList();
                sorted.Sort(DbObject.Compare);
                Assert.True(names.SequenceEqual(sorted.Select(o => o.Name)) && Names(dbNode) == "mf_hide_b,mf_hide_c", "in its place: " + Names(dbNode));
                Assert.True(nav.SelectedNode == Node("mf_hide_b") && Folder()!.Text == "Hidden (2)" && !Stored("mf_hide_b"), "selected, saved");
                Click("mf_hide_a", "Unhide");
                Click("mf_hide_v", "Unhide");
                Assert.True(Folder() is null && Names(dbNode) == "mf_hide_a,mf_hide_b,mf_hide_c,mf_hide_v", "no folder: " + Names(dbNode));
                Assert.True(AccountStore.Load().Single(x => x.Id == account.Id).HiddenTables is null && session.Account.HiddenTables is null, "nothing hidden left");
            });
        }
        finally
        {
            await Scalar(session, db.Database, "DROP VIEW IF EXISTS mf_hide_v; DROP TABLE IF EXISTS mf_hide_a, mf_hide_b, mf_hide_c");
            form.CloseSession(session);
            AccountStore.Save(savedAccounts);
        }
    }
}
