using System.Text;
using ExcelDataReader;
using MiniExcelLibs;
using MiniExcelLibs.OpenXml;

namespace MyFront.Core.Import;

public enum ImportFileKind { Xlsx, Xls, Csv }

/// <summary>CSV reading options; null = detect.</summary>
public sealed class CsvReadOptions
{
    public char? Delimiter { get; set; }
    /// <summary>"utf-8", "windows-1254" or null (BOM / UTF-8 validity check).</summary>
    public string? Encoding { get; set; }
}

/// <summary>Forward-only rows of one sheet. Values are raw: string, double, DateTime, bool, TimeSpan or null.</summary>
public interface IImportReader : IDisposable
{
    IReadOnlyList<string> Headers { get; }
    bool Read();
    object?[] Current { get; }
    /// <summary>Row number in the file (1-based, counting the header row).</summary>
    int RowNumber { get; }
    /// <summary>0..1 when known, else -1.</summary>
    double Progress { get; }
}

public static class ImportFile
{
    static ImportFile() => System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static ImportFileKind KindOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".xls" => ImportFileKind.Xls,
        ".csv" or ".txt" or ".tsv" => ImportFileKind.Csv,
        _ => ImportFileKind.Xlsx,
    };

    public static List<string> GetSheets(string path)
    {
        switch (KindOf(path))
        {
            case ImportFileKind.Xlsx:
                return MiniExcel.GetSheetNames(path);
            case ImportFileKind.Xls:
            {
                using var stream = OpenShared(path);
                using var reader = ExcelReaderFactory.CreateBinaryReader(stream);
                var names = new List<string>();
                do names.Add(reader.Name); while (reader.NextResult());
                return names;
            }
            default:
                return new List<string> { Path.GetFileNameWithoutExtension(path) };
        }
    }

    /// <summary>Data rows (without the header) if the file says so cheaply, else null.</summary>
    public static long? CountRows(string path, string? sheet, bool headerRow)
    {
        try
        {
            if (KindOf(path) != ImportFileKind.Xlsx) return null;
            var names = MiniExcel.GetSheetNames(path);
            int i = sheet is null ? 0 : names.IndexOf(sheet);
            var dims = MiniExcel.GetSheetDimensions(path);
            if (i < 0 || i >= dims.Count) return null;
            long rows = dims[i].Rows.EndIndex;
            // some exporters declare the whole grid (A1:X1048576); only a smaller dimension is believable
            if (rows >= 1_048_576) return null;
            return Math.Max(0, rows - (headerRow ? 1 : 0));
        }
        catch { return null; }
    }

    public static IImportReader Open(string path, string? sheet, bool headerRow, CsvReadOptions? csv = null) => KindOf(path) switch
    {
        ImportFileKind.Xls => new XlsReader(path, sheet, headerRow),
        ImportFileKind.Csv => new CsvImportReader(path, headerRow, csv ?? new CsvReadOptions()),
        _ => new XlsxReader(path, sheet, headerRow, CountRows(path, sheet, headerRow)),
    };

    /// <summary>Exact number of non-empty data rows by reading the sheet (run it in the background).</summary>
    public static long CountRowsByReading(string path, string? sheet, bool headerRow, CsvReadOptions? csv, CancellationToken ct)
    {
        using var r = Open(path, sheet, headerRow, csv);
        long n = 0;
        while (r.Read())
        {
            if ((n & 4095) == 0) ct.ThrowIfCancellationRequested();
            if (!IsEmptyRow(r.Current)) n++;
        }
        return n;
    }

    internal static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16); // works while Excel has the file open

    /// <summary>Header texts: trimmed, empty → "Column N", duplicates → "name (2)".</summary>
    internal static List<string> CleanHeaders(IEnumerable<object?> raw, int count)
    {
        var list = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        foreach (var v in raw.Concat(Enumerable.Repeat<object?>(null, count)).Take(count))
        {
            i++;
            var name = (v switch { DateTime d => d.ToString("yyyy-MM-dd"), null => "", _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "" }).Trim();
            if (name.Length == 0) name = "Column " + i;
            var candidate = name;
            for (int n = 2; !used.Add(candidate); n++) candidate = $"{name} ({n})";
            list.Add(candidate);
        }
        return list;
    }

    internal static bool IsEmptyRow(object?[] row) =>
        row.All(v => v is null || (v is string s && s.Trim().Length == 0));
}

