using MyFront.Core.Db;
using MySqlConnector;

namespace MyFront.SelfTest;

/// <summary>Format SQL: the layout, that only whitespace and reserved words' case change, and that formatted SQL returns the same rows.</summary>
internal static class FormatTests
{
    private static string F(string sql) => SqlFormatter.Format(sql);

    /// <summary>The text without whitespace, in capitals: formatting must not change it.</summary>
    private static string Code(string s) => new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();

    private static readonly string[] Samples =
    {
        "select count(*) from orders where is_paid = 0 and id>100 order by id desc",
        "update url_tag ut join url u on u.id = ut.url_id join markets m on m.id = u.market_id set ut.scraped = 0 where m.market_name = 'ankara'",
        "select a.id, (select max(b.x) from b where b.a_id=a.id) as mx from a left join c on c.id=a.c_id and c.ok=1 where a.d between 1 and 5 and a.id in (select id from z) limit 10 offset 5",
        "select _utf8'a  b', N'x', \"q  q\", `my  col`, {{from_date}}, count(*), @v:=@v+1, -1, x - 1, t.* from t where d >= '{{d}}' -- keep me\n and s like '%a%' /* c  c */ and n = -5",
        "with x as (select 1 as n) select n from x union all select 2",
        "insert into t (a, b) values (1, 'x'), (2, 'y') on duplicate key update b = values(b)",
        "select case when a>1 then 'big' else 'small' end as size, date(created) d, left(name, 3) from t group by d with rollup",
        "delete t1 from t1 inner join t2 on t2.id = t1.id where t2.gone = 1",
    };

    public static void Offline(TestRunner t)
    {
        t.Check("format: clauses on their own lines, AND under WHERE, reserved words and functions in capitals", () =>
        {
            Assert.Equal("SELECT COUNT(*)\nFROM orders\nWHERE is_paid = 0\n    AND id > 100\nORDER BY id DESC", F(Samples[0]), "select");
            Assert.Equal("DELETE FROM orders\nWHERE id > 635737\n    AND is_edited = 0", F("delete from orders where id> 635737 and is_edited=0"), "delete");
            Assert.Equal("UPDATE url_tag ut\n    JOIN url u ON u.id = ut.url_id\n    JOIN markets m ON m.id = u.market_id\nSET ut.scraped = 0\nWHERE m.market_name = 'ankara'",
                F(Samples[1]), "update with joins");
        });

        t.Check("format: a long column list goes one per line, a short one stays on its line", () =>
        {
            Assert.Equal("SELECT\n    `p`.`product_name`,\n    `p`.`current_price`,\n    `p`.`current_price_date`,\n    `p`.`predicted_price`\n" +
                         "FROM `p`\nWHERE mamul = 1\n    AND `product_name` = 'beyaz peynir'\nORDER BY current_price_date DESC",
                F("SELECT `p`.`product_name`, `p`.`current_price`, `p`.`current_price_date`, `p`.`predicted_price` FROM `p` " +
                  "WHERE mamul=1 and `product_name`='beyaz peynir' order by current_price_date desc"), "long");
            Assert.Equal("SELECT unit, market_id\nFROM products\nGROUP BY unit, market_id\nORDER BY market_id ASC",
                F("select unit, market_id from products group by unit, market_id order by market_id asc"), "short");
            Assert.Equal("SELECT DISTINCT\n    aaaaaaaaaaaa,\n    bbbbbbbbbbbb,\n    cccccccccccc,\n    dddddddddddd,\n    eeeeeeeeeeee\nFROM t",
                F("select distinct aaaaaaaaaaaa, bbbbbbbbbbbb, cccccccccccc, dddddddddddd, eeeeeeeeeeee from t"), "DISTINCT stays on the SELECT line");
        });

        t.Check("format: subqueries indented, LEFT JOIN … ON … AND, BETWEEN … AND, LIMIT … OFFSET, WITH, UNION, INSERT … VALUES", () =>
        {
            Assert.Equal("""
                SELECT
                    a.id,
                    (
                        SELECT MAX(b.x)
                        FROM b
                        WHERE b.a_id = a.id
                    ) AS mx
                FROM a
                    LEFT JOIN c ON c.id = a.c_id
                        AND c.ok = 1
                WHERE a.d BETWEEN 1 AND 5
                    AND a.id IN (
                        SELECT id
                        FROM z
                    )
                LIMIT 10 OFFSET 5
                """.ReplaceLineEndings("\n"), F(Samples[2]), "subqueries");
            Assert.Equal("WITH x AS (\n    SELECT 1 AS n\n)\nSELECT n\nFROM x\nUNION ALL\nSELECT 2", F(Samples[4]), "with / union");
            Assert.Equal("INSERT INTO t (a, b)\nVALUES (1, 'x'), (2, 'y')\nON DUPLICATE KEY UPDATE b = VALUES(b)", F(Samples[5]), "insert");
            Assert.Equal("SELECT\n    CASE WHEN a > 1 THEN 'big' ELSE 'small' END AS size,\n    DATE(created) d,\n    LEFT(name, 3)\nFROM t\nGROUP BY d WITH ROLLUP",
                F(Samples[6]), "case, functions, WITH ROLLUP");
            Assert.Equal("DELETE t1\nFROM t1\n    INNER JOIN t2 ON t2.id = t1.id\nWHERE t2.gone = 1", F(Samples[7]), "multi-table delete");
        });

        t.Check("format: only whitespace and reserved words' case change — strings, `names`, comments, {{parameters}}, COUNT(*) stay; formatting twice changes nothing", () =>
        {
            var f = F(Samples[3]);
            foreach (var kept in new[] { "_utf8'a  b'", "N'x'", "\"q  q\"", "`my  col`", "{{from_date}}", "'{{d}}'", "COUNT(*)", "-- keep me\n",
                         "/* c  c */", "@v := @v + 1", " -1,", "x - 1", "t.*", "= -5", "LIKE '%a%'" })
                Assert.True(f.Contains(kept), $"keeps {kept}:\n{f}");
            foreach (var s in Samples)
            {
                var once = F(s);
                Assert.Equal(Code(s), Code(once), "same code: " + s);
                Assert.Equal(once, F(once), "formatting again: " + s);
            }
            return f.Replace("\n", " ⏎ ");
        });

        t.Check("format: other statements and the text between statements stay; one statement per line; a comment at the end keeps the ; out of it", () =>
        {
            Assert.Equal("-- top\nCREATE TABLE x (a int);\n\n\nSELECT 1;\nSELECT 2; # c\nset @a=1;\nSELECT 3 -- end\n;",
                F("-- top\nCREATE TABLE x (a int);\n\n\nselect 1;select 2; # c\nset @a=1;\nselect 3 -- end\n;"), "script");
            Assert.Equal("DELIMITER //\nCREATE PROCEDURE p() BEGIN select 1; END//\nDELIMITER ;\nSELECT 1",
                F("DELIMITER //\nCREATE PROCEDURE p() BEGIN select 1; END//\nDELIMITER ;\nselect 1"), "procedure left alone");
            Assert.Equal("", F(""), "empty");
            Assert.Equal("  \n", F("  \n"), "blank");
        });

        t.Check("format: CRLF text stays CRLF; the caret goes to the same code", () =>
        {
            var crlf = SqlFormatter.Format("select a,\r\nb from t where x=1 and y=2", "\r\n");
            Assert.Equal("SELECT a, b\r\nFROM t\r\nWHERE x = 1\r\n    AND y = 2", crlf, "crlf");
            const string before = "select a from t where x=1";
            var after = F(before);
            int caret = SqlFormatter.MapPosition(before, after, before.IndexOf("where", StringComparison.Ordinal));
            Assert.True(after[caret..].StartsWith("WHERE"), "caret at WHERE: " + after[caret..]);
        });
    }

