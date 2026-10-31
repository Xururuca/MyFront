using MyFront.Core.Accounts;
using MyFront.Core.Db;
using MySqlConnector;

namespace MyFront.SelfTest;

/// <summary>Editable result grids of the SQL editor: which results can be edited and how a row is written (offline, then on the test server).</summary>
internal static class ResultEditTests
{
    private static ColumnInfo Col(string name, string? table = "urunler", string? column = null, string alias = "u", string schema = "db") => new()
    {
        Name = name, BaseSchema = table is null ? null : schema, BaseTable = table, BaseColumn = table is null ? null : column ?? name,
        TableAlias = table is null ? null : alias,
    };

    private static TableInfo Table(bool primaryKey = true, bool uniqueKod = true, bool kodNullable = false)
    {
        var t = new TableInfo { Database = "db", Name = "urunler" };
        t.Columns.Add(new TableColumn { Name = "id", Type = "int(11)" });
        t.Columns.Add(new TableColumn { Name = "kod", Type = "varchar(10)", Nullable = kodNullable });
        t.Columns.Add(new TableColumn { Name = "Ürün adı", Type = "varchar(50)", Nullable = true });
        t.Columns.Add(new TableColumn { Name = "oran", Type = "double", Nullable = true });
        t.Columns.Add(new TableColumn { Name = "resim", Type = "blob", Nullable = true });
        if (primaryKey) t.Indexes.Add(new TableIndex { Name = "PRIMARY", Unique = true, Columns = { "id" } });
        if (uniqueKod) t.Indexes.Add(new TableIndex { Name = "uq_kod", Unique = true, Columns = { "kod" } });
        return t;
    }