/// <summary>.xlsx through MiniExcel (streaming).</summary>
internal sealed class XlsxReader : IImportReader
{
    private readonly IEnumerator<IDictionary<string, object?>> _rows;
    private readonly string[] _keys;
    private readonly long? _total;
    private int _dataRows;

    public XlsxReader(string path, string? sheet, bool headerRow, long? totalDataRows)
    {
        _total = totalDataRows;
        Current = Array.Empty<object?>();
        var stream = ImportFile.OpenShared(path);
        var config = new OpenXmlConfiguration { FillMergedCells = false, IgnoreEmptyRows = false };
        _rows = MiniExcel.Query(stream, useHeaderRow: false, sheetName: sheet, excelType: ExcelType.XLSX, configuration: config)
            .Cast<IDictionary<string, object?>>().GetEnumerator();
        _stream = stream;
        if (!_rows.MoveNext())
        {
            _keys = Array.Empty<string>();
            Headers = Array.Empty<string>();
            _ended = true;
            return;
        }
        _keys = _rows.Current.Keys.ToArray();
        if (headerRow)
        {
            Headers = ImportFile.CleanHeaders(_keys.Select(k => _rows.Current[k]), _keys.Length);
            RowNumber = 1;
        }
        else
        {
            Headers = _keys.Select(k => "Column " + k).ToList();
            _pending = true; // the first row is data
        }
        Current = Array.Empty<object?>();
    }

    private readonly Stream _stream;
    private bool _pending, _ended;

    public IReadOnlyList<string> Headers { get; }
    public object?[] Current { get; private set; }
    public int RowNumber { get; private set; }
    public double Progress => _total is > 0 ? Math.Min(1, _dataRows / (double)_total) : -1;

    public bool Read()
    {
        if (_ended) return false;
        if (_pending) _pending = false;
        else if (!_rows.MoveNext()) { _ended = true; return false; }
        var r = _rows.Current;
        var row = new object?[_keys.Length];
        for (int i = 0; i < _keys.Length; i++) row[i] = r.TryGetValue(_keys[i], out var v) ? v : null;
        Current = row;
        RowNumber++;
        _dataRows++;
        return true;
    }

    public void Dispose()
    {
        _rows.Dispose();
        _stream.Dispose();
    }
}

/// <summary>Legacy .xls through ExcelDataReader.</summary>
internal sealed class XlsReader : IImportReader
{
    private readonly FileStream _stream;
    private readonly IExcelDataReader _reader;
    private readonly int _columns;
    private readonly long _total;
    private bool _pending, _ended;

    public XlsReader(string path, string? sheet, bool headerRow)
    {
        _stream = ImportFile.OpenShared(path);
        _reader = ExcelReaderFactory.CreateBinaryReader(_stream);
        if (sheet is not null)
            while (_reader.Name != sheet)
                if (!_reader.NextResult()) throw new InvalidOperationException($"Sheet \"{sheet}\" not found.");
        _columns = _reader.FieldCount;
        _total = _reader.RowCount - (headerRow ? 1 : 0);
        Current = Array.Empty<object?>();
        if (!_reader.Read()) { _ended = true; Headers = Array.Empty<string>(); return; }
        var first = Values();
        if (headerRow)
        {
            Headers = ImportFile.CleanHeaders(first, _columns);
            RowNumber = 1;
        }
        else
        {
            Headers = Enumerable.Range(1, _columns).Select(i => "Column " + i).ToList();
            Current = first;
            _pending = true;
        }
    }

    private object?[] Values()
    {
        var row = new object?[_columns];
        for (int i = 0; i < _columns; i++)
        {
            var v = _reader.GetValue(i);
            row[i] = v is DBNull ? null : v;
        }
        return row;
    }

    public IReadOnlyList<string> Headers { get; }
    public object?[] Current { get; private set; }
    public int RowNumber { get; private set; }
    public double Progress => _total > 0 ? Math.Min(1, (RowNumber - 1) / (double)_total) : -1;

    public bool Read()
    {
        if (_ended) return false;
        if (_pending) { _pending = false; RowNumber++; return true; }
        if (!_reader.Read()) { _ended = true; return false; }
        Current = Values();
        RowNumber++;
        return true;
    }

    public void Dispose()
    {
        _reader.Dispose();
        _stream.Dispose();
    }
}

/// <summary>CSV with quotes, doubled quotes and line breaks inside quotes. Delimiter and encoding auto-detected.</summary>
internal sealed class CsvImportReader : IImportReader
{
    private readonly FileStream _stream;
    private readonly StreamReader _reader;
    private readonly char _delimiter;
    private readonly long _length;
    private List<string>? _pendingRow;
    private bool _ended;

