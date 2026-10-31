using System.Text;
using MyFront.Core.Db;
using MyFront.Core.Export;
using MySqlConnector;

namespace MyFront.SelfTest;

/// <summary>Export SQL / Import SQL (tree → right-click): the dump format offline, then export → drop → import on the test server.</summary>
internal static class SqlDumpTests
{
    public static void Offline(TestRunner t)
    {
        t.Check("sqldump: values as SQL literals (Turkish, escapes, binary, dates, times, numbers, zero dates)", () =>
        {
            Assert.Equal("NULL", SqlDump.Literal(null, ColumnKind.Text), "null");
            Assert.Equal("'Ç\\'ok \\\\ yol\\nikinci\\r\\0\\Z son'", SqlDump.Literal("Ç'ok \\ yol\nikinci\r\0\u001A son", ColumnKind.Text), "escapes");
            Assert.Equal("0x00FF10", SqlDump.Literal(new byte[] { 0, 255, 16 }, ColumnKind.Binary), "binary");
            Assert.Equal("''", SqlDump.Literal(Array.Empty<byte>(), ColumnKind.Binary), "empty binary");
            Assert.Equal("'2024-02-29'", SqlDump.Literal(new DateTime(2024, 2, 29), ColumnKind.Date), "date");
            Assert.Equal("'2024-02-29 13:05:09.25'", SqlDump.Literal(new DateTime(2024, 2, 29, 13, 5, 9, 250), ColumnKind.DateTime), "datetime");
            Assert.Equal("'-26:03:00'", SqlDump.Literal(-new TimeSpan(1, 2, 3, 0), ColumnKind.Time), "time over 24 h");
            Assert.Equal("1.5E+20", SqlDump.Literal(1.5e20, ColumnKind.Float), "double");
            Assert.Equal("0.1", SqlDump.Literal(0.1f, ColumnKind.Float), "float");
            Assert.Equal("1234.5600", SqlDump.Literal(1234.5600m, ColumnKind.Decimal), "decimal keeps its scale");
            Assert.Equal("5", SqlDump.Literal(5UL, ColumnKind.Integer), "BIT as a number");
            Assert.Equal("-7", SqlDump.Literal(-7, ColumnKind.Integer), "int");
            Assert.Equal("'0000-00-00 00:00:00'", SqlDump.Literal(new MySqlDateTime(0, 0, 0, 0, 0, 0, 0), ColumnKind.DateTime), "zero datetime");
        });

        t.Check("sqldump: DEFINER left out of triggers and views, nothing else touched", () =>
        {
            Assert.Equal("CREATE TRIGGER `t1` BEFORE INSERT ON `x` FOR EACH ROW SET NEW.a = 'DEFINER=`b`@`c`'",
                SqlDump.StripDefiner("CREATE DEFINER=`root`@`localhost` TRIGGER `t1` BEFORE INSERT ON `x` FOR EACH ROW SET NEW.a = 'DEFINER=`b`@`c`'"), "trigger");
            Assert.Equal("CREATE ALGORITHM=UNDEFINED SQL SECURITY DEFINER VIEW `v` AS select 1 AS `1`",
                SqlDump.StripDefiner("CREATE ALGORITHM=UNDEFINED DEFINER=`mf user`@`%` SQL SECURITY DEFINER VIEW `v` AS select 1 AS `1`"), "view");
            Assert.Equal("CREATE TRIGGER t2 AFTER DELETE ON y FOR EACH ROW DELETE FROM z",
                SqlDump.StripDefiner("CREATE DEFINER='u'@'10.0.%' TRIGGER t2 AFTER DELETE ON y FOR EACH ROW DELETE FROM z"), "quoted user / host");
            Assert.Equal("CREATE VIEW `r` AS select 1", SqlDump.StripDefiner("CREATE DEFINER=`app_role` VIEW `r` AS select 1"), "MariaDB role, no host");
            Assert.Equal("CREATE TABLE `d` (a int)", SqlDump.StripDefiner("CREATE TABLE `d` (a int)"), "nothing to strip");
        });

        t.Check("sqldump: a table name with a line break can't escape its comment line", () =>
        {
            var header = SqlDump.Header("10.11.6-MariaDB", "mf_test", new[] { "evil\nDROP DATABASE mf_test;-- " }, new DateTime(2026, 1, 2));
            Assert.True(header.Split('\n').All(l => !l.StartsWith("DROP", StringComparison.OrdinalIgnoreCase)), header);
            Assert.True(SqlSplitter.Split(header).All(s => s.Text.StartsWith("SET ", StringComparison.Ordinal)), "only SET statements: " + string.Join(" | ", SqlSplitter.Split(header).Select(s => s.Text)));
        });

        t.Check("sqldump: the importer's splitter reads a dump back statement by statement (DELIMITER, escapes, header / footer)", () =>
        {
            var dump = SqlDump.Header("10.11", "d", new[] { "t" }, DateTime.Now)
                       + "\nDROP TABLE IF EXISTS `t`;\nCREATE TABLE `t` (\n  `a` varchar(50) DEFAULT 'x;y'\n);\n"
                       + "INSERT INTO `t` (`a`) VALUES\n(" + SqlDump.Literal("Ç'ok; \\ son\nsatır", ColumnKind.Text) + "),\n(NULL);\n"
                       + "SET SESSION SQL_MODE='STRICT_TRANS_TABLES';\nDELIMITER ;;\nCREATE TRIGGER `tr` BEFORE INSERT ON `t` FOR EACH ROW BEGIN SET NEW.a = UPPER(NEW.a); END ;;\nDELIMITER ;\n"
                       + "SET SESSION SQL_MODE='" + SqlDump.ImportSqlMode + "';\n"
                       + SqlDump.Footer(DateTime.Now);
            var statements = SqlSplitter.Split(dump).Select(s => s.Text).ToList();
            Assert.Equal(5 + 3 + 3 + 4, statements.Count, "statements: " + string.Join(" | ", statements));
            Assert.True(statements[7].Contains("'Ç\\'ok; \\\\ son\\nsatır'") && statements[7].EndsWith("(NULL)"), "INSERT intact: " + statements[7]);
            Assert.True(statements[9].StartsWith("CREATE TRIGGER") && statements[9].EndsWith("END"), "trigger body intact: " + statements[9]);
        });

        t.Check("ddl: RENAME TABLE and DROP TABLE / VIEW", () =>
        {
            Assert.Equal("RENAME TABLE `d`.`eski ad` TO `d`.`yeni ``ad```", TableDdl.Rename("d", "eski ad", " yeni `ad` "), "rename");
            Assert.Equal("DROP TABLE `d`.`t`", TableDdl.Drop("d", "t", isView: false), "drop table");
            Assert.Equal("DROP VIEW `d`.`v`", TableDdl.Drop("d", "v", isView: true), "drop view");
            try
            {
                TableDdl.Rename("d", "t", " ");
                throw new Exception("an empty name must be refused");
            }
            catch (ArgumentException) { }
        });
    }

