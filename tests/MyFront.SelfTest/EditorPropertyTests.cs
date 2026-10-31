using MyFront.Core;
using MyFront.Core.Db;
using MyFront.Core.Editors;
using MySqlConnector;

namespace MyFront.SelfTest;

/// <summary>Autosaved SQL editor tabs (offline) and the table Properties loader (test server).</summary>
internal static class EditorPropertyTests
{
    public static void Offline(TestRunner t)
    {
        t.Check("editors: a new connection has SQL Editor 1–5; text round-trips, empty tab = no file", () =>
        {
            var id = "edtest" + Guid.NewGuid().ToString("N");
            var set = EditorStore.Load(id);
            Assert.Equal("1,2,3,4,5", string.Join(",", set.Tabs.Select(x => x.Slot)), "fixed slots");
            Assert.Equal(1, set.Selected, "selected");
            const string sql = "SELECT 'İĞÜŞÖÇ ığüşöç' AS tr;\r\n-- ikinci satır\n";
            EditorStore.WriteText(id, 2, sql);
            Assert.Equal(sql, EditorStore.ReadText(id, 2), "exact text (line endings, Turkish)");
            EditorStore.WriteText(id, 2, "");
            Assert.True(!File.Exists(Path.Combine(EditorStore.Folder(id), "2.sql")), "empty → file removed");
            Assert.Equal("", EditorStore.ReadText(id, 2), "reads back empty");
        });

        t.Check("editors: extra tabs and caret come back; a tab saved just before a crash (no tabs.json) too", () =>
        {
            var id = "edtest" + Guid.NewGuid().ToString("N");
            EditorStore.WriteText(id, 3, "SELECT 3");
            EditorStore.WriteText(id, 7, "SELECT 7");
            EditorStore.SaveIndex(id, new EditorSet
            {
                Selected = 7,
                Tabs = new() { new() { Slot = 3, Caret = 4, FirstLine = 0 }, new() { Slot = 7, FilePath = @"C:\x\rapor.sql", Caret = 2 }, new() { Slot = 9 } },
            });
            EditorStore.WriteText(id, 8, "SELECT 8 -- written, then the app died");
            var set = EditorStore.Load(id);
            Assert.Equal("1,2,3,4,5,7,8", string.Join(",", set.Tabs.Select(x => x.Slot)), "slots (9 had no text → gone)");
            Assert.Equal(7, set.Selected, "selected");
            Assert.Equal(@"C:\x\rapor.sql", set.Tabs.Single(x => x.Slot == 7).FilePath, "file title");
            Assert.Equal(4, set.Tabs.Single(x => x.Slot == 3).Caret, "caret");

            File.WriteAllText(Path.Combine(EditorStore.Folder(id), "tabs.json"), "{ not json");
            set = EditorStore.Load(id);
            Assert.Equal("1,2,3,4,5,7,8", string.Join(",", set.Tabs.Select(x => x.Slot)), "rebuilt from the files after a broken tabs.json");
            Assert.Equal("SELECT 7", EditorStore.ReadText(id, 7), "text kept");

            EditorStore.Remove(id, 7);
            Assert.True(EditorStore.Load(id).Tabs.All(x => x.Slot != 7), "closed extra tab is gone");
        });

        t.Check("editors: each connection has its own folder", () =>
        {
            var a = "edtest" + Guid.NewGuid().ToString("N");
            var b = "edtest" + Guid.NewGuid().ToString("N");
            EditorStore.WriteText(a, 1, "SELECT 'a'");
            EditorStore.WriteText(b, 1, "SELECT 'b'");
            Assert.Equal("SELECT 'a'", EditorStore.ReadText(a, 1), "a");
            Assert.Equal("SELECT 'b'", EditorStore.ReadText(b, 1), "b");
            Assert.True(EditorStore.Folder(a).StartsWith(AppPaths.DataDir, StringComparison.OrdinalIgnoreCase), "under the data folder");
            Assert.True(!EditorStore.Folder("..\\..\\evil").Contains(".."), "account id can't leave the folder");
        });
    }

