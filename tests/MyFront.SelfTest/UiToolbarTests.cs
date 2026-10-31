using MyFront.Core.Accounts;
using MyFront.Core.Db;
using MyFront.Core.Quick;
using MyFront.UI;

namespace MyFront.SelfTest;

/// <summary>The SQL editor round: results ✕, one toolbar, Explain, quick queries in the toolbar, editor typing.</summary>
internal static partial class UiTests
{
    private static async Task ToolbarUi(TestRunner t, TestDb db, MainForm form)
    {
        form.WindowState = FormWindowState.Normal;
        form.Size = new Size(form.LogicalToDeviceUnits(1280), form.LogicalToDeviceUnits(800));
        var session = await ServerSession.ConnectAsync(db.ToAccount(TlsMode.Required), db.Password);
        await form.AddSessionAsync(session);
        await form.Navigator.SelectDatabaseAsync(session, db.Database);
        var tabs = form.EditorTabsOf(session);
        var sqlTab = tabs[0];
        form.ShowSqlTab(sqlTab);
        var editor = sqlTab.Editor;
        ToolStripButton Main(string field) => Field<ToolStripButton>(form, field);
        try
        {
            await t.Check("ui: the SQL editor's own toolbar has just Run and Explain (and Format on the right); the other buttons are in the main toolbar", async () =>
            {
                var own = FindAll<ToolStrip>(sqlTab).First().Items.OfType<ToolStripButton>().Select(b => b.Text).ToList();
                Assert.Equal("Run|Explain|Format", string.Join("|", own), "editor toolbar");
                var top = form.QuickBar.Items.OfType<ToolStripButton>().Select(b => b.Text).ToList();
                foreach (var name in new[] { "Run", "Run all", "Run current statement", "Stop", "Open .sql file", "Save", "SQL history", "Export query" })
                    Assert.True(top.Contains(name), $"main toolbar has {name}: {string.Join(", ", top)}");

                editor.Text = "SELECT 1 AS a;\nSELECT 2 AS b;";
                int second = editor.Text.IndexOf("SELECT 2", StringComparison.Ordinal);
                editor.SetSelection(editor.TextLength, second);
                Main("_tbRunAll").PerformClick();
                await Until(() => !sqlTab.IsRunning && sqlTab.ResultTabs.Count == 2, "Run all (main toolbar) ran both");
                editor.SetEmptySelection(second + 3);
                Main("_tbRunCur").PerformClick();
                await Until(() => !sqlTab.IsRunning && sqlTab.ResultTabs.Count == 1 && sqlTab.ResultTabs[0].Data.Columns[0].Name == "b", "Run current (main toolbar)");

                var view = form.ActiveView!;
                view.Tabs.SelectedTab = view.ObjectPage;
                await Task.Delay(50);
                Assert.True(!Main("_tbRunAll").Enabled && !Main("_tbSave").Enabled && !Main("_tbExportQuery").Enabled, "editor buttons off without an editor in front");
                form.ShowSqlTab(sqlTab);
                await Task.Delay(50);
                Assert.True(Main("_tbRunAll").Enabled && Main("_tbSave").Enabled, "back on with the editor");
                await Task.Delay(100);
                Shot(form, "80-toolbars");
                return string.Join(", ", own);
            });

            await t.Check("ui: the red ✕ left of the result tabs closes the results (also while running); the next run brings them back", async () =>
            {
                var close = Field<Label>(sqlTab, "_closeResults");
                Assert.Equal(ThemeManager.Current.Error, close.ForeColor, "red");
                Assert.True(close.Visible && close.Text == "✕", "shown");
                Shot(form, "81-results-close");
                Call(close, "OnClick", EventArgs.Empty);
                Assert.True(sqlTab.ResultsClosed && sqlTab.ResultTabs.Count == 0, "closed and cleared");
                await Task.Delay(100);
                Shot(form, "82-results-closed");
                editor.Text = "SELECT * FROM products LIMIT 3";
                await sqlTab.RunAsync(RunScope.All);
                Assert.True(!sqlTab.ResultsClosed && sqlTab.ResultTabs.Count == 1, "back after a run");

                editor.Text = "SELECT SLEEP(10)";
                sqlTab.Run(RunScope.All);
                await Until(() => sqlTab.IsRunning, "running");
                await Task.Delay(300);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Call(close, "OnClick", EventArgs.Empty);
                await Until(() => !sqlTab.IsRunning && sqlTab.ResultsClosed, "cancelled and closed", 8000);
                return $"closed a running query after {sw.ElapsedMilliseconds} ms";
            });

            await t.Check("ui: Explain the last query — full table scan + filesort, then a primary key lookup", async () =>
            {
                editor.Text = "SELECT * FROM prices WHERE city = 'Konya' ORDER BY note LIMIT 10";
                await sqlTab.RunAsync(RunScope.All);
                await sqlTab.ExplainAsync();
                var f = sqlTab.ExplainFindings;
                Assert.True(f.Any(x => x.StartsWith("⚠ prices: full table scan")), "full scan: " + string.Join(" | ", f));
                Assert.True(f.Any(x => x.Contains("filesort")), "filesort: " + string.Join(" | ", f));
                Assert.True(sqlTab.ExplainPage is not null && Field<TabControl>(sqlTab, "_results").SelectedTab == sqlTab.ExplainPage, "Explain tab in front");
                Assert.True(sqlTab.StatusTime is null, "status not left at running…: " + sqlTab.StatusTime);
                await Task.Delay(100);
                Shot(form, "83-explain");
                form.ToggleTheme();
                try
                {
                    await Task.Delay(150);
                    Shot(form, "84-dark-explain");
                }
                finally { form.ToggleTheme(); }

                editor.Text = "SELECT * FROM prices WHERE id = 5";
                await sqlTab.RunAsync(RunScope.All);
                Assert.True(sqlTab.ExplainPage is null, "a new run clears the old Explain");
                await sqlTab.ExplainAsync();
                Assert.True(sqlTab.ExplainFindings.Any(x => x.StartsWith("✓ prices: index PRIMARY")), "pk: " + string.Join(" | ", sqlTab.ExplainFindings));
                return string.Join(" | ", f);
            });

            await t.Check("ui: Explain before anything ran takes the statement at the cursor; SHOW can't be explained", async () =>
            {
                var tab2 = tabs[1];
                form.ShowSqlTab(tab2);
                tab2.Editor.Text = "SHOW TABLES;\nSELECT p.name FROM products p JOIN prices x ON x.product_id = p.id WHERE p.id = 3;";
                tab2.Editor.SetEmptySelection(tab2.Editor.Text.IndexOf("JOIN", StringComparison.Ordinal));
                await tab2.ExplainAsync();
                var f = tab2.ExplainFindings;
                Assert.True(f.Any(x => x.StartsWith("✓ p: index PRIMARY")) && f.Any(x => x.Contains("x:")), "join explained: " + string.Join(" | ", f));
                tab2.Editor.SetEmptySelection(3);
                var before = tab2.ExplainFindings;
                await tab2.ExplainAsync();
                var state = Field<ToolStripLabel>(tab2, "_state").Text ?? "";
                Assert.True(state.StartsWith("Explain: run a SELECT"), state);
                Assert.True(ReferenceEquals(before, tab2.ExplainFindings), "nothing explained for SHOW");
                form.ShowSqlTab(sqlTab);
                return string.Join(" | ", f);
            });

            await t.Check("ui: quick query buttons sit right in the main toolbar (one row), first one leftmost, hotkey shown as C+1", async () =>
            {
                form.SetQuickQueries(new List<QuickQuery>
                {
                    new() { Name = "Warns", Sql = "SHOW WARNINGS", Hotkey = 1 },
                    new() { Name = "Version", Sql = "SELECT VERSION()" },
                }, save: false);
                var bars = form.Controls.OfType<ToolStrip>().Where(s => s is not MenuStrip and not StatusStrip).ToList();
                Assert.Equal(1, bars.Count, "one toolbar row");
                var buttons = form.QuickBar.QueryButtons.ToList();
                Assert.Equal("Warns C+1|Version", string.Join("|", buttons.Select(b => b.Text)), "labels in order");
                Assert.True(buttons.All(b => b.Alignment == ToolStripItemAlignment.Right), "right-aligned");
                var add = form.QuickBar.Items.OfType<ToolStripButton>().First(b => b.Text == "New quick query");
                var manage = form.QuickBar.Items.OfType<ToolStripButton>().First(b => b.Text == "Manage…");
                Assert.True(buttons[0].Bounds.X < buttons[1].Bounds.X && buttons[1].Bounds.Right <= add.Bounds.X && add.Bounds.Right <= manage.Bounds.X,
                    $"order Warns {buttons[0].Bounds.X}, Version {buttons[1].Bounds.X}, + {add.Bounds.X}, Manage {manage.Bounds.X}");
                var lastLeft = form.QuickBar.Items.OfType<ToolStripItem>().Where(i => i.Alignment == ToolStripItemAlignment.Left && i.Visible).Max(i => i.Bounds.Right);
                Assert.True(lastLeft < buttons[0].Bounds.X, $"app buttons end at {lastLeft}, quick ones start at {buttons[0].Bounds.X}");
                await Task.Delay(100);
                Shot(form, "85-quick-in-toolbar");
                form.ToggleTheme();
                try
                {
                    await Task.Delay(150);
                    Shot(form, "86-dark-quick-in-toolbar");
                }
                finally { form.ToggleTheme(); }
                return string.Join(", ", buttons.Select(b => b.Text));
            });

            await t.Check("ui: quick query editor — typing a new name keeps every character (spaces too) and the fields stay enabled", async () =>
            {
                using var ed = new MyFront.UI.Dialogs.QuickQueryEditor(form.QuickQueries, Array.Empty<Account>(),
                    _ => Task.FromResult(new List<string> { db.Database }), null);
                ed.Show(form);
                await Task.Delay(150);
                Call(ed, "AddNew");
                var name = Field<TextBox>(ed, "_name");
                var details = Field<Panel>(ed, "_details");
                var list = Field<ListBox>(ed, "_list");
                name.SelectAll();
                foreach (var c in "Uyarılar ve sürüm")
                {
                    name.SelectedText = c.ToString(); // at the caret, like typing
                    Application.DoEvents();
                    Assert.True(details.Enabled, $"fields stayed enabled after '{c}'");
                }
                Assert.Equal("Uyarılar ve sürüm", name.Text, "every character, in order");
                Assert.Equal(name.TextLength, name.SelectionStart, "caret at the end");
                Assert.Equal("Uyarılar ve sürüm", list.SelectedItem?.ToString(), "list entry renamed");
                await Task.Delay(100);
                Shot(ed, "87-quick-editor-typing");
                ed.Close();
                return name.Text;
            });
        }
        finally
        {
            form.SetQuickQueries(new List<QuickQuery>(), save: false);
            form.CloseSession(session);
        }
    }
}
