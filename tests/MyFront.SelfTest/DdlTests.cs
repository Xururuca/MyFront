using MyFront.Core.Accounts;
using MyFront.Core.Db;

namespace MyFront.SelfTest;

/// <summary>Table structure statements built by the Object Browser and tree dialogs: offline, then on the test server.</summary>
internal static class DdlTests
{
    // what MariaDB 10.x / 11.x prints (plus the odd cases), and what MySQL 8 prints
    private const string MariaCreate = """
        CREATE TABLE `products` (
          `id` int(11) NOT NULL AUTO_INCREMENT,
          `name` varchar(100) NOT NULL,
          `category` varchar(50) DEFAULT NULL,
          `ad` varchar(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_turkish_ci DEFAULT 'İsimsiz' COMMENT 'Türkçe ''ad''',
          `updated_at` datetime DEFAULT current_timestamp() ON UPDATE current_timestamp(),
          `full` varchar(201) GENERATED ALWAYS AS (concat(`name`,' ',`category`)) VIRTUAL,
          `j` longtext CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT NULL CHECK (json_valid(`j`)),
          `b` bit(1) DEFAULT b'0',
          `odd ``x`` İş` decimal(10,2) unsigned zerofill NOT NULL DEFAULT 0.00,
          `e` enum('a','b''c') DEFAULT 'b''c',
          `t3` datetime(3) NOT NULL DEFAULT current_timestamp(3),
          PRIMARY KEY (`id`),
          UNIQUE KEY `uq_name` (`name`),
          KEY `ix_cat` (`category`(10))
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci
        """;

    private const string MySqlCreate = """
        CREATE TABLE `t8` (
          `id` int NOT NULL AUTO_INCREMENT,
          `h` int DEFAULT NULL /*!80023 INVISIBLE */,
          `c` varchar(10) CHARACTER SET latin1 COLLATE latin1_swedish_ci DEFAULT NULL,
          `ts` timestamp NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
          `g` int GENERATED ALWAYS AS ((`id` + 1)) STORED NOT NULL,
          `d` datetime DEFAULT (now()),
          `p` point NOT NULL /*!80003 SRID 4326 */,
          PRIMARY KEY (`id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
        """;

    private static string Refused(Action action)
    {
        try { action(); }
        catch (ArgumentException ex) { return ex.Message; }
        throw new Exception("expected the input to be refused");
    }

    private static string Col(string create, string name) => ColumnDefinition.FromCreateTable(create, name).ToSql(false);