    public const string Setup = """
        DROP VIEW IF EXISTS mf_prop_v;
        DROP TABLE IF EXISTS mf_prop_child, mf_prop_parent, mf_prop_part;
        CREATE TABLE mf_prop_parent (
          id INT PRIMARY KEY,
          code VARCHAR(20) NOT NULL,
          UNIQUE KEY uq_code (code)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_turkish_ci COMMENT='ana tablo';
        CREATE TABLE mf_prop_child (
          id INT AUTO_INCREMENT PRIMARY KEY,
          parent_id INT NOT NULL,
          ad VARCHAR(100) COLLATE utf8mb4_turkish_ci DEFAULT 'İsimsiz' COMMENT 'Türkçe ad',
          note TEXT,
          created DATETIME DEFAULT CURRENT_TIMESTAMP,
          KEY ix_ad (ad(10)),
          CONSTRAINT fk_child_parent FOREIGN KEY (parent_id) REFERENCES mf_prop_parent (id) ON DELETE CASCADE ON UPDATE RESTRICT
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 ROW_FORMAT=DYNAMIC AUTO_INCREMENT=42 COMMENT='çocuk tablo';
        CREATE TRIGGER mf_prop_child_bi BEFORE INSERT ON mf_prop_child FOR EACH ROW SET NEW.ad = UPPER(NEW.ad);
        CREATE TABLE mf_prop_part (id INT NOT NULL, yil SMALLINT NOT NULL, PRIMARY KEY (id, yil))
          PARTITION BY RANGE (yil) (PARTITION p2024 VALUES LESS THAN (2025), PARTITION p2025 VALUES LESS THAN (2026), PARTITION pmax VALUES LESS THAN MAXVALUE);
        CREATE VIEW mf_prop_v AS SELECT c.id, c.ad, p.code FROM mf_prop_child c JOIN mf_prop_parent p ON p.id = c.parent_id;
        """;

    public const string Cleanup = "DROP VIEW IF EXISTS mf_prop_v; DROP TABLE IF EXISTS mf_prop_child, mf_prop_parent, mf_prop_part;";

