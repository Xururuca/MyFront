using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MyFront.Core.Accounts;
using MyFront.Core.Db;
using MyFront.Core.History;
using MySqlConnector;

namespace MyFront.SelfTest;

/// <summary>Headless tests: pure logic first, then everything against the real test server.</summary>
internal static class CoreTests
{
    private const string Turkish = "İıŞşĞğÜüÖöÇç — İSTANBUL ığdır Çanakkale";

    public static async Task RunAsync(TestRunner t, TestDb db)
    {
        Console.WriteLine("== offline ==");
        Splitter(t);
        Classifier(t);
        Misc(t);
        EditorPropertyTests.Offline(t);
        ExplainTests.Offline(t);
        ScopeTests.Offline(t);
        FormatTests.Offline(t);
        ConnectionFileTests.Offline(t);
        HiddenTablesTests.Offline(t);
        DdlTests.Offline(t);
        DdlTests.OfflineKeys(t);
        SqlDumpTests.Offline(t);
        DataEditTests.Offline(t);
        ResultEditTests.Offline(t);
        GridClipboardTests.Offline(t);
        NameDragTests.Offline(t);
        await SshTests.RunAsync(t);

        Console.WriteLine("\n== test server ==");
        await Tls(t, db);
        await SshTests.RunLiveAsync(t, db);
        using var session = await ServerSession.ConnectAsync(db.ToAccount(TlsMode.Required), db.Password);
        await Schema(t, session, db);
        await Execution(t, session, db);
        await Cancel(t, session, db);
        await ReadOnly(t, db);
        await Identifiers(t, session, db);
        await EditorPropertyTests.PropertiesAsync(t, session, db);
        await DdlTests.LiveAsync(t, session, db);
        await DataEditTests.LiveAsync(t, session, db);
        await ResultEditTests.LiveAsync(t, session, db);
        await FormatTests.LiveAsync(t, session, db);
    }

    // ---- offline ------------------------------------------------------------------------------

    private static void Splitter(TestRunner t)
    {
        t.Check("splitter: quotes, comments, escapes", () =>
        {
            var sql = "SELECT 'a;b', \"c;d\", `e;f`; -- x;y\nSELECT 'it''s', 'x\\';y' # z;\n;/* a;b */ SELECT 3";
            var s = SqlSplitter.Split(sql);
            Assert.Equal(3, s.Count, "statement count");
            Assert.Equal("SELECT 'a;b', \"c;d\", `e;f`", s[0].Text, "first");
            Assert.Equal("SELECT 'it''s', 'x\\';y' # z;", s[1].Text, "second");
            Assert.Equal("SELECT 3", s[2].Text, "third");
            Assert.Equal(sql.IndexOf("SELECT 3", StringComparison.Ordinal), s[2].Start, "offset");
        });
        t.Check("splitter: DELIMITER and procedure body", () =>
        {
            var sql = "DROP PROCEDURE IF EXISTS p;\r\nDELIMITER $$\r\nCREATE PROCEDURE p()\r\nBEGIN\r\n  SELECT 1;\r\n  SELECT 2;\r\nEND$$\r\nDELIMITER ;\r\nCALL p();";
            var s = SqlSplitter.Split(sql);
            Assert.Equal(3, s.Count, "statement count");
            Assert.True(s[1].Text.StartsWith("CREATE PROCEDURE") && s[1].Text.EndsWith("END"), "procedure body intact: " + s[1].Text);
            Assert.Equal("CALL p()", s[2].Text, "after delimiter reset");
            Assert.Equal(9, s[2].Line, "line number");
        });
        t.Check("splitter: executable comments and comment-only text", () =>
        {
            var s = SqlSplitter.Split("/*!40101 SET NAMES utf8mb4 */;\n-- only a comment\n/* block */");
            Assert.Equal(1, s.Count, "statement count");
            Assert.Equal("/*!40101 SET NAMES utf8mb4 */", s[0].Text, "kept");
        });
        t.Check("splitter: statement at caret", () =>
        {
            var sql = "SELECT 1;\n\nSELECT 2;\nSELECT 3";
            var s = SqlSplitter.Split(sql);
            Assert.Equal("SELECT 1", SqlSplitter.StatementAt(s, 3)!.Value.Text, "inside first");
            Assert.Equal("SELECT 1", SqlSplitter.StatementAt(s, 10)!.Value.Text, "blank line after first");
            Assert.Equal("SELECT 2", SqlSplitter.StatementAt(s, sql.IndexOf("2", StringComparison.Ordinal))!.Value.Text, "second");
            Assert.Equal("SELECT 3", SqlSplitter.StatementAt(s, sql.Length)!.Value.Text, "end of text");
        });
    }