    public static void Offline(TestRunner t)
    {
        t.Check("ddl: literals — quotes doubled, backslashes doubled unless NO_BACKSLASH_ESCAPES, Turkish kept", () =>
        {
            Assert.Equal("'Ürün''ün \\\\ yolu'", SqlLiteral.Quote("Ürün'ün \\ yolu", noBackslashEscapes: false), "default sql_mode");
            Assert.Equal("'Ürün''ün \\ yolu'", SqlLiteral.Quote("Ürün'ün \\ yolu", noBackslashEscapes: true), "NO_BACKSLASH_ESCAPES");
            Assert.Equal("''", SqlLiteral.Quote("", false), "empty");
        });

        t.Check("ddl: literals as SHOW CREATE TABLE prints them read back", () =>
        {
            Assert.Equal("it's", SqlLiteral.Unquote("'it''s'"), "doubled quote");
            Assert.Equal("a'b", SqlLiteral.Unquote("'a\\'b'"), "backslash quote");
            Assert.Equal("x\\y", SqlLiteral.Unquote("'x\\\\y'"), "backslash");
            Assert.Equal("satır\nikinci", SqlLiteral.Unquote("'satır\\nikinci'"), "newline");
            Assert.Equal("abc", SqlLiteral.Unquote("_utf8mb4'abc'"), "introducer");
            Assert.Equal("çift", SqlLiteral.Unquote("\"çift\""), "double quotes");
        });

        t.Check("ddl: MariaDB column lines → edited parts and kept parts; unchanged ones are written back the same", () =>
        {
            var id = ColumnDefinition.FromCreateTable(MariaCreate, "id");
            Assert.True(id is { Type: "int(11)", Nullable: false, AutoIncrement: true, DefaultKind: ColumnDefaultKind.None }, "id parsed");
            Assert.Equal("`id` int(11) NOT NULL AUTO_INCREMENT", id.ToSql(false), "id");
            Assert.Equal("`name` varchar(100) NOT NULL", Col(MariaCreate, "name"), "name");
            Assert.Equal("`category` varchar(50) NULL DEFAULT NULL", Col(MariaCreate, "category"), "category");
            var ad = ColumnDefinition.FromCreateTable(MariaCreate, "ad");
            Assert.True(ad is { CharacterSet: "utf8mb4", Collation: "utf8mb4_turkish_ci", DefaultKind: ColumnDefaultKind.Value, Default: "İsimsiz" }, "ad parsed");
            Assert.Equal("Türkçe 'ad'", ad.Comment, "comment");
            Assert.Equal("`ad` varchar(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_turkish_ci NULL DEFAULT 'İsimsiz' COMMENT 'Türkçe ''ad'''", ad.ToSql(false), "ad");
            Assert.Equal("`b` bit(1) NULL DEFAULT b'0'", Col(MariaCreate, "b"), "bit literal default kept as an expression");
            var odd = ColumnDefinition.FromCreateTable(MariaCreate, "odd `x` İş");
            Assert.Equal("decimal(10,2) unsigned zerofill", odd.Type, "type with modifiers");
            Assert.Equal("`odd ``x`` İş` decimal(10,2) unsigned zerofill NOT NULL DEFAULT 0.00", odd.ToSql(false), "number default unquoted");
            var e = ColumnDefinition.FromCreateTable(MariaCreate, "e");
            Assert.True(e is { Type: "enum('a','b''c')", Default: "b'c" }, $"enum: {e.Type} / {e.Default}");
            Assert.Equal("`e` enum('a','b''c') NULL DEFAULT 'b''c'", e.ToSql(false), "enum");
            Assert.Equal("`t3` datetime(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)", Col(MariaCreate, "t3"), "fractional CURRENT_TIMESTAMP");
            Assert.Equal("`id` int(11) NOT NULL AUTO_INCREMENT", Col(MariaCreate, "ID"), "column names ignore case");
            try
            {
                ColumnDefinition.FromCreateTable(MariaCreate, "uq_name");
                throw new Exception("uq_name is a key, not a column");
            }
            catch (InvalidOperationException) { }
        });

        t.Check("ddl: editing keeps ON UPDATE, the generated expression, CHECK and the character set", () =>
        {
            var updated = ColumnDefinition.FromCreateTable(MariaCreate, "updated_at");
            Assert.True(updated is { DefaultKind: ColumnDefaultKind.CurrentTimestamp, OnUpdate: "ON UPDATE current_timestamp()" }, "parsed");
            updated.Nullable = false;
            Assert.Equal("`updated_at` datetime NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE current_timestamp()", updated.ToSql(false), "ON UPDATE kept");
            var full = ColumnDefinition.FromCreateTable(MariaCreate, "full");
            Assert.True(full.IsGenerated, "generated");
            full.Name = "full_name";
            full.DefaultKind = ColumnDefaultKind.Value; // ignored: a generated column has no default
            full.Default = "x";
            Assert.Equal("`full_name` varchar(201) GENERATED ALWAYS AS (concat(`name`,' ',`category`)) VIRTUAL", full.ToSql(false), "expression kept");
            var j = ColumnDefinition.FromCreateTable(MariaCreate, "j");
            j.Comment = "JSON belge";
            Assert.Equal("`j` longtext CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL DEFAULT NULL COMMENT 'JSON belge' CHECK (json_valid(`j`))", j.ToSql(false), "CHECK stays last");
        });

        t.Check("ddl: MySQL 8 lines — INVISIBLE, SRID, expression defaults, generated NOT NULL", () =>
        {
            Assert.Equal("`id` int NOT NULL AUTO_INCREMENT", Col(MySqlCreate, "id"), "id");
            Assert.Equal("`h` int NULL DEFAULT NULL /*!80023 INVISIBLE */", Col(MySqlCreate, "h"), "invisible");
            Assert.Equal("`ts` timestamp NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP", Col(MySqlCreate, "ts"), "timestamp");
            Assert.Equal("`g` int GENERATED ALWAYS AS ((`id` + 1)) STORED NOT NULL", Col(MySqlCreate, "g"), "generated");
            Assert.Equal("`d` datetime NULL DEFAULT (now())", Col(MySqlCreate, "d"), "expression default");
            Assert.Equal("`p` point NOT NULL /*!80003 SRID 4326 */", Col(MySqlCreate, "p"), "srid");
        });

        t.Check("ddl: collation — unchanged keeps the character set, another one replaces it, empty = table default", () =>
        {
            Assert.Equal("`c` varchar(10) CHARACTER SET latin1 COLLATE latin1_swedish_ci NULL DEFAULT NULL", Col(MySqlCreate, "c"), "kept");
            var c = ColumnDefinition.FromCreateTable(MySqlCreate, "c");
            c.Collation = "utf8mb4_turkish_ci";
            Assert.Equal("`c` varchar(10) COLLATE utf8mb4_turkish_ci NULL DEFAULT NULL", c.ToSql(false), "new collation");
            c.Collation = "";
            Assert.Equal("`c` varchar(10) NULL DEFAULT NULL", c.ToSql(false), "table default");
            c = ColumnDefinition.FromCreateTable(MySqlCreate, "c");
            c.Type = "int";
            Assert.Equal("`c` int NULL DEFAULT NULL", c.ToSql(false), "a number has no character set");
        });

        t.Check("ddl: defaults — value (quoted per sql_mode), number, NULL, none, CURRENT_TIMESTAMP, expression", () =>
        {
            var d = ColumnDefinition.FromCreateTable(MariaCreate, "category");
            d.DefaultKind = ColumnDefaultKind.Value;
            d.Default = "C:\\yol 'x'";
            Assert.Equal("`category` varchar(50) NULL DEFAULT 'C:\\\\yol ''x'''", d.ToSql(false), "value");
            Assert.Equal("`category` varchar(50) NULL DEFAULT 'C:\\yol ''x'''", d.ToSql(true), "value, NO_BACKSLASH_ESCAPES");
            d.Default = "12";
            Assert.Equal("`category` varchar(50) NULL DEFAULT '12'", d.ToSql(false), "digits in a text column stay text");
            d.DefaultKind = ColumnDefaultKind.None;
            Assert.Equal("`category` varchar(50) NULL", d.ToSql(false), "none");
            var n = ColumnDefinition.FromCreateTable(MariaCreate, "id");
            n.AutoIncrement = false;
            n.DefaultKind = ColumnDefaultKind.Value;
            n.Default = "-5";
            Assert.Equal("`id` int(11) NOT NULL DEFAULT -5", n.ToSql(false), "number");
            n.Default = "5 OR 1";
            Assert.Equal("`id` int(11) NOT NULL DEFAULT '5 OR 1'", n.ToSql(false), "not a number → quoted");
            var ts = ColumnDefinition.FromCreateTable(MySqlCreate, "d");
            ts.DefaultKind = ColumnDefaultKind.CurrentTimestamp;
            Assert.Equal("`d` datetime NULL DEFAULT CURRENT_TIMESTAMP", ts.ToSql(false), "CURRENT_TIMESTAMP");
            ts.Type = "datetime(6)";
            Assert.Equal("`d` datetime(6) NULL DEFAULT CURRENT_TIMESTAMP(6)", ts.ToSql(false), "with the type's precision");
        });

        t.Check("ddl: CHANGE COLUMN with FIRST / AFTER and Turkish names", () =>
        {
            var name = ColumnDefinition.FromCreateTable(MariaCreate, "name");
            name.Name = "ürün adı";
            name.Type = "varchar(120)";
            Assert.Equal("ALTER TABLE `mf_test`.`products` CHANGE COLUMN `name` `ürün adı` varchar(120) NOT NULL AFTER `id`",
                TableDdl.ChangeColumn("mf_test", "products", "name", name, ColumnPosition.AfterColumn("id"), false), "after");
            Assert.Equal("ALTER TABLE `mf_test`.`products` CHANGE COLUMN `name` `ürün adı` varchar(120) NOT NULL FIRST",
                TableDdl.ChangeColumn("mf_test", "products", "name", name, ColumnPosition.AtFirst, false), "first");
            Assert.Equal("ALTER TABLE `mf_test`.`products` CHANGE COLUMN `name` `ürün adı` varchar(120) NOT NULL",
                TableDdl.ChangeColumn("mf_test", "products", "name", name, null, false), "where it is");
        });

        t.Check("ddl: incomplete or unsafe field input is refused", () =>
        {
            var d = ColumnDefinition.FromCreateTable(MariaCreate, "category");
            d.Name = " ";
            Assert.True(Refused(() => d.ToSql(false)).Contains("name"), "no name");
            d.Name = "category";
            d.Type = "";
            Assert.True(Refused(() => d.ToSql(false)).Contains("type"), "no type");
            d.Type = "int; DROP TABLE products";
            Assert.True(Refused(() => d.ToSql(false)).Contains(";"), "second statement");
            d.Type = "varchar(50)";
            d.Collation = "utf8mb4_bin, x";
            Refused(() => d.ToSql(false));
            d.Collation = "";
            d.DefaultKind = ColumnDefaultKind.Expression;
            d.Default = "";
            Refused(() => d.ToSql(false));
            d.DefaultKind = ColumnDefaultKind.Value;
            d.Default = "a; b";
            d.Comment = "x; DROP TABLE y";
            Assert.Equal("`category` varchar(50) NULL DEFAULT 'a; b' COMMENT 'x; DROP TABLE y'", d.ToSql(false), "';' inside a value is just text");
        });
    }

