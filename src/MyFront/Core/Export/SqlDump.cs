using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MyFront.Core.Db;
using MySqlConnector;

namespace MyFront.Core.Export;

/// <summary>A table (structure, rows, triggers) or a view (CREATE VIEW) to dump.</summary>
public sealed record SqlDumpObject(string Name, bool IsView);

public sealed class SqlDumpOptions
{
    /// <summary>DROP TABLE IF EXISTS, CREATE TABLE (keys, foreign keys, partitions) and the table's triggers.</summary>
    public bool Structure { get; set; } = true;
    /// <summary>The rows, as multi-row INSERTs.</summary>
    public bool Data { get; set; } = true;
    /// <summary>Leaves out DEFINER=… so the importing user owns the triggers and views.</summary>
    public bool StripDefiner { get; set; } = true;
    /// <summary>An INSERT ends after about this many characters (well below the server's default max_allowed_packet).</summary>
    public int MaxInsertChars { get; set; } = 1 << 20;
}

/// <summary>
/// mysqldump-like .sql export, streamed from the server on its own connection: header (SET NAMES utf8mb4, unique /
/// foreign key checks off, sql_mode, UTC time zone), then per table DROP TABLE IF EXISTS + SHOW CREATE TABLE, the rows as
/// batched multi-row INSERTs and the triggers (after the rows, so they don't fire on import) in DELIMITER blocks; views as
/// CREATE VIEW; settings restored at the end. UTF-8 without BOM, written to "file.part" and renamed when complete.
/// </summary>
public static partial class SqlDump
{
    /// <summary>Session settings while a dump is imported (and how they are put back).</summary>
    public const string ImportSqlMode = "NO_AUTO_VALUE_ON_ZERO";

    public static Task<ExportResult> WriteAsync(ServerSession session, string database, IReadOnlyList<SqlDumpObject> objects, string path,
        SqlDumpOptions options, ExportProgress progress, CancellationToken ct) =>
        Task.Run(() => Write(session, database, objects, path, options, progress, ct), ct);

    public static ExportResult Write(ServerSession session, string database, IReadOnlyList<SqlDumpObject> objects, string path,
        SqlDumpOptions options, ExportProgress progress, CancellationToken ct)
    {
        if (objects.Count == 0) throw new ArgumentException("Nothing to export.");
        if (!options.Structure && !options.Data) throw new ArgumentException("Choose structure, data or both.");
        var sw = Stopwatch.StartNew();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var part = path + ".part";
        try
        {
            // the connection's default database = the dump's, so SHOW CREATE writes names without it
            using (var conn = session.OpenDedicatedAsync(database, ct).GetAwaiter().GetResult())
            using (var w = new StreamWriter(part, false, new UTF8Encoding(false), 1 << 16))
            {
                w.NewLine = "\n";
                Exec(conn, "SET SESSION time_zone = '+00:00'", ct); // TIMESTAMP values in UTC, imported in UTC
                w.Write(Header(session.ServerVersion, database, objects.Select(o => o.Name), DateTime.Now));
                foreach (var o in objects.Where(o => !o.IsView))
                {
                    ct.ThrowIfCancellationRequested();
                    progress.CurrentTable = o.Name;
                    WriteTable(conn, w, database, o.Name, options, progress, ct);
                }
                // views select from the tables, so they come after them
                if (options.Structure)
                    foreach (var v in objects.Where(o => o.IsView))
                    {
                        ct.ThrowIfCancellationRequested();
                        progress.CurrentTable = v.Name;
                        WriteView(conn, w, v.Name, options, ct);
                    }
                w.Write(Footer(DateTime.Now));
            }
            File.Move(part, path, overwrite: true);
        }
        catch
        {
            ExcelExporter.TryDelete(part);
            throw;
        }
        return new ExportResult { Rows = progress.Rows, Files = new() { path }, Elapsed = sw.Elapsed };
    }

    private static void Exec(MySqlConnection conn, string sql, CancellationToken ct)
    {
        using var cmd = new MySqlCommand(sql, conn);
        cmd.ExecuteNonQuery();
        ct.ThrowIfCancellationRequested();
    }

    private static string ShowCreate(MySqlConnection conn, string sql, int column)
    {
        using var cmd = new MySqlCommand(sql, conn);
        using var r = cmd.ExecuteReader();
        if (!r.Read() || r.IsDBNull(column)) throw new InvalidOperationException("The server returned no definition for: " + sql);
        return r.GetString(column);
    }

    /// <summary>Text inside a "-- " comment line: a name with a line break must not end the comment.</summary>
    private static string CommentText(string s) => s.Replace('\r', ' ').Replace('\n', ' ');

