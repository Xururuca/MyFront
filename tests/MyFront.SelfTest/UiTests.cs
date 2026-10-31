using System.Diagnostics;
using System.Drawing.Imaging;
using System.Reflection;
using MyFront.Core.Accounts;
using MyFront.Core.Db;
using MyFront.Core.Editors;
using MyFront.Core.History;
using MyFront.UI;
using MySqlConnector;

namespace MyFront.SelfTest;

/// <summary>
/// Drives the real MainForm in-process against the test server, the way a user would
/// (grid edits go through the editing control and row changes), and saves screenshots.
/// </summary>
internal static partial class UiTests
{
    private static string ShotDir => Path.Combine(TestDb.RepoRoot, "tests", "samples", "out");
    private static readonly List<string> Messages = new();

    /// <param name="only">"current": just the tests of the work in progress (<see cref="NullUndoUi"/>).</param>
    public static void Run(TestRunner t, TestDb db, bool importOnly = false, bool quickOnly = false, string? only = null)
    {
        Directory.CreateDirectory(ShotDir);
        var thread = new Thread(() =>
        {
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency; // same as Program.Main
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetDefaultFont(new Font("Segoe UI", 9f));
            MyFront.Core.AppSettings.Current.Theme = "light"; // start from a known theme whatever an earlier run left
            MyFront.Core.AppSettings.Current.WordWrap = true;   // the default (checked offline in core)
            ThemeManager.Init();
            ThemeManager.Changed += () => Console.WriteLine($"      [theme] now {(ThemeManager.Current.IsDark ? "dark" : "light")} ← " +
                string.Join(" ← ", new StackTrace().GetFrames().Skip(1).Take(6).Select(f => f.GetMethod()?.DeclaringType?.Name + "." + f.GetMethod()?.Name)));
            Ui.MessageSink = m => { lock (Messages) Messages.Add(m); Console.WriteLine("      [ui] " + m); };
            Ui.ConfirmOverride = _ => true;

            using var quiet = QuietDesktop.Start(); // no focus stealing, the other monitor
            var form = new MainForm { ShowAccountsOnStartup = false };
            quiet?.Place(form);
            form.Shown += async (_, _) =>
            {
                try
                {
                    if (only == "current") await NullUndoUi(t, db, form);
                    else if (importOnly) await ImportOnly(t, db, form);
                    else if (quickOnly) await QuickOnly(t, db, form);
                    else await Script(t, db, form);
                }
                catch (Exception ex) { Console.WriteLine("UI script crashed: " + ex); }
                finally { form.Close(); }
            };
            Application.Run(form);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

    private static async Task Until(Func<bool> condition, string what, int timeoutMs = 20000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("timed out waiting for " + what);
            await Task.Delay(25);
        }
    }

    private static T Field<T>(object o, string name) =>
        (T)o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;

    private static object? Call(object o, string name, params object[] args)
    {
        var m = o.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)!;
        // optional parameters left out take their defaults
        var all = args.Concat(Enumerable.Repeat(Type.Missing, Math.Max(0, m.GetParameters().Length - args.Length))).ToArray();
        return m.Invoke(o, all);
    }

    private static IEnumerable<T> FindAll<T>(Control root) where T : Control
    {
        foreach (Control c in root.Controls)
        {
            if (c is T match) yield return match;
            foreach (var inner in FindAll<T>(c)) yield return inner;
        }
    }

    private static void Shot(Form f, string name)
    {
        // dialogs the script opens modeless with an owner can end up under it on screen: unowned for the capture
        var owner = GetWindowLongPtr(f.Handle, GWLP_HWNDPARENT);
        if (owner != IntPtr.Zero) SetWindowLongPtr(f.Handle, GWLP_HWNDPARENT, IntPtr.Zero);
        try
        {
            if (QuietDesktop.Enabled) QuietDesktop.Raise(f.Handle, topmost: true); // on top, focus untouched
            else
            {
                f.TopMost = true;
                f.Activate();
            }
            Application.DoEvents();
            f.Refresh();
            // the desktop composer shows the new z-order a frame or two later: capture after that
            Thread.Sleep(120);
            Application.DoEvents();
            using var bmp = CaptureWindow(f);
            bmp.Save(Path.Combine(ShotDir, name + ".png"), ImageFormat.Png);
            if (QuietDesktop.Enabled) QuietDesktop.Raise(f.Handle, topmost: false);
            else f.TopMost = false;
        }
        finally
        {
            if (owner != IntPtr.Zero) SetWindowLongPtr(f.Handle, GWLP_HWNDPARENT, owner);
        }
    }

    /// <summary>
    /// Exactly the window as drawn: its visible frame (Bounds also spans an invisible resize border, which caught slivers
    /// of whatever was behind it), with the rounded corners made transparent for the same reason.
    /// </summary>
    private static Bitmap CaptureWindow(Form f)
    {
        var b = f.Bounds;
        if (DwmGetWindowAttribute(f.Handle, DWMWA_EXTENDED_FRAME_BOUNDS, out var r, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) == 0)
            b = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        using var screen = new Bitmap(b.Width, b.Height);
        using (var g = Graphics.FromImage(screen)) g.CopyFromScreen(b.Location, Point.Empty, b.Size);
        var shot = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(shot))
        using (var path = new System.Drawing.Drawing2D.GraphicsPath())
        {
            int d = 2 * f.LogicalToDeviceUnits(8); // Windows 11 corner radius: 8 px at 100 %
            path.AddArc(0, 0, d, d, 180, 90);
            path.AddArc(b.Width - d, 0, d, d, 270, 90);
            path.AddArc(b.Width - d, b.Height - d, d, d, 0, 90);
            path.AddArc(0, b.Height - d, d, d, 90, 90);
            path.CloseFigure();
            g.SetClip(path);
            g.DrawImageUnscaled(screen, 0, 0);
        }
        return shot;
    }

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

    private const int GWLP_HWNDPARENT = -8;

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    /// <summary>Types into a grid cell through the real editing control, like a user.</summary>
    private static void TypeInto(DataGridView grid, int col, int row, string text)
    {
        grid.CurrentCell = grid[col, row];
        grid.BeginEdit(true);
        grid.EditingControl!.Text = text;
        grid.NotifyCurrentCellDirty(true);
        grid.EndEdit();
    }

    private static async Task<object?> Scalar(ServerSession s, string db, string sql)
    {
        await using var conn = await s.OpenAsync(db);
        await using var cmd = new MySqlCommand(sql, conn);
        var v = await cmd.ExecuteScalarAsync();
        return v is DBNull ? null : v;
    }

