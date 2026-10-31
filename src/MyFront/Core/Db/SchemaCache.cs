using System.Collections.Concurrent;
using MySqlConnector;

namespace MyFront.Core.Db;

public enum DbObjectType { Table, View, Procedure, Function }

public sealed record DbObject(string Database, string Name, DbObjectType Type)
{
    /// <summary>The order of a database's objects in the tree: tables, views, procedures, functions, each by name ignoring case.</summary>
    public static int Compare(DbObject a, DbObject b) =>
        a.Type != b.Type ? a.Type.CompareTo(b.Type) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
}

public sealed class TableColumn
{
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public bool Nullable { get; init; }
    public string Key { get; init; } = "";
    public string? Default { get; init; }
    public string Extra { get; init; } = "";
    public string? Collation { get; init; }
    public string Comment { get; init; } = "";

    public bool IsAutoIncrement => Extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase);

    public bool IsBinary
    {
        get
        {
            var t = Type.ToLowerInvariant();
            return t.Contains("blob") || t.StartsWith("binary") || t.StartsWith("varbinary") || t is "geometry" or "point";
        }
    }
}

public sealed class TableIndex
{
    public string Name { get; init; } = "";
    public bool Unique { get; init; }
    public string IndexType { get; init; } = "";
    public string Comment { get; init; } = "";
    public List<string> Columns { get; } = new();
    /// <summary>The key's fields in order with their prefix lengths (SHOW INDEX Sub_part) and DESC.</summary>
    public List<IndexPart> Parts { get; } = new();
    /// <summary>A part is an expression (MySQL 8 functional key part), not a field.</summary>
    public bool HasExpression { get; set; }
    public bool IsPrimary => Name == "PRIMARY";
}

public sealed class TableInfo
{
    public string Database { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsView { get; set; }
    public List<TableColumn> Columns { get; } = new();
    public List<TableIndex> Indexes { get; } = new();

    /// <summary>Primary key, or the first unique index whose columns are all NOT NULL. Null = no usable key.</summary>
    public IReadOnlyList<string>? KeyColumns
    {
        get
        {
            var pk = Indexes.FirstOrDefault(i => i.Name == "PRIMARY");
            if (pk is not null) return pk.Columns;
            var uk = Indexes.FirstOrDefault(i => i.Unique && i.Columns.All(c => Columns.FirstOrDefault(x => x.Name == c) is { Nullable: false }));
            return uk?.Columns;
        }
    }
}

public sealed class TableStatus
{
    public string Name { get; init; } = "";
    public bool IsView { get; init; }
    public string? Engine { get; init; }
    public long? Rows { get; init; }
    public long? DataLength { get; init; }
    public long? IndexLength { get; init; }
    public long? AutoIncrement { get; init; }
    public DateTime? CreateTime { get; init; }
    public DateTime? UpdateTime { get; init; }
    public string? Collation { get; init; }
    public string Comment { get; init; } = "";
}

/// <summary>Lazily loaded metadata for one session. Every call runs on a pooled connection.</summary>
public sealed class SchemaCache
{
    private readonly ServerSession _session;
    private readonly ConcurrentDictionary<(string, string), TableInfo> _tables = new();

    internal SchemaCache(ServerSession session) => _session = session;

    public void Invalidate(string? database = null, string? table = null)
    {
        foreach (var k in _tables.Keys)
            if ((database is null || k.Item1 == database) && (table is null || k.Item2 == table))
                _tables.TryRemove(k, out _);
    }

    private async Task<List<string>> GetDatabasesAsyncCore(CancellationToken ct = default)
    {
        await using var conn = await _session.OpenAsync(null, ct);
        await using var cmd = new MySqlCommand("SHOW DATABASES", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<string>();
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    }

    private async Task<List<DbObject>> GetObjectsAsyncCore(string database, CancellationToken ct = default)
    {
        var list = new List<DbObject>();
        await using var conn = await _session.OpenAsync(null, ct);
        await using (var cmd = new MySqlCommand($"SHOW FULL TABLES FROM {Ident.Quote(database)}", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var type = r.GetString(1).Contains("VIEW", StringComparison.OrdinalIgnoreCase) ? DbObjectType.View : DbObjectType.Table;
                list.Add(new DbObject(database, r.GetString(0), type));
            }
        }
        await using (var cmd = new MySqlCommand(
            "SELECT ROUTINE_NAME, ROUTINE_TYPE FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = @db ORDER BY ROUTINE_NAME", conn))
        {
            cmd.Parameters.AddWithValue("@db", database);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var type = r.GetString(1).Equals("FUNCTION", StringComparison.OrdinalIgnoreCase) ? DbObjectType.Function : DbObjectType.Procedure;
                list.Add(new DbObject(database, r.GetString(0), type));
            }
        }
        list.Sort(DbObject.Compare);
        return list;
    }