    private static void Classifier(TestRunner t)
    {
        t.Check("classifier: read-only safe statements", () =>
        {
            foreach (var sql in new[]
                     {
                         "SELECT * FROM t", "  (SELECT 1) UNION (SELECT 2)", "SHOW TABLES", "DESCRIBE t", "EXPLAIN SELECT 1",
                         "WITH x AS (SELECT 1) SELECT * FROM x", "SELECT REPLACE(name,'a','b'), INSERT('abc',1,1,'z') FROM t",
                         "SET @a = 1", "SET NAMES utf8mb4", "USE mf_test", "SELECT * FROM t FOR UPDATE", "-- c\nSELECT 'DELETE'",
                         "SET STATEMENT max_statement_time=1 FOR SELECT 1", "ANALYZE SELECT 1", "/*!40101 SET NAMES utf8 */",
                     })
                Assert.True(SqlClassifier.IsReadOnlySafe(sql), "should be allowed: " + sql);
        });
        t.Check("classifier: read-only blocked statements", () =>
        {
            foreach (var sql in new[]
                     {
                         "INSERT INTO t VALUES (1)", "update t set a=1", "DELETE FROM t", "DROP TABLE t", "TRUNCATE t", "ALTER TABLE t ADD c INT",
                         "CREATE TABLE x (a INT)", "REPLACE INTO t VALUES (1)", "CALL p()", "SET GLOBAL max_connections=1",
                         "SET @@global.sql_mode=''", "SET SESSION TRANSACTION READ WRITE", "SET transaction_read_only = 0",
                         "SELECT 1; DELETE FROM t", "SELECT * FROM t INTO OUTFILE '/tmp/x'", "WITH x AS (SELECT 1) DELETE FROM t",
                         "EXPLAIN ANALYZE DELETE FROM t", "ANALYZE TABLE t", "KILL 5", "GRANT ALL ON *.* TO x", "LOAD DATA INFILE 'x' INTO TABLE t",
                         "SET STATEMENT max_statement_time=1 FOR DELETE FROM t", "/*!40101 DROP TABLE t */", "START TRANSACTION READ WRITE",
                     })
                Assert.True(!SqlClassifier.IsReadOnlySafe(sql), "should be blocked: " + sql);
        });
        t.Check("classifier: dangerous keywords for quick queries", () =>
        {
            Assert.True(SqlClassifier.ContainsDangerous("UPDATE t SET a=1"), "UPDATE");
            Assert.True(SqlClassifier.ContainsDangerous("select 1; drop table x"), "DROP");
            Assert.True(!SqlClassifier.ContainsDangerous("SELECT 'DELETE me' FROM t -- DROP"), "keywords in strings/comments");
            Assert.True(!SqlClassifier.ContainsDangerous("SELECT * FROM t FOR UPDATE"), "FOR UPDATE");
            Assert.True(!SqlClassifier.ContainsDangerous("SELECT updated_at FROM `delete`"), "identifiers");
        });
    }