    public static string Header(string serverVersion, string database, IEnumerable<string> objects, DateTime when) =>
        $"""
        -- MyFront SQL dump
        -- Server: {CommentText(serverVersion)} · Database: {CommentText(database)}
        -- Objects: {CommentText(string.Join(", ", objects))}
        -- Date: {when.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}
        -- Import: right-click a database → Import SQL…, or File → Run SQL file

        SET NAMES utf8mb4;
        SET @OLD_UNIQUE_CHECKS=@@UNIQUE_CHECKS, UNIQUE_CHECKS=0;
        SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS, FOREIGN_KEY_CHECKS=0;
        SET @OLD_SQL_MODE=@@SQL_MODE, SQL_MODE='{ImportSqlMode}';
        SET @OLD_TIME_ZONE=@@TIME_ZONE, TIME_ZONE='+00:00';

        """;

    public static string Footer(DateTime when) =>
        $"""

        SET TIME_ZONE=@OLD_TIME_ZONE;
        SET SQL_MODE=@OLD_SQL_MODE;
        SET FOREIGN_KEY_CHECKS=@OLD_FOREIGN_KEY_CHECKS;
        SET UNIQUE_CHECKS=@OLD_UNIQUE_CHECKS;

        -- Dump completed {when.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}

        """;

    private static void WriteTable(MySqlConnection conn, StreamWriter w, string database, string table, SqlDumpOptions options,
        ExportProgress progress, CancellationToken ct)
    {
        var q = Ident.Quote(table);
        if (options.Structure)
        {
            var create = ShowCreate(conn, $"SHOW CREATE TABLE {q}", 1);
            w.Write($"\n--\n-- Table structure for {CommentText(q)}\n--\n\nDROP TABLE IF EXISTS {q};\n{create};\n");
        }
        if (options.Data) WriteRows(conn, w, table, options, progress, ct);
        if (options.Structure) WriteTriggers(conn, w, database, table, options, ct);
        w.Flush();
    }

    [GeneratedRegex(@"\b(VIRTUAL|STORED|PERSISTENT) GENERATED\b", RegexOptions.IgnoreCase)]
    private static partial Regex GeneratedColumn();