    private async Task<List<TableStatus>> GetTableStatusAsyncCore(string database, CancellationToken ct = default)
    {
        const string sql = """
            SELECT TABLE_NAME, TABLE_TYPE, ENGINE, TABLE_ROWS, DATA_LENGTH, INDEX_LENGTH, AUTO_INCREMENT,
                   CREATE_TIME, UPDATE_TIME, TABLE_COLLATION, TABLE_COMMENT
            FROM information_schema.TABLES WHERE TABLE_SCHEMA = @db ORDER BY TABLE_NAME
            """;
        await using var conn = await _session.OpenAsync(null, ct);
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@db", database);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<TableStatus>();
        while (await r.ReadAsync(ct))
        {
            list.Add(new TableStatus
            {
                Name = r.GetString(0),
                IsView = r.GetString(1).Contains("VIEW", StringComparison.OrdinalIgnoreCase),
                Engine = r.IsDBNull(2) ? null : r.GetString(2),
                Rows = r.IsDBNull(3) ? null : Convert.ToInt64(r.GetValue(3)),
                DataLength = r.IsDBNull(4) ? null : Convert.ToInt64(r.GetValue(4)),
                IndexLength = r.IsDBNull(5) ? null : Convert.ToInt64(r.GetValue(5)),
                AutoIncrement = r.IsDBNull(6) ? null : Convert.ToInt64(r.GetValue(6)),
                CreateTime = ReadDate(r, 7),
                UpdateTime = ReadDate(r, 8),
                Collation = r.IsDBNull(9) ? null : r.GetString(9),
                Comment = r.IsDBNull(10) ? "" : r.GetString(10),
            });
        }
        return list;
    }

    // Public entry points run on the thread pool, so no DB continuation ever runs on the UI thread.
    public Task<List<string>> GetDatabasesAsync(CancellationToken ct = default) => Task.Run(() => GetDatabasesAsyncCore(ct), ct);
    public Task<List<DbObject>> GetObjectsAsync(string database, CancellationToken ct = default) => Task.Run(() => GetObjectsAsyncCore(database, ct), ct);
    public Task<List<TableStatus>> GetTableStatusAsync(string database, CancellationToken ct = default) => Task.Run(() => GetTableStatusAsyncCore(database, ct), ct);
    public Task<TableInfo> GetTableAsync(string database, string table, CancellationToken ct = default) =>
        _tables.TryGetValue((database, table), out var cached) ? Task.FromResult(cached) : Task.Run(() => GetTableAsyncCore(database, table, ct), ct);
    public Task<string> GetCreateStatementAsync(string database, string? name, DbObjectType? type, CancellationToken ct = default) =>
        Task.Run(() => GetCreateStatementAsyncCore(database, name, type, ct), ct);

    private static DateTime? ReadDate(MySqlDataReader r, int i)
    {
        if (r.IsDBNull(i)) return null;
        var v = r.GetValue(i);
        return v switch
        {
            DateTime d => d,
            MySqlDateTime m when m.IsValidDateTime => m.GetDateTime(),
            _ => null,
        };
    }

    private async Task<TableInfo> GetTableAsyncCore(string database, string table, CancellationToken ct = default)
    {
        if (_tables.TryGetValue((database, table), out var cached)) return cached;

        var info = new TableInfo { Database = database, Name = table };
        await using var conn = await _session.OpenAsync(null, ct);
        var q = Ident.Quote(database, table);
        await using (var cmd = new MySqlCommand(
            "SELECT TABLE_TYPE FROM information_schema.TABLES WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @t", conn))
        {
            cmd.Parameters.AddWithValue("@db", database);
            cmd.Parameters.AddWithValue("@t", table);
            info.IsView = Convert.ToString(await cmd.ExecuteScalarAsync(ct))?.Contains("VIEW", StringComparison.OrdinalIgnoreCase) == true;
        }
        await using (var cmd = new MySqlCommand($"SHOW FULL COLUMNS FROM {q}", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                info.Columns.Add(new TableColumn
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
        await using (var cmd = new MySqlCommand($"SHOW INDEX FROM {q}", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            var byName = new Dictionary<string, TableIndex>();
            while (await r.ReadAsync(ct))
            {
                var name = r.GetString("Key_name");
                if (!byName.TryGetValue(name, out var idx))
                {
                    idx = new TableIndex
                    {
                        Name = name,
                        Unique = Convert.ToInt64(r.GetValue(r.GetOrdinal("Non_unique"))) == 0,
                        IndexType = r.GetString("Index_type"),
                        Comment = r.IsDBNull(r.GetOrdinal("Index_comment")) ? "" : r.GetString("Index_comment"),
                    };
                    byName[name] = idx;
                    info.Indexes.Add(idx);
                }
                if (r.IsDBNull(r.GetOrdinal("Column_name")))
                {
                    idx.HasExpression = true;
                    continue;
                }
                var column = r.GetString("Column_name");
                int sub = r.GetOrdinal("Sub_part"), order = r.GetOrdinal("Collation");
                idx.Columns.Add(column);
                idx.Parts.Add(new IndexPart(column, r.IsDBNull(sub) ? null : Convert.ToInt32(r.GetValue(sub)),
                    !r.IsDBNull(order) && r.GetString(order) == "D"));
            }
        }
        _tables[(database, table)] = info;
        return info;
    }

    /// <summary>SHOW CREATE ... for the "Copy CREATE statement" command.</summary>
    private async Task<string> GetCreateStatementAsyncCore(string database, string? name, DbObjectType? type, CancellationToken ct = default)
    {
        string sql;
        int column;
        if (name is null)
        {
            sql = $"SHOW CREATE DATABASE {Ident.Quote(database)}";
            column = 1;
        }
        else
        {
            var q = Ident.Quote(database, name);
            (sql, column) = type switch
            {
                DbObjectType.View => ($"SHOW CREATE VIEW {q}", 1),
                DbObjectType.Procedure => ($"SHOW CREATE PROCEDURE {q}", 2),
                DbObjectType.Function => ($"SHOW CREATE FUNCTION {q}", 2),
                _ => ($"SHOW CREATE TABLE {q}", 1),
            };
        }
        await using var conn = await _session.OpenAsync(null, ct);
        await using var cmd = new MySqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException("Object not found.");
        if (r.IsDBNull(column))
            throw new InvalidOperationException("The server returned no definition (missing privileges?).");
        return r.GetString(column);
    }
}
