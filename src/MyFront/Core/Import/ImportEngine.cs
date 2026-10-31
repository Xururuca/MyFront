using System.Diagnostics;
using System.Text;
using MyFront.Core.Db;
using MySqlConnector;

namespace MyFront.Core.Import;

public enum ImportMode
{
    Insert,
    /// <summary>Empty the table first (DELETE inside the transaction, or real TRUNCATE when chosen), then insert.</summary>
    Replace,
    /// <summary>INSERT … ON DUPLICATE KEY UPDATE.</summary>
    Upsert,
    InsertIgnore,
}

public sealed class ImportOptions
{
    public ImportMode Mode { get; set; } = ImportMode.Insert;
    /// <summary>Replace mode only: TRUNCATE (fast, auto-commits, cannot be undone) instead of DELETE in the transaction.</summary>
    public bool RealTruncate { get; set; }
    /// <summary>Keep going when rows fail; they are collected in <see cref="ImportResult.FailedRows"/>.</summary>
    public bool ContinueOnError { get; set; }
    public NumberFormat Numbers { get; set; } = NumberFormat.Turkish;
    public bool Trim { get; set; } = true;
    /// <summary>Rows per INSERT (also capped by size); bigger batches mean fewer round trips to remote servers.</summary>
    public int BatchSize { get; set; } = 5000;
}

/// <summary>Source column index → target column name.</summary>
public sealed record ColumnMap(int SourceIndex, string Target);

public sealed class FailedRow
{
    public required int RowNumber { get; init; }
    public required object?[] Values { get; init; }
    public required string Error { get; init; }
}

/// <summary>Live counters (one writer thread, read by the UI).</summary>
public sealed class ImportProgress
{
    private long _read, _inserted, _updated, _unchanged, _skipped, _failed;
    public long Read => Interlocked.Read(ref _read);
    public long Inserted => Interlocked.Read(ref _inserted);
    public long Updated => Interlocked.Read(ref _updated);
    public long Unchanged => Interlocked.Read(ref _unchanged);
    public long Skipped => Interlocked.Read(ref _skipped);
    public long Failed => Interlocked.Read(ref _failed);
    public volatile string Phase = "";
    public double Fraction { get; internal set; } = -1;
    internal void AddRead() => Interlocked.Increment(ref _read);
    internal void Add(long inserted, long updated, long unchanged, long skipped)
    {
        Interlocked.Add(ref _inserted, inserted);
        Interlocked.Add(ref _updated, updated);
        Interlocked.Add(ref _unchanged, unchanged);
        Interlocked.Add(ref _skipped, skipped);
    }
    internal void AddFailed() => Interlocked.Increment(ref _failed);
}

public sealed class ImportResult
{
    public long Rows { get; init; }
    public long Inserted { get; init; }
    public long Updated { get; init; }
    public long Unchanged { get; init; }
    public long Skipped { get; init; }
    public long Failed { get; init; }
    public long EmptyRows { get; init; }
    public List<FailedRow> FailedRows { get; init; } = new();
    public IReadOnlyList<string> SourceHeaders { get; init; } = Array.Empty<string>();
    public TimeSpan Elapsed { get; init; }
    public bool Committed { get; init; }
    public bool Cancelled { get; init; }
    /// <summary>Why the import stopped (everything was rolled back), or null.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Batched, parameterized multi-row INSERTs inside one transaction on a dedicated connection.
/// A failing batch is retried row by row to find (and, with ContinueOnError, skip) the bad rows;
/// cancel or a stopping error rolls everything back.
/// </summary>
public static class ImportEngine
{
    private const int DefaultMaxBatchChars = 4_000_000;

    // errors caused by the row's data (the statement is rolled back, the transaction survives)
    private static readonly HashSet<int> DataErrors = new()
    {
        1048, 1062, 1136, 1264, 1265, 1292, 1364, 1366, 1367, 1406, 1411, 1416, 1451, 1452, 1690, 3819, 4025, 1263, 1366, 4078,
    };

    private sealed class AbortException : Exception
    {
        public AbortException(string message) : base(message) { }
    }

    public static Task<ImportResult> RunAsync(ServerSession session, string database, string table, IReadOnlyList<ColumnMap> map,
        Func<IImportReader> open, ImportOptions options, ImportProgress progress, CancellationToken ct) =>
        Task.Run(() => Run(session, database, table, map, open, options, progress, ct), CancellationToken.None);