    public CsvImportReader(string path, bool headerRow, CsvReadOptions options)
    {
        _stream = ImportFile.OpenShared(path);
        _length = Math.Max(1, _stream.Length);
        var encoding = options.Encoding switch
        {
            "utf-8" => new UTF8Encoding(false),
            "windows-1254" => Encoding.GetEncoding(1254),
            _ => DetectEncoding(path),
        };
        _reader = new StreamReader(_stream, encoding, detectEncodingFromByteOrderMarks: true, 1 << 16);
        var firstRaw = ReadRawRecord();
        _delimiter = options.Delimiter ?? SniffDelimiter(firstRaw);
        var first = firstRaw is null ? null : Split(firstRaw);
        Current = Array.Empty<object?>();
        if (first is null) { _ended = true; Headers = Array.Empty<string>(); return; }
        if (headerRow)
        {
            Headers = ImportFile.CleanHeaders(first, first.Count);
            RowNumber = 1;
        }
        else
        {
            Headers = Enumerable.Range(1, first.Count).Select(i => "Column " + i).ToList();
            _pendingRow = first;
        }
    }

    public char Delimiter => _delimiter;
    public IReadOnlyList<string> Headers { get; }
    public object?[] Current { get; private set; }
    public int RowNumber { get; private set; }
    public double Progress => Math.Min(1, _stream.Position / (double)_length);

    public static Encoding DetectEncoding(string path)
    {
        using var fs = ImportFile.OpenShared(path);
        var buf = new byte[Math.Min(fs.Length, 1 << 20)];
        int n = fs.Read(buf, 0, buf.Length);
        if (n >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) return new UTF8Encoding(true);
        if (n >= 2 && ((buf[0] == 0xFF && buf[1] == 0xFE) || (buf[0] == 0xFE && buf[1] == 0xFF))) return Encoding.Unicode;
        try
        {
            // a cut-off multi-byte sequence at the end of the sample is fine
            int end = n;
            while (end > 0 && end > n - 4 && (buf[end - 1] & 0xC0) == 0x80) end--;
            if (end > 0 && buf[end - 1] >= 0xC0) end--;
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(buf, 0, end);
            return new UTF8Encoding(false);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1254); // Turkish Excel's default for "CSV (comma delimited)"
        }
    }

    private static char SniffDelimiter(string? line)
    {
        if (line is null) return ';';
        var counts = new Dictionary<char, int> { [';'] = 0, [','] = 0, ['\t'] = 0, ['|'] = 0 };
        bool quoted = false;
        foreach (var c in line)
        {
            if (c == '"') quoted = !quoted;
            else if (!quoted && counts.ContainsKey(c)) counts[c]++;
        }
        var best = counts.OrderByDescending(kv => kv.Value).First();
        return best.Value == 0 ? ';' : best.Key;
    }

    /// <summary>Reads one logical record (may span lines inside quotes) as raw text; null at the end.</summary>
    private string? ReadRawRecord()
    {
        var line = _reader.ReadLine();
        if (line is null) return null;
        var sb = new StringBuilder(line);
        while (CountQuotes(sb) % 2 == 1)
        {
            var next = _reader.ReadLine();
            if (next is null) break;
            sb.Append('\n').Append(next);
        }
        return sb.ToString();
    }

    private static int CountQuotes(StringBuilder sb)
    {
        int n = 0;
        for (int i = 0; i < sb.Length; i++) if (sb[i] == '"') n++;
        return n;
    }

    private List<string> Split(string record)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < record.Length; i++)
        {
            char c = record[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < record.Length && record[i + 1] == '"') { sb.Append('"'); i++; }
                    else quoted = false;
                }
                else sb.Append(c);
            }
            else if (c == '"' && sb.Length == 0) quoted = true;
            else if (c == _delimiter) { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }

    public bool Read()
    {
        if (_ended) return false;
        List<string>? fields;
        if (_pendingRow is not null) { fields = _pendingRow; _pendingRow = null; }
        else
        {
            var raw = ReadRawRecord();
            if (raw is null) { _ended = true; return false; }
            fields = Split(raw);
        }
        var row = new object?[Headers.Count];
        for (int i = 0; i < row.Length && i < fields.Count; i++) row[i] = fields[i].Length == 0 ? null : fields[i];
        Current = row;
        RowNumber++;
        return true;
    }

    public void Dispose()
    {
        _reader.Dispose();
        _stream.Dispose();
    }
}