    /// <summary>Formatted queries return exactly the rows of the originals on the test server.</summary>
    public static async Task LiveAsync(TestRunner t, ServerSession session, TestDb db)
    {
        await t.Check("format: formatted queries return the same rows on the test server", async () =>
        {
            string[] queries =
            {
                "select p.name, count(*) n, min(x.price_date) first_date from prices x join products p on p.id=x.product_id " +
                "where x.product_id between 1 and 5 and x.city in ('İstanbul','İzmir') group by p.name order by p.name limit 3",
                "select x.city, (select count(*) from prices y where y.city=x.city and y.id<=100) c from prices x where x.id in (select id from prices where id<20) " +
                "and -x.product_id < 0 group by x.city order by x.city",
                "with t as (select id, product_id from prices where id between 10 and 30) select count(*), sum(product_id) from t union all select 1, 2",
                "select case when id%2=0 then 'even' else 'odd' end k, count(*) from prices where id<=50 group by k order by k -- comment\n",
            };
            await using var conn = await session.OpenDedicatedAsync(db.Database);
            async Task<string> Rows(string sql)
            {
                await using var cmd = new MySqlCommand(sql, conn);
                await using var r = await cmd.ExecuteReaderAsync();
                var rows = new List<string>();
                do
                    while (await r.ReadAsync())
                        rows.Add(string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i => Convert.ToString(r.GetValue(i)))));
                while (await r.NextResultAsync());
                return string.Join("; ", rows);
            }
            int n = 0;
            foreach (var q in queries)
            {
                var formatted = SqlFormatter.Format(q);
                Assert.True(formatted != q && formatted.Contains('\n'), "formatted: " + q);
                var expected = await Rows(q);
                Assert.True(expected.Length > 0, "rows: " + q);
                Assert.Equal(expected, await Rows(formatted), "same rows:\n" + formatted);
                n++;
            }
            return $"{n} queries";
        });
    }
}
