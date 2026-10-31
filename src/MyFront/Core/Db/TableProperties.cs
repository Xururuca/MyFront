using System.Text.RegularExpressions;
using MySqlConnector;

namespace MyFront.Core.Db;

public sealed record KeyDetail(string Name, string Columns, bool Unique, string Type, string Comment);
public sealed record ForeignKeyDetail(string Name, string Columns, string References, string OnDelete, string OnUpdate);
public sealed record TriggerDetail(string Name, string Timing, string Event, string Statement, string Definer, DateTime? Created);
public sealed record PartitionDetail(string Name, string Method, string Expression, string Value, long? Rows, long? DataLength, string Comment);
public sealed record DependencyDetail(string Kind, string Object, string Detail);

/// <summary>Everything the table / view Properties dialog shows, read in one go (dependencies on demand).</summary>
public sealed class TableProperties
{
    public string Database { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsView { get; private set; }
    public string TableType { get; private set; } = "";

    public string? Engine { get; private set; }
    public string? Collation { get; private set; }
    /// <summary>The character set is the collation's prefix ("utf8mb4_general_ci" → "utf8mb4").</summary>
    public string? CharacterSet => Collation is null ? null : Collation.Split('_')[0];
    public string Comment { get; private set; } = "";
    public string? RowFormat { get; private set; }
    public long? AutoIncrement { get; private set; }
    public string? CreateOptions { get; private set; }

    public DateTime? Created { get; private set; }
    public DateTime? Updated { get; private set; }
    public DateTime? Checked { get; private set; }
    /// <summary>Estimate for InnoDB (information_schema), exact for MyISAM / Aria.</summary>
    public long? Rows { get; private set; }
    public long? AvgRowLength { get; private set; }
    public long? DataLength { get; private set; }
    public long? IndexLength { get; private set; }
    public long? DataFree { get; private set; }

    // views
    public string? Definer { get; private set; }
    public string? SecurityType { get; private set; }
    public string? CheckOption { get; private set; }
    public string? Updatable { get; private set; }

    public List<TableColumn> Fields { get; } = new();
    public List<KeyDetail> Keys { get; } = new();
    public List<ForeignKeyDetail> ForeignKeys { get; } = new();
    public List<TriggerDetail> Triggers { get; } = new();
    public List<PartitionDetail> Partitions { get; } = new();
    public string CreateStatement { get; private set; } = "";

    public static Task<TableProperties> LoadAsync(ServerSession session, string database, string table, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            var p = new TableProperties { Database = database, Name = table };
            await using var conn = await session.OpenAsync(null, ct);
            // MySQL 8 caches information_schema statistics for up to a day; MariaDB has no such cache
            if (!session.IsMariaDb && session.Version.Major >= 8)
                await Exec(conn, "SET SESSION information_schema_stats_expiry = 0", ct);
            await p.ReadTableAsync(conn, ct);
            if (p.IsView) await p.ReadViewAsync(conn, ct);
            await p.ReadFieldsAsync(conn, ct);
            if (!p.IsView)
            {
                await p.ReadKeysAsync(conn, ct);
                await p.ReadForeignKeysAsync(conn, ct);
                await p.ReadTriggersAsync(conn, ct);
                await p.ReadPartitionsAsync(conn, ct);
            }
            p.CreateStatement = await ReadCreateAsync(conn, database, table, p.IsView, ct);
            return p;
        }, ct);

