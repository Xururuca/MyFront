using MyFront.Core.Accounts;
using MyFront.Core.Db;
using MyFront.UI;

namespace MyFront.SelfTest;

/// <summary>Result grids and the Data Browser: every second row a shade off, in the light and the dark theme.</summary>
internal static partial class UiTests
{
    private static async Task AltRowsUi(TestRunner t, TestDb db, MainForm form)
    {
        form.WindowState = FormWindowState.Normal;
        form.Size = new Size(form.LogicalToDeviceUnits(1280), form.LogicalToDeviceUnits(800));
        var session = await ServerSession.ConnectAsync(db.ToAccount(TlsMode.Required), db.Password);
        await form.AddSessionAsync(session);
        var view = form.ViewOf(session)!;
        var sqlTab = view.Editors.First();
        // a cell's background, away from its text: just above the bottom grid line
        static Color Back(DataGridView g, int column, int row)
        {
            var r = g.GetCellDisplayRectangle(column, row, false);
            using var bmp = new Bitmap(g.Width, g.Height);
            g.DrawToBitmap(bmp, new Rectangle(0, 0, g.Width, g.Height));
            return bmp.GetPixel(r.Left + 2, r.Bottom - 3);
        }
        static string Rgb(Color c) => $"{c.R},{c.G},{c.B}";
        try
        {
            await t.Check("ui: result grid — every second row a shade off; selection the same on both kinds of rows; light and dark", async () =>
            {
                view.Tabs.SelectedTab = sqlTab;
                sqlTab.Editor.Text = "SELECT * FROM products ORDER BY 1 LIMIT 12";
                await sqlTab.RunAsync(RunScope.All);
                var grid = sqlTab.ResultTabs[0].Grid;
                await Until(() => grid.RowCount >= 4, "rows");
                var notes = new List<string>();
                foreach (var dark in new[] { false, true })
                {
                    if (ThemeManager.Current.IsDark != dark) form.ToggleTheme();
                    var th = ThemeManager.Current;
                    grid.ClearSelection();
                    grid.CurrentCell = grid[0, 3];
                    grid[0, 3].Selected = true; // the same current cell again doesn't select it
                    await Task.Delay(100);
                    Assert.Equal(th.AltRow, grid.AlternatingRowsDefaultCellStyle.BackColor, "style");
                    Assert.Equal(Rgb(th.Window), Rgb(Back(grid, 1, 0)), "row 1");
                    Assert.Equal(Rgb(th.AltRow), Rgb(Back(grid, 1, 1)), "row 2");
                    Assert.Equal(Rgb(th.Window), Rgb(Back(grid, 1, 2)), "row 3");
                    Assert.Equal(Rgb(th.SelectionBack), Rgb(Back(grid, 0, 3)), "a selected cell on a shaded row");
                    Shot(form, dark ? "99-alt-rows-dark" : "99-alt-rows-light");
                    notes.Add($"{(dark ? "dark" : "light")} {Rgb(th.Window)} / {Rgb(th.AltRow)}");
                }
                return string.Join(" · ", notes);
            });

            await t.Check("ui: Data Browser — the same shaded rows, and they follow a theme switch", async () =>
            {
                await view.Navigator.SelectObjectAsync(session, db.Database, "products");
                view.Tabs.SelectedTab = view.DataPage;
                var data = view.DataBrowser;
                await Until(() => data.Table == "products" && !data.IsLoading && data.Grid.RowCount >= 3, "rows");
                await Task.Delay(100);
                Assert.Equal(Rgb(Theme.Dark.AltRow), Rgb(Back(data.Grid, 1, 1)), "dark");
                form.ToggleTheme();
                await Task.Delay(100);
                Assert.Equal(Rgb(Theme.Light.Window), Rgb(Back(data.Grid, 1, 0)), "light row 1");
                Assert.Equal(Rgb(Theme.Light.AltRow), Rgb(Back(data.Grid, 1, 1)), "light row 2");
            });
        }
        finally
        {
            if (ThemeManager.Current.IsDark) form.ToggleTheme();
            sqlTab.Editor.Text = "";
            form.CloseSession(session);
        }
    }
}
