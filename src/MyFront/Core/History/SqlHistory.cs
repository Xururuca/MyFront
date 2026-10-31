using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MyFront.Core.Db;

namespace MyFront.Core.History;

public sealed record HistoryEntry(DateTime Time, string? Database, string Info, string Sql);

/// <summary>
/// Append-only SQL history per account in %APPDATA%\MyFront\history\{accountId}.sql.
/// Password literals (IDENTIFIED BY '...', PASSWORD('...')) are masked before writing.
/// </summary>
public static partial class SqlHistory
{
    private const string Marker = "-- @@ ";
    private const int MaxStatementChars = 64 * 1024;
    private const long MaxFileBytes = 8 * 1024 * 1024;
    private static readonly object Gate = new();

    public static bool Enabled { get; set; } = true;

    private static string PathFor(string accountId) => Path.Combine(AppPaths.Dir("history"), accountId + ".sql");

    // IDENTIFIED BY [PASSWORD] '..' · IDENTIFIED VIA|WITH plugin USING|BY|AS [PASSWORD(] '..' · PASSWORD('..') · PASSWORD = '..'
    [GeneratedRegex(@"(?is)(\bIDENTIFIED\s+BY\s+(?:PASSWORD\s+)?|\bIDENTIFIED\s+(?:VIA|WITH)\s+\w+\s+(?:USING|BY|AS)\s+(?:PASSWORD\s*\(\s*)?|\bPASSWORD\s*\(\s*|\bPASSWORD\s*=\s*(?:PASSWORD\s*\(\s*)?)'(?:[^'\\]|\\.|'')*'")]
    private static partial Regex SecretLiteral();

    public static string MaskSecrets(string sql) => SecretLiteral().Replace(sql, "$1'***'");

    public static void Append(string accountId, string? database, StatementOutcome o)
    {
        if (!Enabled) return;
        string info = o.Error is not null ? "error: " + OneLine(o.Error.Message)
            : o.Cancelled ? "cancelled"
            : o.ResultSets > 0 ? $"{o.RowsReturned} rows"
            : $"{Math.Max(o.RecordsAffected, 0)} affected";
        Append(accountId, database, o.Statement.Text, $"{CellFormat.FormatDuration(o.Elapsed)} | {info}");
    }

    public static void Append(string accountId, string? database, string sql, string info)
    {
        if (!Enabled) return;
        try
        {
            var text = MaskSecrets(sql);
            if (text.Length > MaxStatementChars) text = text[..MaxStatementChars] + "\n-- (truncated)";
            var sb = new StringBuilder();
            sb.Append(Marker).Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
              .Append(" | ").Append(database ?? "").Append(" | ").Append(OneLine(info)).Append('\n')
              .Append(text.TrimEnd()).Append('\n');
            lock (Gate)
            {
                var path = PathFor(accountId);
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > MaxFileBytes)
                    File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
            }
        }
        catch (IOException) { /* history must never break query execution */ }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Newest first.</summary>
    public static List<HistoryEntry> Read(string accountId, int max = 5000)
    {
        var path = PathFor(accountId);
        var list = new List<HistoryEntry>();
        if (!File.Exists(path)) return list;
        string[] lines;
        lock (Gate) lines = File.ReadAllLines(path, Encoding.UTF8);

        DateTime time = default;
        string? db = null, info = "";
        var sql = new StringBuilder();
        bool inEntry = false;
        void Flush()
        {
            if (inEntry) list.Add(new HistoryEntry(time, db, info ?? "", sql.ToString().TrimEnd()));
            sql.Clear();
        }
        foreach (var line in lines)
        {
            if (line.StartsWith(Marker, StringComparison.Ordinal))
            {
                Flush();
                var parts = line[Marker.Length..].Split(" | ", 3);
                DateTime.TryParseExact(parts[0], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
                db = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
                info = parts.Length > 2 ? parts[2] : "";
                inEntry = true;
            }
            else if (inEntry)
            {
                sql.Append(line).Append('\n');
            }
        }
        Flush();
        list.Reverse();
        if (list.Count > max) list.RemoveRange(max, list.Count - max);
        return list;
    }

    private static string OneLine(string s) => s.Replace('\r', ' ').Replace('\n', ' ');
}
