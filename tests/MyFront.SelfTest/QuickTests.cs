using MiniExcelLibs;
using MyFront.Core.Accounts;
using MyFront.Core.Db;
using MyFront.Core.Export;
using MyFront.Core.Quick;
using MySqlConnector;

namespace MyFront.SelfTest;

/// <summary>Phase 4: quick query placeholders, store, parameters against the server, straight-to-Excel.</summary>
internal static class QuickTests
{
    public static async Task RunAsync(TestRunner t, TestDb db, ServerSession s)
    {
        Offline(t);
        await Server(t, db, s);
    }

    private static void Offline(TestRunner t)
    {
        t.Check("quick: placeholders found with types, comments and identifiers ignored", () =>
        {
            var ph = ParameterParser.Find("""
                -- {{ignored}}
                SELECT `{{col}}` FROM t /* {{also_ignored}} */
                WHERE city = {{city}} AND d >= '{{from:date}}' AND n > {{n:int}} AND p < {{fiyat:number}} AND x = {{tarih}} AND y = {{city}}
                """);
            Assert.Equal("city:Text,from:Date,n:Int,fiyat:Number,tarih:Date", string.Join(",", ph.Select(p => $"{p.Name}:{p.Type}")), "placeholders");
        });
        t.Check("quick: binding makes real parameters; quoted lone placeholder is the value; same name = same parameter", () =>
        {
            var (sql, ps) = ParameterParser.Bind("SELECT * FROM t WHERE a = {{city}} AND b = '{{city}}' AND c >= \"{{from}}\" -- {{city}}",
                new Dictionary<string, object?> { ["city"] = "İzmir", ["from"] = new DateTime(2024, 1, 1) });
            Assert.Equal("SELECT * FROM t WHERE a = @qp0 AND b = @qp0 AND c >= @qp1 -- {{city}}", sql, "bound SQL");
            Assert.Equal("@qp0=İzmir,@qp1=2024-01-01", string.Join(",", ps.Select(p => $"{p.Name}={CellFormat.ToDisplay(p.Value, ColumnKind.Date)}")), "values");
        });
        t.Check("quick: a placeholder glued into text is refused (no string building)", () =>
        {
            try
            {
                ParameterParser.Bind("SELECT * FROM t WHERE name LIKE '%{{q}}%'", new Dictionary<string, object?> { ["q"] = "x" });
                throw new Exception("expected a ParameterException");
            }
            catch (ParameterException ex)
            {
                Assert.True(ex.Message.Contains("CONCAT"), ex.Message);
            }
        });
        t.Check("quick: typed input conversion (Turkish numbers, dates)", () =>
        {
            Assert.Equal(1000L, ParameterParser.ConvertInput("1.000", ParamType.Int), "int with thousands dot");
            Assert.Equal(1234.56m, ParameterParser.ConvertInput("1.234,56", ParamType.Number), "number");
            Assert.Equal(new DateTime(2024, 2, 29), ParameterParser.ConvertInput("29.02.2024", ParamType.Date), "date");
            Assert.Equal("  as typed ", ParameterParser.ConvertInput("  as typed ", ParamType.Text), "text untouched");
        });
        t.Check("quick: store save/load, export/import re-links the connection by name", () =>
        {
            var mine = new Account { Name = "Prod MariaDB", Host = "h" };
            var q = new QuickQuery { Name = "Günlük", Sql = "SELECT {{d:date}}", Hotkey = 4, Color = "#FFC400", Icon = "calendar", AccountId = "other-pc-id", AccountName = "Prod MariaDB", ToExcel = true };
            QuickQueryStore.Save(new[] { q });
            var loaded = QuickQueryStore.Load().Single();
            Assert.True(loaded.Name == "Günlük" && loaded.Hotkey == 4 && loaded.ToExcel && loaded.ConfirmDangerous && loaded.Icon == "calendar", "round trip");
            var file = Path.Combine(Path.GetTempPath(), "MyFront.SelfTest", "exported-quick.json");
            QuickQueryStore.Export(file, new[] { q });
            var imported = QuickQueryStore.Import(file, new[] { mine }).Single();
            Assert.Equal(mine.Id, imported.AccountId, "re-linked to the local account with the same name");
            var bad = Path.Combine(Path.GetTempPath(), "MyFront.SelfTest", "not-quick.json");
            File.WriteAllText(bad, "[1,2,3]");
            try { QuickQueryStore.Import(bad, Array.Empty<Account>()); throw new Exception("expected InvalidDataException"); }
            catch (InvalidDataException) { }
        });
    }

