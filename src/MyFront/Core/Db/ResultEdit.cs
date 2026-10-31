namespace MyFront.Core.Db;

/// <summary>How a result grid of the SQL editor can be edited, or why it can't (<see cref="ReadOnlyReason"/>).</summary>
public sealed class ResultEditPlan
{
    public string? ReadOnlyReason { get; init; }
    public bool Editable => ReadOnlyReason is null;
    /// <summary>Where edits are written; null while read-only.</summary>
    public EditTarget? Target { get; init; }
    /// <summary>Result columns you can type in (the table's own fields, not BLOB / binary).</summary>
    public bool[] EditableColumns { get; init; } = Array.Empty<bool>();

    public static ResultEditPlan ReadOnly(string reason) => new() { ReadOnlyReason = reason };

    /// <summary>For the result tab's info line: "key: id" / "no key: rows are matched by all their values" / "read-only: …".</summary>
    public string Describe(IReadOnlyList<ColumnInfo> columns) =>
        ReadOnlyReason ?? (Target!.AllValues
            ? "editable · no key: rows are matched by all their values"
            : "editable · key: " + string.Join(", ", Target.KeyIndexes.Select(i => columns[i].Name)));
}

/// <summary>
/// Decides whether a result set of the SQL editor can be edited in its grid. It can when its table columns all come
/// from one use of one base table (expression columns stay read-only), and every row can be found again: by the table's
/// primary key, or a unique key on NOT NULL fields, among the result's columns; for a table without such a key, by all
/// of its fields that compare exactly (<see cref="RowMatch"/>). Aliases are followed to the real column names.
/// </summary>
public static class ResultEdit
{
    /// <summary>The one table the result's columns come from (step 1, from the column metadata alone), or why not.</summary>
    public static (string? Database, string? Table, string? Reason) SourceTable(IReadOnlyList<ColumnInfo> columns, string? sql = null)
    {
        if (sql is not null)
        {
            var tokens = SqlClassifier.Tokens(sql).ToList();
            if (tokens.Any(t => t is "UNION" or "INTERSECT" or "EXCEPT"))
                return (null, null, "read-only: rows of several SELECTs (UNION)");
            // a grouped row stands for several table rows (even when the group key happens to be unique)
            if (tokens.Any(t => t is "DISTINCT" or "DISTINCTROW") || tokens.Zip(tokens.Skip(1)).Any(p => p is ("GROUP", "BY")))
                return (null, null, "read-only: grouped rows (GROUP BY / DISTINCT)");
        }
        (string Schema, string Table, string? Alias)? source = null;
        foreach (var c in columns)
        {
            if (string.IsNullOrEmpty(c.BaseTable) || string.IsNullOrEmpty(c.BaseColumn)) continue; // an expression
            if (string.IsNullOrEmpty(c.BaseSchema)) return (null, null, "read-only: not from a table");
            var s = (c.BaseSchema, c.BaseTable, c.TableAlias);
            if (source is null) source = s;
            else if (source.Value != s) return (null, null, "read-only: columns from several tables");
        }
        return source is { } one ? (one.Schema, one.Table, null) : (null, null, "read-only: no table columns");
    }

    /// <summary>Step 2, with the table's fields and keys (read fresh from the server).</summary>
    public static ResultEditPlan Plan(IReadOnlyList<ColumnInfo> columns, TableInfo table)
    {
        if (table.IsView) return ResultEditPlan.ReadOnly("read-only: view");

        // result column → the table's field (aliases followed); a field shown twice is written through its first column
        var fields = new string?[columns.Count];
        var editable = new bool[columns.Count];
        var firstColumn = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < columns.Count; i++)
        {
            var c = columns[i];
            if (string.IsNullOrEmpty(c.BaseColumn) || string.IsNullOrEmpty(c.BaseTable)) continue;
            var field = table.Columns.FirstOrDefault(f => f.Name.Equals(c.BaseColumn, StringComparison.OrdinalIgnoreCase));
            if (field is null || !firstColumn.TryAdd(field.Name, i)) continue;
            fields[i] = field.Name;
            editable[i] = !field.IsBinary;
        }
        int IndexOf(string field) => firstColumn.TryGetValue(field, out var i) ? i : -1;

        var keys = new List<IReadOnlyList<string>>();
        if (table.Indexes.FirstOrDefault(i => i.Name == "PRIMARY") is { } pk) keys.Add(pk.Columns);
        keys.AddRange(table.Indexes
            .Where(i => i.Name != "PRIMARY" && i.Unique && i.Columns.All(c => table.Columns.FirstOrDefault(x => x.Name == c) is { Nullable: false }))
            .Select(i => (IReadOnlyList<string>)i.Columns));

        int[] keyIndexes;
        bool allValues = keys.Count == 0;
        if (!allValues)
        {
            var key = keys.FirstOrDefault(k => k.All(f => IndexOf(f) >= 0));
            if (key is null)
                return ResultEditPlan.ReadOnly($"read-only: no key in the result (add {string.Join(", ", keys[0])})");
            keyIndexes = key.Select(IndexOf).ToArray();
        }
        else
        {
            var matchable = table.Columns.Where(f => RowMatch.CanMatchBy(f.Type)).Select(f => f.Name).ToList();
            if (matchable.Count == 0) return ResultEditPlan.ReadOnly("read-only: no fields to find a row by");
            if (matchable.Any(f => IndexOf(f) < 0))
                return ResultEditPlan.ReadOnly("read-only: the table has no key, so every field must be in the result");
            keyIndexes = matchable.Select(IndexOf).ToArray();
        }
        return new ResultEditPlan
        {
            Target = new EditTarget(table.Database, table.Name, fields, keyIndexes, allValues),
            EditableColumns = editable,
        };
    }
}
