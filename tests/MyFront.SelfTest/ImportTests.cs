using System.Diagnostics;
using System.Text;
using MiniExcelLibs;
using MyFront.Core.Accounts;
using MyFront.Core.Db;
using MyFront.Core.Import;
using MySqlConnector;

namespace MyFront.SelfTest;

/// <summary>Phase 3: Excel / CSV import against the test server (your sample files + generated Turkish samples).</summary>
internal static class ImportTests
{
    public static string OutDir => Path.Combine(TestDb.RepoRoot, "tests", "samples", "out", "imports");
    private static string Out(string name) => Path.Combine(OutDir, name);

    public static async Task RunAsync(TestRunner t, TestDb db, ServerSession s)
    {
        Directory.CreateDirectory(OutDir);
        Offline(t);
        var scratch = new[] { "mf_imp_dir", "mf_imp_ots", "mf_imp_fasil", "mf_imp_tr", "mf_imp_bad" };
        await Exec(s, db, string.Join(";", scratch.Select(x => $"DROP TABLE IF EXISTS {x}")));
        try
        {
            await UserFiles(t, db, s);
            await Turkish(t, db, s);
            await Errors(t, db, s);
        }
        finally
        {
            await Exec(s, db, string.Join(";", scratch.Select(x => $"DROP TABLE IF EXISTS {x}")));
        }
    }