    public static void OfflineKeys(TestRunner t)
    {
        t.Check("ddl: ADD PRIMARY KEY / UNIQUE / FULLTEXT / INDEX with prefix lengths and a Turkish comment", () =>
        {
            Assert.Equal("ALTER TABLE `d`.`t` ADD PRIMARY KEY (`id`)",
                TableDdl.AddIndex("d", "t", IndexKind.Primary, null, new[] { new IndexPart("id") }, "", false), "primary");
            Assert.Equal("ALTER TABLE `d`.`t` ADD INDEX `idx_ad` (`ad`(10), `yıl`) COMMENT 'Türkçe ''arama'' \\\\ x'",
                TableDdl.AddIndex("d", "t", IndexKind.Index, "idx_ad", new[] { new IndexPart("ad", 10), new IndexPart("yıl") }, "Türkçe 'arama' \\ x", false), "index");
            Assert.Equal("ALTER TABLE `d`.`t` ADD UNIQUE INDEX `uq ``kod``` (`kod`)",
                TableDdl.AddIndex("d", "t", IndexKind.Unique, "uq `kod`", new[] { new IndexPart("kod") }, null, false), "unique, odd name");
            Assert.Equal("ALTER TABLE `d`.`t` ADD FULLTEXT INDEX `ft_metin` (`baslik`, `metin`)",
                TableDdl.AddIndex("d", "t", IndexKind.Fulltext, "ft_metin", new[] { new IndexPart("baslik", 20), new IndexPart("metin") }, "", false), "fulltext: whole fields");
            Assert.Equal("ALTER TABLE `d`.`t` ADD INDEX `i` (`a`) COMMENT 'C:\\yol'",
                TableDdl.AddIndex("d", "t", IndexKind.Index, "i", new[] { new IndexPart("a") }, "C:\\yol", true), "NO_BACKSLASH_ESCAPES");
        });

        t.Check("ddl: a key needs fields, a name (except PRIMARY) and lengths of at least 1; names are suggested", () =>
        {
            Refused(() => TableDdl.AddIndex("d", "t", IndexKind.Index, "i", Array.Empty<IndexPart>(), "", false));
            Assert.True(Refused(() => TableDdl.AddIndex("d", "t", IndexKind.Unique, " ", new[] { new IndexPart("a") }, "", false)).Contains("name"), "name");
            Refused(() => TableDdl.AddIndex("d", "t", IndexKind.Index, "i", new[] { new IndexPart("a", 0) }, "", false));
            Assert.Equal("idx_ad", TableDdl.SuggestIndexName("ad", new[] { "PRIMARY" }), "free");
            Assert.Equal("idx_ad_3", TableDdl.SuggestIndexName("ad", new[] { "PRIMARY", "IDX_AD", "idx_ad_2" }), "taken (names ignore case)");
            Assert.True(ColumnDefinition.NeedsPrefixLength("text") && ColumnDefinition.NeedsPrefixLength("longblob") && !ColumnDefinition.NeedsPrefixLength("varchar(10)"), "TEXT / BLOB need a length");
            Assert.True(ColumnDefinition.IsPrefixType("varchar(10)") && ColumnDefinition.IsPrefixType("varbinary(16)") && !ColumnDefinition.IsPrefixType("int(11)"), "prefix types");
            Assert.True(ColumnDefinition.IsFulltextType("mediumtext") && !ColumnDefinition.IsFulltextType("blob") && !ColumnDefinition.IsFulltextType("date"), "fulltext types");
        });

        t.Check("ddl: edit a key in one statement — DROP INDEX / DROP PRIMARY KEY, then ADD with prefix lengths, DESC and the comment", () =>
        {
            Assert.Equal("ALTER TABLE `d`.`COUNTRY_LINK` DROP INDEX `idx_all`, ADD INDEX `idx_all` (`COUNTRY_ALL`(100), `ISO_CODE`) COMMENT 'tüm ülkeler'",
                TableDdl.ReplaceIndex("d", "COUNTRY_LINK", "idx_all", IndexKind.Index, "idx_all",
                    new[] { new IndexPart("COUNTRY_ALL", 100), new IndexPart("ISO_CODE") }, "tüm ülkeler", false), "TEXT field keeps its length");
            Assert.Equal("ALTER TABLE `d`.`t` DROP PRIMARY KEY, ADD PRIMARY KEY (`ID`, `ISO_CODE` DESC)",
                TableDdl.ReplaceIndex("d", "t", "PRIMARY", IndexKind.Primary, null, new[] { new IndexPart("ID"), new IndexPart("ISO_CODE", null, true) }, "", false), "primary key");
            Assert.Equal("ALTER TABLE `d`.`t` DROP INDEX `idx_FAO_ID`, ADD UNIQUE INDEX `uq_fao` (`FAO_ID`) COMMENT 'FAO ''tekil'''",
                TableDdl.ReplaceIndex("d", "t", "idx_FAO_ID", IndexKind.Unique, "uq_fao", new[] { new IndexPart("FAO_ID") }, "FAO 'tekil'", false), "renamed, unique");
            Assert.Equal("ALTER TABLE `d`.`t` DROP INDEX `ft`, ADD FULLTEXT INDEX `ft` (`a`, `b`)",
                TableDdl.ReplaceIndex("d", "t", "ft", IndexKind.Fulltext, "ft", new[] { new IndexPart("a", 10), new IndexPart("b", null, true) }, null, false), "fulltext: whole fields");
            Assert.Equal("ALTER TABLE `d`.`t` DROP PRIMARY KEY, ADD INDEX `idx_id` (`id`)",
                TableDdl.ReplaceIndex("d", "t", "PRIMARY", IndexKind.Index, "idx_id", new[] { new IndexPart("id") }, "", false), "primary key → index");
            Assert.Equal("ALTER TABLE `d`.`t` DROP INDEX `eski ``ad```, ADD INDEX `yeni` (`a`)",
                TableDdl.ReplaceIndex("d", "t", "eski `ad`", IndexKind.Index, "yeni", new[] { new IndexPart("a") }, "", false), "odd old name");
            Refused(() => TableDdl.ReplaceIndex("d", "t", "i", IndexKind.Index, "i", Array.Empty<IndexPart>(), "", false));

            TableIndex Key(string name, string type, bool unique = false, bool expression = false) =>
                new() { Name = name, IndexType = type, Unique = unique, HasExpression = expression };
            Assert.True(TableDdl.KindOf(Key("PRIMARY", "BTREE", true)) == IndexKind.Primary && TableDdl.KindOf(Key("u", "BTREE", true)) == IndexKind.Unique
                        && TableDdl.KindOf(Key("i", "BTREE")) == IndexKind.Index && TableDdl.KindOf(Key("f", "FULLTEXT")) == IndexKind.Fulltext, "kinds");
            Assert.True(TableDdl.KindOf(Key("s", "SPATIAL")) is null && TableDdl.KindOf(Key("e", "BTREE", expression: true)) is null, "SPATIAL / expression keys aren't offered");
        });
    }

