using MyFront.Core.Accounts;
using MyFront.Core.Db;
using MyFront.Core.Editors;
using MyFront.Core.Quick;
using MyFront.UI;
using MyFront.UI.Dialogs;

namespace MyFront.SelfTest;

/// <summary>Format SQL in the editor; Export… / Import… of a connection in the Open connection dialog.</summary>
internal static partial class UiTests
{
    private static async Task FormatAndTransferUi(TestRunner t, TestDb db, MainForm form)
    {
        form.WindowState = FormWindowState.Normal;
        form.Size = new Size(form.LogicalToDeviceUnits(1280), form.LogicalToDeviceUnits(800));
        var session = await ServerSession.ConnectAsync(db.ToAccount(TlsMode.Required), db.Password);
        await form.AddSessionAsync(session);
        var sqlTab = form.EditorTabsOf(session)[0];
        form.ShowSqlTab(sqlTab);
        var editor = sqlTab.Editor;
        string nl = editor.EolMode == ScintillaNET.Eol.Lf ? "\n" : "\r\n";
        try
        {
            await t.Check("ui: Format (right end of the editor toolbar, Ctrl+Shift+F) formats all of it as one undo step; the caret stays at the same code", async () =>
            {
                var bar = FindAll<ToolStrip>(sqlTab).First();
                var format = bar.Items.OfType<ToolStripButton>().Single(b => b.Text == "Format");
                Assert.True(format.Alignment == ToolStripItemAlignment.Right && format.Visible, "Format on the right");
                const string script = "-- prices per city\nselect x.city, count(*) n, min(x.price_date) first_date, max(x.price_date) last_date from prices x " +
                                      "join products p on p.id = x.product_id where x.product_id between 1 and 5 and x.city <> '' group by x.city order by n desc;\n\n" +
                                      "update products set name = name where id in (select product_id from prices where id < 3);\nselect 1";
                editor.Text = script.ReplaceLineEndings(nl);
                editor.GotoPosition(editor.Text.IndexOf("where x.", StringComparison.Ordinal));
                format.PerformClick();
                var formatted = editor.Text;
                Assert.Equal(SqlFormatter.Format(script.ReplaceLineEndings(nl), nl), formatted, "formatted");
                Assert.True(formatted.Contains(nl + "WHERE x.product_id BETWEEN 1 AND 5" + nl + "    AND x.city <> ''") && !formatted.Replace("\r\n", "").Contains('\r'),
                    "layout, line endings: " + formatted);
                Assert.True(formatted[editor.CurrentPosition..].StartsWith("WHERE x."), "caret at WHERE: " + formatted[editor.CurrentPosition..][..20]);
                await Task.Delay(100);
                Shot(form, "95-format-sql");
                editor.Undo();
                Assert.Equal(script.ReplaceLineEndings(nl), editor.Text, "one undo step back");
                Assert.True(PressKeys(form, Keys.Control | Keys.Shift | Keys.F), "Ctrl+Shift+F handled");
                Assert.Equal(formatted, editor.Text, "Ctrl+Shift+F");
                format.PerformClick();
                Assert.True(Field<ToolStripLabel>(sqlTab, "_state").Text == "Already formatted" && editor.Text == formatted, "twice: no change");
                return $"{script.Length} → {formatted.Length} chars";
            });

            await t.Check("ui: Format with a selection formats only the selection", async () =>
            {
                var text = "select 1;" + nl + "select a,b from t where x=1 and y=2;" + nl + "select 3";
                editor.Text = text;
                int start = text.IndexOf("select a", StringComparison.Ordinal), end = text.IndexOf(';', start);
                editor.SetSelection(end, start);
                sqlTab.FormatSql();
                Assert.Equal("select 1;" + nl + "SELECT a, b" + nl + "FROM t" + nl + "WHERE x = 1" + nl + "    AND y = 2;" + nl + "select 3", editor.Text, "only the selection");
                Assert.Equal("SELECT a, b" + nl + "FROM t" + nl + "WHERE x = 1" + nl + "    AND y = 2", editor.SelectedText, "the formatted part stays selected");
                await Task.Delay(10);
                editor.Text = "";
            });

            await t.Check("ui: Open connection — Export… and Import… between Edit… and Delete; a connection goes out with its editors and buttons and comes back as a new one", async () =>
            {
                var savedAccounts = AccountStore.Load();
                var savedQuick = QuickQueryStore.Load();
                var path = Path.Combine(Path.GetTempPath(), $"myfront-ui-connection-{Guid.NewGuid():N}.json");
                var a = db.ToAccount(TlsMode.Required);
                a.Name = "transfer me";
                a.SetPassword(db.Password);
                try
                {
                    AccountStore.Save(savedAccounts.Append(a));
                    QuickQueryStore.Save(savedQuick.Append(new QuickQuery { Name = "Transfer button", Sql = "SELECT 1", AccountId = a.Id, AccountName = a.Name }));
                    EditorStore.WriteText(a.Id, 2, "select 'editor two'");
                    using var dlg = new AccountsForm();
                    dlg.Show(form);
                    await Task.Delay(100);
                    var buttons = FindAll<Button>(dlg).Select(b => b.Text).ToList();
                    Assert.Equal("Open|New…|Edit…|Export…|Import…|Delete|Close", string.Join("|", buttons), "buttons");
                    var list = Field<ListView>(dlg, "_list");
                    list.Items.Cast<ListViewItem>().Single(i => ((Account)i.Tag!).Id == a.Id).Selected = true;
                    dlg.ExportSelected(path);
                    var status = Field<Label>(dlg, "_status");
                    Assert.True(status.Text.StartsWith("Exported \"transfer me\" with 1 SQL editor and 1 quick query button"), status.Text);
                    var json = File.ReadAllText(path);
                    Assert.True(!json.Contains(db.Password) && !json.Contains("dpapi"), "no password in the file");

                    dlg.ImportFile(path, askPassword: false);
                    var added = AccountStore.Load().Single(x => x.Name == "transfer me (2)");
                    Assert.True(added.Id != a.Id && added.Host == a.Host && !added.HasSavedPassword, "a new connection without a password");
                    Assert.True(list.SelectedItems.Count == 1 && ((Account)list.SelectedItems[0].Tag!).Id == added.Id, "the new one is selected");
                    Assert.True(status.Text.Contains("enter its password"), status.Text);
                    Assert.True(dlg.QuickQueriesChanged, "the main window reloads the buttons");
                    Assert.True(QuickQueryStore.Load().Any(q => q.AccountId == added.Id && q.Name == "Transfer button"), "its button");
                    Assert.Equal("select 'editor two'", EditorStore.ReadText(added.Id, 2), "its SQL Editor 2");
                    await Task.Delay(100);
                    Shot(dlg, "96-connections-export-import");
                    dlg.Close();
                    return status.Text;
                }
                finally
                {
                    AccountStore.Save(savedAccounts);
                    QuickQueryStore.Save(savedQuick);
                    File.Delete(path);
                }
            });
        }
        finally
        {
            editor.Text = "";
            form.CloseSession(session);
        }
    }
}