    public static void Offline(TestRunner t)
    {
        t.Check("result edit: one table → editable by its primary key, aliases followed to the real fields, expressions read-only", () =>
        {
            var cols = new[] { Col("kimlik", column: "id"), Col("ad", column: "Ürün adı"), Col("etiket", table: null), Col("resim") };
            var (db, table, reason) = ResultEdit.SourceTable(cols, "SELECT u.id AS kimlik, u.`Ürün adı` AS ad, CONCAT(u.kod, '!') AS etiket, resim FROM urunler u");
            Assert.True(reason is null && db == "db" && table == "urunler", "source: " + reason);
            var plan = ResultEdit.Plan(cols, Table());
            Assert.True(plan.Editable, "editable: " + plan.ReadOnlyReason);
            var target = plan.Target!;
            Assert.Equal("id,Ürün adı,,resim", string.Join(",", target.Columns.Select(c => c ?? "")), "fields behind the columns");
            Assert.Equal("0", string.Join(",", target.KeyIndexes), "key = the kimlik column");
            Assert.True(!target.AllValues, "by key");
            Assert.Equal("True,True,False,False", string.Join(",", plan.EditableColumns), "the expression and the BLOB can't be typed in");
            Assert.Equal("editable · key: kimlik", plan.Describe(cols), "info line");
            Assert.Equal("UPDATE `db`.`urunler` SET `Ürün adı` = @v1 WHERE `id` = @k0 LIMIT 1", target.UpdateSql(new[] { 1 }), "update");
            Assert.Equal("SELECT `id`, `Ürün adı`, `resim` FROM `db`.`urunler` WHERE `id` = @k0 LIMIT 1", target.SelectSql(), "read back");
            Assert.Equal("1", string.Join(",", target.Changed(new object?[] { 1, "a", "a!", null }, new object?[] { 1, "b", "x", null })), "only the table's fields count as changed");
        });

        t.Check("result edit: read-only with the reason — several tables, a self-join, only expressions, UNION, no schema", () =>
        {
            string? Reason(ColumnInfo[] cols, string? sql = null) => ResultEdit.SourceTable(cols, sql).Reason;
            Assert.Equal("read-only: columns from several tables", Reason(new[] { Col("id"), Col("ad", table: "kategoriler", alias: "k") }), "two tables");
            Assert.Equal("read-only: columns from several tables", Reason(new[] { Col("id", alias: "a"), Col("kod", alias: "b") }), "one table used twice");
            Assert.Equal("read-only: no table columns", Reason(new[] { Col("1", table: null), Col("NOW()", table: null) }), "SELECT 1, NOW()");
            Assert.Equal("read-only: rows of several SELECTs (UNION)", Reason(new[] { Col("id") }, "SELECT id FROM urunler UNION ALL SELECT id FROM urunler"), "union");
            Assert.True(Reason(new[] { Col("id") }, "SELECT id FROM urunler WHERE kod = 'UNION'") is null, "UNION in a string is no union");
            Assert.Equal("read-only: grouped rows (GROUP BY / DISTINCT)", Reason(new[] { Col("id") }, "SELECT id, COUNT(*) FROM urunler GROUP BY id"), "group by");
            Assert.Equal("read-only: grouped rows (GROUP BY / DISTINCT)", Reason(new[] { Col("id") }, "SELECT DISTINCT id FROM urunler"), "distinct");
            Assert.True(Reason(new[] { Col("id") }, "SELECT id FROM urunler WHERE ad = 'group by'") is null, "GROUP BY in a string is no grouping");
            Assert.Equal("read-only: not from a table", Reason(new[] { Col("id", schema: "") }), "derived table");
        });

        t.Check("result edit: the key must be in the result — else a unique NOT NULL key; a nullable unique key doesn't count", () =>
        {
            var noKey = ResultEdit.Plan(new[] { Col("ad", column: "Ürün adı") }, Table());
            Assert.Equal("read-only: no key in the result (add id)", noKey.ReadOnlyReason, "only a name");
            var byKod = ResultEdit.Plan(new[] { Col("kod"), Col("ad", column: "Ürün adı") }, Table());
            Assert.True(byKod.Editable && byKod.Target!.KeyIndexes.SequenceEqual(new[] { 0 }) && !byKod.Target.AllValues, "found by the unique kod: " + byKod.ReadOnlyReason);
            var nullable = ResultEdit.Plan(new[] { Col("kod"), Col("ad", column: "Ürün adı") }, Table(kodNullable: true));
            Assert.Equal("read-only: no key in the result (add id)", nullable.ReadOnlyReason, "a unique key on a NULL-able field is no key");
        });

        t.Check("result edit: a table without a key needs all its fields (matched by all values, DOUBLE left out); a view stays read-only", () =>
        {
            var keyless = Table(primaryKey: false, uniqueKod: false);
            var all = new[] { Col("resim"), Col("id"), Col("kod"), Col("Ürün adı"), Col("oran") };
            var plan = ResultEdit.Plan(all, keyless);
            Assert.True(plan.Editable && plan.Target!.AllValues, "editable by all values: " + plan.ReadOnlyReason);
            Assert.Equal("1,2,3,0", string.Join(",", plan.Target!.KeyIndexes), "every field but the DOUBLE, in table order");
            Assert.Equal("`id` <=> @k0 AND `kod` <=> @k1 AND `Ürün adı` <=> @k2 AND `resim` <=> @k3", plan.Target.KeyWhere(), "NULL-safe match");
            Assert.Equal("editable · no key: rows are matched by all their values", plan.Describe(all), "info line");
            var some = ResultEdit.Plan(new[] { Col("id"), Col("kod") }, keyless);
            Assert.Equal("read-only: the table has no key, so every field must be in the result", some.ReadOnlyReason, "not all fields");
            var view = Table();
            view.IsView = true;
            Assert.Equal("read-only: view", ResultEdit.Plan(new[] { Col("id") }, view).ReadOnlyReason, "view");
        });

        t.Check("result edit: a field shown twice is written through its first column only", () =>
        {
            var plan = ResultEdit.Plan(new[] { Col("id"), Col("ad", column: "Ürün adı"), Col("ad2", column: "Ürün adı") }, Table());
            Assert.Equal("id,Ürün adı,", string.Join(",", plan.Target!.Columns.Select(c => c ?? "")), "second copy not mapped");
            Assert.Equal("True,True,False", string.Join(",", plan.EditableColumns), "and not editable");
        });
    }

    private const string Setup = """
        DROP VIEW IF EXISTS mf_redit_v;
        DROP TABLE IF EXISTS mf_redit, mf_redit_nokey;
        CREATE TABLE mf_redit (id INT AUTO_INCREMENT PRIMARY KEY, kod VARCHAR(10) NOT NULL, ad VARCHAR(50) NOT NULL, fiyat DECIMAL(10,2) NULL,
          updated_at DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP, UNIQUE KEY uq_kod (kod)) DEFAULT CHARSET=utf8mb4;
        INSERT INTO mf_redit (kod, ad, fiyat) VALUES ('E1', 'elma', 10.50), ('A2', 'armut', 20), ('K3', 'kiraz', NULL);
        CREATE TABLE mf_redit_nokey (a INT, b VARCHAR(20), f DOUBLE) DEFAULT CHARSET=utf8mb4;
        INSERT INTO mf_redit_nokey VALUES (1, 'bir', 0.1), (2, NULL, NULL);
        CREATE VIEW mf_redit_v AS SELECT id, ad FROM mf_redit;
        """;