    private static void Misc(TestRunner t)
    {
        t.Check("ident: backtick escaping", () =>
        {
            Assert.Equal("`a``b`", Ident.Quote("a`b"), "quote");
            Assert.Equal("`d`.`t`", Ident.Quote("d", "t"), "qualified");
            Assert.Equal("50\\%\\_x\\\\", Ident.EscapeLike("50%_x\\"), "like");
        });
        t.Check("dpapi: round trip, no plaintext in accounts.json", () =>
        {
            var a = new Account { Name = "dpapi-test", SavePassword = true };
            a.SetPassword("Gizli-Şifre-123");
            Assert.True(a.PasswordEncrypted!.StartsWith("dpapi1:"), "prefix");
            Assert.Equal("Gizli-Şifre-123", a.GetSavedPassword(), "decrypt");
            AccountStore.Save(new[] { a });
            var json = File.ReadAllText(Path.Combine(Core.AppPaths.DataDir, "accounts.json"));
            Assert.True(!json.Contains("Gizli"), "plaintext password found in accounts.json");
            Assert.Equal("Gizli-Şifre-123", AccountStore.Load().Single().GetSavedPassword(), "reload");
        });
        t.Check("history: password literals masked", () =>
        {
            Assert.Equal("CREATE USER x IDENTIFIED BY '***'", SqlHistory.MaskSecrets("CREATE USER x IDENTIFIED BY 'p@ss''w'"), "identified by");
            Assert.Equal("SET PASSWORD FOR x = PASSWORD('***')", SqlHistory.MaskSecrets("SET PASSWORD FOR x = PASSWORD('abc')"), "password()");
            Assert.Equal("SELECT 'IDENTIFIED' , 'x'", SqlHistory.MaskSecrets("SELECT 'IDENTIFIED' , 'x'"), "not a secret");
        });
        t.Check("settings: word wrap is on by default, also for a settings.json written before it existed", () =>
        {
            Assert.True(new Core.AppSettings().WordWrap, "new settings");
            var old = System.Text.Json.JsonSerializer.Deserialize("{ \"Theme\": \"dark\" }", Core.CoreJson.Default.AppSettings)!;
            Assert.True(old.WordWrap && old.Theme == "dark", "older file");
            var off = new Core.AppSettings { WordWrap = false };
            var json = System.Text.Json.JsonSerializer.Serialize(off, Core.CoreJson.Default.AppSettings);
            Assert.True(!System.Text.Json.JsonSerializer.Deserialize(json, Core.CoreJson.Default.AppSettings)!.WordWrap, "off round-trips: " + json);
        });
        t.Check("format: display values", () =>
        {
            Assert.Equal("2024-02-29", CellFormat.ToDisplay(new DateTime(2024, 2, 29), ColumnKind.Date), "date");
            Assert.Equal("2024-02-29 13:05:09.25", CellFormat.ToDisplay(new DateTime(2024, 2, 29, 13, 5, 9, 250), ColumnKind.DateTime), "datetime");
            Assert.Equal("1234.56", CellFormat.ToDisplay(1234.56m, ColumnKind.Decimal), "decimal is invariant");
            Assert.Equal("-26:03:00", CellFormat.ToDisplay(-new TimeSpan(1, 2, 3, 0), ColumnKind.Time), "time > 24h");
            Assert.Equal("(NULL)", CellFormat.ToDisplay(null, ColumnKind.Text), "null");
        });
    }

    // ---- server ---------------------------------------------------------------------------------

