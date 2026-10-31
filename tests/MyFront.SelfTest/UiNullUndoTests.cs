using MyFront.Core.Accounts;
using MyFront.Core.Db;
using MyFront.UI;

namespace MyFront.SelfTest;

/// <summary>Delete on cells sets NULL without asking and Ctrl+Z puts values back — Data Browser and result grids; whole rows are deleted only after asking.</summary>
internal static partial class UiTests
{
    private static async Task NullUndoUi(TestRunner t, TestDb db, MainForm form)
    {
        form.WindowState = FormWindowState.Normal;
        form.Size = new Size(form.LogicalToDeviceUnits(1280), form.LogicalToDeviceUnits(800));
        const string table = "mf_ui_null";
        var session = await ServerSession.ConnectAsync(db.ToAccount(TlsMode.Required), db.Password);
        await Scalar(session, db.Database, $"""
            DROP TABLE IF EXISTS {table};
            CREATE TABLE {table} (id INT PRIMARY KEY, tag VARCHAR(50) NOT NULL, status INT NULL, market_name VARCHAR(50) NULL) DEFAULT CHARSET=utf8mb4;
            INSERT INTO {table} VALUES (60, '/daily-bulletin.html', 404, 'northgate'), (61, 'pages/0/549', 200, 'eastfield'), (62, '/bulletins', 404, 'westbrook');
            """);
        await form.AddSessionAsync(session);
        var view = form.ViewOf(session)!;
        Task<object?> Db(string sql) => Scalar(session, db.Database, sql);
        async Task<string> Markets() => Convert.ToString(await Db($"SELECT GROUP_CONCAT(IFNULL(market_name, 'NULL') ORDER BY id) FROM {table}"))!;
        void Key(DataGridView grid, Keys keys) => Call(grid, "OnKeyDown", new KeyEventArgs(keys));
        var confirms = new List<string>();
        var oldConfirm = Ui.ConfirmOverride;
        Ui.ConfirmOverride = text => { confirms.Add(text); return true; };
        try
        {
            var data = view.DataBrowser;
            var grid = data.Grid;
            await t.Check("ui: Data Browser — Delete on a cell sets it to NULL without asking (saved); Ctrl+Z puts the value back", async () =>
            {
                await view.Navigator.RefreshNodeAsync((await view.Navigator.SelectDatabaseAsync(session, db.Database))!);
                await view.Navigator.SelectObjectAsync(session, db.Database, table);
                view.Tabs.SelectedTab = view.DataPage;
                await Until(() => data.Table == table && !data.IsLoading && grid.RowCount == 3, "rows");
                grid.ClearSelection();
                grid.CurrentCell = grid[3, 0];
                grid[3, 0].Selected = true;
                Key(grid, Keys.Delete);
                await Until(() => data.Source.Rows[0] is { State: EditState.Clean } r && r.Values[3] is null, "NULL saved");
                Assert.True(confirms.Count == 0 && grid.RowCount == 3, "no question, the row stays");
                Assert.Equal("NULL,eastfield,westbrook", await Markets(), "db");
                Assert.True(data.StatusTime?.StartsWith("Set 1 cell to NULL") == true, data.StatusTime ?? "");
                await Task.Delay(100);
                Shot(form, "98-delete-sets-null");

                Key(grid, Keys.Control | Keys.Z);
                await Until(() => data.Source.Rows[0] is { State: EditState.Clean } r && r.Values[3] as string == "northgate", "put back");
                Assert.Equal("northgate,eastfield,westbrook", await Markets(), "db after Ctrl+Z");
                Key(grid, Keys.Control | Keys.Z);
                Assert.Equal("Nothing to undo", data.StatusTime, "nothing more");
                return data.StatusTime;
            });

            await t.Check("ui: Data Browser — Ctrl+Z also undoes typing, and three cells set to NULL in one go; a NOT NULL cell fails, stays marked, Ctrl+Z puts it back", async () =>
            {
                TypeInto(grid, 3, 1, "east field");
                grid.CurrentCell = grid[3, 2]; // leave the row → saved
                await Until(() => data.Source.Rows[1].State == EditState.Clean && (string?)data.Source.Rows[1].Values[3] == "east field", "typed and saved");
                Key(grid, Keys.Control | Keys.Z);
                await Until(() => data.Source.Rows[1].State == EditState.Clean && (string?)data.Source.Rows[1].Values[3] == "eastfield", "typing undone");
                Assert.Equal("northgate,eastfield,westbrook", await Markets(), "db");

                // three cells across two rows, then one Ctrl+Z puts all three back
                grid.ClearSelection();
                grid.CurrentCell = grid[2, 1];
                foreach (var (c, r) in new[] { (2, 1), (3, 1), (3, 2) }) grid[c, r].Selected = true;
                Key(grid, Keys.Delete);
                await Until(() => data.Source.Rows[1].State == EditState.Clean && data.Source.Rows[2].State == EditState.Clean
                                  && data.Source.Rows[1].Values[2] is null && data.Source.Rows[2].Values[3] is null, "three NULLs saved");
                Assert.True(data.StatusTime?.StartsWith("Set 3 cells to NULL in 2 rows") == true, data.StatusTime ?? "");
                Key(grid, Keys.Control | Keys.Z);
                await Until(() => data.Source.Rows[2].State == EditState.Clean && (string?)data.Source.Rows[2].Values[3] == "westbrook", "all back");
                Assert.True(Convert.ToInt32(await Db($"SELECT status FROM {table} WHERE id = 61")) == 200 && await Markets() == "northgate,eastfield,westbrook", "db: all back");

                lock (Messages) Messages.Clear();
                grid.ClearSelection();
                grid.CurrentCell = grid[1, 2];
                Key(grid, Keys.Delete); // tag is NOT NULL
                await Until(() => data.Source.Rows[2].State == EditState.Error, "error");
                Assert.True(Messages.Any(m => m.StartsWith("error:") && m.Contains("cannot be null", StringComparison.OrdinalIgnoreCase)), "the error is shown");
                Key(grid, Keys.Control | Keys.Z);
                await Until(() => data.Source.Rows[2] is { State: EditState.Clean } r && (string?)r.Values[1] == "/bulletins", "put back, nothing to save");
                return data.StatusTime;
            });

            await t.Check("ui: Data Browser — Delete with whole rows selected (row headers) still asks, then deletes; the menu says Set NULL (Del), Undo, Delete selected rows…", async () =>
            {
                var menu = grid.ContextMenuStrip!;
                var items = string.Join("|", menu.Items.OfType<ToolStripMenuItem>().Select(i => i.Text + (i.ShortcutKeyDisplayString is { } k ? " " + k : "")));
                Assert.True(items.Contains("Set NULL Del|Undo Ctrl+Z|Add row|Delete selected rows…") && !items.Contains("rows… Del"), items);
                grid.ClearSelection();
                grid.CurrentCell = grid[1, 2];
                grid.Rows[2].Selected = true;
                Key(grid, Keys.Delete);
                await Until(() => grid.RowCount == 2, "the row deleted");
                Assert.True(confirms.Count == 1 && confirms[0].StartsWith($"Delete 1 row from {db.Database}.{table}?"), string.Join(" | ", confirms));
                Assert.Equal(2L, Convert.ToInt64(await Db($"SELECT COUNT(*) FROM {table}")), "db rows");
                return items;
            });

            var sqlTab = view.Editors.First();
            await t.Check("ui: result grid — Delete sets the cell to NULL without asking, Ctrl+Z puts it back; whole rows: nothing deleted, it says so", async () =>
            {
                view.Tabs.SelectedTab = sqlTab;
                sqlTab.Editor.Text = $"SELECT id, market_name, status FROM {table} ORDER BY id";
                await sqlTab.RunAsync(RunScope.All);
                var result = sqlTab.ResultTabs[0];
                await result.EditPlanned;
                var rg = result.Grid;
                rg.ClearSelection();
                rg.CurrentCell = rg[1, 0];
                Key(rg, Keys.Delete);
                await Until(() => result.EditRows.Find(0) is { State: EditState.Clean } r && r.Values[1] is null, "NULL saved");
                Assert.True(confirms.Count == 1 && await Db($"SELECT market_name FROM {table} WHERE id = 60") is null, "no question, NULL in the db");
                Key(rg, Keys.Control | Keys.Z);
                await Until(() => result.EditRows.Find(0) is { State: EditState.Clean } r && (string?)r.Values[1] == "northgate", "put back");
                Assert.Equal("northgate", (string?)await Db($"SELECT market_name FROM {table} WHERE id = 60"), "db after Ctrl+Z");

                rg.ClearSelection();
                rg.Rows[1].Selected = true;
                Key(rg, Keys.Delete);
                Assert.True(Info(result).Contains("Whole rows are selected") && rg.RowCount == 2 && confirms.Count == 1, Info(result));
                var items = string.Join("|", rg.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>().Select(i => i.Text));
                Assert.True(items.Contains("Set NULL|Undo"), items);

                // a read-only result explains instead
                sqlTab.Editor.Text = $"SELECT market_name FROM {table}";
                await sqlTab.RunAsync(RunScope.All);
                var ro = sqlTab.ResultTabs[0];
                await ro.EditPlanned;
                ro.Grid.CurrentCell = ro.Grid[0, 0];
                Key(ro.Grid, Keys.Delete);
                Assert.True(Info(ro).Contains("Can't set NULL"), Info(ro));
                return Info(ro);
            });
        }
        finally
        {
            Ui.ConfirmOverride = oldConfirm;
            view.Editors.First().Editor.Text = "";
            await Db($"DROP TABLE IF EXISTS {table}");
            form.CloseSession(session);
        }

        static string Info(ResultTab r) => r.Controls.OfType<ToolStrip>().First().Items.OfType<ToolStripLabel>().First().Text ?? "";
    }
}
