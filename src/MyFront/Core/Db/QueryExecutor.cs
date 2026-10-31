using System.Diagnostics;
using MyFront.Core.History;
using MySqlConnector;

namespace MyFront.Core.Db;

/// <summary>One result set as it streams in. The grid reads <see cref="Rows"/> while the worker appends.</summary>
public sealed class ResultSetData
{
    public ResultSetData(int statementIndex, ColumnInfo[] columns, string sql)
    {
        StatementIndex = statementIndex;
        Columns = columns;
        Sql = sql;
    }

    public int StatementIndex { get; }
    public string Sql { get; }
    public ColumnInfo[] Columns { get; }
    public RowStore Rows { get; } = new();
    public TimeSpan ExecTime { get; internal set; }
    public TimeSpan FetchTime { get; internal set; }
    public volatile bool Completed;
    public volatile bool Cancelled;
}

public sealed class StatementOutcome
{
    public int Index { get; init; }
    public SqlStatement Statement { get; init; }
    public long RecordsAffected { get; set; } = -1;
    public int RowsReturned { get; set; }
    public int ResultSets { get; set; }
    public TimeSpan Elapsed { get; set; }
    public Exception? Error { get; set; }
    public bool Cancelled { get; set; }
}

public interface IQueryListener
{
    /// <summary>Worker thread: a result set started; rows keep arriving in rs.Rows.</summary>
    void ResultSetStarted(ResultSetData rs);
    /// <summary>Worker thread: a statement finished (ok, error or cancelled).</summary>
    void StatementFinished(StatementOutcome outcome);
}

/// <summary>Runs statements one by one on a dedicated connection, off the UI thread.</summary>
public static class QueryExecutor
{
    /// <returns>All outcomes; stops at the first error or on cancel.</returns>
    /// <param name="parameters">Values for @qpN markers (quick query placeholders); bound, never concatenated.</param>
    public static Task<List<StatementOutcome>> RunAsync(ServerSession session, MySqlConnection conn,
        IReadOnlyList<SqlStatement> statements, IQueryListener listener, CancellationToken ct, string? databaseForHistory = null,
        IReadOnlyList<(string Name, object? Value)>? parameters = null)
    {
        return Task.Run(() =>
        {
            var outcomes = new List<StatementOutcome>();
            for (int i = 0; i < statements.Count; i++)
            {
                var outcome = RunOne(session, conn, i, statements[i], listener, ct, parameters);
                outcomes.Add(outcome);
                SqlHistory.Append(session.Account.Id, databaseForHistory, outcome);
                listener.StatementFinished(outcome);
                if (outcome.Error is not null || outcome.Cancelled || ct.IsCancellationRequested) break;
            }
            return outcomes;
        }, CancellationToken.None);
    }

    private static StatementOutcome RunOne(ServerSession session, MySqlConnection conn, int index, SqlStatement stmt,
        IQueryListener listener, CancellationToken ct, IReadOnlyList<(string Name, object? Value)>? parameters)
    {
        var outcome = new StatementOutcome { Index = index, Statement = stmt };
        var sw = Stopwatch.StartNew();
        ResultSetData? current = null;
        try
        {
            session.EnsureAllowed(stmt.Text);
            using var cmd = new MySqlCommand(stmt.Text, conn) { CommandTimeout = 0 };
            if (parameters is not null)
                foreach (var (name, value) in parameters)
                    if (stmt.Text.Contains(name, StringComparison.Ordinal)) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            // MySqlConnector's Cancel() opens a second connection and sends KILL QUERY <id>
            using var reg = ct.Register(static c => { try { ((MySqlCommand)c!).Cancel(); } catch { } }, cmd);
            using var reader = cmd.ExecuteReader();
            var execTime = sw.Elapsed;
            do
            {
                if (reader.FieldCount <= 0) continue;
                var fetch = Stopwatch.StartNew();
                current = new ResultSetData(index, ColumnInfo.FromReader(reader), stmt.Text) { ExecTime = execTime };
                listener.ResultSetStarted(current);
                int n = reader.FieldCount;
                var interner = new ValueInterner(n);
                while (reader.Read())
                {
                    var row = new object?[n];
                    reader.GetValues(row!);
                    for (int j = 0; j < n; j++)
                        row[j] = interner.Intern(j, CellFormat.Normalize(row[j]));
                    current.Rows.Add(row);
                    if (ct.IsCancellationRequested) break;
                }
                current.FetchTime = fetch.Elapsed;
                current.Cancelled = ct.IsCancellationRequested;
                current.Completed = true;
                outcome.ResultSets++;
                outcome.RowsReturned += current.Rows.Count;
                current = null;
            } while (!ct.IsCancellationRequested && reader.NextResult());

            outcome.RecordsAffected = reader.RecordsAffected;
            outcome.Cancelled = ct.IsCancellationRequested;
        }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            _ = ex;
            outcome.Cancelled = true;
        }
        catch (Exception ex)
        {
            outcome.Error = ex;
        }
        finally
        {
            if (current is not null)
            {
                current.Cancelled = outcome.Cancelled;
                current.Completed = true;
            }
            outcome.Elapsed = sw.Elapsed;
        }
        return outcome;
    }

    /// <summary>The database the connection is currently in (after USE statements).</summary>
    public static async Task<string?> CurrentDatabaseAsync(MySqlConnection conn, CancellationToken ct = default)
    {
        await using var cmd = new MySqlCommand("SELECT DATABASE()", conn);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? null : Convert.ToString(v);
    }
}