    private static async Task Exec(MySqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private MySqlCommand Cmd(MySqlConnection conn, string sql)
    {
        var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@db", Database);
        cmd.Parameters.AddWithValue("@t", Name);
        return cmd;
    }

    private static string? Str(MySqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i));
    private static long? Long(MySqlDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToInt64(r.GetValue(i));

    private static DateTime? Date(MySqlDataReader r, int i)
    {
        if (r.IsDBNull(i)) return null;
        return r.GetValue(i) switch
        {
            DateTime d => d,
            MySqlDateTime m when m.IsValidDateTime => m.GetDateTime(),
            _ => null,
        };
    }

    private async Task ReadTableAsync(MySqlConnection conn, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, """
            SELECT TABLE_TYPE, ENGINE, TABLE_COLLATION, TABLE_COMMENT, ROW_FORMAT, AUTO_INCREMENT, CREATE_OPTIONS,
                   CREATE_TIME, UPDATE_TIME, CHECK_TIME, TABLE_ROWS, AVG_ROW_LENGTH, DATA_LENGTH, INDEX_LENGTH, DATA_FREE
            FROM information_schema.TABLES WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @t
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException($"{Database}.{Name} not found (dropped, or no privileges?).");
        TableType = Str(r, 0) ?? "";
        IsView = TableType.Contains("VIEW", StringComparison.OrdinalIgnoreCase);
        Engine = Str(r, 1);
        Collation = Str(r, 2);
        Comment = Str(r, 3) ?? "";
        if (IsView && Comment == "VIEW") Comment = ""; // MySQL puts "VIEW" there
        RowFormat = Str(r, 4);
        AutoIncrement = Long(r, 5);
        CreateOptions = Str(r, 6);
        Created = Date(r, 7);
        Updated = Date(r, 8);
        Checked = Date(r, 9);
        Rows = Long(r, 10);
        AvgRowLength = Long(r, 11);
        DataLength = Long(r, 12);
        IndexLength = Long(r, 13);
        DataFree = Long(r, 14);
    }

    private async Task ReadViewAsync(MySqlConnection conn, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, "SELECT DEFINER, SECURITY_TYPE, CHECK_OPTION, IS_UPDATABLE FROM information_schema.VIEWS WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @t");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return;
        Definer = Str(r, 0);
        SecurityType = Str(r, 1);
        CheckOption = Str(r, 2);
        Updatable = Str(r, 3);
    }

    private async Task ReadFieldsAsync(MySqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand($"SHOW FULL COLUMNS FROM {Ident.Quote(Database, Name)}", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            Fields.Add(new TableColumn
            {
                Name = r.GetString("Field"),
                Type = r.GetString("Type"),
                Collation = r.IsDBNull(r.GetOrdinal("Collation")) ? null : r.GetString("Collation"),
                Nullable = r.GetString("Null") == "YES",
                Key = r.GetString("Key"),
                Default = r.IsDBNull(r.GetOrdinal("Default")) ? null : r.GetString("Default"),
                Extra = r.GetString("Extra"),
                Comment = r.GetString("Comment"),
            });
        }
    }

    private async Task ReadKeysAsync(MySqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand($"SHOW INDEX FROM {Ident.Quote(Database, Name)}", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var order = new List<string>();
        var parts = new Dictionary<string, (List<string> Cols, bool Unique, string Type, string Comment)>();
        bool hasExpression = Enumerable.Range(0, r.FieldCount).Any(i => r.GetName(i) == "Expression");
        while (await r.ReadAsync(ct))
        {
            var name = r.GetString("Key_name");
            if (!parts.TryGetValue(name, out var k))
            {
                var comment = r.IsDBNull(r.GetOrdinal("Index_comment")) ? "" : r.GetString("Index_comment");
                k = (new List<string>(), Convert.ToInt64(r.GetValue(r.GetOrdinal("Non_unique"))) == 0, r.GetString("Index_type"), comment);
                parts[name] = k;
                order.Add(name);
            }
            string col;
            if (!r.IsDBNull(r.GetOrdinal("Column_name"))) col = r.GetString("Column_name");
            else if (hasExpression && !r.IsDBNull(r.GetOrdinal("Expression"))) col = "(" + r.GetString("Expression") + ")"; // MySQL 8 functional index
            else col = "?";
            var sub = r.GetOrdinal("Sub_part");
            if (!r.IsDBNull(sub)) col += $"({r.GetValue(sub)})";
            k.Cols.Add(col);
        }
        foreach (var name in order)
        {
            var k = parts[name];
            Keys.Add(new KeyDetail(name, string.Join(", ", k.Cols), k.Unique, k.Type, k.Comment));
        }
    }

    private async Task ReadForeignKeysAsync(MySqlConnection conn, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, """
            SELECT k.CONSTRAINT_NAME, k.COLUMN_NAME, k.REFERENCED_TABLE_SCHEMA, k.REFERENCED_TABLE_NAME, k.REFERENCED_COLUMN_NAME,
                   r.DELETE_RULE, r.UPDATE_RULE
            FROM information_schema.KEY_COLUMN_USAGE k
            JOIN information_schema.REFERENTIAL_CONSTRAINTS r
              ON r.CONSTRAINT_SCHEMA = k.CONSTRAINT_SCHEMA AND r.CONSTRAINT_NAME = k.CONSTRAINT_NAME AND r.TABLE_NAME = k.TABLE_NAME
            WHERE k.TABLE_SCHEMA = @db AND k.TABLE_NAME = @t AND k.REFERENCED_TABLE_NAME IS NOT NULL
            ORDER BY k.CONSTRAINT_NAME, k.ORDINAL_POSITION
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<(string Name, string Col, string RefDb, string RefTable, string RefCol, string Del, string Upd)>();
        while (await r.ReadAsync(ct))
            rows.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6)));
        foreach (var g in rows.GroupBy(x => x.Name))
        {
            var first = g.First();
            var table = first.RefDb.Equals(Database, StringComparison.Ordinal) ? first.RefTable : $"{first.RefDb}.{first.RefTable}";
            ForeignKeys.Add(new ForeignKeyDetail(g.Key, string.Join(", ", g.Select(x => x.Col)),
                $"{table} ({string.Join(", ", g.Select(x => x.RefCol))})", first.Del, first.Upd));
        }
    }

    private async Task ReadTriggersAsync(MySqlConnection conn, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, """
            SELECT TRIGGER_NAME, ACTION_TIMING, EVENT_MANIPULATION, ACTION_STATEMENT, DEFINER, CREATED
            FROM information_schema.TRIGGERS WHERE EVENT_OBJECT_SCHEMA = @db AND EVENT_OBJECT_TABLE = @t
            ORDER BY ACTION_TIMING, EVENT_MANIPULATION, ACTION_ORDER
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            Triggers.Add(new TriggerDetail(r.GetString(0), r.GetString(1), r.GetString(2), Str(r, 3) ?? "", Str(r, 4) ?? "", Date(r, 5)));
    }

    private async Task ReadPartitionsAsync(MySqlConnection conn, CancellationToken ct)
    {
        await using var cmd = Cmd(conn, """
            SELECT PARTITION_NAME, SUBPARTITION_NAME, PARTITION_METHOD, SUBPARTITION_METHOD, PARTITION_EXPRESSION,
                   PARTITION_DESCRIPTION, TABLE_ROWS, DATA_LENGTH, PARTITION_COMMENT
            FROM information_schema.PARTITIONS
            WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @t AND PARTITION_NAME IS NOT NULL
            ORDER BY PARTITION_ORDINAL_POSITION, SUBPARTITION_ORDINAL_POSITION
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var sub = Str(r, 1);
            Partitions.Add(new PartitionDetail(
                sub is null ? r.GetString(0) : $"{r.GetString(0)} / {sub}",
                Str(r, 2) + (Str(r, 3) is { } sm ? " / " + sm : ""),
                Str(r, 4) ?? "", Str(r, 5) ?? "", Long(r, 6), Long(r, 7), Str(r, 8) ?? ""));
        }
    }

    private static async Task<string> ReadCreateAsync(MySqlConnection conn, string database, string table, bool view, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand($"SHOW CREATE {(view ? "VIEW" : "TABLE")} {Ident.Quote(database, table)}", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) && !r.IsDBNull(1) ? r.GetString(1) : "";
    }

    /// <summary>
    /// What uses this table in its database: foreign keys pointing at it, views, routines and other tables' triggers
    /// that mention it, and the tables it references itself. Kept separate: it reads every table of the database.
    /// </summary>
    public Task<List<DependencyDetail>> LoadDependenciesAsync(ServerSession session, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            var list = new List<DependencyDetail>();
            await using var conn = await session.OpenAsync(null, ct);
            foreach (var fk in ForeignKeys)
                list.Add(new DependencyDetail("References (foreign key)", fk.References, $"{fk.Name}: {fk.Columns}"));

            await using (var cmd = Cmd(conn, """
                SELECT TABLE_SCHEMA, TABLE_NAME, CONSTRAINT_NAME, GROUP_CONCAT(COLUMN_NAME ORDER BY ORDINAL_POSITION SEPARATOR ', ')
                FROM information_schema.KEY_COLUMN_USAGE
                WHERE TABLE_SCHEMA = @db AND REFERENCED_TABLE_SCHEMA = @db AND REFERENCED_TABLE_NAME = @t
                GROUP BY TABLE_SCHEMA, TABLE_NAME, CONSTRAINT_NAME ORDER BY TABLE_NAME, CONSTRAINT_NAME
                """))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct))
                    list.Add(new DependencyDetail("Referenced by (foreign key)", r.GetString(1), $"{r.GetString(2)}: {Str(r, 3)}"));

            // views store their definition with qualified names: `db`.`table`
            await using (var cmd = Cmd(conn, "SELECT TABLE_NAME FROM information_schema.VIEWS WHERE TABLE_SCHEMA = @db AND TABLE_NAME <> @t AND VIEW_DEFINITION LIKE @like ORDER BY TABLE_NAME"))
            {
                cmd.Parameters.AddWithValue("@like", "%" + Ident.EscapeLike(Ident.Quote(Database, Name)) + "%");
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) list.Add(new DependencyDetail("View", r.GetString(0), "selects from it"));
            }

            var mention = new Regex($@"(?<![\w$`]){Regex.Escape(Name)}(?![\w$`])|`{Regex.Escape(Name.Replace("`", "``"))}`",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var like = "%" + Ident.EscapeLike(Name) + "%";
            await using (var cmd = Cmd(conn, "SELECT ROUTINE_TYPE, ROUTINE_NAME, ROUTINE_DEFINITION FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = @db AND ROUTINE_DEFINITION LIKE @like ORDER BY ROUTINE_TYPE, ROUTINE_NAME"))
            {
                cmd.Parameters.AddWithValue("@like", like);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    if (mention.IsMatch(Str(r, 2) ?? ""))
                        list.Add(new DependencyDetail(r.GetString(0) == "FUNCTION" ? "Function" : "Procedure", r.GetString(1), "mentions it"));
            }
            await using (var cmd = Cmd(conn, "SELECT TRIGGER_NAME, EVENT_OBJECT_TABLE, ACTION_STATEMENT FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA = @db AND EVENT_OBJECT_TABLE <> @t AND ACTION_STATEMENT LIKE @like ORDER BY TRIGGER_NAME"))
            {
                cmd.Parameters.AddWithValue("@like", like);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    if (mention.IsMatch(Str(r, 2) ?? ""))
                        list.Add(new DependencyDetail("Trigger", r.GetString(0), $"on {r.GetString(1)}, mentions it"));
            }
            return list;
        }, ct);
}