    private static void WriteRows(MySqlConnection conn, StreamWriter w, string table, SqlDumpOptions options, ExportProgress progress, CancellationToken ct)
    {
        var q = Ident.Quote(table);
        // generated columns are computed by the server; invisible ones (MySQL 8) need to be named
        var names = new List<string>();
        using (var cmd = new MySqlCommand($"SHOW FULL COLUMNS FROM {q}", conn))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                if (!GeneratedColumn().IsMatch(r.GetString("Extra"))) names.Add(r.GetString("Field"));
        if (names.Count == 0) return;
        var list = string.Join(", ", names.Select(Ident.Quote));
        var insert = $"INSERT INTO {q} ({list}) VALUES\n";
        w.Write($"\n--\n-- Data for {CommentText(q)}\n--\n\n");

        using var select = new MySqlCommand($"SELECT {list} FROM {q}", conn) { CommandTimeout = 0 };
        // KILL QUERY on cancel, so a long table scan stops at the server too
        using var reg = ct.Register(static c => { try { ((MySqlCommand)c!).Cancel(); } catch { /* best effort */ } }, select);
        using var reader = select.ExecuteReader();
        var cols = ColumnInfo.FromReader(reader);
        var row = new StringBuilder();
        int rowsInStatement = 0;
        long chars = 0;
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            row.Clear().Append('(');
            for (int i = 0; i < cols.Length; i++)
            {
                if (i > 0) row.Append(',');
                if (reader.IsDBNull(i)) row.Append("NULL");
                else if (cols[i].Kind == ColumnKind.Decimal) row.Append(reader.GetMySqlDecimal(i).ToString()); // exact, also beyond .NET decimal
                else AppendLiteral(row, CellFormat.Normalize(reader.GetValue(i)), cols[i].Kind);
            }
            row.Append(')');
            if (rowsInStatement > 0 && chars + row.Length > options.MaxInsertChars)
            {
                w.Write(";\n");
                rowsInStatement = 0;
            }
            if (rowsInStatement == 0) { w.Write(insert); chars = insert.Length; }
            else w.Write(",\n");
            w.Write(row);
            chars += row.Length + 2;
            rowsInStatement++;
            progress.AddRow();
        }
        if (rowsInStatement > 0) w.Write(";\n");
    }

    private static void WriteTriggers(MySqlConnection conn, StreamWriter w, string database, string table, SqlDumpOptions options, CancellationToken ct)
    {
        var names = new List<string>();
        using (var cmd = new MySqlCommand("""
            SELECT TRIGGER_NAME FROM information_schema.TRIGGERS
            WHERE EVENT_OBJECT_SCHEMA = @db AND EVENT_OBJECT_TABLE = @t ORDER BY ACTION_TIMING, EVENT_MANIPULATION, ACTION_ORDER
            """, conn))
        {
            cmd.Parameters.AddWithValue("@db", database);
            cmd.Parameters.AddWithValue("@t", table);
            using var r = cmd.ExecuteReader();
            while (r.Read()) names.Add(r.GetString(0));
        }
        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            string sqlMode, create;
            using (var cmd = new MySqlCommand($"SHOW CREATE TRIGGER {Ident.Quote(name)}", conn))
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read() || r.IsDBNull(2)) throw new InvalidOperationException($"The server returned no definition for trigger {name}.");
                sqlMode = r.IsDBNull(1) ? "" : r.GetString(1);
                create = r.GetString(2);
            }
            if (options.StripDefiner) create = StripDefiner(create);
            // the body is read with the sql_mode it was written in (as mysqldump does)
            w.Write($"\n--\n-- Trigger {CommentText(Ident.Quote(name))}\n--\n\n");
            w.Write($"SET SESSION SQL_MODE={Quote(sqlMode)};\nDELIMITER ;;\n{create} ;;\nDELIMITER ;\nSET SESSION SQL_MODE='{ImportSqlMode}';\n");
        }
    }

    private static void WriteView(MySqlConnection conn, StreamWriter w, string view, SqlDumpOptions options, CancellationToken ct)
    {
        var q = Ident.Quote(view);
        var create = ShowCreate(conn, $"SHOW CREATE VIEW {q}", 1);
        if (options.StripDefiner) create = StripDefiner(create);
        w.Write($"\n--\n-- View {CommentText(q)}\n--\n\nDROP VIEW IF EXISTS {q};\n{create};\n");
        w.Flush();
        ct.ThrowIfCancellationRequested();
    }

    [GeneratedRegex(@"^(\s*CREATE\s+(?:OR\s+REPLACE\s+)?(?:ALGORITHM\s*=\s*\w+\s+)?)DEFINER\s*=\s*(?:`(?:[^`]|``)*`|'(?:[^']|'')*'|[^\s@]+)(?:\s*@\s*(?:`(?:[^`]|``)*`|'(?:[^']|'')*'|\S+))?\s+",
        RegexOptions.IgnoreCase)]
    private static partial Regex Definer();

    /// <summary>"CREATE DEFINER=`u`@`h` TRIGGER …" → "CREATE TRIGGER …" (views: after ALGORITHM=…). Only the head is touched.</summary>
    public static string StripDefiner(string createStatement) => Definer().Replace(createStatement, "$1", 1);

    /// <summary>A string literal as mysqldump writes it (backslash escapes; the dump's sql_mode allows them).</summary>
    public static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        AppendQuoted(sb, s);
        return sb.ToString();
    }

    private static void AppendQuoted(StringBuilder sb, string s)
    {
        sb.Append('\'');
        foreach (var c in s)
        {
            switch (c)
            {
                case '\0': sb.Append("\\0"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\\': sb.Append("\\\\"); break;
                case '\'': sb.Append("\\'"); break;
                case '\u001A': sb.Append("\\Z"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('\'');
    }

    /// <summary>One value of a row as an SQL literal.</summary>
    public static string Literal(object? value, ColumnKind kind)
    {
        var sb = new StringBuilder();
        AppendLiteral(sb, value, kind);
        return sb.ToString();
    }

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static void AppendLiteral(StringBuilder sb, object? v, ColumnKind kind)
    {
        switch (v)
        {
            case null or DBNull: sb.Append("NULL"); break;
            case string s: AppendQuoted(sb, s); break;
            case byte[] b: sb.Append(b.Length == 0 ? "''" : "0x" + Convert.ToHexString(b)); break;
            case DateTime d:
                sb.Append('\'').Append(kind == ColumnKind.Date ? d.ToString("yyyy-MM-dd", Inv) : CellFormat.ToDisplay(d, ColumnKind.DateTime)).Append('\'');
                break;
            case TimeSpan t: sb.Append('\'').Append(CellFormat.FormatTime(t)).Append('\''); break;
            case MySqlDateTime m: sb.Append('\'').Append(CellFormat.ToDisplay(m, kind)).Append('\''); break; // zero dates
            case bool b: sb.Append(b ? '1' : '0'); break;
            case double d: sb.Append(d.ToString("R", Inv)); break;
            case float f: sb.Append(f.ToString("R", Inv)); break;
            case decimal m: sb.Append(m.ToString(Inv)); break;
            case IFormattable f: sb.Append(f.ToString(null, Inv)); break; // integers, BIT (ulong), YEAR
            default: AppendQuoted(sb, Convert.ToString(v, Inv) ?? ""); break;
        }
    }
}
