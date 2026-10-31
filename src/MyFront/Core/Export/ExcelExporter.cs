using System.Collections;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using MiniExcelLibs;
using MiniExcelLibs.Attributes;
using MiniExcelLibs.OpenXml;
using MyFront.Core.Db;

namespace MyFront.Core.Export;

/// <summary>
/// Streams tables into one .xlsx with MiniExcel (one sheet per table; more than 1,048,575 rows continue on
/// "name (2)", "name (3)"…). Header row bold, frozen, with autofilter; dates/numbers keep their types.
/// Written to "file.part" first and renamed when complete, so a cancelled export leaves nothing behind.
/// </summary>
public static class ExcelExporter
{
    public static int MaxDataRowsPerSheet { get; internal set; } = ExportValues.ExcelMaxRows - 1;

    public static Task<ExportResult> ExportAsync(string path, IReadOnlyList<ExportTable> tables, ExportProgress progress, CancellationToken ct) =>
        Task.Run(() => Export(path, tables, progress, ct), ct);

    public static ExportResult Export(string path, IReadOnlyList<ExportTable> tables, ExportProgress progress, CancellationToken ct)
    {
        if (tables.Count == 0) throw new ArgumentException("Nothing to export.");
        var sw = Stopwatch.StartNew();
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var part = path + ".part";
        var config = new OpenXmlConfiguration
        {
            AutoFilter = true,
            FreezeRowCount = 1,
            TableStyles = TableStyles.None,
            EnableWriteNullValueCell = true, // NULL → a truly empty cell
            FastMode = false,                // FastMode buffers the whole zip in memory
            EnableAutoWidth = false,
            Culture = CultureInfo.InvariantCulture,
            DynamicColumns = Array.Empty<DynamicExcelColumn>(),
        };
        var sheets = new LazySheets(tables, config, progress, ct);
        try
        {
            using (var fs = new FileStream(part, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16))
                MiniExcel.SaveAs(fs, sheets, printHeader: true, excelType: ExcelType.XLSX, configuration: config);
            ct.ThrowIfCancellationRequested();
            bool bold = XlsxStylePatcher.MakeHeaderBold(part);
            File.Move(part, path, overwrite: true);
            progress.Files = 1;
            return new ExportResult { Rows = progress.Rows, Sheets = sheets.SheetCount, Files = { path }, Elapsed = sw.Elapsed, HeaderBold = bold };
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    internal static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Column width in Excel units, from the type and the header.</summary>
    private static double WidthFor(ColumnInfo c) => Math.Clamp(Math.Max(c.Name.Length + 3, c.Kind switch
    {
        ColumnKind.Date => 12,
        ColumnKind.DateTime => 20,
        ColumnKind.Time => 10,
        ColumnKind.Integer => 10,
        ColumnKind.Decimal or ColumnKind.Float => 13,
        _ => 18,
    }), 8, 60);

    /// <summary>
    /// Sheets handed to MiniExcel one at a time. A table's source is opened only when its sheet starts
    /// (one DB connection at a time), and its column formats are registered just before.
    /// </summary>
    private sealed class LazySheets : IDictionary<string, object>
    {
        private readonly IReadOnlyList<ExportTable> _tables;
        private readonly OpenXmlConfiguration _config;
        private readonly ExportProgress _progress;
        private readonly CancellationToken _ct;

        public LazySheets(IReadOnlyList<ExportTable> tables, OpenXmlConfiguration config, ExportProgress progress, CancellationToken ct)
        {
            _tables = tables;
            _config = config;
            _progress = progress;
            _ct = ct;
        }

        public int SheetCount { get; private set; }

        private IEnumerable<KeyValuePair<string, object>> Generate()
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int t = 0; t < _tables.Count; t++)
            {
                _ct.ThrowIfCancellationRequested();
                var table = _tables[t];
                _progress.CurrentTable = table.Name;
                using var source = table.Open(_ct);
                var shared = new SharedSource(source);
                // unique keys per table so same-named columns of different tables keep their own formats
                var keys = source.Columns.Select((_, i) => $"t{t}c{i}").ToArray();
                _config.DynamicColumns = _config.DynamicColumns.Concat(source.Columns.Select((c, i) => new DynamicExcelColumn(keys[i])
                {
                    Name = c.Name,
                    Width = WidthFor(c),
                    Format = c.Kind switch
                    {
                        ColumnKind.Date => "yyyy-mm-dd",
                        ColumnKind.DateTime => "yyyy-mm-dd hh:mm:ss",
                        _ => null,
                    },
                })).ToArray();
                int part = 1;
                do
                {
                    var name = ExportValues.SheetName(part == 1 ? table.Name : $"{table.Name} ({part})", used);
                    SheetCount++;
                    _progress.Sheets = SheetCount;
                    yield return new KeyValuePair<string, object>(name, new SheetReader(shared, keys, MaxDataRowsPerSheet, _progress, _ct));
                    part++;
                } while (shared.HasMore());
            }
        }

        public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => Generate().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        // MiniExcel only enumerates; the rest of IDictionary is not needed
        public int Count => throw new NotSupportedException();
        public ICollection<string> Keys => throw new NotSupportedException();
        public ICollection<object> Values => throw new NotSupportedException();
        public bool IsReadOnly => true;
        public object this[string key] { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public void Add(string key, object value) => throw new NotSupportedException();
        public bool ContainsKey(string key) => throw new NotSupportedException();
        public bool Remove(string key) => throw new NotSupportedException();
        public bool TryGetValue(string key, out object value) => throw new NotSupportedException();
        public void Add(KeyValuePair<string, object> item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Contains(KeyValuePair<string, object> item) => throw new NotSupportedException();
        public void CopyTo(KeyValuePair<string, object>[] array, int arrayIndex) => throw new NotSupportedException();
        public bool Remove(KeyValuePair<string, object> item) => throw new NotSupportedException();
    }

    /// <summary>Lets the next sheet continue where the previous one stopped, with one row of look-ahead.</summary>
    private sealed class SharedSource
    {
        private bool _peeked, _peekResult;
        public SharedSource(IExportRowSource source) => Source = source;
        public IExportRowSource Source { get; }

        public bool Next()
        {
            if (_peeked) { _peeked = false; return _peekResult; }
            return Source.Read();
        }

        public bool HasMore()
        {
            if (!_peeked) { _peekResult = Source.Read(); _peeked = true; }
            return _peekResult;
        }
    }

    /// <summary>The reader MiniExcel sees for one sheet: at most maxRows rows, values mapped for Excel.</summary>
    private sealed class SheetReader : DbDataReader
    {
        private readonly SharedSource _src;
        private readonly string[] _keys;
        private readonly int _maxRows;
        private readonly ExportProgress _progress;
        private readonly CancellationToken _ct;
        private int _rows;

        public SheetReader(SharedSource src, string[] keys, int maxRows, ExportProgress progress, CancellationToken ct)
        {
            _src = src;
            _keys = keys;
            _maxRows = maxRows;
            _progress = progress;
            _ct = ct;
        }

        public override bool Read()
        {
            _ct.ThrowIfCancellationRequested();
            if (_rows >= _maxRows || !_src.Next()) return false;
            _rows++;
            _progress.AddRow();
            return true;
        }

        public override int FieldCount => _keys.Length;
        public override string GetName(int ordinal) => _keys[ordinal];
        public override int GetOrdinal(string name) => Array.IndexOf(_keys, name);
        public override object GetValue(int ordinal) => ExportValues.ForExcel(_src.Source.GetValue(ordinal)) ?? DBNull.Value;
        public override bool IsDBNull(int ordinal) => GetValue(ordinal) is DBNull;
        public override int GetValues(object[] values)
        {
            int n = Math.Min(values.Length, FieldCount);
            for (int i = 0; i < n; i++) values[i] = GetValue(i);
            return n;
        }
        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => GetValue(GetOrdinal(name));
        public override Type GetFieldType(int ordinal) => typeof(object);
        public override string GetDataTypeName(int ordinal) => "object";
        public override bool HasRows => true;
        public override bool IsClosed => false;
        public override int Depth => 0;
        public override int RecordsAffected => -1;
        public override bool NextResult() => false;
        public override IEnumerator GetEnumerator() => throw new NotSupportedException();
        public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);
        public override byte GetByte(int ordinal) => Convert.ToByte(GetValue(ordinal));
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override char GetChar(int ordinal) => Convert.ToChar(GetValue(ordinal));
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);
        public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(GetValue(ordinal));
        public override double GetDouble(int ordinal) => Convert.ToDouble(GetValue(ordinal));
        public override float GetFloat(int ordinal) => Convert.ToSingle(GetValue(ordinal));
        public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);
        public override short GetInt16(int ordinal) => Convert.ToInt16(GetValue(ordinal));
        public override int GetInt32(int ordinal) => Convert.ToInt32(GetValue(ordinal));
        public override long GetInt64(int ordinal) => Convert.ToInt64(GetValue(ordinal));
        public override string GetString(int ordinal) => Convert.ToString(GetValue(ordinal), CultureInfo.InvariantCulture) ?? "";
    }
}