    private static async Task Exec(ServerSession s, TestDb db, string sql)
    {
        await using var conn = await s.OpenAsync(db.Database);
        await using var cmd = new MySqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> Scalar(ServerSession s, TestDb db, string sql)
    {
        await using var conn = await s.OpenAsync(db.Database);
        await using var cmd = new MySqlCommand(sql, conn);
        var v = await cmd.ExecuteScalarAsync();
        return v is DBNull ? null : v;
    }

    /// <summary>Auto-match like the wizard does: every matched header becomes a ColumnMap.</summary>
    private static async Task<List<ColumnMap>> AutoMap(ServerSession s, TestDb db, string table, IReadOnlyList<string> headers)
    {
        s.Schema.Invalidate(db.Database, table);
        var info = await s.Schema.GetTableAsync(db.Database, table);
        var match = HeaderMatcher.Match(headers, info.Columns.Select(c => c.Name).ToList());
        return match.Select((target, i) => (target, i)).Where(x => x.target is not null).Select(x => new ColumnMap(x.i, x.target!)).ToList();
    }

    // ---- offline ------------------------------------------------------------------------------

    private static void Offline(TestRunner t)
    {
        t.Check("import: header matching ignores case, spaces/underscores and Turkish letters", () =>
        {
            var targets = new[] { "yıl", "Ay", "IstPoz", "istpoz adi", "ULKE", "İşlem Adı", "olcu-adi", "id" };
            var headers = new[] { "YIL", "AY", "ISTPOZ", "ISTPOZ_ADI", "ulke", "ISLEM_ADI", "OLCU_ADI", "NOT_IN_TABLE" };
            var m = HeaderMatcher.Match(headers, targets);
            Assert.Equal("yıl,Ay,IstPoz,istpoz adi,ULKE,İşlem Adı,olcu-adi,", string.Join(",", m.Select(x => x ?? "")), "matches");
            var exact = HeaderMatcher.Match(new[] { "a b", "a_b" }, new[] { "a_b", "ab" });
            Assert.Equal("ab,a_b", string.Join(",", exact.Select(x => x ?? "")), "exact name wins, each target used once");
        });
        t.Check("import: Turkish and dot number text", () =>
        {
            decimal v;
            Assert.True(ValueConverter.TryParseNumber("1.234,56", NumberFormat.Turkish, out v) && v == 1234.56m, "1.234,56");
            Assert.True(ValueConverter.TryParseNumber("1.234.567", NumberFormat.Turkish, out v) && v == 1234567m, "1.234.567");
            Assert.True(ValueConverter.TryParseNumber("-12,5", NumberFormat.Turkish, out v) && v == -12.5m, "-12,5");
            Assert.True(ValueConverter.TryParseNumber("12.5", NumberFormat.Turkish, out v) && v == 12.5m, "12.5 is decimal even in Turkish mode");
            Assert.True(ValueConverter.TryParseNumber("1,234.56", NumberFormat.Dot, out v) && v == 1234.56m, "1,234.56");
            Assert.True(ValueConverter.TryParseNumber("1,234", NumberFormat.Dot, out v) && v == 1234m, "1,234 thousands");
            Assert.True(ValueConverter.TryParseNumber("1 234,5", NumberFormat.Turkish, out v) && v == 1234.5m, "space thousands");
            Assert.True(!ValueConverter.TryParseNumber("12a", NumberFormat.Turkish, out _), "invalid");
        });
        t.Check("import: date text (dd.MM.yyyy, ISO, dd/MM/yyyy)", () =>
        {
            Assert.True(ValueConverter.TryParseDate("29.02.2024", out var d) && d == new DateTime(2024, 2, 29), "dd.MM.yyyy");
            Assert.True(ValueConverter.TryParseDate("29.02.2024 13:05", out d) && d == new DateTime(2024, 2, 29, 13, 5, 0), "with time");
            Assert.True(ValueConverter.TryParseDate("2024-02-29 13:05:09", out d) && d.Second == 9, "ISO");
            Assert.True(ValueConverter.TryParseDate("29/02/2024", out d) && d.Month == 2, "dd/MM/yyyy");
            Assert.True(ValueConverter.TryParseDate("29 Şubat 2024", out d) && d.Month == 2, "Turkish month name");
        });
        t.Check("import: value conversion into column types", () =>
        {
            string? e;
            Assert.Equal(10511190011L, ValueConverter.Convert(10511190011d, TargetColumn.From("x", "bigint(20)"), NumberFormat.Turkish, true, out e), "BIGINT from Excel number");
            Assert.Equal("10511190011", ValueConverter.Convert(10511190011d, TargetColumn.From("x", "varchar(20)"), NumberFormat.Turkish, true, out e), "code to text without exponent");
            Assert.Equal(168175.12m, ValueConverter.Convert(168175.12d, TargetColumn.From("x", "decimal(12,2)"), NumberFormat.Turkish, true, out e), "DECIMAL from double");
            Assert.Equal(new DateTime(2024, 2, 29), ValueConverter.Convert(45351d, TargetColumn.From("x", "date"), NumberFormat.Turkish, true, out e), "Excel serial to DATE");
            Assert.True(ValueConverter.Convert("   ", TargetColumn.From("x", "varchar(5)"), NumberFormat.Turkish, true, out e) is null && e is null, "blank → NULL");
            Assert.Equal("ab", ValueConverter.Convert("  ab ", TargetColumn.From("x", "varchar(5)"), NumberFormat.Turkish, true, out e), "trimmed");
            ValueConverter.Convert("abc", TargetColumn.From("adet", "int(11)"), NumberFormat.Turkish, true, out e);
            Assert.True(e is not null && e.Contains("adet") && e.Contains("not a number"), "error text: " + e);
            ValueConverter.Convert("123456", TargetColumn.From("kod", "varchar(5)"), NumberFormat.Turkish, true, out e);
            Assert.True(e is not null && e.Contains("too long"), "length check: " + e);
            ValueConverter.Convert(12.5d, TargetColumn.From("n", "int"), NumberFormat.Turkish, true, out e);
            Assert.True(e is not null && e.Contains("whole number"), "12.5 into INT: " + e);
        });
        t.Check("import: remembered mapping key ignores download suffixes", () =>
        {
            Assert.Equal("trade_stats", MappingStore.FileKey(@"C:\x\TRADE_STATS (11).xlsx"), "(11)");
            Assert.Equal("trade_stats", MappingStore.FileKey(@"C:\x\TRADE_STATS (12).xlsx"), "(12)");
            Assert.Equal("rapor", MappingStore.FileKey("rapor - Kopya (2).xlsx"), "Turkish copy suffix");
            Assert.Equal("report", MappingStore.FileKey("report - Copy.csv"), "copy suffix");
            var entry = new SavedImport { FileKey = MappingStore.FileKey("TRADE_STATS (11).xlsx"), Database = "d", Table = "t", Columns = { ["YIL"] = "yil", ["X"] = null } };
            MappingStore.Save(entry);
            var found = MappingStore.Find(@"D:\Downloads\TRADE_STATS (14).xlsx");
            Assert.True(found is not null && found.Table == "t" && found.Columns["YIL"] == "yil" && found.Columns["X"] is null, "found by a later download");
        });
    }

    // ---- your files -----------------------------------------------------------------------------

    private static string? Sample(TestDb db, string contains) =>
        db.ImportSamples.FirstOrDefault(p => Path.GetFileName(p).Contains(contains, StringComparison.OrdinalIgnoreCase) && File.Exists(p));

    /// <summary>Sum of a column straight from the file, as the import should store it.</summary>
    private static (long Rows, decimal Sum) FileSum(string path, string header)
    {
        using var r = ImportFile.Open(path, null, headerRow: true);
        int idx = r.Headers.ToList().IndexOf(header);
        long rows = 0;
        decimal sum = 0;
        while (r.Read())
        {
            if (ImportFile.IsEmptyRow(r.Current)) continue;
            rows++;
            if (r.Current[idx] is double d) sum += Math.Round(Convert.ToDecimal(d), 6);
        }
        return (rows, sum);
    }

    private static async Task UserFiles(TestRunner t, TestDb db, ServerSession s)
    {
        var dir = Sample(db, "_DIR_OTS");
        // the plain *_OTS file: the one that is neither _DIR_OTS nor FASIL_OTS
        var ots = db.ImportSamples.FirstOrDefault(p => Path.GetFileName(p) is var n && n.Contains("_OTS", StringComparison.OrdinalIgnoreCase)
            && !n.Contains("_DIR_", StringComparison.OrdinalIgnoreCase) && !n.Contains("FASIL", StringComparison.OrdinalIgnoreCase) && File.Exists(p));
        var fasil = Sample(db, "FASIL_OTS");
        if (dir is null || ots is null || fasil is null)
        {
            Console.WriteLine("SKIP  import: your sample files (importSamples in testdb.local.json not found)");
            return;
        }

        await t.Check("import: DIR_OTS (48k rows) into a table whose column names are written differently", async () =>
        {
            // same columns as the file, but lower/upper/mixed case, spaces, Turkish letters, dashes
            await Exec(s, db, """
                CREATE TABLE mf_imp_dir (
                  id INT AUTO_INCREMENT PRIMARY KEY,
                  `yıl` INT NOT NULL, `Ay` TINYINT NOT NULL, `IstPoz` BIGINT, `istpoz adi` VARCHAR(500), `Ulke` INT, `ULKE_ADI` VARCHAR(100),
                  `islem_kodu` INT, `İşlem Adı` VARCHAR(200), `olcu-adi` VARCHAR(30),
                  `ithalat_miktar1` DECIMAL(24,6), `IthalatMiktar2` DECIMAL(24,6), `ithalat dolar` DECIMAL(24,6),
                  `IHRACAT_MIKTAR1` DECIMAL(24,6), `ihracat_Miktar2` DECIMAL(24,6), `ihracat-dolar` DECIMAL(24,6),
                  imported_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
                ) DEFAULT CHARSET=utf8mb4
                """);
            using (var r = ImportFile.Open(dir, null, true))
            {
                var map = await AutoMap(s, db, "mf_imp_dir", r.Headers);
                Assert.Equal(15, map.Count, "all 15 file columns matched: " + string.Join(", ", map.Select(m => $"{r.Headers[m.SourceIndex]}→{m.Target}")));
                var progress = new ImportProgress();
                var res = await ImportEngine.RunAsync(s, db.Database, "mf_imp_dir", map, () => ImportFile.Open(dir, null, true),
                    new ImportOptions { Mode = ImportMode.Insert }, progress, CancellationToken.None);
                Assert.True(res.Committed && res.Error is null, res.Error ?? "");
                var (rows, sum) = FileSum(dir, "IHRACAT_DOLAR");
                Assert.Equal(rows, res.Inserted, "inserted = rows in file");
                Assert.Equal(rows, Convert.ToInt64(await Scalar(s, db, "SELECT COUNT(*) FROM mf_imp_dir")), "rows in table");
                Assert.Equal(sum, Convert.ToDecimal(await Scalar(s, db, "SELECT SUM(`ihracat-dolar`) FROM mf_imp_dir")), "SUM(IHRACAT_DOLAR) identical");
                Assert.Equal(1L, Convert.ToInt64(await Scalar(s, db, "SELECT COUNT(*) FROM mf_imp_dir WHERE ULKE_ADI = 'Gürcistan' AND IstPoz = 10511190011 AND `ihracat-dolar` = 168175")), "Turkish text + BIGINT code");
                Assert.Equal(0L, Convert.ToInt64(await Scalar(s, db, "SELECT COUNT(*) FROM mf_imp_dir WHERE imported_at IS NULL")), "unmapped NOT NULL column got its default");
                return $"{res.Inserted:N0} rows in {res.Elapsed.TotalSeconds:0.0} s ({res.Inserted / res.Elapsed.TotalSeconds:N0} rows/s)";
            }
        });

        await t.Check("import: OTS (207k rows) into a NEW table with inferred types", async () =>
        {
            var sw = Stopwatch.StartNew();
            List<InferredColumn> cols;
            using (var r = ImportFile.Open(ots, null, true))
                cols = TypeInference.Infer(r, NumberFormat.Turkish, int.MaxValue, CancellationToken.None);
            var inferMs = sw.ElapsedMilliseconds;
            var types = string.Join(", ", cols.Select(c => $"{c.Name} {c.SqlType}"));
            Assert.True(cols.Single(c => c.Name == "ISTPOZ").SqlType == "BIGINT", "ISTPOZ → BIGINT: " + types);
            Assert.True(cols.Single(c => c.Name == "YIL").SqlType == "INT", "YIL → INT: " + types);
            Assert.True(cols.Single(c => c.Name == "ULKE_ADI").SqlType.StartsWith("VARCHAR("), "ULKE_ADI → VARCHAR: " + types);
            await Exec(s, db, ImportEngine.CreateTableSql(db.Database, "mf_imp_ots", cols));
            var map = cols.Select(c => new ColumnMap(c.SourceIndex, c.Name)).ToList();
            var res = await ImportEngine.RunAsync(s, db.Database, "mf_imp_ots", map, () => ImportFile.Open(ots, null, true),
                new ImportOptions(), new ImportProgress(), CancellationToken.None);
            Assert.True(res.Committed, res.Error ?? "");
            var (rows, sum) = FileSum(ots, "ITHALAT_DOLAR");
            Assert.Equal(rows, Convert.ToInt64(await Scalar(s, db, "SELECT COUNT(*) FROM mf_imp_ots")), "rows");
            Assert.Equal(sum, Convert.ToDecimal(await Scalar(s, db, "SELECT SUM(ITHALAT_DOLAR) FROM mf_imp_ots")), "SUM(ITHALAT_DOLAR) identical");
            Assert.True(Convert.ToInt64(await Scalar(s, db, "SELECT COUNT(*) FROM mf_imp_ots WHERE IHRACAT_MIKTAR2 IS NULL")) > 0, "empty cells became NULL");
            return $"inferred in {inferMs} ms → {types}; imported {res.Inserted:N0} rows in {res.Elapsed.TotalSeconds:0.0} s";
        });

        await t.Check("import: FASIL_OTS (107k) Upsert counts inserted / unchanged / updated, Insert Ignore skips, Replace", async () =>
        {
            await Exec(s, db, """
                CREATE TABLE mf_imp_fasil (yil INT, ay INT, fasil INT, fasil_adi VARCHAR(300), ulke INT, ulke_adi VARCHAR(100),
                  ithalat_dolar DECIMAL(20,2), ihracat_dolar DECIMAL(20,2), PRIMARY KEY (yil, ay, fasil, ulke)) DEFAULT CHARSET=utf8mb4
                """);
            List<ColumnMap> map;
            using (var r = ImportFile.Open(fasil, null, true)) map = await AutoMap(s, db, "mf_imp_fasil", r.Headers);
            Assert.Equal(8, map.Count, "8 columns matched");
            var o = new ImportOptions { Mode = ImportMode.Upsert };
            var first = await ImportEngine.RunAsync(s, db.Database, "mf_imp_fasil", map, () => ImportFile.Open(fasil, null, true), o, new ImportProgress(), CancellationToken.None);
            Assert.True(first.Committed, first.Error ?? "");
            long distinct = Convert.ToInt64(await Scalar(s, db, "SELECT COUNT(*) FROM mf_imp_fasil"));
            Assert.Equal(distinct, first.Inserted, "first upsert: everything inserted");

            var second = await ImportEngine.RunAsync(s, db.Database, "mf_imp_fasil", map, () => ImportFile.Open(fasil, null, true), o, new ImportProgress(), CancellationToken.None);
            Assert.True(second.Inserted == 0 && second.Updated == 0 && second.Unchanged == first.Rows, $"second upsert: {second.Inserted}/{second.Updated}/{second.Unchanged}");

            await Exec(s, db, "UPDATE mf_imp_fasil SET ihracat_dolar = -1 ORDER BY yil, ay, fasil, ulke LIMIT 25");
            await Exec(s, db, "DELETE FROM mf_imp_fasil ORDER BY yil DESC, ay DESC, fasil DESC, ulke DESC LIMIT 7");
            var third = await ImportEngine.RunAsync(s, db.Database, "mf_imp_fasil", map, () => ImportFile.Open(fasil, null, true), o, new ImportProgress(), CancellationToken.None);
            Assert.True(third.Inserted == 7 && third.Updated == 25 && third.Unchanged == first.Rows - 32, $"third upsert: {third.Inserted}/{third.Updated}/{third.Unchanged}");

            var ignore = await ImportEngine.RunAsync(s, db.Database, "mf_imp_fasil", map, () => ImportFile.Open(fasil, null, true),
                new ImportOptions { Mode = ImportMode.InsertIgnore }, new ImportProgress(), CancellationToken.None);
            Assert.True(ignore.Inserted == 0 && ignore.Skipped == first.Rows, $"insert ignore: {ignore.Inserted} inserted, {ignore.Skipped} skipped");

            await Exec(s, db, "INSERT INTO mf_imp_fasil (yil, ay, fasil, ulke) VALUES (1999, 1, 1, 1)");
            var replace = await ImportEngine.RunAsync(s, db.Database, "mf_imp_fasil", map, () => ImportFile.Open(fasil, null, true),
                new ImportOptions { Mode = ImportMode.Replace }, new ImportProgress(), CancellationToken.None);
            Assert.True(replace.Committed && Convert.ToInt64(await Scalar(s, db, "SELECT COUNT(*) FROM mf_imp_fasil")) == distinct, "replace: old rows gone, file rows back");
            return $"{first.Rows:N0} rows; 1st upsert +{first.Inserted:N0}; 2nd {second.Unchanged:N0} unchanged; 3rd +{third.Inserted} / {third.Updated} updated; " +
                   $"ignore skipped {ignore.Skipped:N0}; upsert time {first.Elapsed.TotalSeconds:0.0} s";
        });

        await t.Check("import: cancel in the middle rolls everything back", async () =>
        {
            long before = Convert.ToInt64(await Scalar(s, db, "SELECT COUNT(*) FROM mf_imp_ots"));
            var progress = new ImportProgress();
            using var cts = new CancellationTokenSource();
            using var r = ImportFile.Open(ots, null, true);
            var map = r.Headers.Select((h, i) => new ColumnMap(i, h)).ToList();
            var task = ImportEngine.RunAsync(s, db.Database, "mf_imp_ots", map, () => ImportFile.Open(ots, null, true), new ImportOptions(), progress, cts.Token);
            while (progress.Inserted < 30_000) await Task.Delay(20);
            cts.Cancel();
            var res = await task;
            Assert.True(res.Cancelled && !res.Committed, "cancelled");
            Assert.Equal(before, Convert.ToInt64(await Scalar(s, db, "SELECT COUNT(*) FROM mf_imp_ots")), "rows unchanged after rollback");
            return $"cancelled after {progress.Inserted:N0} rows were sent; table back to {before:N0}";
        });
    }

    // ---- Turkish samples ------------------------------------------------------------------------

    private static async Task Turkish(TestRunner t, TestDb db, ServerSession s)
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["ŞEHİR"] = "İstanbul", ["Ürün Adı"] = "Çay (Rize)", ["FİYAT"] = "1.234,56", ["miktar"] = 12.5, ["Tarih Text"] = "29.02.2024", ["TARİH"] = new DateTime(2024, 3, 1), ["Kod"] = "00123", ["Not"] = "  boşluk  " },
            new() { ["ŞEHİR"] = "Iğdır", ["Ürün Adı"] = "Kayısı", ["FİYAT"] = "87,5", ["miktar"] = 1000.125, ["Tarih Text"] = "01.01.2025", ["TARİH"] = new DateTime(2025, 1, 2), ["Kod"] = "A-7", ["Not"] = null },
            new() { ["ŞEHİR"] = "Şanlıurfa", ["Ürün Adı"] = "İsot", ["FİYAT"] = "-3,10", ["miktar"] = null, ["Tarih Text"] = null, ["TARİH"] = null, ["Kod"] = null, ["Not"] = "ğüşiöç ĞÜŞİÖÇ" },
        };
        var xlsx = Out("turkish.xlsx");
        MiniExcel.SaveAs(xlsx, rows, overwriteFile: true);

        var csv1254 = Out("turkish-1254.csv");
        File.WriteAllText(csv1254,
            "ŞEHİR;Ürün Adı;FİYAT;miktar;Tarih Text;TARİH;Kod;Not\r\n" +
            "İstanbul;\"Çay; Rize\";1.234,56;12,5;29.02.2024;01.03.2024;00123;  boşluk  \r\n" +
            "Iğdır;Kayısı;87,5;1.000,125;01.01.2025;02.01.2025;A-7;\r\n" +
            "Şanlıurfa;İsot;-3,10;;;;;ğüşiöç ĞÜŞİÖÇ\r\n", Encoding.GetEncoding(1254));

        var csvUtf8 = Out("turkish-utf8.csv");
        File.WriteAllText(csvUtf8,
            "ŞEHİR,Ürün Adı,FİYAT,miktar,Tarih Text,TARİH,Kod,Not\r\n" +
            "İstanbul,\"Çay, Rize\",\"1,234.56\",12.5,2024-02-29,2024-03-01,00123,\"  boşluk  \"\r\n" +
            "Iğdır,Kayısı,87.5,1000.125,2025-01-01,2025-01-02,A-7,\r\n" +
            "Şanlıurfa,İsot,-3.10,,,,,\"ğüşiöç ĞÜŞİÖÇ\"\r\n", new UTF8Encoding(true));

        string? xls = MakeXls(xlsx);

        await Exec(s, db, """
            CREATE TABLE mf_imp_tr (id INT AUTO_INCREMENT PRIMARY KEY, sehir VARCHAR(50) NOT NULL, urun_adi VARCHAR(100),
              fiyat DECIMAL(12,2), miktar DECIMAL(12,3), tarih_text DATE, tarih DATE, kod VARCHAR(10), `not` VARCHAR(50)) DEFAULT CHARSET=utf8mb4
            """);
        var cases = new List<(string Name, string Path, NumberFormat Nf)>
        {
            ("xlsx", xlsx, NumberFormat.Turkish), ("csv Windows-1254 ;", csv1254, NumberFormat.Turkish), ("csv UTF-8 ,", csvUtf8, NumberFormat.Dot),
        };
        if (xls is not null) cases.Add(("xls (via Excel)", xls, NumberFormat.Turkish));
        else Console.WriteLine("SKIP  import: .xls sample (Excel not installed to create one)");

        foreach (var (name, path, nf) in cases)
        {
            await t.Check($"import: Turkish sample {name} — letters, 1.234,56, dates, leading zeros, trim, NULL", async () =>
            {
                await Exec(s, db, "TRUNCATE TABLE mf_imp_tr");
                using (var r = ImportFile.Open(path, null, true))
                {
                    var map = await AutoMap(s, db, "mf_imp_tr", r.Headers);
                    Assert.Equal(8, map.Count, "matched: " + string.Join(", ", map.Select(m => r.Headers[m.SourceIndex] + "→" + m.Target)));
                    var res = await ImportEngine.RunAsync(s, db.Database, "mf_imp_tr", map, () => ImportFile.Open(path, null, true),
                        new ImportOptions { Numbers = nf }, new ImportProgress(), CancellationToken.None);
                    Assert.True(res.Committed && res.Inserted == 3, res.Error ?? $"inserted {res.Inserted}");
                }
                await using var conn = await s.OpenAsync(db.Database);
                await using var cmd = new MySqlCommand("SELECT sehir, urun_adi, fiyat, miktar, tarih_text, tarih, kod, `not` FROM mf_imp_tr ORDER BY id", conn);
                await using var rd = await cmd.ExecuteReaderAsync();
                var got = new List<string>();
                while (await rd.ReadAsync())
                    got.Add(string.Join("|", Enumerable.Range(0, 8).Select(i => rd.IsDBNull(i) ? "∅" : CellFormat.ToDisplay(CellFormat.Normalize(rd.GetValue(i)), i is 4 or 5 ? ColumnKind.Date : ColumnKind.Text))));
                var product = name.StartsWith("csv") ? (name.Contains(';') ? "Çay; Rize" : "Çay, Rize") : "Çay (Rize)";
                Assert.Equal($"İstanbul|{product}|1234.56|12.500|2024-02-29|2024-03-01|00123|boşluk", got[0], "row 1");
                Assert.Equal("Iğdır|Kayısı|87.50|1000.125|2025-01-01|2025-01-02|A-7|∅", got[1], "row 2");
                Assert.Equal("Şanlıurfa|İsot|-3.10|∅|∅|∅|∅|ğüşiöç ĞÜŞİÖÇ", got[2], "row 3");
            });
        }
    }

    /// <summary>Saves the xlsx as legacy .xls through Excel (COM), or null when Excel isn't installed.</summary>
    private static string? MakeXls(string xlsx)
    {
        var type = Type.GetTypeFromProgID("Excel.Application");
        if (type is null) return null;
        var xls = Path.ChangeExtension(xlsx, ".xls");
        if (File.Exists(xls)) File.Delete(xls);
        Exception? error = null;
        var th = new Thread(() =>
        {
            dynamic xl = Activator.CreateInstance(type)!;
            try
            {
                xl.Visible = false;
                xl.DisplayAlerts = false;
                dynamic wb = xl.Workbooks.Open(xlsx, 0, true);
                wb.SaveAs(xls, 56); // xlExcel8
                wb.Close(false);
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(wb);
            }
            catch (Exception ex) { error = ex; }
            finally
            {
                xl.Quit();
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(xl);
            }
        });
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        th.Join();
        if (error is not null) throw error;
        return xls;
    }

    // ---- errors ---------------------------------------------------------------------------------

    private static async Task Errors(TestRunner t, TestDb db, ServerSession s)
    {
        var bad = Out("bad-rows.xlsx");
        var rows = new List<Dictionary<string, object?>>();
        for (int i = 1; i <= 2500; i++)
        {
            object? adet = i == 1200 ? "on iki" : i;          // text in an INT column
            object? kod = i == 1800 ? "ÇOK-UZUN-KOD-123" : $"K{i}"; // longer than VARCHAR(10)
            object? ad = i == 2100 ? null : $"Ürün {i}";        // NULL into NOT NULL
            rows.Add(new() { ["ADET"] = adet, ["KOD"] = kod, ["AD"] = ad });
        }
        MiniExcel.SaveAs(bad, rows, overwriteFile: true);
        await Exec(s, db, "CREATE TABLE mf_imp_bad (adet INT, kod VARCHAR(10), ad VARCHAR(50) NOT NULL, UNIQUE KEY (kod)) DEFAULT CHARSET=utf8mb4");
        var map = new List<ColumnMap> { new(0, "adet"), new(1, "kod"), new(2, "ad") };

        await t.Check("import: stop on first error → row number in the message, nothing committed", async () =>
        {
            var res = await ImportEngine.RunAsync(s, db.Database, "mf_imp_bad", map, () => ImportFile.Open(bad, null, true),
                new ImportOptions(), new ImportProgress(), CancellationToken.None);
            Assert.True(!res.Committed && res.Error is not null && res.Error.Contains("Row 1201") && res.Error.Contains("adet"), res.Error ?? "");
            Assert.Equal(0L, Convert.ToInt64(await Scalar(s, db, "SELECT COUNT(*) FROM mf_imp_bad")), "rolled back");
            return res.Error;
        });

        await t.Check("import: continue on error → good rows committed, bad rows logged and saved to .xlsx", async () =>
        {
            // a duplicate key the database rejects (found by retrying the batch row by row)
            await Exec(s, db, "INSERT INTO mf_imp_bad VALUES (0, 'K500', 'already there')");
            var res = await ImportEngine.RunAsync(s, db.Database, "mf_imp_bad", map, () => ImportFile.Open(bad, null, true),
                new ImportOptions { ContinueOnError = true }, new ImportProgress(), CancellationToken.None);
            Assert.True(res.Committed, res.Error ?? "");
            Assert.Equal("1201,1801,2101,501", string.Join(",", res.FailedRows.Select(f => f.RowNumber).OrderBy(n => n < 1000 ? n + 10000 : n)), "failed rows");
            Assert.Equal(2496L, res.Inserted, "inserted");
            Assert.True(res.FailedRows.Single(f => f.RowNumber == 501).Error.Contains("1062"), "duplicate key from the server");
            var path = Out("bad-rows-failed.xlsx");
            await FailedRowsExporter.SaveAsync(path, res);
            var back = MiniExcel.Query(path, useHeaderRow: true).Cast<IDictionary<string, object?>>().ToList();
            Assert.Equal(4, back.Count, "failed rows file");
            Assert.True(back.Any(r => Convert.ToString(r["KOD"]) == "ÇOK-UZUN-KOD-123" && Convert.ToString(r["Error"])!.Contains("too long")), "original values + error");
            return string.Join(" | ", res.FailedRows.Select(f => $"row {f.RowNumber}: {f.Error}"));
        });

        await t.Check("import: read-only connection refuses to import", async () =>
        {
            using var ro = await ServerSession.ConnectAsync(db.ToAccount(TlsMode.Required, readOnly: true), db.Password);
            var ex = await Assert.Throws(() => ImportEngine.RunAsync(ro, db.Database, "mf_imp_bad", map, () => ImportFile.Open(bad, null, true),
                new ImportOptions(), new ImportProgress(), CancellationToken.None), "read-only import");
            Assert.True(ex is ReadOnlyViolationException, ex.Message);
        });
    }
}