    public static async Task ExecScript(ServerSession s, string db, string script)
    {
        await using var conn = await s.OpenAsync(db);
        foreach (var stmt in SqlSplitter.Split(script))
        {
            await using var cmd = new MySqlCommand(stmt.Text, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public static async Task PropertiesAsync(TestRunner t, ServerSession s, TestDb db)
    {
        await t.Check("properties: setup tables with FK, trigger, partitions and a view", () => ExecScript(s, db.Database, Setup));
        try
        {
            await t.Check("properties: General / Information of a table (engine, charset, row format, auto increment, comment)", async () =>
            {
                var p = await TableProperties.LoadAsync(s, db.Database, "mf_prop_child");
                Assert.True(!p.IsView, "table");
                Assert.Equal("InnoDB", p.Engine, "engine");
                Assert.Equal("utf8mb4", p.CharacterSet, "charset");
                Assert.True(p.Collation?.StartsWith("utf8mb4_") == true, "collation " + p.Collation);
                Assert.Equal("çocuk tablo", p.Comment, "comment (Turkish)");
                Assert.Equal("Dynamic", p.RowFormat, "row format");
                Assert.Equal(42L, p.AutoIncrement, "auto increment");
                Assert.True(p.Created is not null, "create time");
                Assert.True(p.DataLength is not null && p.IndexLength is not null, "sizes");
                var parent = await TableProperties.LoadAsync(s, db.Database, "mf_prop_parent");
                Assert.Equal("utf8mb4_turkish_ci", parent.Collation, "table collation");
                return $"{p.Engine} {p.Collation} {p.RowFormat} AI={p.AutoIncrement} data={CellFormat.FormatBytes(p.DataLength)}";
            });

            await t.Check("properties: Fields, Keys (prefix length), Foreign Keys (rules), Triggers, Source Code", async () =>
            {
                var p = await TableProperties.LoadAsync(s, db.Database, "mf_prop_child");
                Assert.Equal("id,parent_id,ad,note,created", string.Join(",", p.Fields.Select(f => f.Name)), "fields");
                var ad = p.Fields.Single(f => f.Name == "ad");
                Assert.Equal("İsimsiz", ad.Default, "default");
                Assert.Equal("utf8mb4_turkish_ci", ad.Collation, "column collation");
                Assert.Equal("Türkçe ad", ad.Comment, "column comment");
                Assert.True(p.Fields[0].IsAutoIncrement && p.Fields[0].Key == "PRI", "id auto_increment PK");
                Assert.Equal("id", p.Keys.Single(k => k.Name == "PRIMARY").Columns, "primary");
                Assert.Equal("ad(10)", p.Keys.Single(k => k.Name == "ix_ad").Columns, "prefix index");
                var fk = p.ForeignKeys.Single();
                Assert.Equal("fk_child_parent", fk.Name, "fk name");
                Assert.Equal("parent_id", fk.Columns, "fk columns");
                Assert.Equal("mf_prop_parent (id)", fk.References, "references");
                Assert.Equal("CASCADE", fk.OnDelete, "on delete");
                Assert.Equal("RESTRICT", fk.OnUpdate, "on update");
                var tr = p.Triggers.Single();
                Assert.True(tr.Name == "mf_prop_child_bi" && tr.Timing == "BEFORE" && tr.Event == "INSERT", $"{tr.Name} {tr.Timing} {tr.Event}");
                Assert.True(tr.Statement.Contains("UPPER", StringComparison.OrdinalIgnoreCase), tr.Statement);
                Assert.True(p.Partitions.Count == 0, "not partitioned");
                Assert.True(p.CreateStatement.StartsWith("CREATE TABLE `mf_prop_child`") && p.CreateStatement.Contains("CONSTRAINT `fk_child_parent`"), p.CreateStatement[..60]);
            });

            await t.Check("properties: Partitions (RANGE, values, MAXVALUE)", async () =>
            {
                var p = await TableProperties.LoadAsync(s, db.Database, "mf_prop_part");
                Assert.Equal("p2024,p2025,pmax", string.Join(",", p.Partitions.Select(x => x.Name)), "names");
                Assert.True(p.Partitions.All(x => x.Method == "RANGE"), "method " + p.Partitions[0].Method);
                Assert.True(p.Partitions[0].Expression.Contains("yil"), "expression " + p.Partitions[0].Expression);
                Assert.Equal("2025", p.Partitions[0].Value, "value");
                Assert.Equal("MAXVALUE", p.Partitions[2].Value, "maxvalue");
                Assert.True(p.Partitions.All(x => x.DataLength > 0), "data size per partition: " + string.Join(", ", p.Partitions.Select(x => x.DataLength?.ToString() ?? "null")));
                Assert.True(p.CreateOptions?.Contains("partitioned", StringComparison.OrdinalIgnoreCase) == true, "create options " + p.CreateOptions);
            });

            await t.Check("properties: Dependencies — referenced by FK, used by a view, references its parent", async () =>
            {
                var parent = await TableProperties.LoadAsync(s, db.Database, "mf_prop_parent");
                var deps = await parent.LoadDependenciesAsync(s);
                Assert.True(deps.Any(d => d.Kind.StartsWith("Referenced by") && d.Object == "mf_prop_child" && d.Detail.Contains("fk_child_parent")), Dump(deps));
                Assert.True(deps.Any(d => d.Kind == "View" && d.Object == "mf_prop_v"), Dump(deps));
                var child = await TableProperties.LoadAsync(s, db.Database, "mf_prop_child");
                var cdeps = await child.LoadDependenciesAsync(s);
                Assert.True(cdeps.Any(d => d.Kind.StartsWith("References") && d.Object == "mf_prop_parent (id)"), Dump(cdeps));
                Assert.True(cdeps.Any(d => d.Kind == "View" && d.Object == "mf_prop_v"), Dump(cdeps));
                var part = await TableProperties.LoadAsync(s, db.Database, "mf_prop_part");
                Assert.Equal(0, (await part.LoadDependenciesAsync(s)).Count, "nothing uses mf_prop_part");
                return Dump(deps);
            });

            await t.Check("properties: a view (definer, security, fields, CREATE VIEW)", async () =>
            {
                var v = await TableProperties.LoadAsync(s, db.Database, "mf_prop_v");
                Assert.True(v.IsView, "view");
                Assert.True(!string.IsNullOrEmpty(v.Definer) && v.SecurityType == "DEFINER", $"{v.Definer} {v.SecurityType}");
                Assert.Equal("id,ad,code", string.Join(",", v.Fields.Select(f => f.Name)), "fields");
                Assert.True(v.CreateStatement.Contains(" VIEW ") && v.CreateStatement.Contains("`mf_prop_v`"), v.CreateStatement);
                Assert.Equal("", v.Comment, "no 'VIEW' comment");
            });

            await t.Check("properties: missing table → clear error", async () =>
            {
                var ex = await Assert.Throws(() => TableProperties.LoadAsync(s, db.Database, "mf_no_such_table"), "missing");
                Assert.True(ex.Message.Contains("not found"), ex.Message);
            });
        }
        finally
        {
            await ExecScript(s, db.Database, Cleanup);
        }
    }

    private static string Dump(IEnumerable<DependencyDetail> deps) => string.Join(" | ", deps.Select(d => $"{d.Kind}: {d.Object} ({d.Detail})"));
}
