using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using MiniExcelLibs;
using MyFront.Core.Db;
using MyFront.Core.Export;
using MySqlConnector;

namespace MyFront.SelfTest;

/// <summary>Phase 2: Excel / CSV export against the test server. Files land in tests\samples\out\exports.</summary>
internal static class ExportTests
{
    private static readonly XNamespace X = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public static string OutDir => Path.Combine(TestDb.RepoRoot, "tests", "samples", "out", "exports");

    public static async Task RunAsync(TestRunner t, TestDb db, ServerSession s)
    {
        Directory.CreateDirectory(OutDir);
        Offline(t);
        SqlDumpTests.Offline(t);
        await Typed(t, db, s);
        await BigTable(t, db, s);
        await Split(t, db, s);
        await Database(t, db, s);
        await Cancel(t, db, s);
        await Csv(t, db, s);
        await SqlDumpTests.LiveAsync(t, s, db);
        await RealExcel(t);
    }

    /// <summary>Opens the files in a hidden, read-only Excel (COM) and checks what Excel itself reports. Skipped without Excel.</summary>
    private static async Task RealExcel(TestRunner t)
    {
        var type = Type.GetTypeFromProgID("Excel.Application");
        if (type is null)
        {
            Console.WriteLine("SKIP  export: real Excel check (Excel not installed)");
            return;
        }
        await t.Check("export: files open in real Excel — bold frozen header, filter, real dates, blanks, split", () =>
        {
            var notes = new List<string>();
            Exception? error = null;
            var thread = new Thread(() =>
            {
                dynamic xl = Activator.CreateInstance(type)!;
                try
                {
                    xl.Visible = false;
                    xl.DisplayAlerts = false;
                    foreach (var name in new[] { "typed.xlsx", "prices.xlsx", "database.xlsx", "split-1.2M.xlsx" })
                    {
                        dynamic wb = xl.Workbooks.Open(Out(name), 0, true);
                        try
                        {
                            dynamic ws = wb.Worksheets.Item(1);
                            ws.Activate();
                            dynamic win = wb.Windows.Item(1);
                            Assert.True((bool)ws.Range("A1").Font.Bold, name + ": header bold");
                            Assert.True((bool)win.FreezePanes && (int)win.SplitRow == 1, name + ": header frozen");
                            Assert.True((bool)ws.AutoFilterMode, name + ": autofilter");
                            if (name == "typed.xlsx")
                            {
                                Assert.Equal(1234.56, (double)ws.Range("C2").Value2, "number");
                                Assert.Equal(45351.0, (double)ws.Range("D2").Value2, "date serial");
                                Assert.Equal("2024-02-29", (string)ws.Range("D2").Text, "date shown");
                                Assert.Equal("2024-02-29 13:05:09", (string)ws.Range("E2").Text, "datetime shown");
                                Assert.Equal("1234567890123456789", (string)ws.Range("G2").Text, "19 digits intact");
                                Assert.Equal(1.0, (double)xl.WorksheetFunction.CountBlank(ws.Range("H2")), "NULL is blank");
                                Assert.Equal("İstanbul Şişli ğüöç", (string)ws.Range("B2").Text, "Turkish");
                            }
                            if (name == "split-1.2M.xlsx")
                            {
                                Assert.Equal(1_048_576, (int)ws.UsedRange.Rows.Count, "sheet 1 rows");
                                Assert.Equal(151_426, (int)wb.Worksheets.Item(2).UsedRange.Rows.Count, "sheet 2 rows");
                            }
                            notes.Add($"{name} ✓");
                        }
                        finally
                        {
                            wb.Close(false);
                            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(wb);
                        }
                    }
                }
                catch (Exception ex) { error = ex; }
                finally
                {
                    xl.Quit();
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(xl);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (error is not null) throw error;
            return Task.FromResult<string?>(string.Join(", ", notes));
        });
    }

    private static string Out(string name) => Path.Combine(OutDir, name);

    // ---- helpers: read the xlsx parts ---------------------------------------------------------

    internal sealed class Xlsx
    {
        public required List<string> SheetNames;
        public required XDocument Styles;
        public required Func<int, string> SheetXml;

        public static Xlsx Open(string path)
        {
            using var z = ZipFile.OpenRead(path);
            // reading every entry to the end makes .NET verify each CRC
            foreach (var e in z.Entries) { using var st = e.Open(); st.CopyTo(Stream.Null); }
            var wb = XDocument.Load(z.GetEntry("xl/workbook.xml")!.Open());
            var styles = XDocument.Load(z.GetEntry("xl/styles.xml")!.Open());
            return new Xlsx
            {
                SheetNames = wb.Descendants(X + "sheet").Select(e => (string)e.Attribute("name")!).ToList(),
                Styles = styles,
                SheetXml = i =>
                {
                    using var zz = ZipFile.OpenRead(path);
                    using var r = new StreamReader(zz.GetEntry($"xl/worksheets/sheet{i}.xml")!.Open());
                    return r.ReadToEnd();
                },
            };
        }

        public string NumFmtOfStyle(int s)
        {
            var xf = Styles.Root!.Element(X + "cellXfs")!.Elements(X + "xf").ElementAt(s);
            int id = (int)xf.Attribute("numFmtId")!;
            var custom = Styles.Root.Element(X + "numFmts")?.Elements(X + "numFmt").FirstOrDefault(n => (int)n.Attribute("numFmtId")! == id);
            return custom is null ? "builtin:" + id : (string)custom.Attribute("formatCode")!;
        }

        public bool StyleIsBold(int s)
        {
            var xf = Styles.Root!.Element(X + "cellXfs")!.Elements(X + "xf").ElementAt(s);
            int fontId = (int?)xf.Attribute("fontId") ?? 0;
            return Styles.Root.Element(X + "fonts")!.Elements(X + "font").ElementAt(fontId).Element(X + "b") is not null;
        }
    }

    /// <summary>The &lt;c&gt; element for a cell reference, e.g. "D2".</summary>
    private static XElement Cell(string sheetXml, string reference) =>
        XDocument.Parse(sheetXml).Descendants(X + "c").First(c => (string)c.Attribute("r")! == reference);

    private static int RowCount(string path, string sheet) => MiniExcel.Query(path, useHeaderRow: false, sheetName: sheet).Count();

    // ---- tests --------------------------------------------------------------------------------

    private static void Offline(TestRunner t)
    {
        t.Check("export: value mapping keeps Excel from damaging data", () =>
        {
            Assert.Equal("1234567890123456789", ExportValues.ForExcel(1234567890123456789L) as string, "19-digit BIGINT as text");
            Assert.Equal(123L, ExportValues.ForExcel(123L), "small BIGINT as number");
            Assert.Equal("12345678901234.5678", ExportValues.ForExcel(12345678901234.5678m) as string, "18-digit DECIMAL as text");
            Assert.Equal(1234.56m, ExportValues.ForExcel(1234.56m), "DECIMAL as number");
            Assert.Equal(1234.5000m, ExportValues.ForExcel(1234.5000m), "trailing zeros don't count as digits");
            Assert.True(ExportValues.ForExcel(new DateTime(1850, 1, 1)) is string, "date before 1900 as text");
            Assert.True(ExportValues.ForExcel(new DateTime(2024, 1, 1)) is DateTime, "date as date");
            Assert.True(ExportValues.ForExcel(null) is null && ExportValues.ForExcel(DBNull.Value) is null, "NULL");
            Assert.Equal(ExportValues.ExcelMaxText, ((string)ExportValues.ForExcel(new string('x', 40_000))!).Length, "text truncated to 32,767");
            Assert.Equal("12,5", ExportValues.ForCsv(12.5m, ColumnKind.Decimal, ','), "CSV decimal comma");
        });
        t.Check("export: sheet names follow Excel rules", () =>
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Assert.Equal("a_b_c_d", ExportValues.SheetName("a/b:c?d", used), "invalid characters");
            Assert.Equal("A_B_C_D (2)", ExportValues.SheetName("A/B:C?D", used), "case-insensitive duplicate");
            var longName = ExportValues.SheetName(new string('ş', 40), used);
            Assert.Equal(31, longName.Length, "31 characters max");
            Assert.Equal(31, ExportValues.SheetName(new string('ş', 40), used).Length, "duplicate of a long name still fits");
        });
    }

    private static async Task Typed(TestRunner t, TestDb db, ServerSession s)
    {
        await t.Check("export: types, NULL, bold frozen header, autofilter, Turkish text (query → xlsx)", async () =>
        {
            const string sql = """
                SELECT 1 AS id, 'İstanbul Şişli ğüöç' AS city, CAST(1234.56 AS DECIMAL(12,2)) AS price,
                       DATE '2024-02-29' AS d, TIMESTAMP '2024-02-29 13:05:09' AS ts, CAST('13:05:09' AS TIME) AS t,
                       1234567890123456789 AS big, NULL AS empty, 0.1e0 AS dbl, X'CAFE' AS bin
                UNION ALL
                SELECT 2, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
                """;
            var path = Out("typed.xlsx");
            var progress = new ExportProgress();
            var r = await ExcelExporter.ExportAsync(path, new[] { ExportTable.FromQuery(s, db.Database, "typed", sql) }, progress, CancellationToken.None);
            Assert.True(r.HeaderBold, "header bold patch applied");
            Assert.Equal(2L, r.Rows, "rows");
            Assert.True(!File.Exists(path + ".part"), "no .part left");
            var x = Xlsx.Open(path);
            var sheet = x.SheetXml(1);
            Assert.True(sheet.Contains("state=\"frozen\"") && sheet.Contains("ySplit=\"1\""), "frozen header row");
            Assert.True(sheet.Contains("<x:autoFilter ref=\"A1:J3\""), "autofilter over the data");
            Assert.True(x.StyleIsBold(int.Parse((string)Cell(sheet, "A1").Attribute("s")!)), "header bold");
            Assert.True(!x.StyleIsBold(int.Parse((string)Cell(sheet, "A2").Attribute("s")!)), "data not bold");
            Assert.Equal("n", (string?)Cell(sheet, "C2").Attribute("t"), "price is a number");
            Assert.Equal("yyyy-mm-dd", x.NumFmtOfStyle(int.Parse((string)Cell(sheet, "D2").Attribute("s")!)), "DATE format");
            Assert.Equal("45351", Cell(sheet, "D2").Value, "DATE is a real Excel date serial");
            Assert.Equal("yyyy-mm-dd hh:mm:ss", x.NumFmtOfStyle(int.Parse((string)Cell(sheet, "E2").Attribute("s")!)), "DATETIME format");
            Assert.True(Cell(sheet, "E2").Value.StartsWith("45351.545"), "DATETIME serial with time");
            Assert.Equal("str", (string?)Cell(sheet, "G2").Attribute("t"), "19-digit number kept as text");
            Assert.True(Cell(sheet, "H2").Element(X + "v") is null, "NULL is a truly empty cell");
            Assert.True(Cell(sheet, "B3").Element(X + "v") is null, "NULL string is a truly empty cell");
            var back = MiniExcel.Query(path, useHeaderRow: true).Cast<IDictionary<string, object?>>().ToList();
            Assert.Equal("İstanbul Şişli ğüöç", back[0]["city"] as string, "Turkish text round trip");
            Assert.Equal("0xCAFE", back[0]["bin"] as string, "binary as hex text");
            return $"{new FileInfo(path).Length} bytes";
        });

        await t.Check("export: result grid rows in display order and column order (store → xlsx)", async () =>
        {
            var cols = new[] { new ColumnInfo { Name = "n", Kind = ColumnKind.Integer }, new ColumnInfo { Name = "s", Kind = ColumnKind.Text } };
            var store = new RowStore();
            foreach (var (n, str) in new[] { (3, "üç"), (1, "bir"), (2, "iki") }) store.Add(new object?[] { n, str });
            store.ApplySortOrder(store.BuildSortOrder(0, descending: false));
            var path = Out("from-grid.xlsx");
            await ExcelExporter.ExportAsync(path, new[] { ExportTable.FromResult("Result 1", cols, store, new[] { 1, 0 }) }, new ExportProgress(), CancellationToken.None);
            var rows = MiniExcel.Query(path, useHeaderRow: true).Cast<IDictionary<string, object?>>().ToList();
            Assert.Equal("bir,iki,üç", string.Join(",", rows.Select(r => r["s"])), "sorted order");
            Assert.Equal("s", rows[0].Keys.First(), "column order");
        });
    }

    private static async Task BigTable(TestRunner t, TestDb db, ServerSession s)
    {
        await t.Check("export: prices table (200k rows) streamed to xlsx — time, memory, content", async () =>
        {
            var path = Out("prices.xlsx");
            var progress = new ExportProgress();
            using var proc = Process.GetCurrentProcess();
            GC.Collect();
            proc.Refresh();
            long baseline = proc.PrivateMemorySize64, peak = baseline;
            using var stop = new CancellationTokenSource();
            var sampler = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    proc.Refresh();
                    peak = Math.Max(peak, proc.PrivateMemorySize64);
                    await Task.Delay(50);
                }
            });
            var r = await ExcelExporter.ExportAsync(path, new[] { ExportTable.FromTable(s, db.Database, "prices") }, progress, CancellationToken.None);
            stop.Cancel();
            await sampler;
            Assert.Equal(200_000L, r.Rows, "rows exported");
            Assert.Equal(1, r.Sheets, "one sheet");
            var x = Xlsx.Open(path);
            Assert.Equal("prices", x.SheetNames.Single(), "sheet name");
            Assert.Equal(200_001, RowCount(path, "prices"), "rows in file incl. header");
            var first = MiniExcel.Query(path, useHeaderRow: true).Cast<IDictionary<string, object?>>().First();
            Assert.Equal("İstanbul", first["city"] as string, "first city");
            Assert.True(first["price_date"] is DateTime, "price_date read back as a date");
            return $"{r.Elapsed.TotalMilliseconds:0} ms, {new FileInfo(path).Length / 1024.0 / 1024:0.0} MB file, " +
                   $"process memory +{(peak - baseline) / 1024 / 1024} MB during export";
        });
    }

    private static async Task Split(TestRunner t, TestDb db, ServerSession s)
    {
        await t.Check("export: small sheet limit splits into name, name (2), name (3)", async () =>
        {
            ExcelExporter.MaxDataRowsPerSheet = 1000;
            try
            {
                var path = Out("split-small.xlsx");
                var r = await ExcelExporter.ExportAsync(path, new[] { ExportTable.FromQuery(s, db.Database, "prices", "SELECT * FROM prices ORDER BY id LIMIT 2500") },
                    new ExportProgress(), CancellationToken.None);
                var x = Xlsx.Open(path);
                Assert.Equal("prices,prices (2),prices (3)", string.Join(",", x.SheetNames), "sheet names");
                Assert.Equal("1001,1001,501", string.Join(",", x.SheetNames.Select(n => RowCount(path, n))), "rows per sheet incl. header");
                var third = MiniExcel.Query(path, useHeaderRow: true, sheetName: "prices (3)").Cast<IDictionary<string, object?>>().First();
                Assert.Equal(2001d, Convert.ToDouble(third["id"]), "sheet 3 continues at row 2001");
                return $"{r.Sheets} sheets";
            }
            finally { ExcelExporter.MaxDataRowsPerSheet = ExportValues.ExcelMaxRows - 1; }
        });

        await t.Check("export: 1.2M-row query splits at Excel's real limit (1,048,576 rows per sheet)", async () =>
        {
            const string sql = """
                SELECT p.id, p.city, x.n FROM prices p
                CROSS JOIN (SELECT 1 AS n UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6) x
                """;
            var path = Out("split-1.2M.xlsx");
            var r = await ExcelExporter.ExportAsync(path, new[] { ExportTable.FromQuery(s, db.Database, "big query", sql) }, new ExportProgress(), CancellationToken.None);
            Assert.Equal(1_200_000L, r.Rows, "rows");
            var x = Xlsx.Open(path);
            Assert.Equal("big query,big query (2)", string.Join(",", x.SheetNames), "sheets");
            int r1 = RowCount(path, "big query"), r2 = RowCount(path, "big query (2)");
            Assert.Equal(1_048_576, r1, "first sheet is full (header + 1,048,575)");
            Assert.Equal(1_200_000 - 1_048_575 + 1, r2, "rest on sheet 2 (+ header)");
            return $"{r.Elapsed.TotalSeconds:0.0} s, {new FileInfo(path).Length / 1024 / 1024} MB, sheets {r1:N0} + {r2:N0} rows";
        });
    }

    private static async Task Database(TestRunner t, TestDb db, ServerSession s)
    {
        await using (var conn = await s.OpenAsync(db.Database))
        await using (var cmd = new MySqlCommand("DROP TABLE IF EXISTS mf_export_empty; CREATE TABLE mf_export_empty (id INT PRIMARY KEY, ad VARCHAR(20), tarih DATE)", conn))
            await cmd.ExecuteNonQueryAsync();
        try
        {
            await t.Check("export: whole database → one sheet per table (views included), empty table keeps its header", async () =>
            {
                var tables = (await s.Schema.GetTableStatusAsync(db.Database))
                    .Where(x => x.Name.StartsWith("mf_export") || x.Name is "products" or "v_latest_prices" or "prices")
                    .Select(x => ExportTable.FromTable(s, db.Database, x.Name, x.Rows)).ToList();
                var path = Out("database.xlsx");
                var r = await ExcelExporter.ExportAsync(path, tables, new ExportProgress(), CancellationToken.None);
                var xl = Xlsx.Open(path);
                Assert.Equal("mf_export_empty,prices,products,v_latest_prices", string.Join(",", xl.SheetNames), "sheet per table");
                Assert.Equal(1, RowCount(path, "mf_export_empty"), "empty table: header row only");
                Assert.True(xl.SheetXml(1).Contains(">tarih<"), "empty table header written");
                Assert.Equal(51, RowCount(path, "products"), "products rows + header");
                return $"{r.Rows:N0} rows in {r.Sheets} sheets, {r.Elapsed.TotalSeconds:0.0} s";
            });
        }
        finally
        {
            await using var conn = await s.OpenAsync(db.Database);
            await using var cmd = new MySqlCommand("DROP TABLE IF EXISTS mf_export_empty", conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task Cancel(TestRunner t, TestDb db, ServerSession s)
    {
        await t.Check("export: cancel stops quickly and leaves no file", async () =>
        {
            var path = Out("cancelled.xlsx");
            ExcelExporter.TryDelete(path);
            var progress = new ExportProgress();
            using var cts = new CancellationTokenSource();
            var sw = Stopwatch.StartNew();
            var task = ExcelExporter.ExportAsync(path, new[] { ExportTable.FromQuery(s, db.Database, "x", "SELECT a.* FROM prices a CROSS JOIN products b") }, progress, cts.Token);
            while (progress.Rows < 20_000) await Task.Delay(20);
            cts.Cancel();
            await Assert.Throws(() => task, "cancelled export");
            var took = sw.ElapsedMilliseconds;
            Assert.True(!File.Exists(path) && !File.Exists(path + ".part"), "no file left");
            // the session can still export afterwards
            var ok = await ExcelExporter.ExportAsync(Out("after-cancel.xlsx"), new[] { ExportTable.FromQuery(s, db.Database, "x", "SELECT 1") }, new ExportProgress(), CancellationToken.None);
            Assert.Equal(1L, ok.Rows, "export after cancel");
            return $"cancelled at {progress.Rows:N0} rows, {took} ms total";
        });
    }

    private static async Task Csv(TestRunner t, TestDb db, ServerSession s)
    {
        const string sql = """
            SELECT 'İzmir; Karşıyaka' AS city, 'He said "merhaba"' AS quote, CAST(1234.5 AS DECIMAL(10,2)) AS price,
                   NULL AS empty, DATE '2024-02-29' AS d, TIMESTAMP '2024-02-29 13:05:09' AS ts, ' padded ' AS pad
            """;
        await t.Check("export: CSV with ';', decimal comma, UTF-8 BOM, quoting, NULL empty", async () =>
        {
            var path = Out("turkish.csv");
            var o = new CsvOptions { Delimiter = ';', DecimalSeparator = ',', Encoding = "utf-8-bom" };
            await CsvExporter.ExportAsync(path, new[] { ExportTable.FromQuery(s, db.Database, "q", sql) }, o, new ExportProgress(), CancellationToken.None);
            var bytes = File.ReadAllBytes(path);
            Assert.True(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "UTF-8 BOM");
            var lines = File.ReadAllLines(path, Encoding.UTF8);
            Assert.Equal("city;quote;price;empty;d;ts;pad", lines[0], "header");
            Assert.Equal("\"İzmir; Karşıyaka\";\"He said \"\"merhaba\"\"\";1234,50;;2024-02-29;2024-02-29 13:05:09;\" padded \"", lines[1], "row");
        });
        await t.Check("export: CSV in Windows-1254 with ',' and decimal point", async () =>
        {
            var path = Out("turkish-1254.csv");
            var o = new CsvOptions { Delimiter = ',', DecimalSeparator = '.', Encoding = "windows-1254" };
            await CsvExporter.ExportAsync(path, new[] { ExportTable.FromQuery(s, db.Database, "q", sql) }, o, new ExportProgress(), CancellationToken.None);
            var text = File.ReadAllText(path, Encoding.GetEncoding(1254));
            Assert.True(text.Contains("İzmir; Karşıyaka") && text.Contains(",1234.50,"), text);
            Assert.True(File.ReadAllBytes(path)[0] == (byte)'c', "no BOM");
        });
        await t.Check("export: database to CSV → one file per table in a folder", async () =>
        {
            var folder = Out("csv-folder");
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            var tables = new[] { ExportTable.FromTable(s, db.Database, "products"), ExportTable.FromTable(s, db.Database, "v_latest_prices") };
            var r = await CsvExporter.ExportAsync(folder, tables, new CsvOptions(), new ExportProgress(), CancellationToken.None);
            Assert.Equal("products.csv,v_latest_prices.csv", string.Join(",", Directory.GetFiles(folder).Select(Path.GetFileName).Order()), "files");
            Assert.Equal(51, File.ReadAllLines(Path.Combine(folder, "products.csv")).Length, "products lines");
            return $"{r.Rows} rows";
        });
        await t.Check("export: 200k rows to CSV", async () =>
        {
            var path = Out("prices.csv");
            var r = await CsvExporter.ExportAsync(path, new[] { ExportTable.FromTable(s, db.Database, "prices") }, new CsvOptions(), new ExportProgress(), CancellationToken.None);
            Assert.Equal(200_000L, r.Rows, "rows");
            return $"{r.Elapsed.TotalMilliseconds:0} ms, {new FileInfo(path).Length / 1024 / 1024} MB";
        });
    }

    /// <summary>For the Excel COM check: which files to open and what to expect.</summary>
    public static readonly string[] FilesForExcel = { "typed.xlsx", "prices.xlsx", "database.xlsx", "split-small.xlsx" };
}