    public static ImportResult Run(ServerSession session, string database, string table, IReadOnlyList<ColumnMap> map,
        Func<IImportReader> open, ImportOptions o, ImportProgress progress, CancellationToken ct)
    {
        if (map.Count == 0) throw new InvalidOperationException("Map at least one column.");
        var sw = Stopwatch.StartNew();
        session.Schema.Invalidate(database, table);
        var info = session.Schema.GetTableAsync(database, table, ct).GetAwaiter().GetResult();
        var columns = map.Select(m => info.Columns.FirstOrDefault(c => c.Name == m.Target)
                                      ?? throw new InvalidOperationException($"Column {m.Target} does not exist in {table}.")).ToList();
        var targets = columns.Select(TargetColumn.From).ToList();
        // NULL into a NOT NULL column that has a default (or auto_increment) → DEFAULT
        var useDefault = columns.Select(c => !c.Nullable && (c.Default is not null || c.IsAutoIncrement || c.Extra.Contains("DEFAULT_GENERATED", StringComparison.OrdinalIgnoreCase))).ToArray();

        var mapped = map.Select(m => m.Target).ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<string>? key = null;
        if (o.Mode == ImportMode.Upsert)
        {
            key = info.Indexes.Where(i => i.Unique).OrderBy(i => i.Name == "PRIMARY" ? 0 : 1)
                .Select(i => (IReadOnlyList<string>)i.Columns).FirstOrDefault(cols => cols.All(mapped.Contains))
                ?? throw new InvalidOperationException("Upsert needs a primary or unique key whose columns are all mapped.");
        }
        int[] keyIdx = key?.Select(k => map.ToList().FindIndex(m => m.Target == k)).ToArray() ?? Array.Empty<int>();

        var target = Ident.Quote(database, table);
        var head = (o.Mode == ImportMode.InsertIgnore ? "INSERT IGNORE INTO " : "INSERT INTO ") + target +
                   " (" + string.Join(", ", map.Select(m => Ident.Quote(m.Target))) + ") VALUES ";
        string tail = "";
        if (o.Mode == ImportMode.Upsert)
        {
            var update = map.Where(m => !key!.Contains(m.Target)).Select(m => m.Target).ToList();
            if (update.Count == 0) update.Add(key![0]);
            bool alias = !session.IsMariaDb && session.Version >= new Version(8, 0, 19); // VALUES() is deprecated in MySQL 8
            tail = alias
                ? " AS new ON DUPLICATE KEY UPDATE " + string.Join(", ", update.Select(c => $"{Ident.Quote(c)} = new.{Ident.Quote(c)}"))
                : " ON DUPLICATE KEY UPDATE " + string.Join(", ", update.Select(c => $"{Ident.Quote(c)} = VALUES({Ident.Quote(c)})"));
        }
        session.EnsureAllowed(head + "(1)");

        var failed = new List<FailedRow>();
        long rows = 0, emptyRows = 0;
        (Task<int> Task, MySqlCommand Cmd, List<(int Row, object?[] Raw, object?[] Values)> Items, long Existing)? pending = null;
        int maxBatchChars = DefaultMaxBatchChars;
        IReadOnlyList<string> headers = Array.Empty<string>();
        using var conn = session.OpenDedicatedAsync(database, ct, compress: true).GetAwaiter().GetResult();
        MySqlTransaction? tx = null;
        try
        {
            using (var mp = new MySqlCommand("SELECT @@max_allowed_packet", conn))
                maxBatchChars = (int)Math.Clamp(Convert.ToInt64(mp.ExecuteScalar()) / 4, 200_000, DefaultMaxBatchChars);
            if (o.Mode == ImportMode.Replace && o.RealTruncate)
            {
                progress.Phase = "Truncating table…";
                Exec(conn, null, $"TRUNCATE TABLE {target}", ct);
            }
            tx = conn.BeginTransaction();
            if (o.Mode == ImportMode.Replace && !o.RealTruncate)
            {
                progress.Phase = "Deleting existing rows…";
                Exec(conn, tx, $"DELETE FROM {target}", ct);
            }

            progress.Phase = "Importing…";
            using var reader = open();
            headers = reader.Headers;
            var batch = new List<(int Row, object?[] Raw, object?[] Values)>(o.BatchSize);
            int chars = 0;
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                var raw = reader.Current;
                if (ImportFile.IsEmptyRow(raw)) { emptyRows++; continue; }
                rows++;
                progress.AddRead();
                progress.Fraction = reader.Progress;

                var values = new object?[map.Count];
                string? error = null;
                for (int i = 0; i < map.Count && error is null; i++)
                {
                    var source = map[i].SourceIndex < raw.Length ? raw[map[i].SourceIndex] : null;
                    values[i] = ValueConverter.Convert(source, targets[i], o.Numbers, o.Trim, out error);
                    if (error is null && values[i] is null && !targets[i].Nullable && !useDefault[i])
                        error = $"{targets[i].Name}: empty, but the column is NOT NULL and has no default";
                    chars += values[i] is string s ? s.Length + 12 : 24;
                }
                if (error is not null)
                {
                    RowFailed(reader.RowNumber, raw, error);
                    continue;
                }
                batch.Add((reader.RowNumber, raw, values));
                if (batch.Count >= o.BatchSize || chars > maxBatchChars)
                {
                    Flush(batch);
                    batch = new List<(int Row, object?[] Raw, object?[] Values)>(o.BatchSize); // the old list is still in flight
                    chars = 0;
                }
            }
            Flush(batch);
            CompletePending();
            ct.ThrowIfCancellationRequested();
            progress.Phase = "Committing…";
            tx.Commit();
            progress.Phase = "Done";
            return Result(committed: true, cancelled: false, error: null);
        }
        catch (Exception ex) when (ex is OperationCanceledException || ct.IsCancellationRequested)
        {
            DrainPending();
            SafeRollback(tx);
            return Result(committed: false, cancelled: true, error: "Cancelled — everything was rolled back.");
        }
        catch (AbortException ex)
        {
            DrainPending();
            SafeRollback(tx);
            return Result(committed: false, cancelled: false, error: ex.Message + " — everything was rolled back.");
        }
        catch (Exception ex)
        {
            DrainPending();
            SafeRollback(tx);
            var msg = ex is MySqlException m ? $"SQL error {m.Number}: {m.Message}" : ex.Message;
            return Result(committed: false, cancelled: false, error: msg + " — everything was rolled back.");
        }

