using MyFront.Core.Accounts;
using MyFront.Core.Db;
using MyFront.UI;

namespace MyFront.SelfTest;

/// <summary>The status bar's message belongs to its connection: it never shows on another connection's tab.</summary>
internal static partial class UiTests
{
    private static async Task StatusPerConnectionUi(TestRunner t, TestDb db, MainForm form)
    {
        var accountA = db.ToAccount(TlsMode.Required);
        accountA.Name = "status A";
        var accountB = db.ToAccount(TlsMode.Required);
        accountB.Name = "status B";
        var a = await ServerSession.ConnectAsync(accountA, db.Password);
        await form.AddSessionAsync(a);
        var b = await ServerSession.ConnectAsync(accountB, db.Password);
        await form.AddSessionAsync(b);
        var viewA = form.ViewOf(a)!;
        var viewB = form.ViewOf(b)!;
        var status = Field<ToolStripStatusLabel>(form, "_stMsg");
        async Task CopyName(ConnectionView view, string? table)
        {
            var node = table is null
                ? await view.Navigator.SelectDatabaseAsync(view.Session, db.Database)
                : await view.Navigator.SelectObjectAsync(view.Session, db.Database, table);
            using var m = (ContextMenuStrip)Call(view.Navigator, "BuildMenu", NavigatorTree.Info(node)!)!;
            m.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "Copy name").PerformClick();
        }
        try
        {
            await t.Check("ui: a status bar message stays with its connection — another connection's tab shows its own (or none)", async () =>
            {
                form.ShowConnection(viewA);
                await CopyName(viewA, null);
                var aText = $"\"{db.Database}\" copied to the clipboard";
                Assert.Equal(aText, status.Text, "A in front");
                form.ShowConnection(viewB);
                Assert.Equal("", status.Text, "B has no message");
                await CopyName(viewB, "products");
                Assert.Equal("\"products\" copied to the clipboard", status.Text, "B's own");
                form.ShowConnection(viewA);
                Assert.Equal(aText, status.Text, "back on A: A's message");
                form.ShowMessage(viewB, "about B, in the background");
                Assert.Equal(aText, status.Text, "a message about B doesn't show on A");
                form.ShowConnection(viewB);
                Assert.Equal("about B, in the background", status.Text, "it shows on B");
                form.CloseSession(b);
                Assert.Equal(aText, status.Text, "B closed: A's again");
                return status.Text;
            });
        }
        finally
        {
            foreach (var s in new[] { a, b }) if (form.ViewOf(s) is not null) form.CloseSession(s);
        }
    }
}
