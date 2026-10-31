using System.Diagnostics;
using MyFront.Core.Db;
using MyFront.UI;

namespace MyFront.SelfTest;

/// <summary>Ad-hoc measurements used while tuning (mode "bench").</summary>
internal static class Bench
{
    /// <summary>Grid cost of binding and growing 200k virtual rows (no server needed).</summary>
    public static Task RunAsync(TestDb db)
    {
        _ = db;
        var thread = new Thread(() =>
        {
            Application.EnableVisualStyles();
            var form = new Form { Width = 1200, Height = 800 };
            var grid = new ResultGrid { Dock = DockStyle.Fill };
            form.Controls.Add(grid);
            form.Shown += (_, _) =>
            {
                var cols = Enumerable.Range(0, 9).Select(i => new ColumnInfo { Name = "c" + i, Kind = ColumnKind.Text }).ToArray();
                var store = new RowStore();
                for (int i = 0; i < 200_000; i++) store.Add(Enumerable.Range(0, 9).Select(c => (object?)("v" + c)).ToArray());
                var sw = Stopwatch.StartNew();
                grid.Bind(cols, new RowStore());
                Console.WriteLine($"bind empty: {sw.ElapsedMilliseconds} ms");
                grid.Bind(cols, store);
                Console.WriteLine($"bind + RowCount=200k at once: {sw.ElapsedMilliseconds} ms");
                grid.Bind(cols, new RowStore());
                var inc = new RowStore();
                grid.Bind(cols, inc);
                long worst = 0;
                sw.Restart();
                for (int i = 0; i < 200_000; i++)
                {
                    inc.Add(store.GetRow(i));
                    if (i % 25_000 == 24_999)
                    {
                        var step = Stopwatch.StartNew();
                        grid.SyncRowCount();
                        worst = Math.Max(worst, step.ElapsedMilliseconds);
                    }
                }
                Console.WriteLine($"incremental RowCount in 25k steps: total {sw.ElapsedMilliseconds} ms, worst step {worst} ms");
                sw.Restart();
                grid.AutoFitColumns();
                Console.WriteLine($"autofit: {sw.ElapsedMilliseconds} ms");
                sw.Restart();
                grid.RowCount = 0;
                Console.WriteLine($"RowCount=0 (clear 200k): {sw.ElapsedMilliseconds} ms");
                form.Close();
            };
            Application.Run(form);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return Task.CompletedTask;
    }
}