    private static async Task<(ColumnInfo[] Columns, List<object?[]> Rows)> QueryAsync(ServerSession s, string db, string sql)
    {
        await using var conn = await s.OpenAsync(db);
        await using var cmd = new MySqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync();
        var cols = ColumnInfo.FromReader(r);
        var rows = new List<object?[]>();
        while (await r.ReadAsync())
        {
            var v = new object?[r.FieldCount];
            r.GetValues(v!);
            rows.Add(v.Select(CellFormat.Normalize).ToArray());
        }
        return (cols, rows);
    }

    private static async Task<ResultEditPlan> PlanAsync(ServerSession s, string db, string sql)
    {
        var (cols, _) = await QueryAsync(s, db, sql);
        var (database, table, reason) = ResultEdit.SourceTable(cols, sql);
        if (reason is not null) return ResultEditPlan.ReadOnly(reason);
        s.Schema.Invalidate(database, table);
        return ResultEdit.Plan(cols, await s.Schema.GetTableAsync(database!, table!));
    }

    private static async Task<object?> Scalar(ServerSession s, string db, string sql)
    {
        await using var conn = await s.OpenAsync(db);
        await using var cmd = new MySqlCommand(sql, conn);
        var v = await cmd.ExecuteScalarAsync();
        return v is DBNull ? null : v;
    }