    private const string OddTable = "mf_dump tür `x`";

    private static readonly string TypesSetup = $"""
        DROP TABLE IF EXISTS {Ident.Quote(OddTable)};
        SET SESSION sql_mode = '';
        CREATE TABLE {Ident.Quote(OddTable)} (
          id INT AUTO_INCREMENT PRIMARY KEY,
          big DECIMAL(40,10),
          d DOUBLE, f FLOAT, b BIT(5), y YEAR, tm TIME(3), dt DATETIME(6), dz DATE, ts TIMESTAMP NULL DEFAULT NULL,
          bl BLOB, e ENUM('elma','armut'), st SET('a','b','c'), txt TEXT,
          hid INT INVISIBLE DEFAULT 7,
          calc VARCHAR(60) AS (CONCAT(e, '-', id)) VIRTUAL
        ) DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_turkish_ci;
        INSERT INTO {Ident.Quote(OddTable)} (big, d, f, b, y, tm, dt, dz, ts, bl, e, st, txt, hid) VALUES
          (123456789012345678901234567890.1234567890, 0.1, 0.1, b'10101', 2024, '-26:03:00.125', '2024-02-29 13:05:09.123456', '0000-00-00',
           '2024-06-01 12:00:00', UNHEX('00FF5C27220A0D1A'), 'armut', 'a,c', 'Çok satırlı\nmetin; \\ ters \'tırnak\' "çift"', 9),
          (NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL),
          (-1.5, -2.5e-300, 3.25, b'0', 1901, '00:00:00', '1000-01-01 00:00:00', '2025-12-31', '2000-01-01 00:00:01', '', 'elma', '', '', 0);
        SET SESSION sql_mode = DEFAULT;
        INSERT INTO mf_prop_parent VALUES (1, 'A-1'), (2, 'İĞÜŞÖÇ ığüşöç \\ \' %');
        INSERT INTO mf_prop_child (parent_id, ad, note) VALUES (1, 'çocuk', 'not 1'), (2, NULL, REPEAT('uzun metin ', 2000)), (2, 'ilk', NULL);
        INSERT INTO mf_prop_part VALUES (1, 2024), (2, 2025), (3, 2031);
        """;

    private static string[] Tables => new[] { "mf_prop_parent", "mf_prop_child", "mf_prop_part", OddTable };

