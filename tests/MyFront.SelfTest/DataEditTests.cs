using MyFront.Core.Db;
using MySqlConnector;

namespace MyFront.SelfTest;

/// <summary>Data Browser editing of tables without a key: rows found by all their values (offline, then on the test server).</summary>
internal static class DataEditTests
{
    public static void Offline(TestRunner t)
    {
        t.Check("edit: a keyless row is matched by every column except FLOAT / DOUBLE, JSON and spatial ones", () =>
        {
            foreach (var type in new[] { "int(11)", "varchar(10)", "decimal(10,2)", "datetime(6)", "date", "time", "text", "blob", "enum('a','b')", "bigint unsigned", "bit(1)", "year" })
                Assert.True(RowMatch.CanMatchBy(type), "matchable: " + type);
            foreach (var type in new[] { "float", "double", "double unsigned", "float(7,4)", "real", "json", "point", "geometry", "multipolygon" })
                Assert.True(!RowMatch.CanMatchBy(type), "left out: " + type);
        });

        t.Check("edit: WHERE by key (=) or by all values (NULL-safe <=>), values as parameters", () =>
        {
            Assert.Equal("`id` = @k0", RowMatch.Where(new[] { "id" }, allValues: false), "key");
            Assert.Equal("`IHRITH` <=> @k0 AND `YIL` <=> @k1 AND `İL adı` <=> @k2", RowMatch.Where(new[] { "IHRITH", "YIL", "İL adı" }, allValues: true), "all values");
            try
            {
                RowMatch.Where(Array.Empty<string>(), true);
                throw new Exception("no columns must be refused");
            }
            catch (ArgumentException) { }
        });
    }

    /// <summary>Shaped like a table the import wizard makes from a trade-statistics sheet (11 nullable columns, no key), plus a DOUBLE.</summary>
    public const string KeylessSetup = """
        DROP TABLE IF EXISTS mf_edit_nokey;
        CREATE TABLE mf_edit_nokey (IHRITH VARCHAR(10), YIL INT, AY INT, IL_KODU INT, IL_ADI VARCHAR(20), FASIL INT, FASIL_ADI VARCHAR(200),
          ULKE INT, ULKE_ADI VARCHAR(50), DOLAR BIGINT, KILOGRAM BIGINT, ORAN DOUBLE) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        INSERT INTO mf_edit_nokey VALUES
          ('IHR', 2024, 1, 34, 'İstanbul', 8, 'Meyveler', 52, 'Türkiye', 1000, 500, 0.1 + 0.2),
          ('IHR', 2024, 1, 34, 'İstanbul', 8, 'Meyveler', 52, 'Türkiye', 1000, 500, 0.1 + 0.2),
          ('ITH', 2024, 2, 6, NULL, 9, NULL, NULL, NULL, NULL, 7, NULL),
          ('ITH', 2025, 3, 35, 'İzmir', 7, 'Sebze ''taze'' \\ yeşil', 49, 'Almanya', 12345678901, 0, 1e-300);
        """;

    private static readonly string[] Columns = { "IHRITH", "YIL", "AY", "IL_KODU", "IL_ADI", "FASIL", "FASIL_ADI", "ULKE", "ULKE_ADI", "DOLAR", "KILOGRAM", "ORAN" };
    private static int[] Match => Enumerable.Range(0, 11).ToArray(); // ORAN (DOUBLE) left out

    private static async Task<List<object?[]>> RowsAsync(MySqlConnection conn)
    {
        var rows = new List<object?[]>();
        await using var cmd = new MySqlCommand("SELECT * FROM mf_edit_nokey", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var v = new object?[r.FieldCount];
            r.GetValues(v!);
            rows.Add(v.Select(CellFormat.Normalize).ToArray()); // as the Data Browser keeps them
        }
        return rows;
    }

