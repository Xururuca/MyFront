using MySqlConnector;

namespace MyFront.Core.Db;

/// <summary>
/// The table a grid writes to and how it finds a row again: the Data Browser's page, or a result set of the SQL editor
/// whose columns come from one table. <see cref="Columns"/> maps each grid column to the table's column behind it.
/// </summary>
public sealed class EditTarget
{
    public EditTarget(string database, string table, string?[] columns, int[] keyIndexes, bool allValues)
    {
        if (keyIndexes.Length == 0 || keyIndexes.Any(i => i < 0 || i >= columns.Length || columns[i] is null))
            throw new ArgumentException("Every key column must be one of the table's columns.");
        Database = database;
        Table = table;
        Columns = columns;
        KeyIndexes = keyIndexes;
        AllValues = allValues;
    }

    public string Database { get; }
    public string Table { get; }
    /// <summary>The table's column behind each grid column; null = not one of the table's (an expression, a repeat).</summary>
    public string?[] Columns { get; }
    /// <summary>Grid columns a row is found by: its key, or (<see cref="AllValues"/>) every column that compares exactly.</summary>
    public int[] KeyIndexes { get; }
    /// <summary>The table has no primary / unique key: rows are matched by all their values (NULL-safe, LIMIT 1).</summary>
    public bool AllValues { get; }

    public string QuotedTable => Ident.Quote(Database, Table);

    /// <summary>"`id` = @k0", or "`a` &lt;=&gt; @k0 AND …" when matching all values.</summary>
    public string KeyWhere() => RowMatch.Where(KeyIndexes.Select(i => Columns[i]!).ToList(), AllValues);

    /// <summary>The values <see cref="KeyWhere"/> compares with (@k0, @k1 …), taken from a grid row.</summary>
    public void AddKeyParameters(MySqlCommand cmd, object?[] values)
    {
        for (int k = 0; k < KeyIndexes.Length; k++) cmd.Parameters.AddWithValue("@k" + k, values[KeyIndexes[k]]);
    }

    /// <summary>Grid columns of the table whose value differs from the row as loaded.</summary>
    public List<int> Changed(object?[] original, object?[] values) =>
        Enumerable.Range(0, Columns.Length).Where(i => Columns[i] is not null && !Equals(values[i], original[i])).ToList();

    /// <summary>UPDATE `db`.`t` SET `a` = @v2, … WHERE key LIMIT 1 (@vN = grid column N).</summary>
    public string UpdateSql(IReadOnlyList<int> changed)
    {
        if (changed.Count == 0) throw new ArgumentException("Nothing changed.");
        return $"UPDATE {QuotedTable} SET {string.Join(", ", changed.Select(i => Ident.Quote(Columns[i]!) + " = @v" + i))} WHERE {KeyWhere()} LIMIT 1";
    }

    /// <summary>SELECT the table's columns of the grid … WHERE key LIMIT 1: the row as stored after a change.</summary>
    public string SelectSql() =>
        $"SELECT {string.Join(", ", Columns.Where(c => c is not null).Select(c => Ident.Quote(c!)))} FROM {QuotedTable} WHERE {KeyWhere()} LIMIT 1";
}

/// <summary>Writes one changed grid row: parameters only, one row at most, read-only connections refused.</summary>
public static class RowUpdate
{
    /// <summary>
    /// UPDATE … SET (the changed columns) WHERE (the key as loaded) LIMIT 1 on a pooled connection, off the UI thread,
    /// then reads the row back so defaults, ON UPDATE columns and the server's formatting show.
    /// </summary>
    /// <returns>The row as stored now (grid columns that aren't the table's keep their values); null when no row
    /// matched: it was changed or deleted by someone else.</returns>
    public static Task<object?[]?> UpdateAsync(ServerSession session, EditTarget target, object?[] original, object?[] values,
        CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            var changed = target.Changed(original, values);
            if (changed.Count == 0) return (object?[]?)(object?[])values.Clone();
            var sql = target.UpdateSql(changed);
            session.EnsureAllowed(sql);
            await using var conn = await session.OpenAsync(target.Database, ct);
            await using (var cmd = new MySqlCommand(sql, conn))
            {
                foreach (var i in changed) cmd.Parameters.AddWithValue("@v" + i, values[i]);
                target.AddKeyParameters(cmd, original);
                // pooled connections report matched rows, so 0 really means "not found"
                if (await cmd.ExecuteNonQueryAsync(ct) == 0) return null;
            }
            return await ReadBackAsync(conn, target, values, ct);
        }, ct);

    /// <summary>
    /// The row found by the key in <paramref name="values"/>, read into a copy of them (the table's columns only).
    /// When it can't be read (no longer found by the values as typed, or a column gone) the values stay as they are:
    /// the change itself was saved.
    /// </summary>
    public static async Task<object?[]> ReadBackAsync(MySqlConnection conn, EditTarget target, object?[] values, CancellationToken ct = default)
    {
        var result = (object?[])values.Clone();
        var mapped = Enumerable.Range(0, target.Columns.Length).Where(i => target.Columns[i] is not null).ToList();
        try
        {
            await using var cmd = new MySqlCommand(target.SelectSql(), conn);
            target.AddKeyParameters(cmd, values);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct) && r.FieldCount == mapped.Count)
                for (int j = 0; j < mapped.Count; j++) result[mapped[j]] = CellFormat.Normalize(r.GetValue(j));
        }
        catch (MySqlException) { /* shown as typed */ }
        return result;
    }
}