    public static async Task LiveAsync(TestRunner t, ServerSession s, TestDb db)
    {
        await EditorPropertyTests.ExecScript(s, db.Database, Setup);
        try
        {
            await t.Check("result edit: the server names the base schema, table, field and the alias of each column", async () =>
            {
                var (cols, _) = await QueryAsync(s, db.Database, "SELECT p.id AS kimlik, p.ad, CONCAT(p.ad, '!') AS etiket FROM mf_redit p");
                Assert.True(cols[0] is { Name: "kimlik", BaseTable: "mf_redit", BaseColumn: "id", TableAlias: "p" } && cols[0].BaseSchema == db.Database,
                    $"kimlik: {cols[0].BaseSchema}.{cols[0].BaseTable}.{cols[0].BaseColumn} as {cols[0].TableAlias}");
                Assert.True(string.IsNullOrEmpty(cols[2].BaseColumn) || string.IsNullOrEmpty(cols[2].BaseTable), "the expression has no base field: " + cols[2].BaseTable + "." + cols[2].BaseColumn);
                return $"{cols[0].BaseSchema}.{cols[0].BaseTable}.{cols[0].BaseColumn} AS {cols[0].Name} (alias {cols[0].TableAlias})";
            });

            await t.Check("result edit: plans on the server — alias, unique key, self-join, missing key, UNION, keyless, view", async () =>
            {
                var notes = new List<string>();
                async Task<ResultEditPlan> P(string sql)
                {
                    var p = await PlanAsync(s, db.Database, sql);
                    notes.Add(p.ReadOnlyReason ?? "editable");
                    return p;
                }
                var alias = await P("SELECT p.id AS kimlik, p.ad AS isim, p.fiyat * 2 AS iki FROM mf_redit p");
                Assert.True(alias.Editable && alias.Target!.KeyIndexes.SequenceEqual(new[] { 0 }) && alias.EditableColumns.SequenceEqual(new[] { true, true, false }),
                    "alias: " + alias.ReadOnlyReason);
                var unique = await P("SELECT kod, ad FROM mf_redit");
                Assert.True(unique.Editable && unique.Target!.Columns[0] == "kod", "by the unique kod: " + unique.ReadOnlyReason);
                Assert.Equal("read-only: columns from several tables", (await P("SELECT a.id, b.ad FROM mf_redit a JOIN mf_redit b ON b.id = a.id")).ReadOnlyReason, "self-join");
                Assert.Equal("read-only: no key in the result (add id)", (await P("SELECT ad, fiyat FROM mf_redit")).ReadOnlyReason, "no key");
                Assert.True(!(await P("SELECT id, ad FROM mf_redit UNION SELECT id, ad FROM mf_redit")).Editable, "union");
                var keyless = await P("SELECT * FROM mf_redit_nokey");
                Assert.True(keyless.Editable && keyless.Target!.AllValues && keyless.Target.KeyIndexes.SequenceEqual(new[] { 0, 1 }), "keyless: " + keyless.ReadOnlyReason);
                Assert.True((await P("SELECT a, b FROM mf_redit_nokey")).Editable, "keyless: the DOUBLE isn't needed (never matched by)");
                Assert.Equal("read-only: the table has no key, so every field must be in the result", (await P("SELECT a FROM mf_redit_nokey")).ReadOnlyReason, "keyless without b");
                var view = await P("SELECT * FROM mf_redit_v");
                Assert.True(!view.Editable || view.Target!.Table == "mf_redit", "a view is read-only (or the server names its base table): " + view.ReadOnlyReason);
                return string.Join(" | ", notes);
            });

            await t.Check("result edit: UPDATE through an alias by key, Turkish text, the row read back (ON UPDATE column, server formatting)", async () =>
            {
                var sql = "SELECT p.id AS kimlik, p.ad AS isim, p.fiyat, p.updated_at, CONCAT(p.kod, '!') AS etiket FROM mf_redit p ORDER BY p.id";
                var plan = await PlanAsync(s, db.Database, sql);
                var (_, rows) = await QueryAsync(s, db.Database, sql);
                var original = rows[0];
                var values = (object?[])original.Clone();
                values[1] = "Çiğ köfte İşkembe";
                values[2] = "12.5";
                values[4] = "changed in the grid only";
                var stored = await RowUpdate.UpdateAsync(s, plan.Target!, original, values);
                Assert.True(stored is not null, "row found");
                Assert.Equal("Çiğ köfte İşkembe", (string?)await Scalar(s, db.Database, "SELECT ad FROM mf_redit WHERE id = 1"), "db value");
                Assert.Equal(12.50m, stored![2], "fiyat read back as DECIMAL");
                Assert.True(stored[3] is DateTime, "updated_at read back");
                Assert.Equal("changed in the grid only", stored[4], "the expression column keeps what the grid had");
                return $"{stored[1]} / {stored[2]} / {stored[3]}";
            });

            await t.Check("result edit: a row deleted since the query ran → not found (null), nothing written", async () =>
            {
                var sql = "SELECT id, ad FROM mf_redit ORDER BY id";
                var plan = await PlanAsync(s, db.Database, sql);
                var (_, rows) = await QueryAsync(s, db.Database, sql);
                await Scalar(s, db.Database, "DELETE FROM mf_redit WHERE id = 3");
                var values = (object?[])rows[2].Clone();
                values[1] = "kayıp";
                Assert.True(await RowUpdate.UpdateAsync(s, plan.Target!, rows[2], values) is null, "not found");
                Assert.Equal(0L, Convert.ToInt64(await Scalar(s, db.Database, "SELECT COUNT(*) FROM mf_redit WHERE ad = 'kayıp'")), "nothing written");
            });

            await t.Check("result edit: keyless table — the row is found by all its values (NULL-safe)", async () =>
            {
                var sql = "SELECT * FROM mf_redit_nokey ORDER BY a";
                var plan = await PlanAsync(s, db.Database, sql);
                var (_, rows) = await QueryAsync(s, db.Database, sql);
                var values = (object?[])rows[1].Clone();
                values[1] = "iki";
                Assert.True(await RowUpdate.UpdateAsync(s, plan.Target!, rows[1], values) is not null, "found by a = 2 AND b <=> NULL");
                Assert.Equal("iki", (string?)await Scalar(s, db.Database, "SELECT b FROM mf_redit_nokey WHERE a = 2"), "db value");
            });

            await t.Check("result edit: a read-only connection refuses the UPDATE", async () =>
            {
                using var ro = await ServerSession.ConnectAsync(db.ToAccount(TlsMode.Required, readOnly: true), db.Password);
                var sql = "SELECT id, ad FROM mf_redit ORDER BY id";
                var plan = await PlanAsync(s, db.Database, sql);
                var (_, rows) = await QueryAsync(s, db.Database, sql);
                var values = (object?[])rows[0].Clone();
                values[1] = "salt okunur";
                var ex = await Assert.Throws(() => RowUpdate.UpdateAsync(ro, plan.Target!, rows[0], values), "read-only");
                Assert.True(ex is ReadOnlyViolationException, ex.GetType().Name + ": " + ex.Message);
                Assert.Equal(0L, Convert.ToInt64(await Scalar(s, db.Database, "SELECT COUNT(*) FROM mf_redit WHERE ad = 'salt okunur'")), "nothing written");
            });
        }
        finally
        {
            await EditorPropertyTests.ExecScript(s, db.Database, "DROP VIEW IF EXISTS mf_redit_v; DROP TABLE IF EXISTS mf_redit, mf_redit_nokey;");
        }
    }
}
