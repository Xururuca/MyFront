using MyFront.Core.Db;

namespace MyFront.SelfTest;

/// <summary>The Explain button's analysis, offline: EXPLAIN rows in, plain findings out.</summary>
internal static class ExplainTests
{
    private static ExplainRow Row(string table, string type, string? key = null, long? rows = 1, string? extra = null, string? possible = null, string? refCol = null) =>
        new("1", "SIMPLE", table, type, possible, key, key is null ? null : "4", refCol, rows, extra);

    public static void Offline(TestRunner t)
    {
        t.Check("explain: which statements EXPLAIN takes without running them", () =>
        {
            foreach (var sql in new[] { "SELECT 1", "  select * from t", "WITH x AS (SELECT 1) SELECT * FROM x", "UPDATE t SET a = 1", "DELETE FROM t", "INSERT INTO t SELECT * FROM u", "REPLACE INTO t VALUES (1)" })
                Assert.True(ExplainAnalyzer.CanExplain(sql), sql);
            foreach (var sql in new[] { "SHOW TABLES", "EXPLAIN SELECT 1", "ANALYZE SELECT 1", "CREATE TABLE t (a INT)", "SET @a = 1", "USE x" })
                Assert.True(!ExplainAnalyzer.CanExplain(sql), sql);
        });

        t.Check("explain: full scan (with / without possible keys), filesort, temporary, join buffer → ⚠ first", () =>
        {
            var f = ExplainAnalyzer.Findings(new[]
            {
                Row("data_products", "ALL", rows: 1_250_000, extra: "Using where; Using temporary; Using filesort"),
                Row("Bot_markets", "ALL", rows: 120, extra: "Using where; Using join buffer (flat, BNL join)", possible: "PRIMARY"),
            });
            Assert.True(f[0].StartsWith("2 table read(s) · 2 full table scan(s)"), f[0]);
            Assert.True(f[1].StartsWith("⚠ data_products: full table scan (≈1.250.000 rows") || f[1].StartsWith("⚠ data_products: full table scan (≈1,250,000 rows"), f[1]);
            Assert.True(f[1].Contains("An index on the columns"), "advice without possible keys: " + f[1]);
            Assert.True(f.Any(x => x.Contains("filesort")) && f.Any(x => x.Contains("temporary table")), string.Join(" | ", f));
            Assert.True(f.Any(x => x.StartsWith("⚠ Bot_markets: full table scan") && x.Contains("Possible keys (PRIMARY) weren't chosen")), string.Join(" | ", f));
            Assert.True(f.Any(x => x.Contains("joined without an index")), "join buffer: " + string.Join(" | ", f));
            Assert.True(f.Skip(1).All(x => x.StartsWith("⚠")), "only warnings here");
        });

        t.Check("explain: index lookups, covering index, impossible WHERE, no tables, derived tables skipped", () =>
        {
            var f = ExplainAnalyzer.Findings(new[]
            {
                Row("p", "const", key: "PRIMARY", refCol: "const"),
                Row("x", "ref", key: "idx_product_date", rows: 4000, extra: "Using index", refCol: "mf_test.p.id"),
                new ExplainRow("1", "PRIMARY", "<derived2>", "ALL", null, null, null, null, 10, null),
            });
            Assert.True(f.Any(x => x == "✓ p: index PRIMARY (const, ≈1 rows, compared with const)"), string.Join(" | ", f));
            Assert.True(f.Any(x => x.StartsWith("✓ x: index idx_product_date (ref")), string.Join(" | ", f));
            Assert.True(f.Any(x => x.StartsWith("✓ x: answered from the index alone")), "covering: " + string.Join(" | ", f));
            Assert.True(!f.Any(x => x.Contains("<derived2>")), "derived table not reported as a table");
            Assert.True(f[0].StartsWith("2 table read(s) · 0 full table scan(s)"), f[0]);
            Assert.True(!ExplainAnalyzer.Findings(new[] { Row("p", "range", key: "PRIMARY", extra: "Using index condition") })
                .Any(x => x.Contains("covering")), "index condition is not a covering index");
            var none = ExplainAnalyzer.Findings(new[] { new ExplainRow("1", "SIMPLE", null, null, null, null, null, null, null, "No tables used") });
            Assert.Equal("ℹ No table is read.", none.Single(), "no tables");
            var impossible = ExplainAnalyzer.Findings(new[] { new ExplainRow("1", "SIMPLE", null, null, null, null, null, null, null, "Impossible WHERE") });
            Assert.True(impossible.Single().StartsWith("ℹ The WHERE can never be true"), impossible.Single());
        });
    }
}