    private const string LiveSetup = """
        DROP TABLE IF EXISTS mf_ddl_cols;
        CREATE TABLE mf_ddl_cols (
          id INT AUTO_INCREMENT PRIMARY KEY,
          name VARCHAR(100) NOT NULL,
          note VARCHAR(50) COLLATE utf8mb4_turkish_ci DEFAULT 'yok' COMMENT 'not',
          updated_at DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
          shout VARCHAR(110) GENERATED ALWAYS AS (CONCAT(UPPER(name), '!')) VIRTUAL
        ) DEFAULT CHARSET=utf8mb4;
        INSERT INTO mf_ddl_cols (name, note) VALUES ('elma', 'kırmızı'), ('armut', NULL);
        """;

    public static async Task LiveAsync(TestRunner t, ServerSession s, TestDb db)
    {
        await EditorPropertyTests.ExecScript(s, db.Database, LiveSetup);
        try
        {
            await t.Check("ddl: edit a field on the server — rename, type, NOT NULL, default and comment with ' and \\, AFTER; data and collation kept", async () =>
            {
                var ctx = await TableDdlContext.LoadAsync(s, db.Database, "mf_ddl_cols");
                await EditorPropertyTests.ExecScript(s, db.Database, "UPDATE mf_ddl_cols SET note = 'yok' WHERE note IS NULL");
                var note = ColumnDefinition.FromCreateTable(ctx.CreateTable, "note");
                note.Name = "açıklama";
                note.Type = "varchar(80)";
                note.Nullable = false;
                note.DefaultKind = ColumnDefaultKind.Value;
                note.Default = "Yok 'hiç'";
                note.Comment = "Ürün notu \\ yol";
                var sql = TableDdl.ChangeColumn(db.Database, "mf_ddl_cols", "note", note, ColumnPosition.AfterColumn("id"), ctx.NoBackslashEscapes);
                await TableDdl.ExecuteAsync(s, db.Database, sql, "selftest");
                s.Schema.Invalidate(db.Database, "mf_ddl_cols");
                var col = (await s.Schema.GetTableAsync(db.Database, "mf_ddl_cols")).Columns[1];
                Assert.Equal("açıklama", col.Name, "renamed and moved after id");
                Assert.True(col.Type.StartsWith("varchar(80)") && !col.Nullable, "type / NOT NULL: " + col.Type);
                Assert.Equal("Yok 'hiç'", col.Default, "default");
                Assert.Equal("Ürün notu \\ yol", col.Comment, "comment with ' and \\");
                Assert.Equal("utf8mb4_turkish_ci", col.Collation, "collation kept");
                Assert.Equal("kırmızı", (string?)await Scalar(s, db.Database, "SELECT `açıklama` FROM mf_ddl_cols WHERE name = 'elma'"), "data kept");
                return sql;
            });

            await t.Check("ddl: ON UPDATE and the generated expression survive editing their columns", async () =>
            {
                var ctx = await TableDdlContext.LoadAsync(s, db.Database, "mf_ddl_cols");
                var updated = ColumnDefinition.FromCreateTable(ctx.CreateTable, "updated_at");
                updated.Nullable = false;
                await TableDdl.ExecuteAsync(s, db.Database, TableDdl.ChangeColumn(db.Database, "mf_ddl_cols", "updated_at", updated, null, ctx.NoBackslashEscapes), "selftest");
                var shout = ColumnDefinition.FromCreateTable(ctx.CreateTable, "shout");
                shout.Name = "bagir";
                shout.Comment = "hesaplanan";
                await TableDdl.ExecuteAsync(s, db.Database, TableDdl.ChangeColumn(db.Database, "mf_ddl_cols", "shout", shout, null, ctx.NoBackslashEscapes), "selftest");
                var create = await s.Schema.GetCreateStatementAsync(db.Database, "mf_ddl_cols", DbObjectType.Table);
                var line = create.Split('\n').Single(l => l.Contains("`updated_at`"));
                Assert.True(line.Contains("NOT NULL") && line.Contains("ON UPDATE", StringComparison.OrdinalIgnoreCase), line);
                Assert.Equal("ELMA!", (string?)await Scalar(s, db.Database, "SELECT bagir FROM mf_ddl_cols WHERE name = 'elma'"), "generated column still computes");
                return create.Split('\n').Single(l => l.Contains("`bagir`")).Trim();
            });

            await t.Check("ddl: keys on the server — primary on a table without one, unique, prefix index with comment, fulltext", async () =>
            {
                await EditorPropertyTests.ExecScript(s, db.Database, """
                    DROP TABLE IF EXISTS mf_ddl_keys;
                    CREATE TABLE mf_ddl_keys (kod INT NOT NULL, ad VARCHAR(100), metin TEXT, yil INT) DEFAULT CHARSET=utf8mb4;
                    INSERT INTO mf_ddl_keys VALUES (1, 'Şanlıurfa', 'isot biberi', 2024), (2, 'Iğdır', 'kayısı', 2025);
                    """);
                try
                {
                    var ctx = await TableDdlContext.LoadAsync(s, db.Database, "mf_ddl_keys");
                    var nbe = ctx.NoBackslashEscapes;
                    foreach (var sql in new[]
                             {
                                 TableDdl.AddIndex(db.Database, "mf_ddl_keys", IndexKind.Primary, null, new[] { new IndexPart("kod") }, "", nbe),
                                 TableDdl.AddIndex(db.Database, "mf_ddl_keys", IndexKind.Unique, "uq_ad_yil", new[] { new IndexPart("ad"), new IndexPart("yil") }, "", nbe),
                                 TableDdl.AddIndex(db.Database, "mf_ddl_keys", IndexKind.Index, "idx_metin", new[] { new IndexPart("metin", 20) }, "arama 'hızlı'", nbe),
                                 TableDdl.AddIndex(db.Database, "mf_ddl_keys", IndexKind.Fulltext, "ft_metin", new[] { new IndexPart("ad"), new IndexPart("metin") }, "", nbe),
                             })
                        await TableDdl.ExecuteAsync(s, db.Database, sql, "selftest");
                    var p = await TableProperties.LoadAsync(s, db.Database, "mf_ddl_keys");
                    var keys = string.Join(" | ", p.Keys.Select(k => $"{k.Name}:{k.Columns}:{(k.Unique ? "U" : "")}:{k.Type}:{k.Comment}"));
                    Assert.True(p.Keys.Any(k => k is { Name: "PRIMARY", Columns: "kod" }), keys);
                    Assert.True(p.Keys.Any(k => k is { Name: "uq_ad_yil", Columns: "ad, yil", Unique: true }), keys);
                    Assert.True(p.Keys.Any(k => k is { Name: "idx_metin", Columns: "metin(20)", Comment: "arama 'hızlı'" }), keys);
                    Assert.True(p.Keys.Any(k => k is { Name: "ft_metin", Type: "FULLTEXT" }), keys);
                    s.Schema.Invalidate(db.Database, "mf_ddl_keys"); // as after the dialog
                    var info = await s.Schema.GetTableAsync(db.Database, "mf_ddl_keys");
                    Assert.Equal("kod", string.Join(",", info.KeyColumns ?? Array.Empty<string>()), "the Data Browser key is now the primary key");
                    return keys;
                }
                finally { await EditorPropertyTests.ExecScript(s, db.Database, "DROP TABLE IF EXISTS mf_ddl_keys"); }
            });

            await t.Check("ddl: edit keys on the server — SHOW INDEX prefix lengths, a TEXT key edited with its length (without: refused, key kept), reorder + rename, primary key; a key a foreign key needs is refused", async () =>
            {
                await EditorPropertyTests.ExecScript(s, db.Database, KeyEditSetup);
                try
                {
                    s.Schema.Invalidate(db.Database, "mf_ddl_link");
                    var info = await s.Schema.GetTableAsync(db.Database, "mf_ddl_link");
                    var all = info.Indexes.Single(i => i.Name == "idx_all");
                    Assert.True(all.Parts.Single() == new IndexPart("COUNTRY_ALL", 100) && all.Comment == "tüm ülkeler", $"idx_all: {string.Join(",", all.Parts)} / {all.Comment}");
                    Assert.Equal("COMTRADE_ID,TURKISH_CONT", string.Join(",", info.Indexes.Single(i => i.Name == "idx_comtrade").Parts.Select(p => p.Column)), "order");
                    var nbe = (await TableDdlContext.LoadAsync(s, db.Database, "mf_ddl_link")).NoBackslashEscapes;
                    string Replace(string old, IndexKind kind, string? name, params IndexPart[] parts) =>
                        TableDdl.ReplaceIndex(db.Database, "mf_ddl_link", old, kind, name, parts, old == "idx_all" ? "tüm ülkeler" : "", nbe);

                    var noLength = await Assert.Throws(() => TableDdl.ExecuteAsync(s, db.Database,
                        Replace("idx_all", IndexKind.Index, "idx_all", new IndexPart("COUNTRY_ALL"), new IndexPart("ISO_CODE")), "selftest"), "TEXT without a length");
                    Assert.True(noLength.Message.Contains("key length", StringComparison.OrdinalIgnoreCase), noLength.Message);
                    await TableDdl.ExecuteAsync(s, db.Database, Replace("idx_all", IndexKind.Index, "idx_all", new IndexPart("COUNTRY_ALL", 100), new IndexPart("ISO_CODE")), "selftest");
                    await TableDdl.ExecuteAsync(s, db.Database, Replace("idx_FAO_ID", IndexKind.Index, "idx_tr_fao", new IndexPart("TURKISH_CONT", 20), new IndexPart("FAO_ID")), "selftest");
                    await TableDdl.ExecuteAsync(s, db.Database, Replace("PRIMARY", IndexKind.Primary, null, new IndexPart("ID"), new IndexPart("ISO_CODE")), "selftest");
                    var fk = await Assert.Throws(() => TableDdl.ExecuteAsync(s, db.Database,
                        Replace("idx_comtrade", IndexKind.Index, "idx_comtrade", new IndexPart("TURKISH_CONT", 20)), "selftest"), "the key the foreign key needs");
                    Assert.True(fk.Message.Contains("foreign key", StringComparison.OrdinalIgnoreCase), "server's error: " + fk.Message);

                    var p = await TableProperties.LoadAsync(s, db.Database, "mf_ddl_link");
                    var keys = string.Join(" | ", p.Keys.Select(k => $"{k.Name}:{k.Columns}:{k.Comment}"));
                    Assert.True(p.Keys.Any(k => k is { Name: "idx_all", Columns: "COUNTRY_ALL(100), ISO_CODE", Comment: "tüm ülkeler" }), keys);
                    Assert.True(p.Keys.Any(k => k is { Name: "idx_tr_fao", Columns: "TURKISH_CONT(20), FAO_ID" }) && p.Keys.All(k => k.Name != "idx_FAO_ID"), keys);
                    Assert.True(p.Keys.Any(k => k is { Name: "PRIMARY", Columns: "ID, ISO_CODE" }), keys);
                    Assert.True(p.Keys.Any(k => k is { Name: "idx_comtrade", Columns: "COMTRADE_ID, TURKISH_CONT" }), "refused change left the key as it was: " + keys);
                    return fk.Message;
                }
                finally { await EditorPropertyTests.ExecScript(s, db.Database, "DROP TABLE IF EXISTS mf_ddl_link, mf_ddl_parent"); }
            });

            await t.Check("ddl: read-only connection refuses ALTER before it reaches the server", async () =>
            {
                using var ro = await ServerSession.ConnectAsync(db.ToAccount(TlsMode.Required, readOnly: true), db.Password);
                var ex = await Assert.Throws(() => TableDdl.ExecuteAsync(ro, db.Database, "ALTER TABLE mf_ddl_cols COMMENT 'x'", "selftest"), "read-only");
                Assert.True(ex is ReadOnlyViolationException, ex.Message);
            });
        }
        finally
        {
            await EditorPropertyTests.ExecScript(s, db.Database, "DROP TABLE IF EXISTS mf_ddl_cols");
        }
    }