    private static async Task Tls(TestRunner t, TestDb db)
    {
        foreach (var mode in Enum.GetValues<TlsMode>())
        {
            await t.Check($"tls: {mode} connects encrypted", async () =>
            {
                using var s = await ServerSession.ConnectAsync(db.ToAccount(mode), db.Password);
                Assert.True(s.TlsVersion is not null && s.TlsCipher is not null, "no TLS on the session");
                return $"{s.TlsVersion} {s.TlsCipher}";
            });
        }

        // A CA that did not sign the server certificate must be rejected.
        var wrongCa = Path.Combine(Path.GetTempPath(), "MyFront.SelfTest", "wrong-ca.pem");
        using (var key = RSA.Create(2048))
        {
            var req = new CertificateRequest("CN=Wrong Test CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            File.WriteAllText(wrongCa, cert.ExportCertificatePem());
        }
        foreach (var mode in new[] { TlsMode.VerifyCA, TlsMode.VerifyFull })
        {
            await t.Check($"tls: {mode} rejects a wrong CA", async () =>
            {
                var a = db.ToAccount(mode);
                a.CaFile = wrongCa;
                var ex = await Assert.Throws(() => ServerSession.ConnectAsync(a, db.Password), "connect with wrong CA");
                return ex.GetType().Name + ": " + ex.Message.Split('\n')[0];
            });
        }
        await t.Check("tls: Verify CA without CA file is refused before connecting", async () =>
        {
            var a = db.ToAccount(TlsMode.VerifyCA);
            a.CaFile = null;
            await Assert.Throws(() => ServerSession.ConnectAsync(a, db.Password), "no CA file");
        });
        await t.Check("auth: wrong password gives a clear error", async () =>
        {
            var ex = await Assert.Throws(() => ServerSession.ConnectAsync(db.ToAccount(), "wrong-password"), "wrong password");
            Assert.True(ex is MySqlException { ErrorCode: MySqlErrorCode.AccessDenied }, "expected access denied, got " + ex.Message);
            return ex.Message;
        });
    }

    private static async Task Schema(TestRunner t, ServerSession s, TestDb db)
    {
        await t.Check("schema: databases, tables, view", async () =>
        {
            var dbs = await s.Schema.GetDatabasesAsync();
            Assert.True(dbs.Contains(db.Database), "database missing");
            var objs = await s.Schema.GetObjectsAsync(db.Database);
            Assert.True(objs.Any(o => o is { Name: "prices", Type: DbObjectType.Table }), "prices");
            Assert.True(objs.Any(o => o is { Name: "products", Type: DbObjectType.Table }), "products");
            Assert.True(objs.Any(o => o is { Name: "v_latest_prices", Type: DbObjectType.View }), "view");
            return $"{dbs.Count} databases, {objs.Count} objects";
        });
        await t.Check("schema: table info, key detection, view flag", async () =>
        {
            var prices = await s.Schema.GetTableAsync(db.Database, "prices");
            Assert.Equal(9, prices.Columns.Count, "prices columns");
            Assert.Equal("id", prices.KeyColumns!.Single(), "prices key");
            Assert.True(!prices.IsView, "prices is a table");
            var view = await s.Schema.GetTableAsync(db.Database, "v_latest_prices");
            Assert.True(view.IsView, "view flag");
            var status = await s.Schema.GetTableStatusAsync(db.Database);
            var p = status.Single(x => x.Name == "prices");
            return $"prices ≈{p.Rows:N0} rows, {CellFormat.FormatBytes(p.DataLength)}, engine {p.Engine}, updated {p.UpdateTime}";
        });
        await t.Check("schema: SHOW CREATE for table, view, database", async () =>
        {
            Assert.True((await s.Schema.GetCreateStatementAsync(db.Database, "prices", DbObjectType.Table)).StartsWith("CREATE TABLE `prices`"), "table");
            var view = await s.Schema.GetCreateStatementAsync(db.Database, "v_latest_prices", DbObjectType.View);
            Assert.True(view.StartsWith("CREATE") && view.Contains("VIEW") && view.Contains("v_latest_prices"), "view: " + view);
            Assert.True((await s.Schema.GetCreateStatementAsync(db.Database, null, null)).StartsWith("CREATE DATABASE"), "database");
        });
    }

    private sealed class Collector : IQueryListener
    {
        public readonly List<ResultSetData> Sets = new();
        public readonly List<StatementOutcome> Outcomes = new();
        public void ResultSetStarted(ResultSetData rs) { lock (Sets) Sets.Add(rs); }
        public void StatementFinished(StatementOutcome o) { lock (Outcomes) Outcomes.Add(o); }
    }

    private static async Task Execution(TestRunner t, ServerSession s, TestDb db)
    {
        await using var conn = await s.OpenDedicatedAsync(db.Database);
        await t.Check("exec: multi-statement script, @vars, Turkish text round trip", async () =>
        {
            var c = new Collector();
            var script = $"SET @x := 41;\nSELECT @x + 1 AS answer, '{Turkish}' AS tr;\nSELECT COUNT(*) FROM prices WHERE city IN ('İstanbul','İzmir');";
            var outcomes = await QueryExecutor.RunAsync(s, conn, SqlSplitter.Split(script), c, CancellationToken.None, db.Database);
            Assert.Equal(3, outcomes.Count, "outcomes");
            Assert.True(outcomes.All(o => o.Error is null), "errors: " + string.Join("; ", outcomes.Select(o => o.Error?.Message)));
            Assert.Equal(2, c.Sets.Count, "result sets");
            Assert.Equal("42", CellFormat.ToDisplay(c.Sets[0].Rows.GetRow(0)[0], c.Sets[0].Columns[0].Kind), "@x + 1");
            Assert.Equal(Turkish, c.Sets[0].Rows.GetRow(0)[1], "Turkish text");
            return $"İstanbul+İzmir rows: {c.Sets[1].Rows.GetRow(0)[0]}";
        });
        await t.Check("exec: error stops the script and reports the statement", async () =>
        {
            var c = new Collector();
            var outcomes = await QueryExecutor.RunAsync(s, conn, SqlSplitter.Split("SELECT 1;\nSELECT * FROM no_such_table;\nSELECT 3;"), c, CancellationToken.None);
            Assert.Equal(2, outcomes.Count, "stopped after the error");
            Assert.True(outcomes[1].Error is MySqlException { ErrorCode: MySqlErrorCode.NoSuchTable }, "error kind");
            Assert.Equal(2, outcomes[1].Statement.Line, "error line");
        });
        await t.Check("exec: zero dates display instead of throwing", async () =>
        {
            var c = new Collector();
            var script = "SET SESSION sql_mode = '';\nCREATE TEMPORARY TABLE z (d DATETIME, dd DATE);\nINSERT INTO z VALUES ('0000-00-00 00:00:00', '0000-00-00'), ('2024-05-01 10:00:00', '2024-05-01');\nSELECT * FROM z;";
            var outcomes = await QueryExecutor.RunAsync(s, conn, SqlSplitter.Split(script), c, CancellationToken.None);
            Assert.True(outcomes.All(o => o.Error is null), "errors: " + string.Join("; ", outcomes.Select(o => o.Error?.Message)));
            var rs = c.Sets.Single();
            var zero = CellFormat.ToDisplay(rs.Rows.GetRow(0)[0], rs.Columns[0].Kind);
            var zeroDate = CellFormat.ToDisplay(rs.Rows.GetRow(0)[1], rs.Columns[1].Kind);
            Assert.Equal("0000-00-00 00:00:00", zero, "zero datetime");
            Assert.Equal("0000-00-00", zeroDate, "zero date");
            Assert.True(rs.Rows.GetRow(1)[0] is DateTime, "valid dates become DateTime");
            await using var reset = new MySqlCommand("SET SESSION sql_mode = DEFAULT; DROP TEMPORARY TABLE z", conn);
            await reset.ExecuteNonQueryAsync();
        });
        await t.Check("exec: fetch all of prices (~200k rows) into the row store", async () =>
        {
            GC.Collect();
            long before = GC.GetTotalMemory(true);
            var c = new Collector();
            var sw = Stopwatch.StartNew();
            var outcomes = await QueryExecutor.RunAsync(s, conn, SqlSplitter.Split("SELECT * FROM prices"), c, CancellationToken.None);
            var elapsed = sw.Elapsed;
            Assert.True(outcomes[0].Error is null, outcomes[0].Error?.Message ?? "");
            var rs = c.Sets.Single();
            Assert.True(rs.Rows.Count > 190_000, "row count " + rs.Rows.Count);
            long after = GC.GetTotalMemory(true);
            var sortSw = Stopwatch.StartNew();
            var order = rs.Rows.BuildSortOrder(3, descending: false); // city, culture-aware
            rs.Rows.ApplySortOrder(order);
            var first = (string?)rs.Rows.GetRow(order.Length - 1)[3];
            return $"{rs.Rows.Count:N0} rows, exec {rs.ExecTime.TotalMilliseconds:0} ms, fetch {rs.FetchTime.TotalMilliseconds:0} ms, total {elapsed.TotalMilliseconds:0} ms, " +
                   $"memory +{(after - before) / 1024 / 1024} MB, client sort by city {sortSw.ElapsedMilliseconds} ms (last: {first})";
        });
    }

    private static async Task Cancel(TestRunner t, ServerSession s, TestDb db)
    {
        await using var conn = await s.OpenDedicatedAsync(db.Database);
        await t.Check("cancel: SLEEP(30) is killed within ~1 s, connection stays usable", async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
            var sw = Stopwatch.StartNew();
            var outcomes = await QueryExecutor.RunAsync(s, conn, SqlSplitter.Split("SELECT SLEEP(30)"), new Collector(), cts.Token);
            var took = sw.ElapsedMilliseconds;
            Assert.True(outcomes[0].Cancelled, "not marked cancelled: " + outcomes[0].Error?.Message);
            Assert.True(took < 3000, $"took {took} ms");
            await using var cmd = new MySqlCommand("SELECT 1", conn);
            Assert.Equal(1L, Convert.ToInt64(await cmd.ExecuteScalarAsync()), "connection usable after cancel");
            return $"stopped after {took} ms";
        });
        await t.Check("cancel: huge streaming result (10M-row cross join) stops mid-fetch", async () =>
        {
            var c = new Collector();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            var sw = Stopwatch.StartNew();
            var outcomes = await QueryExecutor.RunAsync(s, conn, SqlSplitter.Split("SELECT a.id, b.name FROM prices a CROSS JOIN products b"), c, cts.Token);
            var took = sw.ElapsedMilliseconds;
            Assert.True(outcomes[0].Cancelled, "not marked cancelled: " + outcomes[0].Error?.Message);
            Assert.True(took < 5000, $"took {took} ms");
            await using var cmd = new MySqlCommand("SELECT 1", conn);
            Assert.Equal(1L, Convert.ToInt64(await cmd.ExecuteScalarAsync()), "connection usable after cancel");
            return $"{c.Sets.Single().Rows.Count:N0} rows streamed before cancel, stopped after {took} ms";
        });
    }