    /// <summary>The statement the Data Browser runs for an edited cell of a keyless table.</summary>
    private static async Task<int> UpdateAsync(MySqlConnection conn, object?[] original, int column, object? value)
    {
        var where = RowMatch.Where(Match.Select(i => Columns[i]).ToList(), allValues: true);
        await using var cmd = new MySqlCommand($"UPDATE mf_edit_nokey SET {Ident.Quote(Columns[column])} = @v WHERE {where} LIMIT 1", conn);
        cmd.Parameters.AddWithValue("@v", value);
        for (int k = 0; k < Match.Length; k++) cmd.Parameters.AddWithValue("@k" + k, original[Match[k]]);
        return await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(MySqlConnection conn, string where)
    {
        await using var cmd = new MySqlCommand("SELECT COUNT(*) FROM mf_edit_nokey WHERE " + where, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public static async Task LiveAsync(TestRunner t, ServerSession s, TestDb db)
    {
        await EditorPropertyTests.ExecScript(s, db.Database, KeylessSetup);
        try
        {
            await using var conn = await s.OpenAsync(db.Database); // pooled: affected = matched rows, like the Data Browser
            await t.Check("edit: keyless table — the row with NULLs is found (<=>) and only it changes", async () =>
            {
                var row = (await RowsAsync(conn)).Single(r => (int?)r[2] == 2);
                Assert.Equal(1, await UpdateAsync(conn, row, 4, "Ankara"), "affected");
                Assert.Equal(1L, await CountAsync(conn, "IL_ADI = 'Ankara' AND AY = 2"), "updated");
                Assert.Equal(1L, await CountAsync(conn, "AY = 2 AND FASIL_ADI IS NULL AND ULKE IS NULL AND DOLAR IS NULL"), "its other NULLs stay");
            });

            await t.Check("edit: two identical rows — LIMIT 1 changes one of them", async () =>
            {
                var row = (await RowsAsync(conn)).First(r => (string?)r[0] == "IHR");
                Assert.Equal(1, await UpdateAsync(conn, row, 10, 600L), "affected");
                Assert.True(await CountAsync(conn, "IHRITH = 'IHR' AND KILOGRAM = 500") == 1 && await CountAsync(conn, "IHRITH = 'IHR' AND KILOGRAM = 600") == 1, "one of the twins");
            });

            await t.Check("edit: a DOUBLE that doesn't round-trip (1e-300, 0.1+0.2) doesn't stop the match; quotes and \\ in values", async () =>
            {
                var row = (await RowsAsync(conn)).Single(r => (int?)r[1] == 2025);
                Assert.Equal("Sebze 'taze' \\ yeşil", (string?)row[6], "value read");
                Assert.Equal(1, await UpdateAsync(conn, row, 8, "Hollanda"), "affected");
                Assert.Equal(1L, await CountAsync(conn, "ULKE_ADI = 'Hollanda' AND YIL = 2025"), "updated");
            });

            await t.Check("edit: a row changed by someone else since it was read → 0 rows (the Data Browser then says so and reloads)", async () =>
            {
                var row = (await RowsAsync(conn)).Single(r => (int?)r[1] == 2025);
                await using (var other = new MySqlCommand("UPDATE mf_edit_nokey SET DOLAR = DOLAR + 1 WHERE YIL = 2025", conn))
                    await other.ExecuteNonQueryAsync();
                Assert.Equal(0, await UpdateAsync(conn, row, 8, "Belçika"), "not found");
                Assert.Equal(0L, await CountAsync(conn, "ULKE_ADI = 'Belçika'"), "nothing changed");
            });

            await t.Check("edit: deleting both twins by all values in one transaction", async () =>
            {
                // make them identical again, then delete both, as if both rows were selected
                await using (var same = new MySqlCommand("UPDATE mf_edit_nokey SET KILOGRAM = 500 WHERE IHRITH = 'IHR'", conn))
                    await same.ExecuteNonQueryAsync();
                var twins = (await RowsAsync(conn)).Where(r => (string?)r[0] == "IHR").ToList();
                var where = RowMatch.Where(Match.Select(i => Columns[i]).ToList(), allValues: true);
                int deleted = 0;
                await using (var tx = await conn.BeginTransactionAsync())
                {
                    foreach (var row in twins)
                    {
                        await using var cmd = new MySqlCommand($"DELETE FROM mf_edit_nokey WHERE {where} LIMIT 1", conn, tx);
                        for (int k = 0; k < Match.Length; k++) cmd.Parameters.AddWithValue("@k" + k, row[Match[k]]);
                        deleted += await cmd.ExecuteNonQueryAsync();
                    }
                    await tx.CommitAsync();
                }
                Assert.Equal(2, deleted, "deleted");
                Assert.Equal(2L, await CountAsync(conn, "1 = 1"), "rows left");
            });
        }
        finally
        {
            await EditorPropertyTests.ExecScript(s, db.Database, "DROP TABLE IF EXISTS mf_edit_nokey");
        }
    }
}