        ImportResult Result(bool committed, bool cancelled, string? error) => new ImportResult
        {
            Rows = rows,
            Inserted = committed ? progress.Inserted : 0,
            Updated = committed ? progress.Updated : 0,
            Unchanged = committed ? progress.Unchanged : 0,
            Skipped = committed ? progress.Skipped : 0,
            Failed = progress.Failed,
            EmptyRows = emptyRows,
            FailedRows = failed,
            SourceHeaders = headers,
            Elapsed = sw.Elapsed,
            Committed = committed,
            Cancelled = cancelled,
            Error = error,
        };

        // wait for (and ignore) a statement still running when the import stops
        void DrainPending()
        {
            if (pending is not { } p) return;
            pending = null;
            try { p.Task.GetAwaiter().GetResult(); } catch { }
            p.Cmd.Dispose();
        }

        void RowFailed(int rowNumber, object?[] raw, string error)
        {
            failed.Add(new FailedRow { RowNumber = rowNumber, Values = raw, Error = error });
            progress.AddFailed();
            if (!o.ContinueOnError) throw new AbortException($"Row {rowNumber}: {error}");
        }

        // One statement is in flight while the next batch is read, converted and built (pipelining):
        // the import then takes about as long as the slower of "reading the file" and "the server inserting".
        void Flush(List<(int Row, object?[] Raw, object?[] Values)> items)
        {
            if (items.Count == 0) return;
            var cmd = Build(items);
            CompletePending();
            long existing = o.Mode == ImportMode.Upsert ? CountExisting(items) : 0;
            pending = (cmd.ExecuteNonQueryAsync(ct), cmd, items, existing);
        }

        void CompletePending()
        {
            if (pending is not { } p) return;
            pending = null;
            try
            {
                long affected = p.Task.GetAwaiter().GetResult();
                Account(p.Items.Count, affected, p.Existing);
            }
            catch (MySqlException ex) when (DataErrors.Contains(ex.Number) && !ct.IsCancellationRequested)
            {
                // The failed statement was rolled back on its own. Split it in halves until the bad rows are
                // isolated: one bad row in 1,000 costs ~20 statements instead of 1,000 round trips.
                Bisect(p.Items, ex);
            }
            finally { p.Cmd.Dispose(); }
        }

        void Bisect(List<(int Row, object?[] Raw, object?[] Values)> items, MySqlException error)
        {
            if (items.Count == 1)
            {
                RowFailed(items[0].Row, items[0].Raw, $"SQL error {error.Number}: {error.Message}");
                return;
            }
            int mid = items.Count / 2;
            foreach (var half in new[] { items.GetRange(0, mid), items.GetRange(mid, items.Count - mid) })
            {
                ct.ThrowIfCancellationRequested();
                long existing = o.Mode == ImportMode.Upsert ? CountExisting(half) : 0;
                try { Account(half.Count, Execute(half), existing); }
                catch (MySqlException ex) when (DataErrors.Contains(ex.Number)) { Bisect(half, ex); }
            }
        }