    private static async Task ReadOnly(TestRunner t, TestDb db)
    {
        using var ro = await ServerSession.ConnectAsync(db.ToAccount(TlsMode.Required, readOnly: true), db.Password);
        await t.Check("read-only: client guard blocks writes, reads still work", async () =>
        {
            await using var conn = await ro.OpenDedicatedAsync(db.Database);
            var c = new Collector();
            var outcomes = await QueryExecutor.RunAsync(ro, conn, SqlSplitter.Split("SELECT COUNT(*) FROM products; UPDATE products SET name = name WHERE id = 1;"), c, CancellationToken.None);
            Assert.True(outcomes[0].Error is null, "select failed");
            Assert.True(outcomes[1].Error is ReadOnlyViolationException, "update not blocked: " + outcomes[1].Error?.Message);
            return outcomes[1].Error!.Message;
        });
        await t.Check("read-only: server also refuses writes that bypass the client check", async () =>
        {
            await using var conn = await ro.OpenAsync(db.Database);
            await using var cmd = new MySqlCommand("UPDATE products SET name = name WHERE id = 1", conn);
            var ex = await Assert.Throws(() => cmd.ExecuteNonQueryAsync(), "server-side read-only");
            return ex.Message;
        });
    }

    private static async Task Identifiers(TestRunner t, ServerSession s, TestDb db)
    {
        await t.Check("ident: table and column names with backticks, spaces, Turkish letters", async () =>
        {
            const string table = "mf selftest `odd` tâblo İş";
            const string column = "fiyat `x` ğüş";
            var q = Ident.Quote(db.Database, table);
            await using var conn = await s.OpenAsync(db.Database);
            async Task Exec(string sql, params (string, object)[] ps)
            {
                await using var cmd = new MySqlCommand(sql, conn);
                foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
                await cmd.ExecuteNonQueryAsync();
            }
            await Exec($"DROP TABLE IF EXISTS {q}");
            await Exec($"CREATE TABLE {q} (id INT PRIMARY KEY, {Ident.Quote(column)} VARCHAR(50)) DEFAULT CHARSET=utf8mb4");
            try
            {
                await Exec($"INSERT INTO {q} VALUES (1, @v)", ("@v", Turkish));
                await using var cmd = new MySqlCommand($"SELECT {Ident.Quote(column)} FROM {q}", conn);
                Assert.Equal(Turkish, (string?)await cmd.ExecuteScalarAsync(), "value");
                var objs = await s.Schema.GetObjectsAsync(db.Database);
                Assert.True(objs.Any(o => o.Name == table), "listed with its real name");
                var info = await s.Schema.GetTableAsync(db.Database, table);
                Assert.Equal(column, info.Columns[1].Name, "column name");
            }
            finally
            {
                await Exec($"DROP TABLE IF EXISTS {q}");
            }
        });
    }
}
