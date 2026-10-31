using System.Diagnostics;
using System.Text;

namespace MyFront.Core.Export;

public sealed class CsvOptions
{
    public char Delimiter { get; set; } = ';';
    public char DecimalSeparator { get; set; } = ',';
    /// <summary>"utf-8-bom" (Excel-friendly), "utf-8" or "windows-1254".</summary>
    public string Encoding { get; set; } = "utf-8-bom";
    public bool Header { get; set; } = true;

    static CsvOptions() => System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public Encoding GetEncoding() => Encoding switch
    {
        "utf-8" => new UTF8Encoding(false),
        "windows-1254" => System.Text.Encoding.GetEncoding(1254),
        _ => new UTF8Encoding(true),
    };
}

/// <summary>Streams rows to CSV (RFC 4180 quoting). One table → one file; several → one file per table in a folder.</summary>
public static class CsvExporter
{
    public static Task<ExportResult> ExportAsync(string target, IReadOnlyList<ExportTable> tables, CsvOptions options, ExportProgress progress, CancellationToken ct) =>
        Task.Run(() => Export(target, tables, options, progress, ct), ct);

    /// <param name="target">File path for one table, folder for several.</param>
    public static ExportResult Export(string target, IReadOnlyList<ExportTable> tables, CsvOptions options, ExportProgress progress, CancellationToken ct)
    {
        if (tables.Count == 0) throw new ArgumentException("Nothing to export.");
        var sw = Stopwatch.StartNew();
        var files = new List<string>();
        bool toFolder = tables.Count > 1;
        if (toFolder) Directory.CreateDirectory(target);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
        {
            ct.ThrowIfCancellationRequested();
            progress.CurrentTable = table.Name;
            var path = toFolder ? Path.Combine(target, UniqueFileName(table.Name, usedNames) + ".csv") : target;
            WriteOne(path, table, options, progress, ct);
            files.Add(path);
            progress.Files = files.Count;
        }
        return new ExportResult { Rows = progress.Rows, Files = files, Elapsed = sw.Elapsed };
    }

    private static string UniqueFileName(string name, HashSet<string> used)
    {
        var baseName = ExportValues.FileName(name);
        var candidate = baseName;
        for (int n = 2; !used.Add(candidate); n++) candidate = $"{baseName} ({n})";
        return candidate;
    }

    private static void WriteOne(string path, ExportTable table, CsvOptions o, ExportProgress progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var part = path + ".part";
        try
        {
            using (var source = table.Open(ct))
            using (var w = new StreamWriter(part, false, o.GetEncoding(), 1 << 16))
            {
                var cols = source.Columns;
                if (o.Header)
                {
                    for (int i = 0; i < cols.Length; i++)
                    {
                        if (i > 0) w.Write(o.Delimiter);
                        WriteField(w, cols[i].Name, o.Delimiter);
                    }
                    w.Write("\r\n");
                }
                while (source.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    for (int i = 0; i < cols.Length; i++)
                    {
                        if (i > 0) w.Write(o.Delimiter);
                        WriteField(w, ExportValues.ForCsv(source.GetValue(i), cols[i].Kind, o.DecimalSeparator), o.Delimiter);
                    }
                    w.Write("\r\n");
                    progress.AddRow();
                }
            }
            File.Move(part, path, overwrite: true);
        }
        catch
        {
            ExcelExporter.TryDelete(part);
            throw;
        }
    }

    private static void WriteField(StreamWriter w, string s, char delimiter)
    {
        bool quote = s.Length > 0 && (s.IndexOf(delimiter) >= 0 || s.AsSpan().IndexOfAny('"', '\r', '\n') >= 0 || s[0] == ' ' || s[^1] == ' ');
        if (!quote)
        {
            w.Write(s);
            return;
        }
        w.Write('"');
        w.Write(s.Replace("\"", "\"\""));
        w.Write('"');
    }
}
