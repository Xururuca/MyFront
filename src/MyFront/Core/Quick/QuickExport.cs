using MyFront.Core.Db;
using MyFront.Core.Export;
using MySqlConnector;

namespace MyFront.Core.Quick;

/// <summary>
/// "Send the result straight to Excel": every statement that returns rows becomes a sheet; other statements
/// (SET @x, USE …) run in order on the same connection before the sheet that follows them.
/// </summary>
public static class QuickExport
{
    private static readonly HashSet<string> ResultKeywords = new(StringComparer.Ordinal)
    {
        "SELECT", "WITH", "SHOW", "DESC", "DESCRIBE", "EXPLAIN", "VALUES", "TABLE", "(",
    };

    public static bool ReturnsRows(string sql) => ResultKeywords.Contains(SqlClassifier.FirstKeyword(sql));

    public static Task<ExportResult> ToExcelAsync(ServerSession session, string? database, string name, string boundSql,
        IReadOnlyList<(string Name, object? Value)> parameters, string path, ExportProgress progress, CancellationToken ct) =>
        Task.Run(async () =>
        {
            var statements = SqlSplitter.Split(boundSql).Select(s => s.Text).ToList();
            foreach (var s in statements) session.EnsureAllowed(s);
            if (!statements.Any(ReturnsRows))
                throw new InvalidOperationException("The query returns no rows to export (no SELECT).");

            await using var conn = await session.OpenDedicatedAsync(database, ct);
            int next = 0; // statements before this index have run
            void RunUpTo(int index)
            {
                for (; next < index; next++)
                {
                    using var cmd = new MySqlCommand(statements[next], conn) { CommandTimeout = 0 };
                    foreach (var (n, v) in parameters)
                        if (statements[next].Contains(n, StringComparison.Ordinal)) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
                    using var reg = ct.Register(static c => { try { ((MySqlCommand)c!).Cancel(); } catch { } }, cmd);
                    cmd.ExecuteNonQuery();
                }
            }

            var tables = new List<ExportTable>();
            for (int i = 0; i < statements.Count; i++)
            {
                if (!ReturnsRows(statements[i])) continue;
                int index = i;
                tables.Add(new ExportTable(name, token =>
                {
                    RunUpTo(index);
                    next = index + 1;
                    return DbRowSource.OpenOn(conn, statements[index], token, parameters);
                }));
            }
            var result = ExcelExporter.Export(path, tables, progress, ct);
            RunUpTo(statements.Count); // trailing statements after the last SELECT
            return result;
        }, ct);
}