    private static async Task Script(TestRunner t, TestDb db, MainForm form)
    {
        form.WindowState = FormWindowState.Normal;
        form.Size = new Size(form.LogicalToDeviceUnits(1280), form.LogicalToDeviceUnits(800));
        NavigatorTree nav = null!; // each connection has its own tree
        ServerSession session = null!;

        await t.Check("ui: connect, tree loads databases and objects", async () =>
        {
            session = await ServerSession.ConnectAsync(db.ToAccount(TlsMode.VerifyFull), db.Password);
            await form.AddSessionAsync(session);
            nav = form.ViewOf(session)!.Navigator;
            Assert.True(form.Navigator == nav && nav.Nodes.Count == 1, "the connection's own tree, one server node");
            var server = nav.FindServer(session)!;
            var dbNode = server.Nodes.Cast<TreeNode>().First(n => n.Text == db.Database);
            await Until(() => dbNode.Nodes.Count >= 3 && NavigatorTree.Info(dbNode.Nodes[0])?.Kind != NodeKind.Loading, "tables in tree");
            await Until(() => form.ObjectBrowser.Controls.OfType<SplitContainer>().First().Panel1.Controls.OfType<ListView>().First().Items.Count >= 3, "object browser");
            Shot(form, "01-object-browser");
            return string.Join(", ", dbNode.Nodes.Cast<TreeNode>().Select(n => n.Text));
        });

        await t.Check("ui: table node shows columns and indexes", async () =>
        {
            await nav.SelectObjectAsync(session, db.Database, "prices");
            var list = form.ObjectBrowser.Controls.OfType<SplitContainer>().First().Panel1.Controls.OfType<ListView>().First();
            await Until(() => list.Items.Count == 9 && list.Columns[0].Text == "Field", "column list");
            Shot(form, "02-table-columns");
        });

        var data = form.DataBrowser;
        var grid = data.Grid;
        await t.Check("ui: Data Browser loads first page of prices", async () =>
        {
            var sw = Stopwatch.StartNew();
            form.Tabs.SelectedTab = form.DataPage;
            await Until(() => data.Table == "prices" && grid.RowCount == 1000, "1000 rows");
            var load = sw.ElapsedMilliseconds;
            grid.FirstDisplayedScrollingRowIndex = 990;
            grid.Refresh();
            Shot(form, "03-data-browser");
            return $"page of 1000 rows in {load} ms, info: {Field<ToolStripLabel>(data, "_info").Text}";
        });

        await t.Check("ui: Data Browser sort by city (server side) and paging", async () =>
        {
            var info = Field<ToolStripLabel>(data, "_info");
            await Until(() => info.Text!.Contains(" of "), "total count");
            var city = Array.FindIndex(grid.ColumnInfos, c => c.Name == "city");
            typeof(DataGridView).GetMethod("OnColumnHeaderMouseClick", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(grid, new object[] { new DataGridViewCellMouseEventArgs(city, -1, 5, 5, new MouseEventArgs(MouseButtons.Left, 1, 5, 5, 0)) });
            await Until(() => grid.Columns[city].HeaderCell.SortGlyphDirection == SortOrder.Ascending && grid.RowCount == 1000, "sorted page");
            var first = (string?)data.Source.Rows[0].Values[city];
            Call(data, "Page", 1);
            await Until(() => Field<ToolStripTextBox>(data, "_offset").Text == "1000" && !data.IsLoading
                              && (info.Text!.Contains("1.001") || info.Text!.Contains("1,001")), "page 2");
            return $"first city ascending: {first}; page 2: {info.Text}";
        });

        await t.Check("ui: Data Browser WHERE filter and quick search with Turkish text", async () =>
        {
            var filter = Field<ToolStripTextBox>(data, "_filter");
            var search = Field<ToolStripTextBox>(data, "_search");
            filter.Text = "city = 'İzmir' AND avg_price > 100";
            await data.LoadPageAsync(resetOffset: true);
            var city = Array.FindIndex(grid.ColumnInfos, c => c.Name == "city");
            Assert.True(data.Source.Count > 0 && data.Source.Rows.All(r => (string?)r.Values[city] == "İzmir"), "filter result");
            int filtered = data.Source.Count;
            filter.Text = "";
            search.Text = "şanlı";
            Field<System.Windows.Forms.Timer>(data, "_searchDelay").Stop(); // as if Enter was pressed
            await data.LoadPageAsync(resetOffset: true);
            Assert.True(data.Source.Count > 0 && data.Source.Rows.All(r => ((string?)r.Values[city])?.Contains("Şanlı") == true), "search result");
            Shot(form, "04-filter-search");
            search.Text = "";
            Field<System.Windows.Forms.Timer>(data, "_searchDelay").Stop();
            await data.LoadPageAsync(resetOffset: true);
            return $"filter → {filtered} rows on page, search 'şanlı' → {data.Source.Count} rows on page";
        });

        // ---- editing on a scratch table ---------------------------------------------------------
        const string scratch = "mf_ui_edit";
        await using (var conn = await session.OpenAsync(db.Database))
        await using (var cmd = new MySqlCommand($"""
            DROP TABLE IF EXISTS {scratch};
            CREATE TABLE {scratch} (id INT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(50) NOT NULL, price DECIMAL(10,2) NULL,
              d DATE NULL, updated_at DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP) DEFAULT CHARSET=utf8mb4;
            INSERT INTO {scratch} (name, price, d) VALUES ('elma', 10.50, '2024-01-01'), ('armut', 20, '2024-01-02'), ('kiraz', 30, NULL);
            """, conn))
            await cmd.ExecuteNonQueryAsync();
        try
        {
            await t.Check("ui: edit a cell, leave the row → UPDATE by key (Turkish text)", async () =>
            {
                await form.RefreshCurrentAsync();
                form.Tabs.SelectedTab = form.ObjectPage;
                var dbNode = await nav.SelectDatabaseAsync(session, db.Database);
                await nav.RefreshNodeAsync(dbNode!);
                await nav.SelectObjectAsync(session, db.Database, scratch);
                form.Tabs.SelectedTab = form.DataPage;
                await Until(() => data.Table == scratch && grid.RowCount == 3, "scratch rows");
                Assert.True(!grid.ReadOnly, "grid should be editable");
                TypeInto(grid, 1, 0, "Çiğ köfte İş");
                grid.CurrentCell = grid[1, 1]; // leave the row → post
                await Until(() => data.Source.Rows[0].State == EditState.Clean && data.Source.Rows[0].Values[1] as string == "Çiğ köfte İş", "row 0 posted");
                Assert.Equal("Çiğ köfte İş", (string?)await Scalar(session, db.Database, $"SELECT name FROM {scratch} WHERE id = 1"), "db value");
            });

            await t.Check("ui: Set NULL → UPDATE ... = NULL", async () =>
            {
                grid.CurrentCell = grid[2, 1];
                Call(data, "SetSelectedNull");
                grid.CurrentCell = grid[1, 2];
                await Until(() => data.Source.Rows[1].State == EditState.Clean, "row 1 posted");
                Assert.True(await Scalar(session, db.Database, $"SELECT price FROM {scratch} WHERE id = 2") is null, "price is NULL");
            });

            await t.Check("ui: add row → INSERT, auto-increment id and defaults read back", async () =>
            {
                Call(data, "AddRow");
                grid.EditingControl!.Text = "Yeni ürün";
                grid.NotifyCurrentCellDirty(true);
                grid.EndEdit();
                TypeInto(grid, 2, 3, "12.5");
                TypeInto(grid, 3, 3, "2025-12-31");
                grid.CurrentCell = grid[1, 0];
                await Until(() => data.Source.Rows[3].State == EditState.Clean, "new row posted");
                var row = data.Source.Rows[3].Values;
                Assert.Equal(4, Convert.ToInt32(row[0]), "auto-increment id");
                Assert.True(row[4] is DateTime, "updated_at default read back");
                Assert.Equal(12.50m, Convert.ToDecimal(await Scalar(session, db.Database, $"SELECT price FROM {scratch} WHERE name = 'Yeni ürün'")), "inserted price");
                Shot(form, "05-data-edit");
            });

            await t.Check("ui: constraint error keeps the row marked, revert restores it", async () =>
            {
                lock (Messages) Messages.Clear();
                grid.CurrentCell = grid[1, 2];
                Call(data, "SetSelectedNull");        // name is NOT NULL
                grid.CurrentCell = grid[1, 0];
                await Until(() => data.Source.Rows[2].State == EditState.Error, "error state");
                Assert.True(data.Source.Rows[2].Error!.Contains("cannot be null", StringComparison.OrdinalIgnoreCase), "error text: " + data.Source.Rows[2].Error);
                grid.CurrentCell = grid[1, 2];
                Call(data, "RevertCurrent");
                Assert.Equal("kiraz", data.Source.Rows[2].Values[1] as string, "reverted");
                Assert.True(data.Source.Rows[2].State == EditState.Clean, "clean after revert");
                return data.Source.Rows[2].Error ?? "(error cleared)";
            });

            await t.Check("ui: delete selected row → DELETE by key", async () =>
            {
                grid.CurrentCell = grid[0, 3];
                await data.DeleteSelectedAsync();
                Assert.Equal(3, grid.RowCount, "grid rows");
                Assert.Equal(3L, Convert.ToInt64(await Scalar(session, db.Database, $"SELECT COUNT(*) FROM {scratch}")), "db rows");
            });

            await t.Check("ui: Delete key on whole rows (row headers) → one confirm, DELETE by key in one transaction; on cells it sets NULL without asking, Ctrl+Z puts them back", async () =>
            {
                await Scalar(session, db.Database, $"INSERT INTO {scratch} (name, price) VALUES ('silinecek 1', 1), ('silinecek 2', 2), ('silinecek 3', 3), ('silinecek 4', 4)");
                await data.RefreshAsync();
                await Until(() => !data.IsLoading && grid.RowCount == 7, "7 rows");
                var confirms = new List<string>();
                var old = Ui.ConfirmOverride;
                Ui.ConfirmOverride = text => { confirms.Add(text); return true; };
                try
                {
                    // two whole rows picked by their row headers
                    grid.ClearSelection();
                    grid.CurrentCell = grid[1, 3];
                    grid.Rows[3].Selected = true;
                    grid.Rows[4].Selected = true;
                    Call(grid, "OnKeyDown", new KeyEventArgs(Keys.Delete));
                    await Until(() => grid.RowCount == 5, "two rows gone from the grid");
                    Assert.True(confirms.Count == 1 && confirms[0].StartsWith($"Delete 2 rows from {db.Database}.{scratch}?") && confirms[0].Contains("DELETE FROM"),
                        "one confirmation with the SQL: " + string.Join(" | ", confirms));
                    Assert.Equal(5L, Convert.ToInt64(await Scalar(session, db.Database, $"SELECT COUNT(*) FROM {scratch}")), "db rows after deleting two");
                    Assert.Equal(0L, Convert.ToInt64(await Scalar(session, db.Database, $"SELECT COUNT(*) FROM {scratch} WHERE name IN ('silinecek 1', 'silinecek 2')")), "the selected ones");
                    // cells in two different rows, no row header: NULL, no question, saved; the rows stay
                    async Task<string> Prices() => Convert.ToString(await Scalar(session, db.Database,
                        $"SELECT GROUP_CONCAT(IFNULL(price, 'NULL') ORDER BY id) FROM {scratch} WHERE name IN ('silinecek 3', 'silinecek 4')"))!;
                    bool Saved(int r) => data.Source.Rows[r].State == EditState.Clean;
                    grid.ClearSelection();
                    grid.CurrentCell = grid[2, 3];
                    grid[2, 3].Selected = true;
                    grid[2, 4].Selected = true;
                    Call(grid, "OnKeyDown", new KeyEventArgs(Keys.Delete));
                    await Until(() => Saved(3) && Saved(4) && data.Source.Rows[3].Values[2] is null && data.Source.Rows[4].Values[2] is null, "NULL saved");
                    Assert.True(confirms.Count == 1 && grid.RowCount == 5, "no question, no row deleted");
                    Assert.Equal("NULL,NULL", await Prices(), "db: NULL");
                    Assert.True(data.StatusTime?.StartsWith("Set 2 cells to NULL in 2 rows") == true, data.StatusTime ?? "");
                    Call(grid, "OnKeyDown", new KeyEventArgs(Keys.Control | Keys.Z));
                    await Until(() => Saved(3) && Saved(4) && data.Source.Rows[3].Values[2] is not null && data.Source.Rows[4].Values[2] is not null, "put back");
                    Assert.Equal("3.00,4.00", await Prices(), "db: back after Ctrl+Z");
                    Assert.True(data.StatusTime?.StartsWith("Undone: 2 values put back") == true, data.StatusTime ?? "");
                    // and those two rows by their headers
                    grid.ClearSelection();
                    grid.Rows[3].Selected = true;
                    grid.Rows[4].Selected = true;
                    Call(grid, "OnKeyDown", new KeyEventArgs(Keys.Delete));
                    await Until(() => grid.RowCount == 3, "the two rows gone");
                    Assert.True(confirms.Count == 2 && confirms[1].StartsWith("Delete 2 rows"), "second confirmation: " + confirms.LastOrDefault());
                    Assert.Equal(3L, Convert.ToInt64(await Scalar(session, db.Database, $"SELECT COUNT(*) FROM {scratch}")), "db rows after deleting two more");
                    // while a cell is being edited Delete edits the text, it never deletes rows
                    grid.CurrentCell = grid[1, 0];
                    grid.BeginEdit(true);
                    Call(grid, "OnKeyDown", new KeyEventArgs(Keys.Delete));
                    grid.CancelEdit();
                    grid.EndEdit();
                    await Task.Delay(200);
                    Assert.True(confirms.Count == 2 && grid.RowCount == 3, "no delete while editing");
                }
                finally { Ui.ConfirmOverride = old; }
                return confirms[0].Split('\n')[0];
            });

            await t.Check("ui: view opens read-only (and can't delete rows)", async () =>
            {
                await nav.SelectObjectAsync(session, db.Database, "v_latest_prices");
                await Until(() => data.Table == "v_latest_prices" && !data.IsLoading && grid.RowCount > 0, "view rows", 30000);
                Assert.True(grid.ReadOnly, "view must be read-only");
                var menu = grid.ContextMenuStrip!;
                typeof(ToolStripDropDown).GetMethod("OnOpening", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(menu, new object[] { new System.ComponentModel.CancelEventArgs() });
                Assert.True(!menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "Delete selected rows…").Enabled, "menu item disabled");
                var confirms = 0;
                var old = Ui.ConfirmOverride;
                Ui.ConfirmOverride = _ => { confirms++; return true; };
                try
                {
                    grid.CurrentCell = grid[0, 0];
                    Call(grid, "OnKeyDown", new KeyEventArgs(Keys.Delete));
                    await Task.Delay(200);
                }
                finally { Ui.ConfirmOverride = old; }
                Assert.True(confirms == 0 && data.StatusTime?.StartsWith("Can't set NULL") == true, "Delete key explains instead: " + data.StatusTime);
                return $"{grid.RowCount} rows, {Field<ToolStripLabel>(data, "_info").Text}";
            });
        }
        finally
        {
            await using var conn = await session.OpenAsync(db.Database);
            await using var cmd = new MySqlCommand($"DROP TABLE IF EXISTS {scratch}", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        await KeylessUi(t, db, form, session);

        // ---- SQL editor ---------------------------------------------------------------------------
        var sqlTab = form.Tabs.TabPages.OfType<SqlEditorTab>().First();
        await nav.SelectDatabaseAsync(session, db.Database);

        await t.Check("ui: SQL editor runs 2 statements → 2 result tabs, 200k rows, UI stays responsive", async () =>
        {
            var paint = Stopwatch.StartNew();
            form.Tabs.SelectedTab = sqlTab;
            sqlTab.Editor.Text = "SELECT * FROM prices;\nSELECT city, COUNT(*) AS n, ROUND(AVG(avg_price), 2) AS avg FROM prices GROUP BY city ORDER BY n DESC;";
            form.Refresh();
            var firstShow = paint.ElapsedMilliseconds;
            await Task.Delay(500);
            var runs = new List<string> { $"first show of SQL tab {firstShow} ms" };
            long maxGap = 0, total = 0;
            for (int run = 0; run < 2; run++)
            {
                long gapMax = 0;
                var gaps = new List<string>();
                var last = Stopwatch.StartNew();
                int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
                using var probe = new System.Windows.Forms.Timer { Interval = 10 };
                probe.Tick += (_, _) =>
                {
                    var gap = last.ElapsedMilliseconds;
                    if (gap > 60)
                        gaps.Add($"{gap}ms(gc {GC.CollectionCount(0) - g0}/{GC.CollectionCount(1) - g1}/{GC.CollectionCount(2) - g2})");
                    g0 = GC.CollectionCount(0); g1 = GC.CollectionCount(1); g2 = GC.CollectionCount(2);
                    gapMax = Math.Max(gapMax, gap);
                    last.Restart();
                };
                probe.Start();
                var swRun = Stopwatch.StartNew();
                await sqlTab.RunAsync(RunScope.All);
                total = swRun.ElapsedMilliseconds;
                probe.Stop();
                maxGap = gapMax;
                runs.Add($"run {run + 1}: {total} ms, max stall {gapMax} ms [{string.Join(" ", gaps)}]");
            }
            var sw = Stopwatch.StartNew();
            Assert.Equal(2, sqlTab.ResultTabs.Count, "result tabs");
            var r1 = sqlTab.ResultTabs[0];
            Assert.Equal(200_000, r1.Data.Rows.Count, "rows");
            Assert.Equal(200_000, r1.Grid.RowCount, "grid rows");
            sw.Restart();
            r1.Grid.FirstDisplayedScrollingRowIndex = 199_990;
            r1.Grid.Refresh();
            var scroll = sw.ElapsedMilliseconds;
            Shot(form, "06-sql-editor");
            return $"{string.Join(" | ", runs)} | jump to row 199,990 + repaint {scroll} ms, tab: {r1.Text}";
        });

        await t.Check("ui: client-side sort of 200k rows from the column header", async () =>
        {
            var r1 = sqlTab.ResultTabs[0];
            var sw = Stopwatch.StartNew();
            typeof(DataGridView).GetMethod("OnColumnHeaderMouseClick", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(r1.Grid, new object[] { new DataGridViewCellMouseEventArgs(3, -1, 5, 5, new MouseEventArgs(MouseButtons.Left, 1, 5, 5, 0)) });
            await Until(() => r1.Grid.Columns[3].HeaderCell.SortGlyphDirection == SortOrder.Ascending, "sorted");
            return $"{sw.ElapsedMilliseconds} ms, first: {r1.Data.Rows.GetRow(0)[3]}, last: {r1.Data.Rows.GetRow(r1.Data.Rows.Count - 1)[3]}";
        });

        await t.Check("ui: dark mode toggle re-themes every screen and is remembered", async () =>
        {
            form.Tabs.SelectedTab = sqlTab;
            form.ToggleTheme();
            Assert.True(form.IsDarkTheme, "dark after toggle");
            Assert.True(File.ReadAllText(MyFront.Core.AppPaths.File("settings.json")).Contains("\"dark\""), "settings.json says dark");
            Assert.Equal(Theme.Dark.Window, sqlTab.ResultTabs[0].Grid.BackgroundColor, "result grid background");
            Assert.Equal(Theme.Dark.Window, nav.BackColor, "navigator background");
            await Task.Delay(150);
            Shot(form, "20-dark-sql-editor");
            form.Tabs.SelectedTab = form.DataPage;
            await Until(() => !data.IsLoading, "data page");
            await Task.Delay(150);
            Shot(form, "21-dark-data-browser");
            form.Tabs.SelectedTab = form.ObjectPage;
            await nav.SelectDatabaseAsync(session, db.Database);
            await Task.Delay(400);
            Shot(form, "22-dark-object-browser");
            using (var edit = new MyFront.UI.Dialogs.AccountEditForm(db.ToAccount(TlsMode.VerifyFull), Array.Empty<string>(), isNew: false))
            {
                edit.Show(form);
                await Task.Delay(150);
                Shot(edit, "23-dark-dialog");
                FindAll<TabControl>(edit).Single().SelectedIndex = 1;
                Field<CheckBox>(edit, "_sshOn").Checked = true;
                await Task.Delay(150);
                Shot(edit, "23b-dark-dialog-ssh");
                edit.Close();
            }
            form.ToggleTheme();
            Assert.True(!form.IsDarkTheme, "light after second toggle");
            Assert.Equal(SystemColors.Window, sqlTab.ResultTabs[0].Grid.BackgroundColor, "light grid background");
            form.Tabs.SelectedTab = sqlTab;
            await Task.Delay(150);
            Shot(form, "24-light-again");
        });

        await t.Check("ui: run current statement (caret in the 2nd statement)", async () =>
        {
            sqlTab.Editor.CurrentPosition = sqlTab.Editor.Text.IndexOf("GROUP BY", StringComparison.Ordinal);
            sqlTab.Editor.SetEmptySelection(sqlTab.Editor.CurrentPosition);
            await sqlTab.RunAsync(RunScope.Current);
            Assert.Equal(1, sqlTab.ResultTabs.Count, "one result");
            Assert.True(sqlTab.ResultTabs[0].Data.Columns[0].Name == "city", "the GROUP BY query ran");
            return $"{sqlTab.ResultTabs[0].Data.Rows.Count} cities";
        });

        await t.Check("ui: Run (F9) runs the selection, or everything without one; Run all ignores the selection; filled ▶ icon", async () =>
        {
            form.Tabs.SelectedTab = sqlTab;
            sqlTab.Editor.Text = "SELECT 1 AS a;\nSELECT 2 AS b;";
            int second = sqlTab.Editor.Text.IndexOf("SELECT 2", StringComparison.Ordinal);
            sqlTab.Editor.SetSelection(sqlTab.Editor.TextLength, second);
            Assert.True(PressKeys(form, Keys.F9), "F9 handled");
            await Until(() => !sqlTab.IsRunning && sqlTab.ResultTabs.Count == 1 && sqlTab.ResultTabs[0].Data.Columns[0].Name == "b", "only the selection ran");
            sqlTab.Editor.SetEmptySelection(0);
            Assert.True(PressKeys(form, Keys.F9), "F9 handled");
            await Until(() => !sqlTab.IsRunning && sqlTab.ResultTabs.Count == 2, "no selection → all statements");
            sqlTab.Editor.SetSelection(sqlTab.Editor.TextLength, second);
            Field<ToolStripButton>(form, "_tbRunAll").PerformClick(); // Run all is in the main toolbar now
            await Until(() => !sqlTab.IsRunning && sqlTab.ResultTabs.Count == 2 && sqlTab.ResultTabs[0].Data.Columns[0].Name == "a", "Run all with a selection");
            sqlTab.Editor.SetSelection(sqlTab.Editor.TextLength, second);
            Field<ToolStripButton>(sqlTab, "_run").PerformClick();
            await Until(() => !sqlTab.IsRunning && sqlTab.ResultTabs.Count == 1, "Run button = selection");
            var icon = (Bitmap)Field<ToolStripButton>(sqlTab, "_run").Image!;
            Assert.True(icon.GetPixel(icon.Width * 7 / 16, icon.Height / 2).A == 255, "the ▶ is filled, not an outline");
            await Task.Delay(100);
            Shot(form, "06b-run-button");
        });

        await t.Check("ui: View → Word wrap: on, saved, reaches every editor, new tabs and the Properties source", async () =>
        {
            var item = form.MainMenuStrip!.Items.OfType<ToolStripMenuItem>().First(i => i.Text == "&View")
                .DropDownItems.OfType<ToolStripMenuItem>().First(i => i.Text == "&Word wrap");
            var editors = form.EditorTabsOf(session).Select(x => x.Editor).ToList();
            Assert.True(item.Checked && editors.All(e => e.WrapMode == ScintillaNET.WrapMode.Word && !e.HScrollBar), "wrapping, no horizontal scrollbar");
            using var props = new MyFront.UI.Dialogs.TablePropertiesForm(session, db.Database, "products");
            props.Show(form);
            await props.Loaded;
            var source = FindAll<SqlEditor>(props).Single();
            Assert.True(source.WrapMode == ScintillaNET.WrapMode.Word, "Properties source wraps");
            item.PerformClick();
            try
            {
                Assert.True(!item.Checked && editors.All(e => e.WrapMode == ScintillaNET.WrapMode.None && e.HScrollBar), "off in every editor");
                Assert.True(source.WrapMode == ScintillaNET.WrapMode.None, "off in the open Properties dialog");
                Assert.True(File.ReadAllText(MyFront.Core.AppPaths.File("settings.json")).Contains("\"WordWrap\": false"), "saved");
                var extra = form.NewSqlTab(session)!;
                Assert.True(extra.Editor.WrapMode == ScintillaNET.WrapMode.None, "a new tab follows the setting");
                Call(form, "CloseSqlTab", extra, false);
                form.ShowSqlTab(sqlTab);
            }
            finally { if (!item.Checked) item.PerformClick(); }
            Assert.True(editors.All(e => e.WrapMode == ScintillaNET.WrapMode.Word), "on again");
            props.Close();
        });

        await t.Check("ui: syntax error → Messages tab, error marked in the editor", async () =>
        {
            sqlTab.Editor.Text = "SELECT 1;\nSELEC 2;\nSELECT 3;";
            await sqlTab.RunAsync(RunScope.All);
            var msgs = sqlTab.Messages.Items.Cast<ListViewItem>().ToList();
            Assert.Equal(2, msgs.Count, "stopped after the error");
            Assert.True(msgs[1].SubItems[2].Text.Contains("1064"), "error 1064: " + msgs[1].SubItems[2].Text);
            Assert.Equal("SELEC 2", sqlTab.Editor.SelectedText, "error statement selected");
            Shot(form, "07-sql-error");
            return msgs[1].SubItems[2].Text;
        });

        await t.Check("ui: Stop cancels a running query (KILL QUERY) and the tab stays usable", async () =>
        {
            sqlTab.Editor.Text = "SELECT SLEEP(20)";
            var sw = Stopwatch.StartNew();
            var run = sqlTab.RunAsync(RunScope.All);
            await Until(() => sqlTab.IsRunning, "running");
            await Task.Delay(700);
            sqlTab.Stop();
            await run;
            var took = sw.ElapsedMilliseconds;
            Assert.True(took < 4000, $"took {took} ms");
            Assert.True(sqlTab.Messages.Items[0].SubItems[2].Text == "Cancelled", "message: " + sqlTab.Messages.Items[0].SubItems[2].Text);
            sqlTab.Editor.Text = "SELECT 'still alive'";
            await sqlTab.RunAsync(RunScope.All);
            Assert.Equal("still alive", sqlTab.ResultTabs[0].Data.Rows.GetRow(0)[0] as string, "next run");
            return $"cancelled after {took} ms";
        });

        // the next two USE another database: the tree shows all of them meanwhile (with its database set, only that one)
        await form.ViewOf(session)!.SetDefaultDatabaseAsync(null);
        await t.Check("ui: USE switches the tree, DDL refreshes it", async () =>
        {
            sqlTab.Editor.Text = "CREATE TABLE mf_ui_ddl (id INT PRIMARY KEY);";
            await sqlTab.RunAsync(RunScope.All);
            var dbNode = nav.FindServer(session)!.Nodes.Cast<TreeNode>().First(n => n.Text == db.Database);
            await Until(() => dbNode.Nodes.Cast<TreeNode>().Any(n => n.Text == "mf_ui_ddl"), "new table in tree");
            sqlTab.Editor.Text = "DROP TABLE mf_ui_ddl;\nUSE information_schema;\nSELECT DATABASE();";
            await sqlTab.RunAsync(RunScope.All);
            await Until(() => nav.SelectedInfo?.Database == "information_schema", "tree follows USE");
            Assert.Equal("information_schema", sqlTab.ResultTabs[0].Data.Rows.GetRow(0)[0] as string, "DATABASE()");
            await nav.SelectDatabaseAsync(session, db.Database);
            sqlTab.Editor.Text = "SELECT DATABASE()";
            await sqlTab.RunAsync(RunScope.All);
            Assert.True(sqlTab.ResultTabs.Count == 1, "no result tab; messages: " +
                string.Join(" | ", sqlTab.Messages.Items.Cast<ListViewItem>().Select(i => i.SubItems[1].Text + " → " + i.SubItems[2].Text)) +
                $"; tree: {nav.SelectedInfo?.Kind} {nav.SelectedInfo?.Database}");
            Assert.Equal(db.Database, sqlTab.ResultTabs[0].Data.Rows.GetRow(0)[0] as string, "editor follows the tree");
        });

        await t.Check("ui: clicking a table in the tree while an SQL editor is in front shows it in the Object Browser; code-made selections don't", async () =>
        {
            form.Tabs.SelectedTab = sqlTab;
            var products = (await nav.SelectObjectAsync(session, db.Database, "products"))!; // programmatic, like USE or a quick query
            Assert.True(form.Tabs.SelectedTab == sqlTab, "programmatic selection keeps the editor in front");
            Call(nav, "OnAfterSelect", new TreeViewEventArgs(products, TreeViewAction.ByMouse)); // what a click raises
            Assert.True(form.Tabs.SelectedTab == form.ObjectPage, "click → Object Browser");
            var list = form.ObjectBrowser.Controls.OfType<SplitContainer>().First().Panel1.Controls.OfType<ListView>().First();
            await Until(() => list.Columns.Count > 0 && list.Columns[0].Text == "Field" && list.Items.Count > 0, "fields of products");

            form.Tabs.SelectedTab = sqlTab;
            var r = products.Bounds;
            Call(nav, "OnNodeMouseClick", new TreeNodeMouseClickEventArgs(products, MouseButtons.Left, 1, r.X + r.Width / 2, r.Y + r.Height / 2));
            Assert.True(form.Tabs.SelectedTab == form.ObjectPage, "click on the table that was already selected → Object Browser");

            form.Tabs.SelectedTab = sqlTab;
            var prices = (await nav.SelectObjectAsync(session, db.Database, "prices"))!;
            Call(nav, "OnAfterSelect", new TreeViewEventArgs(prices, TreeViewAction.ByKeyboard));
            Assert.True(form.Tabs.SelectedTab == form.ObjectPage, "arrow keys → Object Browser");

            form.Tabs.SelectedTab = form.DataPage;
            products = (await nav.SelectObjectAsync(session, db.Database, "products"))!;
            Call(nav, "OnAfterSelect", new TreeViewEventArgs(products, TreeViewAction.ByMouse));
            await Until(() => form.DataBrowser.Table == "products" && !form.DataBrowser.IsLoading, "products in the Data Browser");
            Assert.True(form.Tabs.SelectedTab == form.DataPage, "the Data Browser stays and shows the clicked table");

            form.Tabs.SelectedTab = sqlTab;
            sqlTab.Editor.Text = "USE information_schema";
            await sqlTab.RunAsync(RunScope.All);
            await Until(() => nav.SelectedInfo?.Database == "information_schema", "tree follows USE");
            Assert.True(form.Tabs.SelectedTab == sqlTab, "USE moves the tree, not the tab");
            await nav.SelectDatabaseAsync(session, db.Database);
        });
        await form.ViewOf(session)!.SetDefaultDatabaseAsync(db.Database);

        await ResultEditUi(t, db, form, session);
        await PasteUi(t, db, form, session);

        await t.Check("ui: Processes and Variables views", async () =>
        {
            var server = nav.FindServer(session)!;
            nav.SelectedNode = server.Nodes.Cast<TreeNode>().First(n => n.Text == "Processes");
            var host = form.ObjectPage.Controls[0];
            await Until(() => host.Controls.OfType<ProcessesView>().FirstOrDefault()?.Controls.OfType<ResultGrid>().First().RowCount > 0, "processes");
            Shot(form, "08-processes");
            int processes = host.Controls.OfType<ProcessesView>().First().Controls.OfType<ResultGrid>().First().RowCount;
            nav.SelectedNode = server.Nodes.Cast<TreeNode>().First(n => n.Text == "Variables");
            await Until(() => host.Controls.OfType<VariablesView>().FirstOrDefault()?.Controls.OfType<ResultGrid>().First().RowCount > 300, "variables");
            int vars = host.Controls.OfType<VariablesView>().First().Controls.OfType<ResultGrid>().First().RowCount;
            return $"{processes} process(es), {vars} variables";
        });

        await t.Check("ui: read-only connection — grid not editable, editor blocks UPDATE", async () =>
        {
            var ro = await ServerSession.ConnectAsync(db.ToAccount(TlsMode.Required, readOnly: true), db.Password);
            await form.AddSessionAsync(ro);
            var roView = form.ViewOf(ro)!;
            Assert.True(form.ActiveView == roView, "the new connection's tab is in front");
            var roData = roView.DataBrowser;
            await roView.Navigator.SelectObjectAsync(ro, db.Database, "products");
            form.Tabs.SelectedTab = form.DataPage;
            await Until(() => roData.Session == ro && roData.Table == "products" && !roData.IsLoading && roData.Grid.RowCount == 50, "products rows");
            Assert.True(roData.Grid.ReadOnly, "read-only grid");
            var roTab = form.Tabs.TabPages.OfType<SqlEditorTab>().First(x => x.Session == ro);
            form.Tabs.SelectedTab = roTab;
            roTab.Editor.Text = "UPDATE products SET name = name WHERE id = 1";
            await roTab.RunAsync(RunScope.All);
            var msg = roTab.Messages.Items[0].SubItems[2].Text;
            Assert.True(msg.Contains("read-only"), msg);
            roTab.Editor.Text = "SELECT * FROM products";
            await roTab.RunAsync(RunScope.All);
            await roTab.ResultTabs[0].EditPlanned;
            Assert.True(roTab.ResultTabs[0].Grid.ReadOnly && roTab.ResultTabs[0].EditPlan?.ReadOnlyReason == "read-only connection",
                "result grid of a read-only connection: " + roTab.ResultTabs[0].EditPlan?.ReadOnlyReason);
            var st = Field<ToolStripStatusLabel>(form, "_stConn");
            Assert.True(st.Text?.Contains("READ-ONLY") == true && st.ForeColor == ThemeManager.Current.Error, "red READ-ONLY status: " + st.Text);
            lock (Messages) Messages.Clear();
            Call(roView, "EditField", new NodeInfo(NodeKind.Table, ro, db.Database, "products"), "name");
            Assert.True(Messages.Any(m => m.Contains("read-only connection")), "Edit field blocked: " + string.Join(" | ", Messages));
            lock (Messages) Messages.Clear();
            Call(roView, "AddKey", new NodeInfo(NodeKind.Table, ro, db.Database, "products"));
            Assert.True(Messages.Any(m => m.Contains("read-only connection")), "Add key blocked: " + string.Join(" | ", Messages));
            lock (Messages) Messages.Clear();
            Call(roView, "EditKey", new NodeInfo(NodeKind.Table, ro, db.Database, "products"), "PRIMARY");
            Assert.True(Messages.Any(m => m.Contains("read-only connection")), "Edit key blocked: " + string.Join(" | ", Messages));
            Shot(form, "09-read-only");
            form.CloseSession(ro);
            Assert.Equal(1, form.Sessions.Count, "sessions after close");
            Assert.True(form.ActiveView?.Session == session, "back on the first connection's tab");
            return msg;
        });

        await t.Check("ui: SQL history recorded (and passwords masked)", () =>
        {
            var h = SqlHistory.Read(session.Account.Id);
            Assert.True(h.Count >= 10, "entries: " + h.Count);
            Assert.True(h.Any(e => e.Sql.Contains("SLEEP(20)") && e.Info.Contains("cancelled")), "cancelled query logged");
            return Task.FromResult<string?>($"{h.Count} entries, newest: {h[0].Info}");
        });

        await Dialogs(t, db, form, session);
        await PropertiesUi(t, db, form, session);
        await StructureUi(t, db, form, session);
        await KeyEditUi(t, db, form, session);
        await TreeChildrenUi(t, db, form, session);
        await DragUi(t, db, form, session);
        await TreeCommandsUi(t, db, form, session);
        await EditorTabsUi(t, db, form, session);
        await ExportUi(t, db, form, session);
        await ImportUi(t, db, form, session);
        await QuickUi(t, db, form, session);
        form.CloseSession(session);
        await ToolbarUi(t, db, form);
        await ConnectionScopeUi(t, db, form);
        await FormatAndTransferUi(t, db, form);
        await HiddenTablesUi(t, db, form);
        await NullUndoUi(t, db, form);
        await AltRowsUi(t, db, form);
        await StatusPerConnectionUi(t, db, form);
    }

    /// <summary>Result grids of the SQL editor: editable when the columns come from one table with its key (like the Data Browser).</summary>
    private static async Task ResultEditUi(TestRunner t, TestDb db, MainForm form, ServerSession session)
    {
        const string table = "mf_ui_redit";
        await Scalar(session, db.Database, $"""
            DROP TABLE IF EXISTS {table};
            CREATE TABLE {table} (id INT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(50) NOT NULL, price DECIMAL(10,2) NULL, d DATE NULL) DEFAULT CHARSET=utf8mb4;
            INSERT INTO {table} (name, price, d) VALUES ('elma', 10.50, '2024-01-01'), ('armut', 20, '2024-01-02'), ('kiraz', 30, NULL);
            """);
        var view = form.ViewOf(session)!;
        var sqlTab = view.Editors.First();
        async Task<ResultTab> Run(string sql)
        {
            form.Tabs.SelectedTab = sqlTab;
            sqlTab.Editor.Text = sql;
            await sqlTab.RunAsync(RunScope.All);
            var r = sqlTab.ResultTabs[0];
            await r.EditPlanned;
            return r;
        }
        string Info(ResultTab r) => r.Controls.OfType<ToolStrip>().First().Items.OfType<ToolStripLabel>().First().Text ?? "";
        Task<object?> Db(string sql) => Scalar(session, db.Database, sql);
        try
        {
            ResultTab result = null!;
            await t.Check("ui: SELECT with aliases and an expression → result grid editable by its key, the expression read-only", async () =>
            {
                result = await Run($"SELECT id AS kimlik, name AS ad, price, d, CONCAT(name, '!') AS etiket FROM {table} ORDER BY id");
                var grid = result.Grid;
                Assert.True(result.EditPlan!.Editable && !grid.ReadOnly, "editable: " + result.EditPlan.ReadOnlyReason);
                Assert.True(Info(result).Contains("editable · key: kimlik"), Info(result));
                Assert.True(result.Editor.IsEditableColumn(1) && !result.Editor.IsEditableColumn(4), "ad editable, etiket not");
                grid.CurrentCell = grid[4, 0];
                grid.BeginEdit(true);
                Assert.True(!grid.IsCurrentCellInEditMode, "typing into the expression column doesn't start an edit");
                return Info(result);
            });

            await t.Check("ui: result grid — type, leave the row → UPDATE by key (Turkish text), value read back into the grid", async () =>
            {
                var grid = result.Grid;
                TypeInto(grid, 1, 0, "Çiğ köfte İş");
                TypeInto(grid, 2, 0, "11.5");
                Assert.True(result.EditRows.Find(0)?.State == EditState.Dirty, "marked while not saved");
                grid.CurrentCell = grid[1, 1]; // leave the row → save
                await Until(() => result.EditRows.Find(0)?.State == EditState.Clean, "row 0 saved");
                Assert.Equal("Çiğ köfte İş", (string?)await Db($"SELECT name FROM {table} WHERE id = 1"), "db value");
                Assert.Equal(11.50m, result.Data.Rows.GetRow(0)[2], "the row store has the stored DECIMAL");
                Assert.True(Info(result).Contains("saved a row of " + table), Info(result));
            });

            await t.Check("ui: result grid — Enter on the last row saves it; Esc reverts; Ctrl+0 sets NULL", async () =>
            {
                var grid = result.Grid;
                int last = grid.RowCount - 1;
                TypeInto(grid, 1, last, "son satır");
                Call(grid, "ProcessDialogKey", Keys.Enter);
                await Until(() => result.EditRows.Find(last)?.State == EditState.Clean, "last row saved by Enter");
                Assert.Equal("son satır", (string?)await Db($"SELECT name FROM {table} WHERE id = 3"), "db value after Enter");

                TypeInto(grid, 1, 1, "geri alınacak");
                grid.CurrentCell = grid[1, 1];
                Call(grid, "OnKeyDown", new KeyEventArgs(Keys.Escape));
                Assert.Equal("armut", (string?)result.Data.Rows.GetRow(1)[1], "reverted in the grid");
                Assert.True(result.EditRows.Find(1)?.State == EditState.Clean, "clean after Esc");

                grid.CurrentCell = grid[2, 1];
                Call(grid, "OnKeyDown", new KeyEventArgs(Keys.Control | Keys.D0));
                grid.CurrentCell = grid[1, 0];
                await Until(() => result.EditRows.Find(1)?.State == EditState.Clean, "NULL saved");
                Assert.True(await Db($"SELECT price FROM {table} WHERE id = 2") is null, "price is NULL");
                Assert.Equal("armut", (string?)await Db($"SELECT name FROM {table} WHERE id = 2"), "the reverted name was never written");
            });

            await t.Check("ui: result grid — a server error keeps the row marked with the error; Esc puts it back", async () =>
            {
                lock (Messages) Messages.Clear();
                var grid = result.Grid;
                grid.CurrentCell = grid[1, 1];
                Call(grid, "OnKeyDown", new KeyEventArgs(Keys.Control | Keys.D0)); // name is NOT NULL
                grid.CurrentCell = grid[1, 0];
                await Until(() => result.EditRows.Find(1)?.State == EditState.Error, "error state");
                var error = result.EditRows.Find(1)!.Error ?? "";
                Assert.True(error.Contains("cannot be null", StringComparison.OrdinalIgnoreCase), "error: " + error);
                Assert.True(Messages.Any(m => m.StartsWith("error:")), "error shown");
                await Task.Delay(100);
                Shot(form, "68-result-edit");
                form.ToggleTheme();
                try
                {
                    await Task.Delay(150);
                    Shot(form, "69-dark-result-edit");
                }
                finally { form.ToggleTheme(); }
                grid.CurrentCell = grid[1, 1];
                Call(grid, "OnKeyDown", new KeyEventArgs(Keys.Escape));
                Assert.True(result.EditRows.Find(1)?.State == EditState.Clean && (string?)result.Data.Rows.GetRow(1)[1] == "armut", "reverted");
                return error;
            });

            await t.Check("ui: result grid — client-side sort keeps edits with their rows", async () =>
            {
                var grid = result.Grid;
                TypeInto(grid, 1, 0, "zeytin");
                typeof(DataGridView).GetMethod("OnColumnHeaderMouseClick", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(grid, new object[] { new DataGridViewCellMouseEventArgs(1, -1, 5, 5, new MouseEventArgs(MouseButtons.Left, 1, 5, 5, 0)) });
                await Until(() => grid.Columns[1].HeaderCell.SortGlyphDirection == SortOrder.Ascending, "sorted");
                Assert.Equal("zeytin", (string?)await Db($"SELECT name FROM {table} WHERE id = 1"), "saved before the sort");
                Assert.Equal("zeytin", (string?)result.Data.Rows.GetRow(grid.RowCount - 1)[1], "the edited row sorted last");
            });

            await t.Check("ui: result grid read-only with the reason — two uses of a table, no key in the result, UNION", async () =>
            {
                var notes = new List<string>();
                foreach (var (sql, reason) in new[]
                {
                    ($"SELECT a.id, b.name FROM {table} a JOIN {table} b ON b.id = a.id", "columns from several tables"),
                    ($"SELECT name, price FROM {table}", "no key in the result (add id)"),
                    ($"SELECT id, name FROM {table} UNION SELECT id, name FROM {table}", "UNION"),
                    ("SELECT 1 AS bir, NOW() AS simdi", "no table columns"),
                })
                {
                    var r = await Run(sql);
                    Assert.True(r.Grid.ReadOnly && Info(r).Contains(reason), sql + " → " + Info(r));
                    notes.Add(Info(r)[(Info(r).IndexOf("read-only", StringComparison.Ordinal))..]);
                }
                await Task.Delay(100);
                Shot(form, "70-result-read-only");
                return string.Join(" | ", notes);
            });

            await t.Check("ui: running again with an unsaved result row asks first", async () =>
            {
                var r = await Run($"SELECT * FROM {table} ORDER BY id");
                TypeInto(r.Grid, 1, 0, "kaydedilmedi");
                var confirms = new List<string>();
                var old = Ui.ConfirmOverride;
                Ui.ConfirmOverride = text => { confirms.Add(text); return false; };
                try { await sqlTab.RunAsync(RunScope.All); }
                finally { Ui.ConfirmOverride = old; }
                Assert.True(confirms.Count == 1 && confirms[0].Contains("unsaved row changes") && sqlTab.ResultTabs[0] == r, "kept: " + string.Join(" | ", confirms));
                r.Grid.CurrentCell = r.Grid[1, 0];
                Call(r.Grid, "OnKeyDown", new KeyEventArgs(Keys.Escape));
                Assert.True(!sqlTab.HasPendingEdits, "nothing pending after Esc");
            });
        }
        finally
        {
            await Scalar(session, db.Database, $"DROP TABLE IF EXISTS {table}");
        }
    }

    /// <summary>Ctrl+C / Ctrl+V on a grid, the way the keys reach it.</summary>
    private static void Keys_(DataGridView grid, Keys keys)
    {
        var args = new object[] { new Message(), keys };
        grid.GetType().GetMethod("ProcessCmdKey", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(grid, args);
    }

    /// <summary>Copy and paste of cells (tab-separated, as Excel does) in result grids and the Data Browser.</summary>
    private static async Task PasteUi(TestRunner t, TestDb db, MainForm form, ServerSession session)
    {
        const string table = "mf_ui_paste";
        await Scalar(session, db.Database, $"""
            DROP TABLE IF EXISTS {table};
            CREATE TABLE {table} (id INT AUTO_INCREMENT PRIMARY KEY, il VARCHAR(50) NOT NULL, ilce VARCHAR(50) NULL, nufus INT NULL,
              oran DECIMAL(5,2) NULL, UNIQUE KEY uq_il (il)) DEFAULT CHARSET=utf8mb4;
            INSERT INTO {table} (il, ilce, nufus, oran) VALUES ('İstanbul', 'Kadıköy', 480000, 1.5), ('Ankara', NULL, 950000, 2.25),
              ('İzmir', 'Karşıyaka', 350000, NULL), ('Bursa', 'Nilüfer', 500000, 0.75);
            """);
        var view = form.ViewOf(session)!;
        var sqlTab = view.Editors.First();
        string Info(ResultTab r) => r.Controls.OfType<ToolStrip>().First().Items.OfType<ToolStripLabel>().First().Text ?? "";
        Task<object?> Db(string sql) => Scalar(session, db.Database, sql);
        async Task<ResultTab> Run(string sql)
        {
            form.Tabs.SelectedTab = sqlTab;
            sqlTab.Editor.Text = sql;
            await sqlTab.RunAsync(RunScope.All);
            await sqlTab.ResultTabs[0].EditPlanned;
            return sqlTab.ResultTabs[0];
        }
        try
        {
            ResultTab result = null!;
            await t.Check("ui: grid menu — Copy, Paste; no Copy with headers", async () =>
            {
                result = await Run($"SELECT id, il, ilce, nufus, oran, CONCAT(il, '/', IFNULL(ilce, '-')) AS yer FROM {table} ORDER BY id");
                var menu = result.Grid.ContextMenuStrip!;
                typeof(ToolStripDropDown).GetMethod("OnOpening", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(menu, new object[] { new System.ComponentModel.CancelEventArgs() });
                var items = string.Join("|", menu.Items.OfType<ToolStripMenuItem>().Where(i => i.Available).Select(i => i.Text));
                Assert.True(items.StartsWith("Copy|Paste|") && !items.Contains("Copy with headers"), items);
                return items;
            });

            await t.Check("ui: Ctrl+C copies the selected cells as tab-separated rows (NULL = empty field)", () =>
            {
                var grid = result.Grid;
                grid.ClearSelection();
                grid.CurrentCell = grid[1, 0];
                grid[1, 0].Selected = grid[2, 0].Selected = grid[1, 1].Selected = grid[2, 1].Selected = true;
                Keys_(grid, Keys.Control | Keys.C);
                Assert.Equal("İstanbul\tKadıköy\r\nAnkara\t\r\n", Clipboard.GetText(), "clipboard");
                return Task.FromResult<string?>(Clipboard.GetText().Replace("\t", "→").Replace("\r\n", "↵"));
            });

            await t.Check("ui: Ctrl+V pastes a block from Excel into a result grid from the current cell on; each row saved by its key", async () =>
            {
                var grid = result.Grid;
                Clipboard.SetText("Edirne\tMerkez\r\nKonya\tSelçuklu\r\n");
                grid.ClearSelection();
                grid.CurrentCell = grid[1, 2];
                Keys_(grid, Keys.Control | Keys.V);
                await Until(() => Info(result).Contains("Pasted 4 values into 2 rows"), "pasted and saved: " + Info(result));
                Assert.Equal("Edirne/Merkez/Konya/Selçuklu",
                    $"{await Db($"SELECT il FROM {table} WHERE id = 3")}/{await Db($"SELECT ilce FROM {table} WHERE id = 3")}/{await Db($"SELECT il FROM {table} WHERE id = 4")}/{await Db($"SELECT ilce FROM {table} WHERE id = 4")}",
                    "db values");
                return Info(result);
            });

            await t.Check("ui: paste is clipped at the grid's edge and leaves out read-only columns (the expression)", async () =>
            {
                var grid = result.Grid;
                Clipboard.SetText("1.25\tdeğer\tfazla\r\n2.50\tdeğer\tfazla\r\n9\tx\ty\r\n");
                grid.ClearSelection();
                grid.CurrentCell = grid[4, 2];
                Keys_(grid, Keys.Control | Keys.V);
                await Until(() => Info(result).Contains("Pasted 2 values into 2 rows, 2 read-only cells left out"), "clipped: " + Info(result));
                Assert.Equal(1.25m, Convert.ToDecimal(await Db($"SELECT oran FROM {table} WHERE id = 3")), "oran 3");
                Assert.Equal(2.50m, Convert.ToDecimal(await Db($"SELECT oran FROM {table} WHERE id = 4")), "oran 4");
                Assert.Equal(4, grid.RowCount, "no rows added");
                return Info(result);
            });

            await t.Check("ui: one value pasted into several selected cells fills each of them", async () =>
            {
                var grid = result.Grid;
                Clipboard.SetText("Türkiye");
                grid.ClearSelection();
                grid.CurrentCell = grid[2, 0];
                grid[2, 0].Selected = grid[2, 1].Selected = grid[2, 3].Selected = true;
                Keys_(grid, Keys.Control | Keys.V);
                await Until(() => Info(result).Contains("Pasted 3 values into 3 rows"), "filled: " + Info(result));
                Assert.Equal(3L, Convert.ToInt64(await Db($"SELECT COUNT(*) FROM {table} WHERE ilce = 'Türkiye'")), "three cells");
            });

            await t.Check("ui: a pasted row the server refuses keeps its mark and error; the other pasted rows are saved", async () =>
            {
                var grid = result.Grid;
                var taken = (string)result.Data.Rows.GetRow(3)[1]!; // the unique il of the last row
                Clipboard.SetText($"Sivas\r\n{taken}\r\nKars\r\n");
                grid.ClearSelection();
                grid.CurrentCell = grid[1, 0];
                Keys_(grid, Keys.Control | Keys.V);
                await Until(() => Info(result).Contains("1 row not saved"), "one failed: " + Info(result));
                Assert.True(result.EditRows.Find(1)?.State == EditState.Error && result.EditRows.Find(1)!.Error!.Contains("Duplicate"), "row 1: " + result.EditRows.Find(1)?.Error);
                Assert.Equal("Sivas/Ankara/Kars", $"{await Db($"SELECT il FROM {table} WHERE id = 1")}/{await Db($"SELECT il FROM {table} WHERE id = 2")}/{await Db($"SELECT il FROM {table} WHERE id = 3")}", "rows 0 and 2 saved");
                await Task.Delay(100);
                Shot(form, "71-paste");
                form.ToggleTheme();
                try
                {
                    await Task.Delay(150);
                    Shot(form, "72-dark-paste");
                }
                finally { form.ToggleTheme(); }
                grid.CurrentCell = grid[1, 1];
                Call(grid, "OnKeyDown", new KeyEventArgs(Keys.Escape));
                Assert.True(!sqlTab.HasPendingEdits, "reverted");
                return Info(result);
            });

            await t.Check("ui: pasting into a read-only result says why instead", async () =>
            {
                var r = await Run($"SELECT il, COUNT(*) AS n FROM {table} GROUP BY il");
                Clipboard.SetText("x");
                r.Grid.CurrentCell = r.Grid[0, 0];
                Keys_(r.Grid, Keys.Control | Keys.V);
                Assert.True(Info(r).Contains("Can't paste: read-only: grouped rows"), Info(r));
                return Info(r);
            });

            await t.Check("ui: Data Browser — copy a cell with a line break, paste it into three selected cells (one cell each); a view says it is read-only", async () =>
            {
                await Db($"UPDATE {table} SET ilce = 'Çok\nsatırlı' WHERE id = 1");
                var nav = view.Navigator;
                var data = view.DataBrowser;
                var grid = data.Grid;
                await nav.RefreshNodeAsync((await nav.SelectDatabaseAsync(session, db.Database))!);
                await nav.SelectObjectAsync(session, db.Database, table);
                form.Tabs.SelectedTab = form.DataPage;
                await Until(() => data.Table == table && !data.IsLoading && grid.RowCount == 4, "rows");
                await data.RefreshAsync();
                int ilce = Array.FindIndex(grid.ColumnInfos, c => c.Name == "ilce");
                grid.ClearSelection();
                grid.CurrentCell = grid[ilce, 0];
                Keys_(grid, Keys.Control | Keys.C);
                Assert.Equal("Çok\nsatırlı", Clipboard.GetText(), "a single cell is copied as its text");
                grid.ClearSelection();
                grid.CurrentCell = grid[ilce, 1];
                grid[ilce, 1].Selected = grid[ilce, 2].Selected = grid[ilce, 3].Selected = true;
                Keys_(grid, Keys.Control | Keys.V);
                await Until(() => data.StatusTime?.StartsWith("Pasted 3 values into 3 rows") == true, "pasted: " + data.StatusTime);
                Assert.Equal(4L, Convert.ToInt64(await Db($"SELECT COUNT(*) FROM {table} WHERE ilce = 'Çok\nsatırlı'")), "one cell each, line break kept");

                await nav.SelectObjectAsync(session, db.Database, "v_latest_prices");
                await Until(() => data.Table == "v_latest_prices" && !data.IsLoading && grid.RowCount > 0, "view rows", 30000);
                grid.CurrentCell = grid[0, 0];
                Keys_(grid, Keys.Control | Keys.V);
                Assert.True(data.StatusTime?.StartsWith("Can't paste: read-only") == true, "view: " + data.StatusTime);
                return data.StatusTime;
            });
        }
        finally
        {
            await Scalar(session, db.Database, $"DROP TABLE IF EXISTS {table}");
        }
    }

    /// <summary>Edit key… on a table shaped like the user's COUNTRY_LINK (a MEDIUMTEXT key with a prefix length, a foreign key).</summary>
    private static async Task KeyEditUi(TestRunner t, TestDb db, MainForm form, ServerSession session)
    {
        const string table = "mf_ddl_link";
        await EditorPropertyTests.ExecScript(session, db.Database, DdlTests.KeyEditSetup);
        var view = form.ViewOf(session)!;
        var nav = view.Navigator;
        var objects = view.ObjectBrowser;
        var indexes = objects.Controls.OfType<SplitContainer>().First().Panel2.Controls.OfType<ListView>().First();
        string Names(ListBox l) => string.Join(",", l.Items.Cast<object>());
        MyFront.UI.Dialogs.AddKeyForm.KeyField F(ListBox l, string name) => l.Items.Cast<MyFront.UI.Dialogs.AddKeyForm.KeyField>().First(f => f.Column.Name == name);
        try
        {
            form.Tabs.SelectedTab = form.ObjectPage;
            await nav.RefreshNodeAsync((await nav.SelectDatabaseAsync(session, db.Database))!);
            await nav.SelectObjectAsync(session, db.Database, table);
            await Until(() => indexes.Items.Count == 5, "keys of " + table);

            await t.Check("ui: Object Browser → right-click a key → Edit key… (and Add key…); double-click opens Edit key too", () =>
            {
                var item = indexes.Items.Cast<ListViewItem>().First(i => i.Text == "idx_all");
                var r = item.Bounds;
                using var menu = objects.BuildTableMenu(indexes, new Point(r.X + 4, r.Y + r.Height / 2))!;
                var names = string.Join("|", menu.Items.OfType<ToolStripMenuItem>().Select(i => i.Text));
                Assert.Equal("Edit key…|Add key…", names, "menu on a key");
                string? seen = null;
                var sw = Stopwatch.StartNew();
                using var timer = new System.Windows.Forms.Timer { Interval = 100 };
                timer.Tick += (_, _) =>
                {
                    var f = Application.OpenForms.OfType<MyFront.UI.Dialogs.AddKeyForm>().FirstOrDefault();
                    if (f is null || (f.KeyList.Items.Count == 0 && sw.ElapsedMilliseconds < 15000)) return;
                    seen = f.Text + " · " + Names(f.KeyList);
                    timer.Stop();
                    f.Close();
                };
                timer.Start();
                indexes.SelectedItems.Clear();
                item.Selected = true;
                Call(indexes, "OnDoubleClick", EventArgs.Empty); // modal: returns when the timer closed it
                Assert.Equal($"Edit key idx_all — {table} · COUNTRY_ALL(100)", seen, "dialog");
                return Task.FromResult<string?>(names + " → " + seen);
            });

            await t.Check("ui: Edit key — filled in (name, fields in order with prefix lengths, comment); add a field → DROP INDEX …, ADD … in one ALTER", async () =>
            {
                string sql;
                using (var dlg = new MyFront.UI.Dialogs.AddKeyForm(session, db.Database, table, "idx_all"))
                {
                    dlg.Show(form);
                    await dlg.Loaded;
                    var ok = Field<Button>(dlg, "_ok");
                    Assert.True(Field<TextBox>(dlg, "_name").Text == "idx_all" && Field<TextBox>(dlg, "_comment").Text == "tüm ülkeler"
                                && !Field<CheckBox>(dlg, "_unique").Checked && Field<RadioButton>(dlg, "_named").Checked, "name, comment, kind");
                    Assert.Equal("COUNTRY_ALL(100)", Names(dlg.KeyList), "key field with its length");
                    Assert.True(!ok.Enabled && dlg.Sql.StartsWith("ALTER TABLE"), "OK waits for a change");
                    dlg.DropFields(dlg.AvailableList, new[] { F(dlg.AvailableList, "ISO_CODE") }, dlg.KeyList, dlg.KeyList.PointToScreen(new Point(4, dlg.KeyList.ClientSize.Height - 3)));
                    sql = dlg.Sql;
                    Assert.Equal($"ALTER TABLE `{db.Database}`.`{table}` DROP INDEX `idx_all`, ADD INDEX `idx_all` (`COUNTRY_ALL`(100), `ISO_CODE`) COMMENT 'tüm ülkeler'", sql, "preview");
                    Assert.True(ok.Enabled, "OK once changed");
                    await Task.Delay(100);
                    Shot(dlg, "73-edit-key");
                    await dlg.ApplyAsync();
                    Assert.True(dlg.DialogResult == DialogResult.OK, "applied: " + Field<Label>(dlg, "_state").Text);
                    dlg.Close();
                }
                view.AfterTableChanged(db.Database, table); // what OK does
                await Until(() => indexes.Items.Cast<ListViewItem>().Any(i => i.Text == "idx_all" && i.SubItems[1].Text == "COUNTRY_ALL, ISO_CODE"), "Object Browser shows the changed key");
                return sql;
            });

            await t.Check("ui: Edit key — remove a field a foreign key needs: warned up front, the server's error shown, the dialog stays open", async () =>
            {
                using var dlg = new MyFront.UI.Dialogs.AddKeyForm(session, db.Database, table, "idx_comtrade");
                dlg.Show(form);
                await dlg.Loaded;
                var state = Field<Label>(dlg, "_state");
                Assert.Equal("COMTRADE_ID,TURKISH_CONT", Names(dlg.KeyList), "two fields in order");
                dlg.KeyList.ClearSelected();
                dlg.KeyList.SelectedItem = F(dlg.KeyList, "TURKISH_CONT");
                dlg.MoveSelected(-1);
                Assert.Equal("TURKISH_CONT,COMTRADE_ID", Names(dlg.KeyList), "reordered");
                Assert.True(state.Text.Contains("fk_link_comtrade"), "warned: " + state.Text);
                dlg.KeyList.ClearSelected();
                dlg.KeyList.SelectedItem = F(dlg.KeyList, "COMTRADE_ID");
                dlg.RemoveSelected();
                await dlg.ApplyAsync();
                Assert.True(dlg.DialogResult == DialogResult.None && dlg.Visible && state.Text.Contains("SQL Error") && state.Text.Contains("foreign key", StringComparison.OrdinalIgnoreCase),
                    "server error shown: " + state.Text);
                dlg.Close();
                return state.Text;
            });

            await t.Check("ui: Edit primary key → DROP PRIMARY KEY, ADD PRIMARY KEY; in dark mode", async () =>
            {
                form.ToggleTheme();
                try
                {
                    using var dlg = new MyFront.UI.Dialogs.AddKeyForm(session, db.Database, table, "PRIMARY");
                    dlg.Show(form);
                    await dlg.Loaded;
                    Assert.True(dlg.Text == "Edit primary key — " + table && Field<RadioButton>(dlg, "_primary") is { Checked: true, Enabled: true }, "primary: " + dlg.Text);
                    Assert.Equal("ID", Names(dlg.KeyList), "ID");
                    dlg.AvailableList.ClearSelected();
                    dlg.AvailableList.SelectedItem = F(dlg.AvailableList, "ISO_CODE");
                    dlg.AddSelected();
                    Assert.Equal($"ALTER TABLE `{db.Database}`.`{table}` DROP PRIMARY KEY, ADD PRIMARY KEY (`ID`, `ISO_CODE`)", dlg.Sql, "preview");
                    await Task.Delay(150);
                    Shot(dlg, "74-dark-edit-key");
                    await dlg.ApplyAsync();
                    Assert.True(dlg.DialogResult == DialogResult.OK, Field<Label>(dlg, "_state").Text);
                    dlg.Close();
                }
                finally { form.ToggleTheme(); }
                var p = await TableProperties.LoadAsync(session, db.Database, table);
                Assert.True(p.Keys.Any(k => k is { Name: "PRIMARY", Columns: "ID, ISO_CODE" }), string.Join(" | ", p.Keys.Select(k => k.Name + ":" + k.Columns)));
            });
        }
        finally
        {
            await EditorPropertyTests.ExecScript(session, db.Database, "DROP TABLE IF EXISTS mf_ddl_link, mf_ddl_parent");
            await nav.RefreshNodeAsync((await nav.SelectDatabaseAsync(session, db.Database))!);
        }
    }

    /// <summary>Tables in the tree expand to their keys and fields (shaped like the user's FAO table); menus, Copy name, rename.</summary>
    private static async Task TreeChildrenUi(TestRunner t, TestDb db, MainForm form, ServerSession session)
    {
        const string table = "mf_ui_tree";
        await Scalar(session, db.Database, $"""
            DROP VIEW IF EXISTS {table}_v;
            DROP TABLE IF EXISTS {table};
            CREATE TABLE {table} (ID INT AUTO_INCREMENT PRIMARY KEY, Area_Code INT, Area VARCHAR(100), Item VARCHAR(100), `Year` INT, `Value` DOUBLE,
              KEY idx_area (Area_Code), KEY fao_idx_item_year (Item, `Year`)) DEFAULT CHARSET=utf8mb4;
            INSERT INTO {table} (Area_Code, Area, Item, `Year`, `Value`) VALUES (223, 'Türkiye', 'Fındık', 2024, 684000);
            CREATE VIEW {table}_v AS SELECT ID, Area FROM {table};
            """);
        var view = form.ViewOf(session)!;
        var nav = view.Navigator;
        var objects = view.ObjectBrowser;
        var fields = objects.Controls.OfType<SplitContainer>().First().Panel1.Controls.OfType<ListView>().First();
        var indexes = objects.Controls.OfType<SplitContainer>().First().Panel2.Controls.OfType<ListView>().First();
        TreeNode? TryNode(string name) => nav.FindServer(session)?.Nodes.Cast<TreeNode>().FirstOrDefault(n => n.Text == db.Database)?
            .Nodes.Cast<TreeNode>().FirstOrDefault(n => NavigatorTree.Info(n)?.Name == name && NavigatorTree.Info(n)!.IsTableLike);
        TreeNode Node(string name) => TryNode(name) ?? throw new Exception(name + " is not in the tree");
        TreeNode Child(TreeNode parent, string name) => parent.Nodes.Cast<TreeNode>().First(n => NavigatorTree.Info(n)?.Child == name);
        string Texts(TreeNode n) => string.Join(",", n.Nodes.Cast<TreeNode>().Select(c => c.Text.TrimEnd()));
        string Menu(NodeInfo i)
        {
            using var m = (ContextMenuStrip)Call(nav, "BuildMenu", i)!;
            return string.Join("|", m.Items.OfType<ToolStripMenuItem>().Select(x => x.Text));
        }
        try
        {
            form.Tabs.SelectedTab = form.ObjectPage;
            await nav.RefreshNodeAsync((await nav.SelectDatabaseAsync(session, db.Database, expand: true))!);

            await t.Check("ui: a table in the tree expands to its keys (Primary Key in bold first) and then its fields (the primary key's in bold)", async () =>
            {
                var node = Node(table);
                Assert.True(node.Nodes.Count == 1 && NavigatorTree.Info(node.Nodes[0])?.Kind == NodeKind.Loading, "an expand arrow before loading");
                node.Expand(); // loads like a click on the arrow
                await Until(() => node.Nodes.Count > 1, "keys and fields");
                var texts = Texts(node);
                Assert.Equal("Primary Key", node.Nodes[0].Text.TrimEnd(), "primary key first");
                Assert.True(texts.Contains("idx_area") && texts.Contains("fao_idx_item_year"), texts);
                Assert.True(texts.EndsWith("ID,Area_Code,Area,Item,Year,Value"), "fields after the keys, in table order: " + texts);
                var keys = node.Nodes.Cast<TreeNode>().Where(n => NavigatorTree.Info(n)?.Kind == NodeKind.Key).ToList();
                Assert.True(keys.Count == 3 && keys.All(k => k.ImageKey == "key"), "key icons");
                Assert.True(node.Nodes.Cast<TreeNode>().Where(n => NavigatorTree.Info(n)?.Kind == NodeKind.Field).All(f => f.ImageKey == "field"), "field icons");
                Assert.True(node.Nodes[0].NodeFont?.Bold == true && Child(node, "ID").NodeFont?.Bold == true && Child(node, "Area").NodeFont is null,
                    "bold: Primary Key and ID only");
                Assert.True(Child(node, "fao_idx_item_year").ToolTipText == "KEY (Item, Year)", "key tooltip: " + Child(node, "fao_idx_item_year").ToolTipText);
                nav.SelectedNode = Child(node, "Area");
                await Until(() => fields.Columns.Count > 0 && fields.Columns[0].Text == "Field" && fields.SelectedItems.Count == 1, "Object Browser on the field");
                Assert.Equal("Area", fields.SelectedItems[0].Text, "the field is selected in the Object Browser");
                nav.SelectedNode = Child(node, "idx_area");
                await Until(() => indexes.SelectedItems.Count == 1 && indexes.SelectedItems[0].Text == "idx_area", "the key is selected in the Object Browser");
                await Task.Delay(150);
                Shot(form, "75-tree-fields");
                form.ToggleTheme();
                try
                {
                    await Task.Delay(150);
                    Shot(form, "76-dark-tree-fields");
                }
                finally { form.ToggleTheme(); }
                return texts;
            });

            await t.Check("ui: a view expands to its fields only (read-only: Copy name)", async () =>
            {
                var node = Node(table + "_v");
                await nav.LoadChildrenAsync(node);
                node.Expand();
                Assert.Equal("ID,Area", Texts(node), "fields, no keys");
                var info = NavigatorTree.Info(node.Nodes[0])!;
                Assert.True(info.InView, "a view's field");
                Assert.Equal("Copy name", Menu(info), "view field menu");
                lock (Messages) Messages.Clear();
                Call(view, "OnTreeCommand", info, "editfield");
                Assert.True(Messages.Any(m => m.Contains("is a view")), "edit refused for a view: " + string.Join(" | ", Messages));
            });

            await t.Check("ui: tree menus — field: Edit field…, Rename…, Copy name; key: Edit key…, Copy name; table, view, database: Copy name", () =>
            {
                var node = Node(table);
                var field = NavigatorTree.Info(Child(node, "Area"))!;
                var key = NavigatorTree.Info(Child(node, "PRIMARY"))!;
                Assert.Equal("Edit field…|Rename…|Copy name", Menu(field), "field");
                Assert.Equal("Edit key…|Copy name", Menu(key), "key");
                Assert.True(Menu(NavigatorTree.Info(node)!).Contains("Copy name"), "table");
                Assert.True(Menu(NavigatorTree.Info(Node(table + "_v"))!).Contains("Copy name"), "view");
                Assert.True(Menu(new NodeInfo(NodeKind.Database, session, db.Database)).Contains("Copy name"), "database");
                return Task.FromResult<string?>(Menu(field) + " / " + Menu(key));
            });

            await t.Check("ui: Copy name copies the plain name (field, primary key, table, database)", () =>
            {
                var node = Node(table);
                var copied = new List<string>();
                foreach (var info in new[] { NavigatorTree.Info(Child(node, "Area_Code"))!, NavigatorTree.Info(Child(node, "PRIMARY"))!, NavigatorTree.Info(node)!,
                             new NodeInfo(NodeKind.Database, session, db.Database) })
                {
                    Call(view, "OnTreeCommand", info, "copyname");
                    copied.Add(Clipboard.GetText());
                }
                Assert.Equal($"Area_Code|PRIMARY|{table}|{db.Database}", string.Join("|", copied), "copied");
                return Task.FromResult<string?>(string.Join("|", copied));
            });

            await t.Check("ui: double-click a field in the tree → Edit field… for it; a key → Edit key…", () =>
            {
                var node = Node(table);
                var seen = new List<string>();
                var sw = Stopwatch.StartNew();
                using var timer = new System.Windows.Forms.Timer { Interval = 100 };
                timer.Tick += (_, _) =>
                {
                    Form? f = Application.OpenForms.OfType<MyFront.UI.Dialogs.EditFieldForm>().FirstOrDefault(d => d.Sql.Length > 0 || sw.ElapsedMilliseconds > 15000);
                    f ??= Application.OpenForms.OfType<MyFront.UI.Dialogs.AddKeyForm>().FirstOrDefault(d => d.KeyList.Items.Count > 0 || sw.ElapsedMilliseconds > 15000);
                    if (f is null) return;
                    seen.Add(f.Text);
                    f.Close();
                };
                timer.Start();
                foreach (var name in new[] { "Item", "fao_idx_item_year" })
                {
                    sw.Restart();
                    Call(nav, "OnNodeMouseDoubleClick", new TreeNodeMouseClickEventArgs(Child(node, name), MouseButtons.Left, 2, 0, 0)); // modal until closed
                }
                timer.Stop();
                Assert.Equal($"Edit field Item — {table}|Edit key fao_idx_item_year — {table}", string.Join("|", seen), "dialogs");
                return Task.FromResult<string?>(string.Join("|", seen));
            });

            await t.Check("ui: Rename… a field from the tree → CHANGE COLUMN with its whole definition; the table stays expanded, selection kept, Object Browser follows", async () =>
            {
                var node = Node(table);
                nav.SelectedNode = Child(node, "Item");
                string sql;
                using (var dlg = new MyFront.UI.Dialogs.RenameForm(session, db.Database, table, "Area"))
                {
                    dlg.Show(form);
                    await dlg.Loaded;
                    Assert.True(!Field<Button>(dlg, "_ok").Enabled, "same name: nothing to do");
                    Field<TextBox>(dlg, "_name").Text = "Bölge";
                    sql = dlg.Sql;
                    Assert.True(sql.StartsWith($"ALTER TABLE `{db.Database}`.`{table}` CHANGE COLUMN `Area` `Bölge` varchar(100)"), sql);
                    await Task.Delay(100);
                    Shot(dlg, "77-rename-field");
                    await dlg.ApplyAsync();
                    Assert.True(dlg.DialogResult == DialogResult.OK, Field<Label>(dlg, "_state").Text);
                    dlg.Close();
                }
                view.AfterTableChanged(db.Database, table); // what OK does
                await Until(() => TryNode(table) is { IsExpanded: true } n && n.Nodes.Cast<TreeNode>().Any(c => c.Text.TrimEnd() == "Bölge"), "the tree shows the new name");
                await Until(() => NavigatorTree.Info(nav.SelectedNode)?.Child == "Item", "the selected field is selected again");
                await Until(() => fields.Items.Cast<ListViewItem>().Any(i => i.Text == "Bölge"), "Object Browser");
                Assert.Equal("Türkiye", (string?)await Scalar(session, db.Database, $"SELECT `Bölge` FROM {table}"), "data kept");
                return sql;
            });
        }
        finally
        {
            await Scalar(session, db.Database, $"DROP VIEW IF EXISTS {table}_v; DROP TABLE IF EXISTS {table}");
            await nav.RefreshNodeAsync((await nav.SelectDatabaseAsync(session, db.Database))!);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);

    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004,
        MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;

    private static void MouseTo(Point p)
    {
        var vs = SystemInformation.VirtualScreen;
        int x = (int)Math.Round((p.X - vs.Left) * 65535.0 / Math.Max(1, vs.Width - 1));
        int y = (int)Math.Round((p.Y - vs.Top) * 65535.0 / Math.Max(1, vs.Height - 1));
        mouse_event(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, x, y, 0, UIntPtr.Zero);
    }

    /// <summary>
    /// A real drag with the mouse: press at one screen point, move there in steps, let go. Runs on a worker thread,
    /// because the drag runs a modal loop on the UI thread.
    /// </summary>
    private static Task MouseDrag(Point from, Point to) => Task.Run(() =>
    {
        MouseTo(from);
        Thread.Sleep(150);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(100);
        for (int i = 1; i <= 25; i++)
        {
            MouseTo(new Point(from.X + (to.X - from.X) * i / 25, from.Y + (to.Y - from.Y) * i / 25));
            Thread.Sleep(25);
        }
        Thread.Sleep(250);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(150);
    });

    /// <summary>Names dragged from the tree into the SQL editor; the main window's file drop is not in the way.</summary>
    private static async Task DragUi(TestRunner t, TestDb db, MainForm form, ServerSession session)
    {
        const string table = "mf_ui_drag";
        await Scalar(session, db.Database, $"""
            DROP TABLE IF EXISTS {table};
            CREATE TABLE {table} (id INT AUTO_INCREMENT PRIMARY KEY, Area_Code INT, `order` INT, `Yıl` INT) DEFAULT CHARSET=utf8mb4;
            """);
        var view = form.ViewOf(session)!;
        var nav = view.Navigator;
        var sqlTab = view.Editors.First();
        var editor = sqlTab.Editor;
        TreeNode? tableNode = null;
        TreeNode Field(string name) => tableNode!.Nodes.Cast<TreeNode>().First(n => NavigatorTree.Info(n)?.Child == name);
        Point AtPosition(int pos) => editor.PointToScreen(new Point(editor.PointXFromPosition(pos) + 1, editor.PointYFromPosition(pos) + editor.LogicalToDeviceUnits(7)));
        DragEventArgs Drag(IDataObject data, Point screen) => new(data, 0, screen.X, screen.Y, DragDropEffects.Copy, DragDropEffects.None);
        try
        {
            var dbNode = (await nav.SelectDatabaseAsync(session, db.Database, expand: true))!;
            await nav.RefreshNodeAsync(dbNode);
            tableNode = dbNode.Nodes.Cast<TreeNode>().First(n => NavigatorTree.Info(n)?.Name == table);
            await nav.LoadChildrenAsync(tableNode);
            tableNode.Expand();
            form.Tabs.SelectedTab = sqlTab;

            await t.Check("ui: a name dropped on the SQL editor goes in at the drop point (caret after it); file drops still reach the import, text never does", () =>
            {
                editor.Text = "SELECT  FROM x";
                var name = NavigatorTree.DragText(NavigatorTree.Info(Field("order")))!;
                Assert.Equal("`order`", name, "reserved word in backticks");
                var args = Drag(SqlEditor.NameData(name), AtPosition(7));
                Call(editor, "OnDragEnter", args);
                Assert.Equal(DragDropEffects.Copy, args.Effect, "the editor takes a name");
                Call(editor, "OnDragDrop", args);
                Assert.Equal("SELECT `order` FROM x", editor.Text, "inserted at the drop point");
                Assert.Equal(7 + name.Length, editor.CurrentPosition, "caret after the name");

                var file = new DataObject(DataFormats.FileDrop, new[] { @"C:\veri\ihracat.xlsx" });
                var fileArgs = Drag(file, AtPosition(3));
                Call(editor, "OnDragOver", fileArgs);
                Assert.Equal(DragDropEffects.Copy, fileArgs.Effect, "an .xlsx over the editor is still offered to the import");
                Assert.Equal(DragDropEffects.None, ((IFileDropTarget)form).FileDragEffect(SqlEditor.NameData("FAO")), "the window refuses a name");
                Assert.Equal(DragDropEffects.None, ((IFileDropTarget)form).FileDragEffect(new DataObject(DataFormats.UnicodeText, "metin")), "and plain text");
                Assert.True(!nav.AllowDrop, "the tree takes no drops (dragging within it does nothing)");
                return Task.FromResult<string?>(editor.Text);
            });

            await t.Check("ui: drag a table, then a field, from the tree into the SQL editor with the real mouse → the dragged node's name where it was dropped", async () =>
            {
                if (QuietDesktop.Enabled) return "skipped: it moves the real mouse (MYFRONT_UITEST_INTERACTIVE=1 runs it)";
                var other = Field("id");
                nav.SelectedNode = other; // the selection must not decide what goes in
                editor.Text = "SELECT  FROM ";
                form.TopMost = true;
                form.Activate();
                try
                {
                    tableNode.EnsureVisible();
                    var r = tableNode.Bounds;
                    await MouseDrag(nav.PointToScreen(new Point(r.X + Math.Min(20, r.Width / 2), r.Y + r.Height / 2)), AtPosition(editor.TextLength));
                    await Until(() => editor.Text == $"SELECT  FROM {table}", "the table's name at the end: " + editor.Text, 5000);
                    var field = Field("Yıl");
                    field.EnsureVisible();
                    r = field.Bounds;
                    await MouseDrag(nav.PointToScreen(new Point(r.X + Math.Min(15, r.Width / 2), r.Y + r.Height / 2)), AtPosition(7));
                    await Until(() => editor.Text == $"SELECT `Yıl` FROM {table}", "the field's name after SELECT: " + editor.Text, 5000);
                }
                finally { form.TopMost = false; }
                Assert.True(nav.SelectedNode == other, "dragging didn't change the tree's selection");
                Assert.True(editor.Focused, "the editor has the focus after the drop");
                await Task.Delay(100);
                Shot(form, "78-drag-names");
                return editor.Text;
            });
        }
        finally
        {
            await Scalar(session, db.Database, $"DROP TABLE IF EXISTS {table}");
        }
    }

    /// <summary>Data Browser on a table without a key, shaped like one the import wizard makes from a trade-statistics sheet.</summary>
    private static async Task KeylessUi(TestRunner t, TestDb db, MainForm form, ServerSession session)
    {
        await EditorPropertyTests.ExecScript(session, db.Database, DataEditTests.KeylessSetup);
        var view = form.ViewOf(session)!;
        var nav = view.Navigator;
        var data = view.DataBrowser;
        var grid = data.Grid;
        int Col(string name) => Array.FindIndex(grid.ColumnInfos, c => c.Name == name);
        int RowWhere(Func<object?[], bool> pick) => data.Source.Rows.FindIndex(r => pick(r.Values));
        async Task Leave(int row)
        {
            grid.CurrentCell = grid[0, row == 0 ? 1 : 0]; // leaving the row posts it
            await Until(() => data.Source.Rows.All(r => r.State == EditState.Clean) && !data.IsLoading, "row posted");
        }
        Task<long> Count(string where) => Scalar(session, db.Database, "SELECT COUNT(*) FROM mf_edit_nokey WHERE " + where).ContinueWith(x => Convert.ToInt64(x.Result));
        try
        {
            await nav.RefreshNodeAsync((await nav.SelectDatabaseAsync(session, db.Database))!);
            await nav.SelectObjectAsync(session, db.Database, "mf_edit_nokey");
            form.Tabs.SelectedTab = form.DataPage;
            await Until(() => data.Table == "mf_edit_nokey" && !data.IsLoading && grid.RowCount == 4, "rows of the keyless table");

            await t.Check("ui: a table without a key (like the imported one) is editable; the status line says rows are matched by all values", () =>
            {
                var info = Field<ToolStripLabel>(data, "_info").Text ?? "";
                Assert.True(!grid.ReadOnly, "editable");
                Assert.True(info.Contains("matched by all their values") && info.Contains("FLOAT / DOUBLE"), info);
                Shot(form, "67-keyless-table");
                return Task.FromResult<string?>(info);
            });

            await t.Check("ui: edit keyless rows → UPDATE … WHERE every value <=> … LIMIT 1 (a row with NULLs, one of two twins, a DOUBLE row)", async () =>
            {
                var nullRow = RowWhere(v => Convert.ToInt32(v[2]) == 2);
                TypeInto(grid, Col("IL_ADI"), nullRow, "Ankara");
                await Leave(nullRow);
                Assert.Equal(1L, await Count("IL_ADI = 'Ankara' AND AY = 2"), "the NULL row");
                var twin = RowWhere(v => (string?)v[0] == "IHR");
                TypeInto(grid, Col("KILOGRAM"), twin, "600");
                await Leave(twin);
                Assert.True(await Count("IHRITH = 'IHR' AND KILOGRAM = 500") == 1 && await Count("IHRITH = 'IHR' AND KILOGRAM = 600") == 1, "only one twin");
                var withDouble = RowWhere(v => Convert.ToInt32(v[1]) == 2025);
                TypeInto(grid, Col("ULKE_ADI"), withDouble, "Hollanda");
                await Leave(withDouble);
                Assert.Equal(1L, await Count("ULKE_ADI = 'Hollanda' AND YIL = 2025"), "the row with a DOUBLE");
            });

            await t.Check("ui: a keyless row changed by someone else since loading → message, the page reloads with what is there now", async () =>
            {
                lock (Messages) Messages.Clear();
                await Scalar(session, db.Database, "UPDATE mf_edit_nokey SET DOLAR = DOLAR + 1 WHERE YIL = 2025");
                var row = RowWhere(v => Convert.ToInt32(v[1]) == 2025);
                TypeInto(grid, Col("ULKE_ADI"), row, "Belçika");
                grid.CurrentCell = grid[0, row == 0 ? 1 : 0];
                await Until(() => Messages.Any(m => m.Contains("No row with these values")), "message");
                await Until(() => !data.IsLoading && grid.RowCount == 4 && data.Source.Rows.All(r => r.State == EditState.Clean), "reloaded");
                Assert.Equal(0L, await Count("ULKE_ADI = 'Belçika'"), "nothing written");
                Assert.Equal(12345678902L, Convert.ToInt64(data.Source.Rows[RowWhere(v => Convert.ToInt32(v[1]) == 2025)].Values[Col("DOLAR")]), "the grid shows the new value");
                return Messages.First(m => m.Contains("No row"));
            });

            await t.Check("ui: delete both of two identical keyless rows with the Delete key", async () =>
            {
                await Scalar(session, db.Database, "UPDATE mf_edit_nokey SET KILOGRAM = 500 WHERE IHRITH = 'IHR'");
                await data.RefreshAsync();
                await Until(() => !data.IsLoading && grid.RowCount == 4, "reloaded");
                grid.ClearSelection();
                var twins = Enumerable.Range(0, grid.RowCount).Where(i => (string?)data.Source.Rows[i].Values[0] == "IHR").ToList();
                grid.CurrentCell = grid[1, twins[0]];
                foreach (var i in twins) grid.Rows[i].Selected = true;
                Call(grid, "OnKeyDown", new KeyEventArgs(Keys.Delete));
                await Until(() => grid.RowCount == 2, "both gone from the grid");
                Assert.Equal(0L, await Count("IHRITH = 'IHR'"), "both deleted");
            });

            await t.Check("ui: add a row to a keyless table — empty fields get the server's values (the page reloads)", async () =>
            {
                Call(data, "AddRow");
                grid.EditingControl!.Text = "YENİ";
                grid.NotifyCurrentCellDirty(true);
                grid.EndEdit();
                var added = grid.RowCount - 1;
                TypeInto(grid, Col("YIL"), added, "2026");
                grid.CurrentCell = grid[0, 0];
                await Until(() => !data.IsLoading && grid.RowCount == 3 && data.Source.Rows.All(r => r.State == EditState.Clean && !r.IsNew), "inserted and reloaded");
                Assert.Equal(1L, await Count("IHRITH = 'YENİ' AND YIL = 2026 AND AY IS NULL"), "inserted");
            });
        }
        finally
        {
            await EditorPropertyTests.ExecScript(session, db.Database, "DROP TABLE IF EXISTS mf_edit_nokey");
            await nav.RefreshNodeAsync((await nav.SelectDatabaseAsync(session, db.Database))!);
        }
    }

    private static async Task PropertiesUi(TestRunner t, TestDb db, MainForm form, ServerSession session)
    {
        var nav = form.Navigator;
        await EditorPropertyTests.ExecScript(session, db.Database, EditorPropertyTests.Setup);
        try
        {
            var dbNode = (await nav.SelectDatabaseAsync(session, db.Database))!;
            await nav.RefreshNodeAsync(dbNode);

            await t.Check("ui: right-click a table → Properties… (views too, not databases)", () =>
            {
                string Items(NodeInfo i)
                {
                    using var m = (ContextMenuStrip)Call(nav, "BuildMenu", i)!;
                    return string.Join("|", m.Items.OfType<ToolStripMenuItem>().Select(x => x.Text));
                }
                var table = Items(new NodeInfo(NodeKind.Table, session, db.Database, "mf_prop_child"));
                Assert.True(table.EndsWith("Properties…"), "table menu: " + table);
                Assert.True(Items(new NodeInfo(NodeKind.View, session, db.Database, "mf_prop_v")).Contains("Properties…"), "view menu");
                Assert.True(!Items(new NodeInfo(NodeKind.Database, session, db.Database)).Contains("Properties…"), "no Properties for a database");
                return Task.FromResult<string?>(table);
            });

            await t.Check("ui: Alt+Enter on the table in the tree opens its Properties dialog", async () =>
            {
                await nav.SelectObjectAsync(session, db.Database, "mf_prop_child");
                string? seen = null;
                var sw = Stopwatch.StartNew();
                using var timer = new System.Windows.Forms.Timer { Interval = 100 };
                timer.Tick += (_, _) =>
                {
                    var f = Application.OpenForms.OfType<MyFront.UI.Dialogs.TablePropertiesForm>().FirstOrDefault();
                    if (f is null || (f.Properties is null && sw.ElapsedMilliseconds < 15000)) return;
                    seen = f.Text + " · " + string.Join(", ", f.Tabs.TabPages.Cast<TabPage>().Select(p => p.Text));
                    timer.Stop();
                    f.Close();
                };
                timer.Start();
                Call(nav, "OnKeyDown", new KeyEventArgs(Keys.Alt | Keys.Enter)); // modal: returns when the timer closed it
                Assert.True(seen?.StartsWith("Properties of table mf_prop_child") == true, "dialog: " + seen);
                return seen;
            });

            await t.Check("ui: Properties dialog — General, Information, Fields, Keys, Foreign Keys, Triggers, Partitions, Dependencies, Source Code", async () =>
            {
                using var dlg = new MyFront.UI.Dialogs.TablePropertiesForm(session, db.Database, "mf_prop_child");
                dlg.StartPosition = FormStartPosition.CenterScreen;
                dlg.Show(); // no owner: the test shows it modeless, an owner could end up drawn over it
                await dlg.Loaded;
                var names = string.Join(",", dlg.Tabs.TabPages.Cast<TabPage>().Select(p => p.Text));
                Assert.Equal("General,Information,Fields,Keys,Foreign Keys,Triggers,Partitions,Dependencies,Source Code", names, "tabs");
                var general = FindAll<TextBox>(dlg.Tabs.TabPages[0]).Select(x => x.Text).ToList();
                Assert.True(general.Contains("InnoDB") && general.Contains("utf8mb4") && general.Contains("Dynamic") && general.Contains("42") && general.Contains("çocuk tablo"),
                    "general: " + string.Join(" | ", general));
                await Task.Delay(100);
                Shot(dlg, "32-properties-general");
                foreach (var (index, shot) in new[] { (2, "33-properties-fields"), (4, "34-properties-foreign-keys") })
                {
                    dlg.Tabs.SelectedIndex = index;
                    await Task.Delay(100);
                    Shot(dlg, shot);
                }
                var fk = FindAll<ListView>(dlg.Tabs.TabPages[4]).Single().Items[0];
                Assert.Equal("fk_child_parent|parent_id|mf_prop_parent (id)|CASCADE|RESTRICT",
                    string.Join("|", fk.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(x => x.Text)), "foreign key row");
                dlg.Tabs.SelectedIndex = 7;
                await dlg.DependenciesLoaded;
                var deps = FindAll<ListView>(dlg.Tabs.TabPages[7]).Single().Items.Cast<ListViewItem>().Select(i => i.SubItems[1].Text).ToList();
                Assert.True(deps.Contains("mf_prop_parent (id)") && deps.Contains("mf_prop_v"), "dependencies: " + string.Join(", ", deps));
                await Task.Delay(100);
                Shot(dlg, "35-properties-dependencies");
                dlg.Tabs.SelectedIndex = 8;
                var source = FindAll<SqlEditor>(dlg.Tabs.TabPages[8]).Single();
                Assert.True(source.ReadOnly && source.Text.StartsWith("CREATE TABLE `mf_prop_child`"), "source: " + source.Text[..Math.Min(40, source.Text.Length)]);
                await Task.Delay(100);
                Shot(dlg, "36-properties-source");
                dlg.Close();
                return names;
            });

            await t.Check("ui: Properties of a view, and the dialog in dark mode", async () =>
            {
                using (var view = new MyFront.UI.Dialogs.TablePropertiesForm(session, db.Database, "mf_prop_v"))
                {
                    view.Show(form);
                    await view.Loaded;
                    Assert.Equal("General,Fields,Source Code", string.Join(",", view.Tabs.TabPages.Cast<TabPage>().Select(p => p.Text)), "view tabs");
                    view.Close();
                }
                form.ToggleTheme();
                try
                {
                    using var dlg = new MyFront.UI.Dialogs.TablePropertiesForm(session, db.Database, "mf_prop_part");
                    dlg.StartPosition = FormStartPosition.CenterScreen;
                    dlg.Show(); // no owner: the test shows it modeless, an owner could end up drawn over it
                    await dlg.Loaded;
                    dlg.Tabs.SelectedIndex = 6; // Partitions
                    var parts = FindAll<ListView>(dlg.Tabs.TabPages[6]).Single();
                    Assert.Equal(3, parts.Items.Count, "partitions listed");
                    Assert.Equal(Theme.Dark.Window, parts.BackColor, "dark list");
                    await Task.Delay(150);
                    Shot(dlg, "37-dark-properties-partitions");
                    dlg.Tabs.SelectedIndex = 0;
                    await Task.Delay(100);
                    Shot(dlg, "38-dark-properties-general");
                    dlg.Close();
                }
                finally { form.ToggleTheme(); }
            });
        }
        finally
        {
            await EditorPropertyTests.ExecScript(session, db.Database, EditorPropertyTests.Cleanup);
        }
    }

    /// <summary>Object Browser structure editing on a scratch table shaped like the user's <c>products</c>.</summary>
    private static async Task StructureUi(TestRunner t, TestDb db, MainForm form, ServerSession session)
    {
        const string table = "mf_ui_struct";
        await Scalar(session, db.Database, $"""
            DROP TABLE IF EXISTS {table};
            CREATE TABLE {table} (id INT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(100) NOT NULL, category VARCHAR(50) NULL,
              unit VARCHAR(10) NULL, UNIQUE KEY uq_name (name)) DEFAULT CHARSET=utf8mb4;
            INSERT INTO {table} (name, category, unit) VALUES ('Domates', 'Sebze', 'kg'), ('Çilek', 'Meyve', 'kg');
            """);
        var view = form.ViewOf(session)!;
        var nav = view.Navigator;
        var objects = view.ObjectBrowser;
        var fields = objects.Controls.OfType<SplitContainer>().First().Panel1.Controls.OfType<ListView>().First();
        try
        {
            form.Tabs.SelectedTab = form.ObjectPage;
            await nav.RefreshNodeAsync((await nav.SelectDatabaseAsync(session, db.Database))!);
            await nav.SelectObjectAsync(session, db.Database, table);
            await Until(() => fields.Columns.Count > 0 && fields.Columns[0].Text == "Field" && fields.Items.Count == 4, "fields of " + table);

            await t.Check("ui: Object Browser → right-click a field → Edit field… opens the dialog for that field", () =>
            {
                var item = fields.Items.Cast<ListViewItem>().First(i => i.Text == "category");
                var r = item.Bounds;
                using var menu = objects.BuildTableMenu(fields, new Point(r.X + 4, r.Y + r.Height / 2))!;
                var names = string.Join("|", menu.Items.OfType<ToolStripMenuItem>().Select(i => i.Text));
                string? seen = null;
                var sw = Stopwatch.StartNew();
                using var timer = new System.Windows.Forms.Timer { Interval = 100 };
                timer.Tick += (_, _) =>
                {
                    var f = Application.OpenForms.OfType<MyFront.UI.Dialogs.EditFieldForm>().FirstOrDefault();
                    if (f is null || (f.Sql.Length == 0 && sw.ElapsedMilliseconds < 15000)) return;
                    seen = f.Text + " · " + f.Sql;
                    timer.Stop();
                    f.Close();
                };
                timer.Start();
                menu.Items.OfType<ToolStripMenuItem>().First(i => i.Text == "Edit field…").PerformClick(); // modal: returns when the timer closed it
                Assert.True(seen?.Contains("CHANGE COLUMN `category` `category` varchar(50) NULL DEFAULT NULL") == true, "dialog: " + seen);
                return Task.FromResult<string?>(names + " → " + seen);
            });

            await t.Check("ui: Edit field — preview, ALTER with Turkish default and comment, a server error keeps the dialog open, the views refresh", async () =>
            {
                string sql;
                using (var dlg = new MyFront.UI.Dialogs.EditFieldForm(session, db.Database, table, "category"))
                {
                    dlg.Show(form);
                    await dlg.Loaded;
                    Field<TextBox>(dlg, "_name").Text = "kategori";
                    Field<ComboBox>(dlg, "_type").Text = "varchar(60)";
                    Field<CheckBox>(dlg, "_null").Checked = false;
                    Field<ComboBox>(dlg, "_default").SelectedIndex = (int)ColumnDefaultKind.Value;
                    Field<TextBox>(dlg, "_value").Text = "Diğer";
                    Field<TextBox>(dlg, "_comment").Text = "Ürün kategorisi 'ana'";
                    Field<ComboBox>(dlg, "_position").SelectedIndex = 2; // after id
                    sql = dlg.Sql;
                    Assert.Equal($"ALTER TABLE `{db.Database}`.`{table}` CHANGE COLUMN `category` `kategori` varchar(60) NOT NULL DEFAULT 'Diğer' COMMENT 'Ürün kategorisi ''ana''' AFTER `id`",
                        sql, "preview");
                    await Task.Delay(100);
                    Shot(dlg, "60-edit-field");
                    Field<ComboBox>(dlg, "_type").Text = "no_such_type(5)";
                    await dlg.ApplyAsync();
                    var state = Field<Label>(dlg, "_state").Text;
                    Assert.True(dlg.DialogResult == DialogResult.None && dlg.Visible && state.Contains("SQL Error"), "server error shown, dialog open: " + state);
                    Field<ComboBox>(dlg, "_type").Text = "varchar(60)";
                    await dlg.ApplyAsync();
                    Assert.True(dlg.DialogResult == DialogResult.OK, "applied: " + Field<Label>(dlg, "_state").Text);
                    dlg.Close();
                }
                view.AfterTableChanged(db.Database, table); // what OK does
                await Until(() => fields.Items.Cast<ListViewItem>().Any(i => i.Text == "kategori"), "Object Browser shows the new field");
                Assert.Equal("kategori", fields.Items[1].Text, "moved after id");
                Assert.Equal("NO", fields.Items[1].SubItems[2].Text, "NOT NULL");
                var col = (await session.Schema.GetTableAsync(db.Database, table)).Columns.Single(c => c.Name == "kategori");
                Assert.True(col is { Nullable: false, Default: "Diğer", Comment: "Ürün kategorisi 'ana'" }, $"{col.Default} / {col.Comment}");
                Assert.Equal("Sebze", (string?)await Scalar(session, db.Database, $"SELECT kategori FROM {table} WHERE name = 'Domates'"), "data kept");
                return sql;
            });

            await t.Check("ui: right-click an empty area of the field or index list → Add key…", () =>
            {
                var last = fields.Items[fields.Items.Count - 1].Bounds;
                using var empty = objects.BuildTableMenu(fields, new Point(last.X + 4, last.Bottom + 3))!;
                Assert.Equal("Add key…", string.Join("|", empty.Items.OfType<ToolStripMenuItem>().Select(i => i.Text)), "empty area of the fields");
                var indexes = objects.Controls.OfType<SplitContainer>().First().Panel2.Controls.OfType<ListView>().First();
                using var onIndexes = objects.BuildTableMenu(indexes, new Point(4, indexes.ClientSize.Height - 3))!;
                Assert.Equal("Add key…", string.Join("|", onIndexes.Items.OfType<ToolStripMenuItem>().Select(i => i.Text)), "index list");
                var r = fields.Items[0].Bounds;
                using var onField = objects.BuildTableMenu(fields, new Point(r.X + 4, r.Y + r.Height / 2))!;
                Assert.Equal("Edit field…|Add key…", string.Join("|", onField.Items.OfType<ToolStripMenuItem>().Select(i => i.Text)), "on a field");
                string? seen = null;
                var sw = Stopwatch.StartNew();
                using var timer = new System.Windows.Forms.Timer { Interval = 100 };
                timer.Tick += (_, _) =>
                {
                    var f = Application.OpenForms.OfType<MyFront.UI.Dialogs.AddKeyForm>().FirstOrDefault();
                    if (f is null || (f.AvailableList.Items.Count == 0 && sw.ElapsedMilliseconds < 15000)) return;
                    seen = f.Text + " · " + f.AvailableList.Items.Count + " fields";
                    timer.Stop();
                    f.Close();
                };
                timer.Start();
                empty.Items.OfType<ToolStripMenuItem>().Single().PerformClick(); // modal: returns when the timer closed it
                Assert.True(seen?.StartsWith("Add key — " + table) == true, "dialog: " + seen);
                return Task.FromResult<string?>(seen);
            });

            await t.Check("ui: Add key — drag & drop, buttons, prefix length, Unique / Fulltext, the TEXT length rule, preview, ALTER", async () =>
            {
                await Scalar(session, db.Database, $"ALTER TABLE {table} ADD COLUMN aciklama TEXT NULL");
                string Names(ListBox l) => string.Join(",", l.Items.Cast<object>());
                MyFront.UI.Dialogs.AddKeyForm.KeyField F(ListBox l, string name) => l.Items.Cast<MyFront.UI.Dialogs.AddKeyForm.KeyField>().First(f => f.Column.Name == name);
                string sql;
                using (var dlg = new MyFront.UI.Dialogs.AddKeyForm(session, db.Database, table))
                {
                    dlg.Show(form);
                    await dlg.Loaded;
                    var keys = dlg.KeyList;
                    var avail = dlg.AvailableList;
                    var ok = Field<Button>(dlg, "_ok");
                    var length = Field<NumericUpDown>(dlg, "_length");
                    var unique = Field<CheckBox>(dlg, "_unique");
                    var fulltext = Field<CheckBox>(dlg, "_fulltext");
                    Assert.True(!Field<RadioButton>(dlg, "_primary").Enabled, "Primary Key disabled: the table has one");
                    Assert.True(keys.Items.Count == 0 && !ok.Enabled, "Key Fields start empty, OK disabled");
                    Assert.Equal("id,kategori,name,unit,aciklama", Names(avail), "Available Fields = all fields, in table order");

                    dlg.DropFields(avail, new[] { F(avail, "unit") }, keys, keys.PointToScreen(new Point(4, 4))); // drag unit in
                    Assert.Equal("unit", Names(keys), "dropped into Key Fields");
                    Assert.Equal("idx_unit", Field<TextBox>(dlg, "_name").Text, "suggested name");
                    avail.ClearSelected();
                    avail.SelectedItem = F(avail, "name");
                    dlg.AddSelected();
                    Assert.Equal("unit,name", Names(keys), "◀ adds the selected field");
                    var first = keys.GetItemRectangle(0);
                    dlg.DropFields(keys, new[] { F(keys, "name") }, keys, keys.PointToScreen(new Point(first.X + 4, first.Y + 2)));
                    Assert.Equal("name,unit", Names(keys), "reordered by dragging inside Key Fields");
                    Assert.Equal("idx_name", Field<TextBox>(dlg, "_name").Text, "the suggestion follows the first field");
                    keys.ClearSelected();
                    keys.SelectedIndex = 0;
                    dlg.MoveSelected(1);
                    Assert.Equal("unit,name", Names(keys), "▼");
                    dlg.MoveSelected(-1);
                    Assert.Equal("name,unit", Names(keys), "▲");

                    keys.ClearSelected();
                    keys.SelectedItem = F(keys, "name");
                    Assert.True(length.Enabled, "length for a varchar field");
                    length.Value = 20;
                    Assert.Equal("name(20),unit", Names(keys), "prefix length shown");
                    Field<TextBox>(dlg, "_comment").Text = "Ürün 'arama' anahtarı";
                    Assert.Equal($"ALTER TABLE `{db.Database}`.`{table}` ADD INDEX `idx_name` (`name`(20), `unit`) COMMENT 'Ürün ''arama'' anahtarı'", dlg.Sql, "preview");

                    Assert.True(fulltext.Enabled, "Fulltext: all key fields are text");
                    unique.Checked = true;
                    fulltext.Checked = true;
                    Assert.True(!unique.Checked, "Unique and Fulltext exclude each other");
                    Assert.True(dlg.Sql.Contains("ADD FULLTEXT INDEX `idx_name` (`name`, `unit`)"), "fulltext takes whole fields: " + dlg.Sql);
                    keys.ClearSelected();
                    keys.SelectedIndex = 1;
                    dlg.MoveSelected(-1);
                    Assert.True(fulltext.Checked && dlg.Sql.Contains("(`unit`, `name`)"), "reordering keeps Fulltext: " + dlg.Sql);
                    dlg.MoveSelected(1);
                    unique.Checked = true;
                    Assert.True(!fulltext.Checked, "and back");

                    dlg.DropFields(avail, new[] { F(avail, "id") }, keys, keys.PointToScreen(new Point(4, keys.ClientSize.Height - 3)));
                    Assert.True(Names(keys) == "name(20),unit,id" && !fulltext.Enabled, "an int field: no Fulltext");
                    dlg.DropFields(keys, new[] { F(keys, "id") }, avail, avail.PointToScreen(new Point(4, 4)));
                    Assert.Equal("id,kategori,aciklama", Names(avail), "dragged back, in table order");

                    dlg.DropFields(avail, new[] { F(avail, "aciklama") }, keys, keys.PointToScreen(new Point(4, keys.ClientSize.Height - 3)));
                    var state = Field<Label>(dlg, "_state");
                    Assert.True(!ok.Enabled && state.Text.Contains("aciklama a length"), "TEXT without a length: " + state.Text);
                    keys.ClearSelected();
                    keys.SelectedItem = F(keys, "aciklama");
                    length.Value = 50;
                    Assert.True(ok.Enabled, "OK once it has a length: " + state.Text);
                    sql = dlg.Sql;
                    await Task.Delay(100);
                    Shot(dlg, "62-add-key");
                    await dlg.ApplyAsync();
                    Assert.True(dlg.DialogResult == DialogResult.OK, "applied: " + state.Text);
                    dlg.Close();
                }
                view.AfterTableChanged(db.Database, table); // what OK does
                var indexes = objects.Controls.OfType<SplitContainer>().First().Panel2.Controls.OfType<ListView>().First();
                await Until(() => indexes.Items.Cast<ListViewItem>().Any(i => i.Text == "idx_name"), "Object Browser shows the new key");
                var p = await TableProperties.LoadAsync(session, db.Database, table);
                var key = p.Keys.Single(k => k.Name == "idx_name");
                Assert.True(key is { Columns: "name(20), unit, aciklama(50)", Unique: true, Comment: "Ürün 'arama' anahtarı" }, $"{key.Columns} {key.Unique} {key.Comment}");
                return sql;
            });

            await t.Check("ui: Add key on a table without a primary key (like an imported one): Primary Key, add / remove all", async () =>
            {
                await Scalar(session, db.Database, "DROP TABLE IF EXISTS mf_ui_nokey; CREATE TABLE mf_ui_nokey (IL_KODU INT, YIL INT, IL_ADI VARCHAR(20)) DEFAULT CHARSET=utf8mb4");
                try
                {
                    using var dlg = new MyFront.UI.Dialogs.AddKeyForm(session, db.Database, "mf_ui_nokey");
                    dlg.Show(form);
                    await dlg.Loaded;
                    var primary = Field<RadioButton>(dlg, "_primary");
                    Assert.True(primary.Enabled, "Primary Key offered");
                    primary.Checked = true;
                    dlg.AddAll();
                    Assert.Equal("IL_KODU,YIL,IL_ADI", string.Join(",", dlg.KeyList.Items.Cast<object>()), "◀◀ adds all");
                    Assert.True(!Field<TextBox>(dlg, "_name").Enabled && !Field<CheckBox>(dlg, "_unique").Enabled, "no name / unique for the primary key");
                    Assert.Equal($"ALTER TABLE `{db.Database}`.`mf_ui_nokey` ADD PRIMARY KEY (`IL_KODU`, `YIL`, `IL_ADI`)", dlg.Sql, "primary key");
                    dlg.RemoveAll();
                    Assert.True(dlg.KeyList.Items.Count == 0 && dlg.AvailableList.Items.Count == 3 && dlg.Sql.Length == 0, "▶▶ removes all");
                    form.ToggleTheme();
                    try
                    {
                        dlg.AddAll();
                        ThemeManager.Apply(dlg);
                        await Task.Delay(150);
                        Shot(dlg, "63-dark-add-key");
                    }
                    finally { form.ToggleTheme(); }
                    dlg.Close();
                }
                finally { await Scalar(session, db.Database, "DROP TABLE IF EXISTS mf_ui_nokey"); }
            });

            await t.Check("ui: Edit field in dark mode; blocked for views", async () =>
            {
                form.ToggleTheme();
                try
                {
                    using var dlg = new MyFront.UI.Dialogs.EditFieldForm(session, db.Database, table, "unit");
                    dlg.Show(form);
                    await dlg.Loaded;
                    await Task.Delay(150);
                    Shot(dlg, "61-dark-edit-field");
                    dlg.Close();
                }
                finally { form.ToggleTheme(); }
                lock (Messages) Messages.Clear();
                Call(view, "EditField", new NodeInfo(NodeKind.View, session, db.Database, "v_latest_prices"), "city");
                Assert.True(Messages.Any(m => m.Contains("is a view")), "view: " + string.Join(" | ", Messages));
                Assert.True(!Application.OpenForms.OfType<MyFront.UI.Dialogs.EditFieldForm>().Any(), "no dialog for a view");
            });
        }
        finally
        {
            await Scalar(session, db.Database, $"DROP TABLE IF EXISTS {table}");
            await nav.RefreshNodeAsync((await nav.SelectDatabaseAsync(session, db.Database))!);
        }
    }

    /// <summary>Tree right-click: Export SQL, Delete, Import SQL (round trip), Rename, read-only refusals.</summary>
    private static async Task TreeCommandsUi(TestRunner t, TestDb db, MainForm form, ServerSession session)
    {
        var view = form.ViewOf(session)!;
        var nav = view.Navigator;
        var dir = Path.Combine(ExportTests.OutDir, "sql");
        Directory.CreateDirectory(dir);
        string Items(NodeInfo i)
        {
            using var m = (ContextMenuStrip)Call(nav, "BuildMenu", i)!;
            return string.Join("|", m.Items.OfType<ToolStripMenuItem>().Select(x => x.Text));
        }
        await EditorPropertyTests.ExecScript(session, db.Database, EditorPropertyTests.Setup);
        await Scalar(session, db.Database, "INSERT INTO mf_prop_parent VALUES (1, 'Kök İş'); INSERT INTO mf_prop_child (parent_id, ad) VALUES (1, 'ağaç'), (1, NULL)");
        try
        {
            await nav.RefreshNodeAsync((await nav.SelectDatabaseAsync(session, db.Database))!);

            await t.Check("ui: tree menus — table / view: Export SQL…, Rename…, Delete…; database: Import SQL…", () =>
            {
                var table = Items(new NodeInfo(NodeKind.Table, session, db.Database, "mf_prop_child"));
                var v = Items(new NodeInfo(NodeKind.View, session, db.Database, "mf_prop_v"));
                var database = Items(new NodeInfo(NodeKind.Database, session, db.Database));
                Assert.True(table.Contains("Export SQL…") && table.Contains("Rename…") && table.Contains("Delete…") && table.EndsWith("Properties…"), "table: " + table);
                Assert.True(v.Contains("Export SQL…") && v.Contains("Rename…") && v.Contains("Delete…"), "view: " + v);
                Assert.True(database.Contains("Import SQL…") && !database.Contains("Rename…") && !database.Contains("Export SQL…"), "database: " + database);
                return Task.FromResult<string?>(table);
            });

            var file = Path.Combine(dir, "mf_prop_child.sql");
            string? createBefore = null;
            await t.Check("ui: Export SQL… → <table>.sql: drop + create + rows + trigger, progress, done", async () =>
            {
                createBefore = await session.Schema.GetCreateStatementAsync(db.Database, "mf_prop_child", DbObjectType.Table);
                string state;
                using (var dlg = new MyFront.UI.Dialogs.SqlExportForm(session, db.Database, "mf_prop_child", false, 2))
                {
                    dlg.Show(form);
                    Assert.Equal("mf_prop_child.sql", Path.GetFileName(Field<TextBox>(dlg, "_path").Text), "default file name");
                    Field<TextBox>(dlg, "_path").Text = file;
                    if (File.Exists(file)) File.Delete(file);
                    await dlg.ExportAsync();
                    state = Field<Label>(dlg, "_state").Text;
                    await Task.Delay(100);
                    Shot(dlg, "64-export-sql");
                    dlg.Close();
                }
                Assert.True(state.StartsWith("Done: 2 rows"), state);
                var text = File.ReadAllText(file);
                Assert.True(text.Contains("DROP TABLE IF EXISTS `mf_prop_child`;") && text.Contains("INSERT INTO `mf_prop_child`") && text.Contains("'AĞAÇ'")
                            && text.Contains("DELIMITER ;;") && !text.Contains("DEFINER="), "contents");
                return state.Replace('\n', ' ');
            });

            await t.Check("ui: Delete… asks with the object's name and the SQL, drops the table, the tree follows", async () =>
            {
                var confirms = new List<string>();
                var old = Ui.ConfirmOverride;
                Ui.ConfirmOverride = text => { confirms.Add(text); return true; };
                try
                {
                    await nav.SelectObjectAsync(session, db.Database, "mf_prop_child");
                    Call(view, "OnTreeCommand", new NodeInfo(NodeKind.Table, session, db.Database, "mf_prop_child"), "drop");
                    var dbNode = nav.FindServer(session)!.Nodes.Cast<TreeNode>().First(n => n.Text == db.Database);
                    await Until(() => dbNode.Nodes.Count > 0 && NavigatorTree.Info(dbNode.Nodes[0])?.Kind != NodeKind.Loading
                                      && dbNode.Nodes.Cast<TreeNode>().All(n => n.Text != "mf_prop_child"), "table gone from the tree");
                }
                finally { Ui.ConfirmOverride = old; }
                Assert.True(confirms.Count == 1 && confirms[0].Contains($"{db.Database}.mf_prop_child") && confirms[0].Contains($"DROP TABLE `{db.Database}`.`mf_prop_child`"),
                    "confirmation: " + string.Join(" | ", confirms));
                Assert.Equal(0L, Convert.ToInt64(await Scalar(session, db.Database, "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'mf_prop_child'")), "dropped");
                return confirms[0].Split('\n')[0];
            });

            await t.Check("ui: Import SQL into the database recreates the exported table exactly (keys, foreign key, trigger, rows)", async () =>
            {
                string state, log;
                using (var f = new MyFront.UI.Dialogs.RunSqlFileForm(session, db.Database, file, "Import SQL into " + db.Database))
                {
                    f.Show(form);
                    await (Task)Call(f, "RunAsync")!;
                    state = Field<Label>(f, "_state").Text;
                    log = Field<TextBox>(f, "_log").Text;
                    Shot(f, "65-import-sql");
                    f.Close();
                }
                Assert.True(state.Contains(" 0 error(s)"), state + " / " + log);
                session.Schema.Invalidate(db.Database);
                Assert.Equal(createBefore, await session.Schema.GetCreateStatementAsync(db.Database, "mf_prop_child", DbObjectType.Table), "SHOW CREATE TABLE");
                Assert.Equal(2L, Convert.ToInt64(await Scalar(session, db.Database, "SELECT COUNT(*) FROM mf_prop_child")), "rows");
                Assert.Equal("AĞAÇ", (string?)await Scalar(session, db.Database, "SELECT ad FROM mf_prop_child WHERE ad IS NOT NULL"), "Turkish text (not upper-cased twice)");
                Assert.Equal(1L, Convert.ToInt64(await Scalar(session, db.Database, "SELECT COUNT(*) FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA = DATABASE() AND TRIGGER_NAME = 'mf_prop_child_bi'")), "trigger");
                return state;
            });

            await t.Check("ui: Rename… a table and a view (RENAME TABLE); a taken name keeps the dialog open with the error", async () =>
            {
                using (var dlg = new MyFront.UI.Dialogs.RenameForm(session, db.Database, "mf_prop_part", false))
                {
                    dlg.Show(form);
                    Assert.True(!Field<Button>(dlg, "_ok").Enabled, "same name: nothing to do");
                    Field<TextBox>(dlg, "_name").Text = "mf_prop_bölüm";
                    Assert.Equal($"RENAME TABLE `{db.Database}`.`mf_prop_part` TO `{db.Database}`.`mf_prop_bölüm`", dlg.Sql, "preview");
                    await Task.Delay(100);
                    Shot(dlg, "66-rename");
                    await dlg.ApplyAsync();
                    Assert.True(dlg.DialogResult == DialogResult.OK, Field<Label>(dlg, "_state").Text);
                    dlg.Close();
                }
                using (var dlg = new MyFront.UI.Dialogs.RenameForm(session, db.Database, "mf_prop_bölüm", false))
                {
                    dlg.Show(form);
                    Field<TextBox>(dlg, "_name").Text = "mf_prop_parent";
                    await dlg.ApplyAsync();
                    var state = Field<Label>(dlg, "_state").Text;
                    Assert.True(dlg.DialogResult == DialogResult.None && state.Contains("SQL Error"), "taken name: " + state);
                    dlg.Close();
                }
                using (var dlg = new MyFront.UI.Dialogs.RenameForm(session, db.Database, "mf_prop_v", true))
                {
                    dlg.Show(form);
                    Field<TextBox>(dlg, "_name").Text = "mf_prop_görünüm";
                    await dlg.ApplyAsync();
                    Assert.True(dlg.DialogResult == DialogResult.OK, Field<Label>(dlg, "_state").Text);
                    dlg.Close();
                }
                var names = new List<string>();
                await using (var conn = await session.OpenAsync(db.Database))
                await using (var cmd = new MySqlCommand("SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME LIKE 'mf\\_prop\\_%' ORDER BY 1", conn))
                await using (var r = await cmd.ExecuteReaderAsync())
                    while (await r.ReadAsync()) names.Add(r.GetString(0));
                Assert.True(names.Contains("mf_prop_bölüm") && names.Contains("mf_prop_görünüm") && !names.Contains("mf_prop_part") && !names.Contains("mf_prop_v"), string.Join(", ", names));
                return string.Join(", ", names);
            });

            await t.Check("ui: read-only connection — Rename, Delete and Import SQL are refused; Export SQL works", async () =>
            {
                var ro = await ServerSession.ConnectAsync(db.ToAccount(TlsMode.Required, readOnly: true), db.Password);
                await form.AddSessionAsync(ro);
                try
                {
                    var roView = form.ViewOf(ro)!;
                    var table = new NodeInfo(NodeKind.Table, ro, db.Database, "mf_prop_parent");
                    foreach (var (command, node) in new[] { ("rename", table), ("drop", table), ("import-sql", new NodeInfo(NodeKind.Database, ro, db.Database)) })
                    {
                        lock (Messages) Messages.Clear();
                        Call(roView, "OnTreeCommand", node, command);
                        Assert.True(Messages.Any(m => m.Contains("read-only connection")), command + ": " + string.Join(" | ", Messages));
                    }
                    var roFile = Path.Combine(dir, "read-only.sql");
                    await MyFront.Core.Export.SqlDump.WriteAsync(ro, db.Database, new[] { new MyFront.Core.Export.SqlDumpObject("mf_prop_parent", false) }, roFile,
                        new MyFront.Core.Export.SqlDumpOptions(), new MyFront.Core.Export.ExportProgress(), CancellationToken.None);
                    Assert.True(File.ReadAllText(roFile).Contains("INSERT INTO `mf_prop_parent`"), "export from a read-only connection");
                }
                finally { form.CloseSession(ro); }
            });
        }
        finally
        {
            await EditorPropertyTests.ExecScript(session, db.Database, EditorPropertyTests.Cleanup + " DROP VIEW IF EXISTS `mf_prop_görünüm`; DROP TABLE IF EXISTS `mf_prop_bölüm`;");
            await nav.RefreshNodeAsync((await nav.SelectDatabaseAsync(session, db.Database))!);
        }
    }

    private static async Task EditorTabsUi(TestRunner t, TestDb db, MainForm form, ServerSession main)
    {
        var accountA = db.ToAccount(TlsMode.Required);
        accountA.Name = "editors A";
        var accountB = db.ToAccount(TlsMode.Required);
        accountB.Name = "editors B";
        ServerSession? a = null, b = null;
        var visible = () => form.Tabs.TabPages.OfType<SqlEditorTab>().ToList();
        try
        {
            await t.Check("ui: a connection opens with SQL Editor 1–5; typing is autosaved within a second", async () =>
            {
                a = await ServerSession.ConnectAsync(accountA, db.Password);
                await form.AddSessionAsync(a);
                var tabs = form.EditorTabsOf(a);
                Assert.Equal("SQL Editor 1,SQL Editor 2,SQL Editor 3,SQL Editor 4,SQL Editor 5", string.Join(",", tabs.Select(x => x.Text)), "tabs");
                Assert.True(visible().Count == 5 && visible().All(x => x.Session == a), "only this connection's 5 tabs in the strip: " + string.Join(", ", visible().Select(x => x.Text)));
                form.ShowSqlTab(tabs[1]);
                tabs[1].Editor.Text = "SELECT 'ikinci sekme' AS s;";
                tabs[3].Editor.Text = "SELECT 4;\nSELECT 'dört';";
                tabs[3].Editor.SetEmptySelection(9);
                var sw = Stopwatch.StartNew();
                await Until(() => EditorStore.ReadText(accountA.Id, 2) == "SELECT 'ikinci sekme' AS s;", "autosave of tab 2", 3000);
                await Until(() => EditorStore.ReadText(accountA.Id, 4).Contains("dört"), "autosave of tab 4", 3000);
                var ms = sw.ElapsedMilliseconds;
                await Task.Delay(100);
                Shot(form, "30-editor-tabs");
                return $"saved {ms} ms after the last change";
            });

            await t.Check("ui: SQL Editor 1–5 can't be closed; an extra tab closes without a prompt, its text kept in SQL history", () =>
            {
                int prompts = 0;
                var confirm = Ui.ConfirmOverride;
                Ui.ConfirmOverride = _ => { prompts++; return true; };
                try
                {
                    var tabs = form.EditorTabsOf(a!);
                    Call(form, "CloseSqlTab", tabs[0], false);
                    Assert.True(form.EditorTabsOf(a!).Contains(tabs[0]), "fixed tab stays");
                    var extra = form.NewSqlTab(a)!;
                    Assert.Equal("SQL Editor 6", extra.Text, "extra tab title");
                    Assert.True(form.Tabs.SelectedTab == extra, "selected");
                    extra.Editor.Text = "SELECT 'altıncı' AS gecici;";
                    extra.SaveNow();
                    Assert.Equal("SELECT 'altıncı' AS gecici;", EditorStore.ReadText(accountA.Id, 6), "extra tab autosaved");
                    Call(form, "CloseSqlTab", extra, false);
                    Assert.True(!form.EditorTabsOf(a!).Contains(extra) && !visible().Contains(extra), "closed");
                    Assert.Equal("", EditorStore.ReadText(accountA.Id, 6), "its file removed");
                    var h = SqlHistory.Read(accountA.Id);
                    Assert.True(h.Any(e => e.Sql.Contains("altıncı") && e.Info.Contains("closed tab SQL Editor 6")), "in history: " + (h.Count > 0 ? h[0].Info : "(empty)"));
                    Assert.Equal(0, prompts, "no prompts");
                }
                finally { Ui.ConfirmOverride = confirm; }
                return Task.CompletedTask;
            });

            await t.Check("ui: close right after typing → nothing lost; reconnect restores the text, caret and last tab", async () =>
            {
                var tabs = form.EditorTabsOf(a!);
                tabs[2].Editor.Text = "SELECT 'son anda yazıldı';"; // no wait: closing must write it
                form.ShowSqlTab(tabs[3]);
                tabs[3].Editor.SetEmptySelection(9);
                form.CloseSession(a!);
                a = null;
                Assert.Equal("SELECT 'son anda yazıldı';", EditorStore.ReadText(accountA.Id, 3), "written on close");
                Assert.True(form.ActiveView?.Session == main && visible().All(x => x.Session == main), "the other connection's tab is in front");

                var sw = Stopwatch.StartNew();
                a = await ServerSession.ConnectAsync(accountA, db.Password);
                var connectMs = sw.ElapsedMilliseconds;
                sw.Restart();
                await form.AddSessionAsync(a);
                var restoreMs = sw.ElapsedMilliseconds;
                tabs = form.EditorTabsOf(a);
                Assert.Equal(5, tabs.Count, "the closed extra tab stayed closed");
                Assert.Equal("SELECT 'ikinci sekme' AS s;", tabs[1].Editor.Text, "tab 2");
                Assert.Equal("SELECT 'son anda yazıldı';", tabs[2].Editor.Text, "tab 3");
                Assert.Equal("SELECT 4;\nSELECT 'dört';", tabs[3].Editor.Text, "tab 4");
                Assert.Equal(9, tabs[3].Editor.CurrentPosition, "caret");
                Assert.True(form.Tabs.SelectedTab == tabs[3], "closed in SQL Editor 4, opens there again: " + form.Tabs.SelectedTab?.Text);
                Assert.True(!tabs[1].Editor.CanUndo, "restored text is not an undo step");
                // what the 5 editors cost when a connection opens (the rest of restoreMs is the tree from the server)
                sw.Restart();
                var probe = new List<SqlEditorTab>();
                for (int i = 1; i <= EditorStore.FixedTabs; i++)
                {
                    var tab = new SqlEditorTab(form, a) { Slot = 100 + i };
                    tab.Restore(new EditorTabState { Slot = 100 + i }, "SELECT 1;");
                    probe.Add(tab);
                }
                var editorsMs = sw.ElapsedMilliseconds;
                probe.ForEach(x => x.Dispose());
                return $"connect {connectMs} ms, tabs + tree {restoreMs} ms (5 editors alone {editorsMs} ms)";
            });

            await t.Check("ui: each connection is its own top-level tab with its own tree and editors; switching keeps text and runs", async () =>
            {
                b = await ServerSession.ConnectAsync(accountB, db.Password);
                await form.AddSessionAsync(b);
                var tb = form.EditorTabsOf(b);
                Assert.True(form.ActiveView?.Session == b && form.Tabs.SelectedTab == form.ObjectPage, "B's tab in front, on the Object Browser");
                Assert.True(tb.All(x => x.Editor.TextLength == 0), "B starts empty");
                Assert.True(visible().Count == 5 && visible().All(x => x.Session == b), "only B's tabs");
                var titles = string.Join("|", form.ConnectionTabs.TabPages.Cast<TabPage>().Select(p => p.Text));
                Assert.Equal($"{main.DisplayName}|editors A|editors B|+", titles, "connection tabs");
                var treeA = form.ViewOf(a!)!.Navigator;
                var treeB = form.ViewOf(b)!.Navigator;
                Assert.True(treeA != treeB && treeB.Nodes.Count == 1 && treeB.FindServer(a!) is null && treeA.FindServer(b) is null, "separate trees");
                tb[1].Editor.Text = "SELECT 'B bağlantısı';";
                form.ShowConnection(form.ViewOf(a!)!); // like clicking A's tab
                var ta = form.EditorTabsOf(a!);
                Assert.True(visible().Count == 5 && visible().All(x => x.Session == a), "A's tabs back");
                Assert.True(form.Navigator == treeA, "A's tree");
                Assert.Equal("SELECT 'ikinci sekme' AS s;", ta[1].Editor.Text, "A's text survived while B was in front");
                form.ShowSqlTab(ta[1]);
                await ta[1].RunAsync(RunScope.All);
                Assert.True(ta[1].ResultTabs.Count == 1 && ta[1].ResultTabs[0].Data.Columns[0].Name == "s", "query ran in A's tab");
                await Until(() => EditorStore.ReadText(accountB.Id, 2) == "SELECT 'B bağlantısı';", "B autosaved", 3000);
                Assert.Equal("SELECT 'ikinci sekme' AS s;", EditorStore.ReadText(accountA.Id, 2), "A's file untouched");
                form.ShowSqlTab(tb[1]);
                Assert.True(form.ActiveView?.Session == b && form.Tabs.SelectedTab == tb[1], "ShowSqlTab brings B's tab to the front");
                await Task.Delay(100);
                Shot(form, "31-connection-tabs");
                return titles;
            });

            await t.Check("ui: opening an account that is already open switches to its tab (no second connection)", async () =>
            {
                form.ShowConnection(form.ViewOf(a!)!);
                int count = form.Sessions.Count;
                var dup = await ServerSession.ConnectAsync(accountB, db.Password);
                await form.AddSessionAsync(dup);
                Assert.Equal(count, form.Sessions.Count, "no second tab");
                Assert.True(form.ActiveView?.Session == b, "B's tab in front");
                var accounts = AccountStore.Load();
                try
                {
                    AccountStore.Save(new[] { accountB });
                    using var dlg = new MyFront.UI.Dialogs.AccountsForm(id => form.Views.Any(v => v.Session.Account.Id == id));
                    dlg.Show(form);
                    await (Task)Call(dlg, "OpenAsync")!;
                    Assert.True(dlg.OpenAccountId == accountB.Id && dlg.Session is null, "the Accounts dialog doesn't connect again");
                    dlg.Close();
                }
                finally { AccountStore.Save(accounts); }
            });

            await t.Check("ui: dark mode reaches the tabs of a connection that was not in front", async () =>
            {
                form.ShowConnection(form.ViewOf(b!)!);
                form.ToggleTheme();
                try
                {
                    form.ShowConnection(form.ViewOf(a!)!);
                    var ta = form.EditorTabsOf(a!);
                    form.ShowSqlTab(ta[1]);
                    Assert.Equal(Theme.Dark.EdBack, ta[1].Editor.Styles[ScintillaNET.Style.Default].BackColor, "editor background");
                    Assert.Equal(Theme.Dark.Window, form.Navigator.BackColor, "A's tree");
                    await Task.Delay(150);
                    Shot(form, "39-dark-connection-tabs");
                }
                finally { form.ToggleTheme(); }
                Assert.Equal(Theme.Light.EdBack, form.EditorTabsOf(a!)[1].Editor.Styles[ScintillaNET.Style.Default].BackColor, "light again");
            });

            await t.Check("ui: middle-click on a connection tab closes it, its editors saved first", () =>
            {
                var tb = form.EditorTabsOf(b!);
                tb[2].Editor.Text = "SELECT 'kapatmadan önce';";
                int index = form.ConnectionTabs.TabPages.IndexOf(form.ViewOf(b!)!);
                var r = form.ConnectionTabs.GetTabRect(index);
                Call(form, "OnConnectionTabsMouseUp", form.ConnectionTabs,
                    new MouseEventArgs(MouseButtons.Middle, 1, r.X + r.Width / 2, r.Y + r.Height / 2, 0));
                Assert.True(form.ViewOf(b!) is null && !form.Sessions.Contains(b!), "B's tab closed");
                Assert.Equal("SELECT 'kapatmadan önce';", EditorStore.ReadText(accountB.Id, 3), "saved on close");
                b = null;
                return Task.CompletedTask;
            });
        }
        finally
        {
            if (a is not null) form.CloseSession(a);
            if (b is not null) form.CloseSession(b);
            await form.ViewOf(main)!.Navigator.SelectDatabaseAsync(main, db.Database);
        }
    }

    private static async Task QuickOnly(TestRunner t, TestDb db, MainForm form)
    {
        var session = await ServerSession.ConnectAsync(db.ToAccount(TlsMode.Required), db.Password);
        await form.AddSessionAsync(session);
        await QuickUi(t, db, form, session);
        form.CloseSession(session);
    }

    private static bool PressKeys(MainForm form, Keys keys)
    {
        var m = typeof(MainForm).GetMethod("ProcessCmdKey", BindingFlags.NonPublic | BindingFlags.Instance)!;
        // a real WM_KEYDOWN for the window: menu shortcuts (F9 …) are matched through the message's window handle
        var msg = Message.Create(form.Handle, 0x0100, (IntPtr)(int)(keys & Keys.KeyCode), IntPtr.Zero);
        var args = new object[] { msg, keys };
        return (bool)m.Invoke(form, args)!;
    }

    private static async Task QuickUi(TestRunner t, TestDb db, MainForm form, ServerSession session)
    {
        var settings = MyFront.Core.AppSettings.Current;
        settings.ExportFolder = Path.Combine(ExportTests.OutDir, "quick");
        var opened = new List<string>();
        Ui.OpenPathOverride = p => opened.Add(p);
        var asked = new List<string>();
        MyFront.UI.Dialogs.ParameterDialog.Override = ph =>
        {
            asked.AddRange(ph.Select(p => p.Name));
            return ph.ToDictionary(p => p.Name, p => (object?)(p.Name switch { "city" => "İzmir", "ad" => "a", "id" => 2L, _ => null }), StringComparer.OrdinalIgnoreCase);
        };
        var confirms = new List<string>();
        bool answer = true;
        Ui.ConfirmOverride = text => { confirms.Add(text); return answer; };
        await Scalar(session, db.Database, "DROP TABLE IF EXISTS mf_qq; CREATE TABLE mf_qq (id INT PRIMARY KEY, v INT); INSERT INTO mf_qq VALUES (1, 10), (2, 20)");
        var acc = session.Account.Id;
        var queries = new List<MyFront.Core.Quick.QuickQuery>
        {
            new() { Name = "İzmir fiyatları", Color = "#0078D4", Hotkey = 1, AccountId = acc, AccountName = session.DisplayName, Database = db.Database,
                    Sql = "SELECT city, COUNT(*) AS n, ROUND(AVG(avg_price), 2) AS ortalama FROM prices WHERE city = {{city}} GROUP BY city" },
            new() { Name = "Ürün raporu", ToExcel = true, Hotkey = 2, AccountId = acc, Database = db.Database,
                    Sql = "SELECT id, name, category FROM products WHERE name LIKE CONCAT('%', {{ad}}, '%') ORDER BY id" },
            new() { Name = "Değer sıfırla", Icon = "flash", Color = "#C42B1C", Hotkey = 3, AccountId = acc, Database = db.Database,
                    Sql = "UPDATE mf_qq SET v = 0 WHERE id = {{id:int}}" },
            new() { Name = "Şehirler", Icon = "globe", Sql = "SELECT DISTINCT city FROM prices ORDER BY city", Database = db.Database },
        };
        try
        {
            await t.Check("ui: Quick Query bar shows the buttons (colour, icon, Excel)", async () =>
            {
                form.SetQuickQueries(queries);
                Assert.Equal(4, form.QuickBar.QueryButtons.Count(), "buttons");
                Assert.True(File.Exists(MyFront.Core.Quick.QuickQueryStore.FilePath), "quickqueries.json written");
                await Task.Delay(100);
                Shot(form, "50-quick-bar");
            });

            await t.Check("ui: click → parameter → result on the one quick query page; every button reuses it; Shift → new tab", async () =>
            {
                asked.Clear();
                var tabsBefore = form.Tabs.TabPages.Count;
                await form.RunQuickQueryAsync(queries[0], newTab: false);
                var tab = form.Tabs.TabPages.OfType<SqlEditorTab>().Single(x => x.IsQuickPage);
                Assert.Equal("city", string.Join(",", asked), "asked for {{city}}");
                Assert.True(tab.ResultTabs.Count == 1 && (string?)tab.ResultTabs[0].Data.Rows.GetRow(0)[0] == "İzmir", "İzmir row");
                Assert.True(tab.Text.StartsWith("⚡ İzmir"), "tab title " + tab.Text);
                Shot(form, "51-quick-result");
                var n = tab.ResultTabs[0].Data.Rows.GetRow(0)[1];
                await form.RunQuickQueryAsync(queries[0], newTab: false);
                Assert.Equal(tabsBefore + 1, form.Tabs.TabPages.Count, "same page reused");
                var other = queries.First(q => !q.ToExcel && !q.Sql.Contains("{{") && !MyFront.Core.Db.SqlClassifier.ContainsDangerous(q.Sql));
                await form.RunQuickQueryAsync(other, newTab: false);
                Assert.Equal(tabsBefore + 1, form.Tabs.TabPages.Count, "another button: still one page");
                Assert.True(tab.Text == "⚡ " + other.Name && tab.QuickQueryId == other.Id && tab.Editor.Text == other.Sql, "page now shows " + tab.Text);
                await form.RunQuickQueryAsync(queries[0], newTab: true);
                Assert.Equal(tabsBefore + 2, form.Tabs.TabPages.Count, "Shift → another tab");
                Assert.Equal(1, form.Tabs.TabPages.OfType<SqlEditorTab>().Count(x => x.IsQuickPage), "still one quick page");
                return $"n = {n}";
            });

            await t.Check("ui: a button pressed while the quick page still runs the previous one cancels it and shows the new one", async () =>
            {
                var tab = form.Tabs.TabPages.OfType<SqlEditorTab>().Single(x => x.IsQuickPage);
                var slow = new MyFront.Core.Quick.QuickQuery { Name = "Yavaş", Sql = "SELECT SLEEP(10) AS bekle", AccountId = queries[0].AccountId, Database = db.Database };
                var other = queries.First(q => !q.ToExcel && !q.Sql.Contains("{{") && !MyFront.Core.Db.SqlClassifier.ContainsDangerous(q.Sql));
                var first = form.RunQuickQueryAsync(slow, newTab: false);
                await Until(() => tab.IsRunning && tab.Text == "⚡ Yavaş", "slow one running");
                var sw = Stopwatch.StartNew();
                await form.RunQuickQueryAsync(other, newTab: false);
                await first;
                Assert.True(sw.ElapsedMilliseconds < 5000, $"didn't wait for the slow query ({sw.ElapsedMilliseconds} ms)");
                Assert.True(tab.Text == "⚡ " + other.Name && !tab.IsRunning && tab.ResultTabs.Count > 0, "now shows " + tab.Text);
                return $"switched after {sw.ElapsedMilliseconds} ms";
            });

            await t.Check("ui: Ctrl+2 → straight to Excel, file opened", async () =>
            {
                opened.Clear();
                Assert.True(PressKeys(form, Keys.Control | Keys.D2), "Ctrl+2 handled");
                await Until(() => opened.Count == 1, "export opened", 30000);
                var rows = MiniExcelLibs.MiniExcel.Query(opened[0], useHeaderRow: true).Cast<IDictionary<string, object?>>().ToList();
                Assert.Equal(Convert.ToInt64(await Scalar(session, db.Database, "SELECT COUNT(*) FROM products WHERE name LIKE '%a%'")), (long)rows.Count, "rows matching {{ad}} = 'a'");
                Assert.True(Path.GetFileName(opened[0]).StartsWith("Ürün raporu_"), opened[0]);
                return $"{rows.Count} rows → {Path.GetFileName(opened[0])}";
            });

            await t.Check("ui: UPDATE button asks first — No leaves the data, Yes runs it (Ctrl+3)", async () =>
            {
                confirms.Clear();
                answer = false;
                await form.RunQuickQueryAsync(queries[2], newTab: false);
                Assert.True(confirms.Count == 1 && confirms[0].Contains("UPDATE"), "confirmation shown: " + string.Join(" | ", confirms));
                Assert.Equal(20, Convert.ToInt32(await Scalar(session, db.Database, "SELECT v FROM mf_qq WHERE id = 2")), "not run after No");
                answer = true;
                Assert.True(PressKeys(form, Keys.Control | Keys.D3), "Ctrl+3 handled");
                await Until(() => form.Tabs.TabPages.OfType<SqlEditorTab>().Any(x => x.QuickQueryId == queries[2].Id && !x.IsRunning && x.Messages.Items.Count > 0), "update ran");
                Assert.Equal(0, Convert.ToInt32(await Scalar(session, db.Database, "SELECT v FROM mf_qq WHERE id = 2")), "updated after Yes");
                Assert.Equal(10, Convert.ToInt32(await Scalar(session, db.Database, "SELECT v FROM mf_qq WHERE id = 1")), "only the parameter row");
                return confirms[0].Split('\n')[0];
            });

            await t.Check("ui: button whose connection is missing tells you instead of guessing", async () =>
            {
                lock (Messages) Messages.Clear();
                await form.RunQuickQueryAsync(new MyFront.Core.Quick.QuickQuery { Name = "eski", AccountId = "nope", AccountName = "Old server", Sql = "SELECT 1" }, false);
                Assert.True(Messages.Any(m => m.Contains("doesn't exist")), string.Join(" | ", Messages));
            });

            await t.Check("ui: quick query editor — add, reorder, hotkey moves, export / import", async () =>
            {
                using var ed = new MyFront.UI.Dialogs.QuickQueryEditor(form.QuickQueries, Array.Empty<MyFront.Core.Accounts.Account>(),
                    _ => Task.FromResult(new List<string> { db.Database }), queries[0].Id);
                ed.Show(form);
                await Task.Delay(200);
                Shot(ed, "52-quick-editor");
                Call(ed, "AddNew");
                Field<TextBox>(ed, "_name").Text = "Yeni sorgu";
                Field<ComboBox>(ed, "_hotkey").SelectedIndex = 1; // Ctrl+1 → taken from "İzmir fiyatları"
                ed.MoveSelected(-1);
                var list = ed.Result;
                Assert.Equal("İzmir fiyatları,Ürün raporu,Değer sıfırla,Yeni sorgu,Şehirler", string.Join(",", list.Select(q => q.Name)), "order after move up");
                Assert.True(list.Single(q => q.Name == "Yeni sorgu").Hotkey == 1 && list.Single(q => q.Name == "İzmir fiyatları").Hotkey is null, "hotkey moved");
                var file = Path.Combine(ExportTests.OutDir, "quick", "my-quick.json");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                ed.ExportTo(file);
                ed.ImportList(MyFront.Core.Quick.QuickQueryStore.Import(file, Array.Empty<MyFront.Core.Accounts.Account>()), replace: false);
                Assert.Equal(5, ed.Result.Count, "re-import of the same file updates, doesn't duplicate");
                ed.Close();
                return string.Join(", ", list.Select(q => q.Name + (q.Hotkey is { } k ? $" [Ctrl+{k}]" : "")));
            });

            await t.Check("ui: parameter dialog and editor in dark mode", async () =>
            {
                MyFront.UI.Dialogs.ParameterDialog.Override = null;
                form.ToggleTheme();
                try
                {
                    using var pd = new MyFront.UI.Dialogs.ParameterDialog("İzmir fiyatları", queries[0].Sql + " AND d >= {{from:date}}",
                        MyFront.Core.Quick.ParameterParser.Find(queries[0].Sql + " AND d >= {{from:date}} AND n > {{n:int}}"));
                    pd.Show(form);
                    await Task.Delay(150);
                    Shot(pd, "53-quick-parameters-dark");
                    pd.Close();
                    using var ed = new MyFront.UI.Dialogs.QuickQueryEditor(form.QuickQueries, Array.Empty<MyFront.Core.Accounts.Account>(),
                        _ => Task.FromResult(new List<string>()), queries[2].Id);
                    ed.Show(form);
                    await Task.Delay(200);
                    Shot(ed, "54-quick-editor-dark");
                    ed.Close();
                }
                finally { form.ToggleTheme(); }
            });
        }
        finally
        {
            Ui.OpenPathOverride = null;
            Ui.ConfirmOverride = _ => true;
            MyFront.UI.Dialogs.ParameterDialog.Override = null;
            form.SetQuickQueries(new List<MyFront.Core.Quick.QuickQuery>());
            await Scalar(session, db.Database, "DROP TABLE IF EXISTS mf_qq");
        }
    }

    private static async Task ImportOnly(TestRunner t, TestDb db, MainForm form)
    {
        var session = await ServerSession.ConnectAsync(db.ToAccount(TlsMode.Required), db.Password);
        await form.AddSessionAsync(session);
        await ImportUi(t, db, form, session);
        form.CloseSession(session);
    }

    private static async Task ImportUi(TestRunner t, TestDb db, MainForm form, ServerSession session)
    {
        var fasil = db.ImportSamples.FirstOrDefault(p => p.Contains("FASIL") && File.Exists(p));
        var dir = Path.Combine(ImportTests.OutDir, "ui");
        Directory.CreateDirectory(dir);
        await Scalar(session, db.Database, "DROP TABLE IF EXISTS mf_wiz_fasil, mf_wiz_new, mf_wiz_bad");
        try
        {
            if (fasil is not null)
            {
                await Scalar(session, db.Database, """
                    CREATE TABLE mf_wiz_fasil (`Yıl` INT, `ay` INT, `Fasıl` INT, `FASIL ADI` VARCHAR(300), `Ülke` INT, `ulke_adi` VARCHAR(100),
                      `İthalat Dolar` DECIMAL(20,2), `ihracat_dolar` DECIMAL(20,2), PRIMARY KEY (`Yıl`, ay, `Fasıl`, `Ülke`)) DEFAULT CHARSET=utf8mb4
                    """);
                await t.Check("ui: import wizard — your FASIL file, columns written differently, all auto-matched, Upsert", async () =>
                {
                    using var w = new MyFront.UI.Dialogs.ImportWizard(form, session, db.Database, "mf_wiz_fasil");
                    w.Show(form);
                    await Until(() => Field<ComboBox>(w, "_tables").Items.Count > 0, "tables");
                    await w.LoadFileAsync(fasil);
                    Assert.Equal(8, Field<IReadOnlyList<string>>(w, "_headers").Count, "headers");
                    var info = Field<Label>(w, "_sourceInfo");
                    await Until(() => info.Text.Contains("106.745 rows") || info.Text.Contains("106,745 rows"), "real row count: " + info.Text);
                    Shot(w, "40-import-source");
                    await (Task)Call(w, "NextAsync")!;
                    await Until(() => Field<TableInfo?>(w, "_tableInfo")?.Name == "mf_wiz_fasil", "table info");
                    await (Task)Call(w, "NextAsync")!;
                    var grid = Field<DataGridView>(w, "_mapGrid");
                    var mapped = grid.Rows.Cast<DataGridViewRow>().Select(r => $"{r.Cells[0].Value}→{r.Cells[2].Value}").ToList();
                    Assert.True(grid.Rows.Cast<DataGridViewRow>().All(r => r.Cells[2].Value as string != "— skip —"), "all matched: " + string.Join(", ", mapped));
                    Field<RadioButton>(w, "_mUpsert").Checked = true;
                    Shot(w, "41-import-mapping");
                    await w.RunImportAsync();
                    Assert.True(w.Result is { Committed: true, Failed: 0 }, w.Result?.Error ?? "");
                    Shot(w, "42-import-done");
                    w.Close();
                    return $"{w.Result!.Inserted:N0} rows in {w.Result.Elapsed.TotalSeconds:0.0} s; " + string.Join(", ", mapped);
                });

                await t.Check("ui: repeat import of the next download \"(12)\" — remembered, Import now", async () =>
                {
                    var next = Path.Combine(dir, MyFront.Core.Import.MappingStore.FileKey(fasil).ToUpperInvariant() + " (12).xlsx"); // the next download of the same file
                    File.Copy(fasil, next, overwrite: true);
                    using var w = new MyFront.UI.Dialogs.ImportWizard(form, session, db.Database);
                    w.Show(form);
                    await Until(() => Field<ComboBox>(w, "_tables").Items.Count > 0, "tables");
                    await w.LoadFileAsync(next);
                    Assert.True(Field<FlowLayoutPanel>(w, "_remember").Visible, "remembered banner shown");
                    Shot(w, "43-import-remembered");
                    await w.ImportNowAsync();
                    var r = w.Result!;
                    Assert.True(r.Committed && r.Inserted == 0 && r.Updated == 0 && r.Unchanged == r.Rows, $"upsert again: {r.Inserted}/{r.Updated}/{r.Unchanged}");
                    w.Close();
                    return Field<Label>(w, "_rememberText").Text;
                });
            }
            else Console.WriteLine("SKIP  ui: import wizard with your files (importSamples not found)");

            var tr = Path.Combine(dir, "Türkçe Fiyatlar.xlsx");
            MiniExcelLibs.MiniExcel.SaveAs(tr, new List<Dictionary<string, object?>>
            {
                new() { ["Şehir"] = "İzmir", ["Fiyat"] = "1.234,56", ["Tarih"] = "29.02.2024", ["Kod"] = "007" },
                new() { ["Şehir"] = "Muğla", ["Fiyat"] = "99,9", ["Tarih"] = "01.03.2024", ["Kod"] = "010" },
            }, overwriteFile: true);
            await t.Check("ui: import wizard — new table with editable inferred types", async () =>
            {
                using var w = new MyFront.UI.Dialogs.ImportWizard(form, session, db.Database);
                w.Show(form);
                await Until(() => Field<ComboBox>(w, "_tables").Items.Count > 0, "tables");
                await w.LoadFileAsync(tr);
                Field<RadioButton>(w, "_newTable").Checked = true;
                Field<TextBox>(w, "_newName").Text = "mf_wiz_new";
                await (Task)Call(w, "NextAsync")!;
                await Until(() => Field<List<MyFront.Core.Import.InferredColumn>?>(w, "_inferred") is not null, "inference");
                var types = Field<DataGridView>(w, "_newColumns").Rows.Cast<DataGridViewRow>().Select(r => $"{r.Cells[2].Value} {r.Cells[3].Value}").ToList();
                Assert.Equal("Şehir VARCHAR(10),Fiyat DECIMAL(6,2),Tarih DATE,Kod VARCHAR(10)", string.Join(",", types), "inferred");
                Shot(w, "44-import-new-table");
                await (Task)Call(w, "NextAsync")!;
                await w.RunImportAsync();
                Assert.True(w.Result is { Committed: true, Inserted: 2 }, w.Result?.Error ?? "");
                Assert.Equal("1234.56", Convert.ToString(await Scalar(session, db.Database, "SELECT Fiyat FROM mf_wiz_new WHERE `Şehir` = 'İzmir'"), System.Globalization.CultureInfo.InvariantCulture), "Turkish number");
                Assert.Equal("007", (string?)await Scalar(session, db.Database, "SELECT Kod FROM mf_wiz_new WHERE `Şehir` = 'İzmir'"), "leading zeros kept");
                w.Close();
                return string.Join(", ", types);
            });

            var bad = Path.Combine(dir, "hatali.xlsx");
            MiniExcelLibs.MiniExcel.SaveAs(bad, Enumerable.Range(1, 300).Select(i => new Dictionary<string, object?>
            {
                ["Adet"] = i == 50 ? "elli" : i, ["Ad"] = i == 200 ? null : $"Ürün {i}",
            }).ToList(), overwriteFile: true);
            await Scalar(session, db.Database, "CREATE TABLE mf_wiz_bad (adet INT, ad VARCHAR(30) NOT NULL) DEFAULT CHARSET=utf8mb4");
            await t.Check("ui: import wizard — continue on error, save failed rows", async () =>
            {
                using var w = new MyFront.UI.Dialogs.ImportWizard(form, session, db.Database, "mf_wiz_bad");
                w.Show(form);
                await Until(() => Field<ComboBox>(w, "_tables").Items.Count > 0, "tables");
                await w.LoadFileAsync(bad);
                await (Task)Call(w, "NextAsync")!;
                await Until(() => Field<TableInfo?>(w, "_tableInfo")?.Name == "mf_wiz_bad", "table info");
                await (Task)Call(w, "NextAsync")!;
                Field<CheckBox>(w, "_continue").Checked = true;
                await w.RunImportAsync();
                Assert.True(w.Result is { Committed: true, Inserted: 298, Failed: 2 }, $"{w.Result?.Inserted} inserted, {w.Result?.Failed} failed {w.Result?.Error}");
                Assert.True(Field<Button>(w, "_saveFailed").Enabled, "save button enabled");
                var failedPath = Path.Combine(dir, "hatali_failed.xlsx");
                MyFront.Core.AppSettings.Current.OpenAfterExport = false;
                await w.SaveFailedToAsync(failedPath);
                Assert.Equal(2, MiniExcelLibs.MiniExcel.Query(failedPath, useHeaderRow: true).Count(), "failed rows file");
                Shot(w, "45-import-failed-rows");
                w.Close();
                return Field<Label>(w, "_runResult").Text.Replace('\n', ' ');
            });

            await t.Check("ui: import wizard in dark mode", async () =>
            {
                form.ToggleTheme();
                try
                {
                    using var w = new MyFront.UI.Dialogs.ImportWizard(form, session, db.Database, "mf_wiz_bad");
                    w.Show(form);
                    await Until(() => Field<ComboBox>(w, "_tables").Items.Count > 0, "tables");
                    await w.LoadFileAsync(bad);
                    await (Task)Call(w, "NextAsync")!;
                    await Until(() => Field<TableInfo?>(w, "_tableInfo")?.Name == "mf_wiz_bad", "table info");
                    await (Task)Call(w, "NextAsync")!;
                    await Task.Delay(200);
                    Shot(w, "46-import-dark");
                    w.Close();
                }
                finally { form.ToggleTheme(); }
            });

            await t.Check("ui: read-only connection cannot open the import wizard", async () =>
            {
                lock (Messages) Messages.Clear();
                using var ro = await ServerSession.ConnectAsync(db.ToAccount(TlsMode.Required, readOnly: true), db.Password);
                form.ShowImport(ro, db.Database);
                Assert.True(Messages.Any(m => m.Contains("read-only")), "message: " + string.Join(" | ", Messages));
            });
        }
        finally
        {
            await Scalar(session, db.Database, "DROP TABLE IF EXISTS mf_wiz_fasil, mf_wiz_new, mf_wiz_bad");
        }
    }

    private static async Task ExportUi(TestRunner t, TestDb db, MainForm form, ServerSession session)
    {
        var settings = MyFront.Core.AppSettings.Current;
        settings.ExportFolder = Path.Combine(ExportTests.OutDir, "ui");
        settings.OpenAfterExport = true;
        var opened = new List<string>();
        Ui.OpenPathOverride = p => opened.Add(p);
        var requests = new List<MyFront.UI.Dialogs.ExportRequest>();
        form.ExportOverride = r => requests.Add(r);
        var nav = form.Navigator;
        var sqlTab = form.Tabs.TabPages.OfType<SqlEditorTab>().First(x => x.Session == session);
        try
        {
            await t.Check("ui: one-click Excel on a result tab (sorted grid, opens the file)", async () =>
            {
                form.Tabs.SelectedTab = sqlTab;
                sqlTab.Editor.Text = "SELECT * FROM prices";
                await sqlTab.RunAsync(RunScope.All);
                var tab = sqlTab.ResultTabs[0];
                typeof(DataGridView).GetMethod("OnColumnHeaderMouseClick", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(tab.Grid, new object[] { new DataGridViewCellMouseEventArgs(3, -1, 5, 5, new MouseEventArgs(MouseButtons.Left, 1, 5, 5, 0)) });
                await Until(() => tab.Grid.Columns[3].HeaderCell.SortGlyphDirection == SortOrder.Ascending, "sorted");
                var sw = Stopwatch.StartNew();
                await tab.QuickExportAsync();
                var path = tab.LastExportPath!;
                Assert.True(path.StartsWith(settings.ExportFolder!) && Path.GetFileName(path).StartsWith("prices_"), "file name: " + path);
                Assert.Equal(path, opened.LastOrDefault(), "opened after export");
                var first = MiniExcelLibs.MiniExcel.Query(path, useHeaderRow: true).Cast<IDictionary<string, object?>>().First();
                Assert.Equal("Ankara", first["city"] as string, "export follows the grid's sort order");
                Shot(form, "30-one-click-export");
                return $"{sw.ElapsedMilliseconds} ms → {Path.GetFileName(path)}";
            });

            await t.Check("ui: grid context menu → export request for the result", () =>
            {
                requests.Clear();
                var menu = sqlTab.ResultTabs[0].Grid.ContextMenuStrip!;
                menu.Items.Cast<ToolStripItem>().First(i => i.Text == "Export to CSV…").PerformClick();
                Assert.True(requests.Count == 1 && requests[0].Format == "csv" && requests[0].Items.Count == 1, "request");
                return Task.FromResult<string?>(requests[0].Title);
            });

            await t.Check("ui: SQL editor exports only read statements (DELETE is refused, never run)", async () =>
            {
                requests.Clear();
                lock (Messages) Messages.Clear();
                sqlTab.Editor.Text = "DELETE FROM products WHERE id = 1;\nSELECT * FROM products;";
                sqlTab.Editor.CurrentPosition = 3;
                sqlTab.Editor.SetEmptySelection(3);
                sqlTab.ExportQuery();
                Assert.True(requests.Count == 0 && Messages.Any(m => m.Contains("Only statements that just read")), "DELETE refused");
                sqlTab.Editor.SetEmptySelection(sqlTab.Editor.Text.IndexOf("FROM products;", StringComparison.Ordinal));
                sqlTab.ExportQuery();
                Assert.True(requests.Count == 1, "SELECT accepted");
                var r = await MyFront.Core.Export.ExcelExporter.ExportAsync(Path.Combine(settings.ExportFolder!, "query.xlsx"),
                    requests[0].Items.Select(i => i.Table).ToList(), new MyFront.Core.Export.ExportProgress(), CancellationToken.None);
                Assert.Equal(50L, r.Rows, "streamed query rows");
                Assert.Equal(50L, Convert.ToInt64(await Scalar(session, db.Database, "SELECT COUNT(*) FROM products")), "DELETE was not executed");
            });

            await t.Check("ui: Data Browser export = whole table with the current filter (streamed)", async () =>
            {
                requests.Clear();
                await nav.SelectObjectAsync(session, db.Database, "prices");
                form.Tabs.SelectedTab = form.DataPage;
                await Until(() => form.DataBrowser.Table == "prices" && !form.DataBrowser.IsLoading, "prices page");
                Field<ToolStripTextBox>(form.DataBrowser, "_filter").Text = "city = 'İzmir'";
                await form.DataBrowser.LoadPageAsync(resetOffset: true);
                var request = form.DataBrowser.BuildExportRequest("xlsx")!;
                var path = Path.Combine(settings.ExportFolder!, "izmir.xlsx");
                var r = await MyFront.Core.Export.ExcelExporter.ExportAsync(path, request.Items.Select(i => i.Table).ToList(),
                    new MyFront.Core.Export.ExportProgress(), CancellationToken.None);
                var expected = Convert.ToInt64(await Scalar(session, db.Database, "SELECT COUNT(*) FROM prices WHERE city = 'İzmir'"));
                Assert.Equal(expected, r.Rows, "all filtered rows, not just the 1000-row page");
                Field<ToolStripTextBox>(form.DataBrowser, "_filter").Text = "";
                await form.DataBrowser.LoadPageAsync(resetOffset: true);
                return $"{r.Rows:N0} rows (page shows 1,000) — {request.Title}";
            });

            await t.Check("ui: export dialog for a table → xlsx with progress, then opens it", async () =>
            {
                var node = NavigatorTree.Info(await nav.SelectObjectAsync(session, db.Database, "prices"))!;
                var request = (await form.BuildNodeExportAsync(node, "xlsx"))!;
                using var dlg = new MyFront.UI.Dialogs.ExportForm(request);
                dlg.Show(form);
                var path = Path.Combine(settings.ExportFolder!, "dialog-prices.xlsx");
                MyFront.Core.Export.ExcelExporter.TryDelete(path);
                Field<TextBox>(dlg, "_path").Text = path;
                opened.Clear();
                var run = (Task)Call(dlg, "ExportAsync")!;
                await Task.Delay(500);
                Shot(dlg, "31-export-progress");
                await run;
                var state = Field<Label>(dlg, "_state").Text;
                Assert.True(state.StartsWith("Done: 200.000 rows") || state.StartsWith("Done: 200,000 rows"), state);
                Assert.Equal(path, opened.SingleOrDefault(), "opened");
                Shot(dlg, "32-export-done");
                dlg.Close();
                return state.Replace('\n', ' ');
            });

            await t.Check("ui: database export dialog → CSV folder, views unticked by default", async () =>
            {
                var dbNode = NavigatorTree.Info(await nav.SelectDatabaseAsync(session, db.Database))!;
                var request = (await form.BuildNodeExportAsync(dbNode, "csv"))!;
                Assert.True(request.IsDatabase && request.Items.Single(i => i.Table.Name == "v_latest_prices").Selected == false, "view not selected");
                using var dlg = new MyFront.UI.Dialogs.ExportForm(request);
                dlg.Show(form);
                var folder = Path.Combine(settings.ExportFolder!, "db-csv");
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
                Field<TextBox>(dlg, "_path").Text = folder;
                Shot(dlg, "33-export-database");
                await (Task)Call(dlg, "ExportAsync")!;
                var files = Directory.GetFiles(folder).Select(Path.GetFileName).Order().ToList();
                Assert.True(files.Contains("prices.csv") && files.Contains("products.csv") && !files.Contains("v_latest_prices.csv"), string.Join(",", files));
                dlg.Close();
                return string.Join(", ", files);
            });

            await t.Check("ui: Cancel in the export dialog stops the export, nothing written", async () =>
            {
                var request = new MyFront.UI.Dialogs.ExportRequest
                {
                    Title = "big",
                    BaseName = "big",
                    Items = new() { new MyFront.UI.Dialogs.ExportItem(MyFront.Core.Export.ExportTable.FromQuery(session, db.Database, "big",
                        "SELECT a.* FROM prices a CROSS JOIN products b"), false, true) },
                };
                using var dlg = new MyFront.UI.Dialogs.ExportForm(request);
                dlg.Show(form);
                var path = Path.Combine(settings.ExportFolder!, "cancel-me.xlsx");
                Field<TextBox>(dlg, "_path").Text = path;
                var run = (Task)Call(dlg, "ExportAsync")!;
                await Task.Delay(1500);
                Field<Button>(dlg, "_close").PerformClick(); // "Cancel" while running
                await run;
                var state = Field<Label>(dlg, "_state").Text;
                Assert.True(state.StartsWith("Cancelled"), state);
                Assert.True(!File.Exists(path) && !File.Exists(path + ".part"), "no file");
                dlg.Close();
                return state;
            });
        }
        finally
        {
            Ui.OpenPathOverride = null;
            form.ExportOverride = null;
        }
    }

    private static async Task Dialogs(TestRunner t, TestDb db, MainForm form, ServerSession session)
    {
        Account? saved = null;
        await t.Check("ui: account editor → Test connection (Verify Full + CA), OK stores DPAPI password", async () =>
        {
            using var edit = new MyFront.UI.Dialogs.AccountEditForm(new Account { Name = "" }, new[] { "other" }, isNew: true);
            edit.Show(form);
            Field<TextBox>(edit, "_name").Text = "Test MariaDB";
            Field<TextBox>(edit, "_host").Text = db.Host;
            Field<NumericUpDown>(edit, "_port").Value = db.Port;
            Field<TextBox>(edit, "_user").Text = db.User;
            Field<TextBox>(edit, "_password").Text = db.Password;
            Field<ComboBox>(edit, "_database").Text = db.Database;
            Field<ComboBox>(edit, "_tls").SelectedIndex = (int)TlsMode.VerifyFull;
            Field<TextBox>(edit, "_caFile").Text = db.CaFile!;
            await (Task)Call(edit, "TestAsync")!;
            var result = Field<Label>(edit, "_testResult").Text;
            Assert.True(result.StartsWith("Connected"), result);
            Shot(edit, "10-account-edit");
            Call(edit, "OnOk");
            Assert.True(edit.DialogResult == DialogResult.OK, "dialog result");
            saved = edit.Result;
            Assert.True(saved.PasswordEncrypted!.StartsWith("dpapi1:") && saved.GetSavedPassword() == db.Password, "encrypted password");
            Assert.True(Field<TextBox>(edit, "_password").Text.Length == 0, "password box cleared");
            edit.Close();
            return result.Replace('\n', ' ');
        });

        await t.Check("ui: account editor SSH tab → Test reports the SSH error, OK stores the tunnel with a DPAPI secret", async () =>
        {
            var closed = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            closed.Start();
            var port = ((System.Net.IPEndPoint)closed.LocalEndpoint).Port;
            closed.Stop();
            using var edit = new MyFront.UI.Dialogs.AccountEditForm(saved!, Array.Empty<string>(), isNew: false);
            edit.Show(form);
            var tabs = FindAll<TabControl>(edit).Single();
            tabs.SelectedIndex = 1;
            Assert.True(!Field<TableLayoutPanel>(edit, "_sshFields").Enabled, "fields off until SSH is ticked");
            Field<CheckBox>(edit, "_sshOn").Checked = true;
            Field<TextBox>(edit, "_sshHost").Text = "127.0.0.1";
            Field<NumericUpDown>(edit, "_sshPort").Value = port;
            Field<TextBox>(edit, "_sshUser").Text = "tunnel";
            Field<TextBox>(edit, "_sshPassword").Text = "Ssh-Şifre";
            Assert.True(Field<TableLayoutPanel>(edit, "_sshFields").Enabled && !Field<TextBox>(edit, "_sshKey").Enabled, "password mode");
            await (Task)Call(edit, "TestAsync")!;
            var result = Field<Label>(edit, "_testResult").Text;
            Assert.True(result.Contains($"tunnel@127.0.0.1:{port}") && result.Contains("not reachable"), result);
            Shot(edit, "12-account-ssh");
            Call(edit, "OnOk");
            Assert.True(edit.DialogResult == DialogResult.OK, "dialog result");
            var ssh = edit.Result.Ssh!;
            Assert.True(edit.Result.UsesSsh && ssh.Port == port && ssh.User == "tunnel", "tunnel settings");
            Assert.True(ssh.PasswordEncrypted!.StartsWith("dpapi1:") && ssh.Password == "Ssh-Şifre", "SSH password encrypted");
            Assert.True(ssh.PasswordOverride is null, "typed secret not kept in memory");
            Assert.True(saved!.Ssh is null, "editing works on a copy");
            edit.Close();
            return result.Replace('\n', ' ');
        });

        if (db.Ssh is { } sshCfg && (!string.IsNullOrEmpty(sshCfg.Password) || !string.IsNullOrEmpty(sshCfg.KeyFile)))
            await t.Check("ui: Test connection through the real SSH tunnel, confirmed host key kept on OK", async () =>
            {
                using var edit = new MyFront.UI.Dialogs.AccountEditForm(saved!, Array.Empty<string>(), isNew: false);
                edit.Show(form);
                FindAll<TabControl>(edit).Single().SelectedIndex = 1;
                Field<CheckBox>(edit, "_sshOn").Checked = true;
                Field<TextBox>(edit, "_sshHost").Text = sshCfg.Host;
                Field<NumericUpDown>(edit, "_sshPort").Value = sshCfg.Port;
                Field<TextBox>(edit, "_sshUser").Text = sshCfg.User;
                if (!string.IsNullOrEmpty(sshCfg.KeyFile))
                {
                    Field<RadioButton>(edit, "_sshByKey").Checked = true;
                    Field<TextBox>(edit, "_sshKey").Text = sshCfg.KeyFile;
                    Field<TextBox>(edit, "_sshPassphrase").Text = sshCfg.Passphrase ?? "";
                }
                else Field<TextBox>(edit, "_sshPassword").Text = sshCfg.Password!;
                Field<TextBox>(edit, "_sshDbHost").Text = sshCfg.DbHost;
                Field<CheckBox>(edit, "_sshSave").Checked = false;
                var asked = new List<string>();
                MyFront.UI.Dialogs.SshPromptsUi.TrustOverride = (server, fp) => { asked.Add(server + " " + fp); return true; };
                try { await (Task)Call(edit, "TestAsync")!; }
                finally { MyFront.UI.Dialogs.SshPromptsUi.TrustOverride = null; }
                var result = Field<Label>(edit, "_testResult").Text;
                Assert.True(result.StartsWith("Connected") && result.Contains($"SSH {sshCfg.User}@{sshCfg.Host}:{sshCfg.Port}"), result);
                Assert.Equal(1, asked.Count, "host key confirmed once");
                Assert.True(Field<Label>(edit, "_sshHostKey").Text.Contains("SHA256:"), "key shown");
                Shot(edit, "13-account-ssh-live");
                Call(edit, "OnOk");
                var ssh = edit.Result.Ssh!;
                Assert.True(ssh.HostKey?.Contains(" SHA256:") == true, "host key kept: " + ssh.HostKey);
                if (sshCfg.HostKey?.StartsWith("ssh-") == true) Assert.Equal(sshCfg.HostKey, ssh.HostKey, "expected key");
                Assert.True(ssh.PasswordEncrypted is null && ssh.PassphraseEncrypted is null, "secret not saved (box unticked)");
                edit.Close();
                return result.Replace('\n', ' ');
            });

        await t.Check("ui: accounts dialog lists the saved account and opens it", async () =>
        {
            AccountStore.Save(new[] { saved! });
            using var dlg = new MyFront.UI.Dialogs.AccountsForm();
            dlg.Show(form);
            var list = Field<ListView>(dlg, "_list");
            Assert.Equal(1, list.Items.Count, "accounts listed");
            Shot(dlg, "11-accounts");
            await (Task)Call(dlg, "OpenAsync")!;
            Assert.True(dlg.Session is not null, "no session: " + Field<Label>(dlg, "_status").Text);
            var summary = dlg.Session!.Summary;
            dlg.Session.Dispose();
            Assert.True(AccountStore.Load().Single().LastLogin is not null, "last login saved");
            dlg.Close();
            return summary;
        });

        await t.Check("ui: Run SQL file — DELIMITER, procedure, Turkish text in UTF-8 and Windows-1254", async () =>
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            var dir = Path.Combine(Path.GetTempPath(), "MyFront.SelfTest");
            var utf8 = Path.Combine(dir, "run-utf8.sql");
            var cp1254 = Path.Combine(dir, "run-1254.sql");
            File.WriteAllText(utf8, """
                -- sample dump
                DROP TABLE IF EXISTS mf_sqlfile;
                CREATE TABLE mf_sqlfile (id INT PRIMARY KEY, ad VARCHAR(50)) DEFAULT CHARSET=utf8mb4;
                INSERT INTO mf_sqlfile VALUES (1, 'Şırnak'), (2, 'Iğdır');
                DROP PROCEDURE IF EXISTS mf_sqlfile_p;
                DELIMITER $$
                CREATE PROCEDURE mf_sqlfile_p()
                BEGIN
                  UPDATE mf_sqlfile SET ad = CONCAT(ad, ' ✓') WHERE id = 1;
                  SELECT COUNT(*) FROM mf_sqlfile;
                END$$
                DELIMITER ;
                CALL mf_sqlfile_p();
                """, new System.Text.UTF8Encoding(false));
            File.WriteAllText(cp1254, "INSERT INTO mf_sqlfile VALUES (3, 'ĞğİıŞşÇçÖöÜü');\n", System.Text.Encoding.GetEncoding(1254));
            try
            {
                using var f = new MyFront.UI.Dialogs.RunSqlFileForm(session, db.Database, utf8);
                f.Show(form);
                await (Task)Call(f, "RunAsync")!;
                var state1 = Field<Label>(f, "_state").Text;
                // DROP TABLE, CREATE TABLE, INSERT, DROP PROCEDURE, CREATE PROCEDURE (DELIMITER block), CALL
                Assert.True(state1.StartsWith("6 statement(s) ok, 0 error(s)"), state1 + " / " + Field<TextBox>(f, "_log").Text);
                Field<TextBox>(f, "_path").Text = cp1254;
                Field<ComboBox>(f, "_encoding").SelectedIndex = 1;
                await (Task)Call(f, "RunAsync")!;
                Shot(f, "12-run-sql-file");
                f.Close();
                Assert.Equal("Şırnak ✓", (string?)await Scalar(session, db.Database, "SELECT ad FROM mf_sqlfile WHERE id = 1"), "utf-8 file + procedure");
                Assert.Equal("ĞğİıŞşÇçÖöÜü", (string?)await Scalar(session, db.Database, "SELECT ad FROM mf_sqlfile WHERE id = 3"), "windows-1254 file");
                return state1;
            }
            finally
            {
                await Scalar(session, db.Database, "DROP TABLE IF EXISTS mf_sqlfile");
                await Scalar(session, db.Database, "DROP PROCEDURE IF EXISTS mf_sqlfile_p");
            }
        });

        await t.Check("ui: history dialog shows entries and returns the chosen SQL", () =>
        {
            using var h = new MyFront.UI.Dialogs.HistoryForm(session.Account.Id, session.DisplayName);
            h.Show(form);
            var list = Field<ListView>(h, "_list");
            int count = list.Items.Count;
            Assert.True(count > 5, "history items " + count);
            list.Items[0].Selected = true;
            Shot(h, "13-history");
            Call(h, "Insert");
            Assert.True(!string.IsNullOrEmpty(h.SelectedSql), "selected sql");
            h.Close();
            return Task.FromResult<string?>($"{count} entries, newest: {h.SelectedSql!.Split('\n')[0]}");
        });
    }
}