        long Execute(List<(int Row, object?[] Raw, object?[] Values)> items)
        {
            using var cmd = Build(items);
            using var reg = ct.Register(static c => { try { ((MySqlCommand)c!).Cancel(); } catch { } }, cmd);
            return cmd.ExecuteNonQuery(); // dedicated connections report affected (changed) rows
        }

        MySqlCommand Build(List<(int Row, object?[] Raw, object?[] Values)> items)
        {
            var sql = new StringBuilder(head, head.Length + items.Count * map.Count * 8);
            var cmd = new MySqlCommand { Connection = conn, Transaction = tx, CommandTimeout = 0 };
            int p = 0;
            for (int r = 0; r < items.Count; r++)
            {
                sql.Append(r == 0 ? "(" : ",(");
                var v = items[r].Values;
                for (int c = 0; c < v.Length; c++)
                {
                    if (c > 0) sql.Append(',');
                    if (v[c] is null && useDefault[c]) { sql.Append("DEFAULT"); continue; }
                    var name = "@p" + p++;
                    sql.Append(name);
                    cmd.Parameters.AddWithValue(name, v[c] ?? DBNull.Value);
                }
                sql.Append(')');
            }
            sql.Append(tail);
            cmd.CommandText = sql.ToString();
            return cmd;
        }

        long CountExisting(List<(int Row, object?[] Raw, object?[] Values)> items)
        {
            var sql = new StringBuilder("SELECT COUNT(*) FROM ").Append(target).Append(" WHERE (")
                .Append(string.Join(", ", key!.Select(Ident.Quote))).Append(") IN (");
            using var cmd = new MySqlCommand { Connection = conn, Transaction = tx };
            int p = 0;
            for (int r = 0; r < items.Count; r++)
            {
                sql.Append(r == 0 ? "(" : ",(");
                for (int k = 0; k < keyIdx.Length; k++)
                {
                    if (k > 0) sql.Append(',');
                    var name = "@k" + p++;
                    sql.Append(name);
                    cmd.Parameters.AddWithValue(name, items[r].Values[keyIdx[k]] ?? DBNull.Value);
                }
                sql.Append(')');
            }
            cmd.CommandText = sql.Append(')').ToString();
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        void Account(int n, long affected, long existing)
        {
            switch (o.Mode)
            {
                case ImportMode.InsertIgnore:
                    progress.Add(affected, 0, 0, n - affected);
                    break;
                case ImportMode.Upsert:
                {
                    // affected rows: 1 per insert, 2 per changed row, 0 per unchanged row
                    long inserted = Math.Max(0, n - existing);
                    long updated = Math.Clamp((affected - inserted) / 2, 0, existing);
                    progress.Add(inserted, updated, Math.Max(0, existing - updated), 0);
                    break;
                }
                default:
                    progress.Add(affected, 0, 0, 0);
                    break;
            }
        }
    }

    private static void Exec(MySqlConnection conn, MySqlTransaction? tx, string sql, CancellationToken ct)
    {
        using var cmd = new MySqlCommand(sql, conn, tx) { CommandTimeout = 0 };
        using var reg = ct.Register(static c => { try { ((MySqlCommand)c!).Cancel(); } catch { } }, cmd);
        cmd.ExecuteNonQuery();
    }

    private static void SafeRollback(MySqlTransaction? tx)
    {
        try { tx?.Rollback(); } catch { /* connection may be gone; the server rolls back on disconnect */ }
    }

    /// <summary>CREATE TABLE for the wizard's "new table" option (utf8mb4, every column NULL-able).</summary>
    public static string CreateTableSql(string database, string table, IEnumerable<InferredColumn> columns)
    {
        var cols = columns.Where(c => c.Include).ToList();
        if (cols.Count == 0) throw new InvalidOperationException("Choose at least one column.");
        var dup = cols.GroupBy(c => c.Name.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) throw new InvalidOperationException($"Column name \"{dup.Key}\" is used twice.");
        foreach (var c in cols)
            if (!System.Text.RegularExpressions.Regex.IsMatch(c.SqlType, @"^[A-Za-z]+(\s*\(\s*\d+(\s*,\s*\d+)?\s*\))?(\s+UNSIGNED)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new InvalidOperationException($"\"{c.SqlType}\" is not a column type I can use for {c.Name} (e.g. INT, DECIMAL(12,2), VARCHAR(100)).");
        return $"CREATE TABLE {Ident.Quote(database, table)} (\n  " +
               string.Join(",\n  ", cols.Select(c => $"{Ident.Quote(c.Name.Trim())} {c.SqlType.ToUpperInvariant()} NULL")) +
               "\n) DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci";
    }
}