    /// <summary>Shaped like the user's COUNTRY_LINK: a key on a MEDIUMTEXT with a prefix length, keys of two fields, one a foreign key needs.</summary>
    internal const string KeyEditSetup = """
        DROP TABLE IF EXISTS mf_ddl_link, mf_ddl_parent;
        CREATE TABLE mf_ddl_parent (id INT PRIMARY KEY) ENGINE=InnoDB;
        INSERT INTO mf_ddl_parent VALUES (792);
        CREATE TABLE mf_ddl_link (ID INT AUTO_INCREMENT PRIMARY KEY, COUNTRY_ALL MEDIUMTEXT, TURKISH_CONT VARCHAR(255), ISO_CODE INT,
          COMTRADE_ID INT, FAO_ID INT,
          KEY idx_fao (FAO_ID), KEY idx_all (COUNTRY_ALL(100)) COMMENT 'tüm ülkeler', KEY idx_comtrade (COMTRADE_ID, TURKISH_CONT),
          KEY idx_FAO_ID (FAO_ID, TURKISH_CONT),
          CONSTRAINT fk_link_comtrade FOREIGN KEY (COMTRADE_ID) REFERENCES mf_ddl_parent (id)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
        INSERT INTO mf_ddl_link (COUNTRY_ALL, TURKISH_CONT, ISO_CODE, COMTRADE_ID, FAO_ID) VALUES ('Türkiye Cumhuriyeti', 'Türkiye', 792, 792, 223);
        """;

    private static async Task<object?> Scalar(ServerSession s, string db, string sql)
    {
        await using var conn = await s.OpenAsync(db);
        await using var cmd = new MySqlConnector.MySqlCommand(sql, conn);
        var v = await cmd.ExecuteScalarAsync();
        return v is DBNull ? null : v;
    }
}