    private sealed record Snapshot(Dictionary<string, string> Creates, Dictionary<string, string> Checksums, Dictionary<string, long> Counts,
        string Triggers, string View, string Hidden);

    private static async Task<object?> Scalar(MySqlConnection conn, string sql, int column = 0)
    {
        await using var cmd = new MySqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() && !r.IsDBNull(column) ? r.GetValue(column) : null;
    }

    private static async Task<Snapshot> TakeAsync(ServerSession s, string db)
    {
        await using var conn = await s.OpenAsync(db);
        var creates = new Dictionary<string, string>();
        var sums = new Dictionary<string, string>();
        var counts = new Dictionary<string, long>();
        foreach (var t in Tables)
        {
            creates[t] = (string)(await Scalar(conn, $"SHOW CREATE TABLE {Ident.Quote(t)}", 1))!;
            sums[t] = Convert.ToString(await Scalar(conn, $"CHECKSUM TABLE {Ident.Quote(t)}", 1)) ?? "";
            counts[t] = Convert.ToInt64(await Scalar(conn, $"SELECT COUNT(*) FROM {Ident.Quote(t)}"));
        }
        var triggers = new StringBuilder();
        await using (var cmd = new MySqlCommand("SELECT TRIGGER_NAME, ACTION_TIMING, EVENT_MANIPULATION, EVENT_OBJECT_TABLE, ACTION_STATEMENT, SQL_MODE FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA = @db ORDER BY 1", conn))
        {
            cmd.Parameters.AddWithValue("@db", db);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) triggers.Append(string.Join("|", Enumerable.Range(0, 6).Select(r.GetString))).Append('\n');
        }
        var view = (string)(await Scalar(conn, "SHOW CREATE VIEW mf_prop_v", 1))!;
        var hidden = Convert.ToString(await Scalar(conn, $"SELECT GROUP_CONCAT(hid ORDER BY id) FROM {Ident.Quote(OddTable)}")) ?? "";
        return new Snapshot(creates, sums, counts, triggers.ToString(), view, hidden);
    }

    /// <summary>What Import SQL does (RunSqlFileForm): the file statement by statement on one dedicated connection.</summary>
    internal static async Task<int> ImportAsync(ServerSession s, string db, string path)
    {
        await using var conn = await s.OpenDedicatedAsync(db);
        return await Task.Run(() =>
        {
            int n = 0;
            using var reader = new StreamReader(path, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            foreach (var stmt in SqlSplitter.Split(reader))
            {
                s.EnsureAllowed(stmt.Text);
                using var cmd = new MySqlCommand(stmt.Text, conn) { CommandTimeout = 0 };
                cmd.ExecuteNonQuery();
                n++;
            }
            return n;
        });
    }

    public static async Task LiveAsync(TestRunner t, ServerSession s, TestDb db)
    {
        var dir = Path.Combine(Path.GetTempPath(), "MyFront.SelfTest", "sqldump");
        Directory.CreateDirectory(dir);
        var objects = Tables.Select(n => new SqlDumpObject(n, false)).Append(new SqlDumpObject("mf_prop_v", true)).ToList();
        await EditorPropertyTests.ExecScript(s, db.Database, EditorPropertyTests.Setup);
        await EditorPropertyTests.ExecScript(s, db.Database, TypesSetup);
        try
        {
            Snapshot? before = null;
            var file = Path.Combine(dir, "mf_prop.sql");
            await t.Check("sqldump: export the mf_prop_* tables (FK, trigger, partitions, view) and an odd-named table of many types", async () =>
            {
                before = await TakeAsync(s, db.Database);
                var progress = new ExportProgress();
                var r = await SqlDump.WriteAsync(s, db.Database, objects, file, new SqlDumpOptions(), progress, CancellationToken.None);
                var text = await File.ReadAllTextAsync(file);
                var bytes = await File.ReadAllBytesAsync(file);
                Assert.True(bytes.Length > 3 && !(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF), "UTF-8 without BOM");
                Assert.True(text.Contains("SET NAMES utf8mb4;") && text.Contains("FOREIGN_KEY_CHECKS=0") && text.Contains("UNIQUE_CHECKS=0"), "header");
                Assert.True(text.TrimEnd().Split('\n').Reverse().Skip(2).Take(4).Any(l => l.StartsWith("SET FOREIGN_KEY_CHECKS=@OLD")), "checks restored at the end");
                Assert.True(text.Contains("DROP TABLE IF EXISTS `mf_prop_child`;") && text.Contains("CONSTRAINT `fk_child_parent`"), "drop + create with the foreign key");
                Assert.True(text.Contains("DELIMITER ;;") && text.Contains("CREATE TRIGGER") && text.Contains("mf_prop_child_bi") && !text.Contains("DEFINER="), "trigger without DEFINER");
                Assert.True(text.IndexOf("CREATE TRIGGER", StringComparison.Ordinal) > text.IndexOf("INSERT INTO `mf_prop_child`", StringComparison.Ordinal), "trigger after the rows");
                Assert.True(text.Contains("DROP VIEW IF EXISTS `mf_prop_v`;") && text.IndexOf("CREATE ALGORITHM", StringComparison.Ordinal) > text.LastIndexOf("INSERT INTO", StringComparison.Ordinal), "view last");
                Assert.True(!text.Contains("`calc`)") && text.Contains("`hid`"), "generated column left out of the INSERT, invisible one named");
                Assert.Equal(11L, r.Rows, "rows written");
                return $"{new FileInfo(file).Length:N0} bytes, {r.Rows} rows, {CellFormat.FormatDuration(r.Elapsed)}";
            });

            await t.Check("sqldump: drop them, Import SQL → same CREATE TABLE, rows (CHECKSUM), triggers, view", async () =>
            {
                await EditorPropertyTests.ExecScript(s, db.Database, $"DROP VIEW mf_prop_v; DROP TABLE mf_prop_child, mf_prop_parent, mf_prop_part, {Ident.Quote(OddTable)};");
                var n = await ImportAsync(s, db.Database, file);
                var after = await TakeAsync(s, db.Database);
                foreach (var tbl in Tables)
                {
                    Assert.Equal(before!.Creates[tbl], after.Creates[tbl], "SHOW CREATE TABLE " + tbl);
                    Assert.Equal(before.Counts[tbl], after.Counts[tbl], "rows of " + tbl);
                    Assert.Equal(before.Checksums[tbl], after.Checksums[tbl], "CHECKSUM TABLE " + tbl);
                }
                Assert.Equal(before!.Triggers, after.Triggers, "triggers (statement, timing, sql_mode)");
                Assert.Equal(before.View, after.View, "SHOW CREATE VIEW");
                Assert.Equal(before.Hidden, after.Hidden, "invisible column values");
                return $"{n} statements";
            });

            await t.Check("sqldump: structure only / data only / cancel (no file left behind)", async () =>
            {
                var structure = Path.Combine(dir, "structure.sql");
                await SqlDump.WriteAsync(s, db.Database, objects, structure, new SqlDumpOptions { Data = false }, new ExportProgress(), CancellationToken.None);
                var st = await File.ReadAllTextAsync(structure);
                Assert.True(st.Contains("CREATE TABLE") && st.Contains("CREATE TRIGGER") && !st.Contains("INSERT INTO"), "structure only");
                var data = Path.Combine(dir, "data.sql");
                // tiny batches: every row of mf_prop_part becomes its own INSERT
                await SqlDump.WriteAsync(s, db.Database, objects, data, new SqlDumpOptions { Structure = false, MaxInsertChars = 40 }, new ExportProgress(), CancellationToken.None);
                var dt = await File.ReadAllTextAsync(data);
                Assert.True(dt.Contains("INSERT INTO") && !dt.Contains("CREATE TABLE") && !dt.Contains("DROP TABLE") && !dt.Contains("CREATE TRIGGER"), "data only");
                Assert.Equal(3, dt.Split("INSERT INTO `mf_prop_part`").Length - 1, "batches");
                // data only goes into the existing tables
                await EditorPropertyTests.ExecScript(s, db.Database, $"DELETE FROM mf_prop_child; DELETE FROM mf_prop_parent; DELETE FROM mf_prop_part; DELETE FROM {Ident.Quote(OddTable)};");
                await ImportAsync(s, db.Database, data);
                var again = await TakeAsync(s, db.Database);
                foreach (var tbl in Tables) Assert.Equal(before!.Checksums[tbl], again.Checksums[tbl], "data-only re-import of " + tbl);
                var cancelled = Path.Combine(dir, "cancelled.sql");
                using var cts = new CancellationTokenSource();
                cts.Cancel();
                await Assert.Throws(() => SqlDump.WriteAsync(s, db.Database, objects, cancelled, new SqlDumpOptions(), new ExportProgress(), cts.Token), "cancelled");
                Assert.True(!File.Exists(cancelled) && !File.Exists(cancelled + ".part"), "nothing written");
            });
        }
        finally
        {
            await EditorPropertyTests.ExecScript(s, db.Database, EditorPropertyTests.Cleanup + $" DROP TABLE IF EXISTS {Ident.Quote(OddTable)};");
        }
    }
}