    private sealed class Collector : IQueryListener
    {
        public readonly List<ResultSetData> Sets = new();
        public void ResultSetStarted(ResultSetData rs) { lock (Sets) Sets.Add(rs); }
        public void StatementFinished(StatementOutcome o) { }
    }

    private static async Task Server(TestRunner t, TestDb db, ServerSession s)
    {
        await t.Check("quick: parameters against the server; an injection attempt is just a value", async () =>
        {
            var sql = "SELECT COUNT(*) FROM prices WHERE city = {{city}} AND price_date >= {{from:date}}";
            async Task<long> Count(string city)
            {
                var binder = new ParameterParser.ParameterBinder(new Dictionary<string, object?> { ["city"] = city, ["from"] = new DateTime(2024, 1, 1) });
                var stmts = SqlSplitter.Split(sql).Select(x => x with { Text = binder.Bind(x.Text) }).ToList();
                await using var conn = await s.OpenDedicatedAsync(db.Database);
                var c = new Collector();
                var o = await QueryExecutor.RunAsync(s, conn, stmts, c, CancellationToken.None, null, binder.Parameters);
                Assert.True(o.Single().Error is null, o.Single().Error?.Message ?? "");
                return Convert.ToInt64(c.Sets.Single().Rows.GetRow(0)[0]);
            }
            long izmir = await Count("İzmir");
            await using var conn2 = await s.OpenAsync(db.Database);
            await using var cmd = new MySqlCommand("SELECT COUNT(*) FROM prices WHERE city = 'İzmir' AND price_date >= '2024-01-01'", conn2);
            Assert.Equal(Convert.ToInt64(await cmd.ExecuteScalarAsync()), izmir, "same count as plain SQL");
            Assert.Equal(0L, await Count("x' OR '1'='1"), "injection text matches nothing");
            return $"İzmir since 2024: {izmir:N0}";
        });

        await t.Check("quick: straight to Excel — SET @var carries over, one sheet per SELECT, trailing statement runs", async () =>
        {
            const string script = """
                SET @from := {{min:int}};
                SELECT COUNT(*) AS n FROM prices WHERE id > @from AND city = {{city}};
                SELECT id, name FROM products WHERE name LIKE CONCAT('%', {{ad}}, '%') ORDER BY id;
                SET @done := 1;
                """;
            var (sql, ps) = ParameterParser.Bind(script, new Dictionary<string, object?> { ["min"] = 150_000L, ["city"] = "Konya", ["ad"] = "a" });
            var path = Path.Combine(ExportTests.OutDir, "quick-report.xlsx");
            var r = await QuickExport.ToExcelAsync(s, db.Database, "Rapor", sql, ps, path, new ExportProgress(), CancellationToken.None);
            var sheets = MiniExcel.GetSheetNames(path);
            Assert.Equal("Rapor,Rapor (2)", string.Join(",", sheets), "sheets");
            var n = Convert.ToInt64(MiniExcel.Query(path, useHeaderRow: true, sheetName: "Rapor").Cast<IDictionary<string, object?>>().Single()["n"]);
            await using (var conn = await s.OpenAsync(db.Database))
            await using (var cmd = new MySqlCommand("SELECT COUNT(*) FROM prices WHERE id > 150000 AND city = 'Konya'", conn))
                Assert.Equal(Convert.ToInt64(await cmd.ExecuteScalarAsync()), n, "count uses @from from the previous statement");
            var names = MiniExcel.Query(path, useHeaderRow: true, sheetName: "Rapor (2)").Cast<IDictionary<string, object?>>().Select(x => (string)x["name"]!).ToList();
            await using (var conn = await s.OpenAsync(db.Database))
            await using (var cmd = new MySqlCommand("SELECT COUNT(*) FROM products WHERE name LIKE '%a%'", conn))
                Assert.Equal(Convert.ToInt64(await cmd.ExecuteScalarAsync()), (long)names.Count, "LIKE with a bound value (server collation: case-insensitive)");
            return $"{r.Rows:N0} rows in {r.Sheets} sheets";
        });

        await t.Check("quick: read-only connection blocks a writing quick query", async () =>
        {
            using var ro = await ServerSession.ConnectAsync(db.ToAccount(TlsMode.Required, readOnly: true), db.Password);
            var ex = await Assert.Throws(() => QuickExport.ToExcelAsync(ro, db.Database, "x", "UPDATE products SET name = name; SELECT 1",
                new List<(string, object?)>(), Path.Combine(ExportTests.OutDir, "never.xlsx"), new ExportProgress(), CancellationToken.None), "read-only");
            Assert.True(ex is ReadOnlyViolationException, ex.Message);
        });
    }
}
