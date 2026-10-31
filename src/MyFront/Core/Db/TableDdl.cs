using MyFront.Core.History;
using MySqlConnector;

namespace MyFront.Core.Db;

/// <summary>Where CHANGE COLUMN puts the column: <see cref="First"/>, after another column, or (null) where it is.</summary>
public sealed record ColumnPosition(bool First, string? After)
{
    public static readonly ColumnPosition AtFirst = new(true, null);
    public static ColumnPosition AfterColumn(string column) => new(false, column);
    public string ToSql() => First ? " FIRST" : After is null ? "" : " AFTER " + Ident.Quote(After);
}

public enum IndexKind { Index, Unique, Fulltext, Primary }

/// <summary>A field of a key; <see cref="Length"/> = prefix length for text / blob fields (null = the whole field);
/// <see cref="Descending"/> = DESC (MySQL 8, MariaDB 10.8+).</summary>
public sealed record IndexPart(string Column, int? Length = null, bool Descending = false);

/// <summary>
/// ALTER / RENAME / DROP statements built from the dialogs' choices. DDL can't take parameters: names go through
/// <see cref="Ident.Quote(string)"/>, values (defaults, comments) through <see cref="SqlLiteral.Quote"/>.
/// </summary>
public static class TableDdl
{
    public static string ChangeColumn(string database, string table, string oldName, ColumnDefinition column,
        ColumnPosition? position, bool noBackslashEscapes) =>
        $"ALTER TABLE {Ident.Quote(database, table)} CHANGE COLUMN {Ident.Quote(oldName)} {column.ToSql(noBackslashEscapes)}{position?.ToSql()}";

    /// <summary>
    /// ALTER TABLE … ADD PRIMARY KEY / UNIQUE INDEX / FULLTEXT INDEX / INDEX `name` (`a`(10), `b`) [COMMENT '…'].
    /// Fulltext keys take whole fields, so their prefix lengths are left out.
    /// </summary>
    /// <exception cref="ArgumentException">No fields, no name (other than for the primary key), or a length below 1.</exception>
    public static string AddIndex(string database, string table, IndexKind kind, string? name, IReadOnlyList<IndexPart> parts,
        string? comment, bool noBackslashEscapes) =>
        $"ALTER TABLE {Ident.Quote(database, table)} {AddClause(kind, name, parts, comment, noBackslashEscapes)}";

    /// <summary>
    /// Changes an existing key in one statement, so the table is never without it:
    /// ALTER TABLE … DROP INDEX `old` (DROP PRIMARY KEY for the primary key), ADD … (as <see cref="AddIndex"/>).
    /// </summary>
    public static string ReplaceIndex(string database, string table, string oldName, IndexKind kind, string? name,
        IReadOnlyList<IndexPart> parts, string? comment, bool noBackslashEscapes)
    {
        var add = AddClause(kind, name, parts, comment, noBackslashEscapes);
        var drop = oldName == "PRIMARY" ? "DROP PRIMARY KEY" : "DROP INDEX " + Ident.Quote(oldName);
        return $"ALTER TABLE {Ident.Quote(database, table)} {drop}, {add}";
    }

    private static string AddClause(IndexKind kind, string? name, IReadOnlyList<IndexPart> parts, string? comment, bool noBackslashEscapes)
    {
        if (parts.Count == 0) throw new ArgumentException("Add at least one field to the key.");
        if (kind != IndexKind.Primary && string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Enter a name for the key.");
        var fields = new List<string>();
        foreach (var p in parts)
        {
            if (p.Length is < 1) throw new ArgumentException($"The length of {p.Column} must be at least 1.");
            fields.Add(Ident.Quote(p.Column) + (p.Length is { } n && kind != IndexKind.Fulltext ? $"({n})" : "")
                       + (p.Descending && kind != IndexKind.Fulltext ? " DESC" : ""));
        }
        var what = kind switch
        {
            IndexKind.Primary => "PRIMARY KEY",
            IndexKind.Unique => "UNIQUE INDEX " + Ident.Quote(name!.Trim()),
            IndexKind.Fulltext => "FULLTEXT INDEX " + Ident.Quote(name!.Trim()),
            _ => "INDEX " + Ident.Quote(name!.Trim()),
        };
        var clause = $"ADD {what} ({string.Join(", ", fields)})";
        return string.IsNullOrEmpty(comment) ? clause : clause + " COMMENT " + SqlLiteral.Quote(comment, noBackslashEscapes);
    }

    /// <summary>The kind of an existing key, as the Add key dialog offers it (null: SPATIAL and others it can't make).</summary>
    public static IndexKind? KindOf(TableIndex index) =>
        index.IsPrimary ? IndexKind.Primary
        : index.IndexType.Equals("FULLTEXT", StringComparison.OrdinalIgnoreCase) ? IndexKind.Fulltext
        : index.IndexType.Equals("SPATIAL", StringComparison.OrdinalIgnoreCase) || index.HasExpression ? null
        : index.Unique ? IndexKind.Unique : IndexKind.Index;

    /// <summary>RENAME TABLE `db`.`old` TO `db`.`new` (views too).</summary>
    public static string Rename(string database, string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) throw new ArgumentException("Enter the new name.");
        return $"RENAME TABLE {Ident.Quote(database, oldName)} TO {Ident.Quote(database, newName.Trim())}";
    }

    public static string Drop(string database, string name, bool isView) =>
        $"DROP {(isView ? "VIEW" : "TABLE")} {Ident.Quote(database, name)}";

    /// <summary>"idx_" + the first field, made unique among the table's keys (idx_ad, idx_ad_2 …).</summary>
    public static string SuggestIndexName(string firstColumn, IEnumerable<string> existing)
    {
        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var name = "idx_" + firstColumn;
        for (int i = 2; taken.Contains(name); i++) name = $"idx_{firstColumn}_{i}";
        return name;
    }

    /// <summary>Runs one DDL statement on a pooled connection, off the UI thread, and records it in SQL history. Read-only connections refuse it.</summary>
    public static Task ExecuteAsync(ServerSession session, string? database, string sql, string historyInfo, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            session.EnsureAllowed(sql);
            await using var conn = await session.OpenAsync(database, ct);
            await using var cmd = new MySqlCommand(sql, conn) { CommandTimeout = 0 };
            await cmd.ExecuteNonQueryAsync(ct);
            SqlHistory.Append(session.Account.Id, database, sql, historyInfo);
        }, ct);
}

/// <summary>What the table-editing dialogs need from the server, read fresh when they open.</summary>
public sealed class TableDdlContext
{
    public required TableInfo Table { get; init; }
    public required string CreateTable { get; init; }
    /// <summary>The server's sql_mode has NO_BACKSLASH_ESCAPES: backslashes in literals are not doubled.</summary>
    public bool NoBackslashEscapes { get; init; }
    public List<string> Collations { get; init; } = new();

    public static Task<TableDdlContext> LoadAsync(ServerSession session, string database, string table, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            session.Schema.Invalidate(database, table); // someone may have changed it since it was cached
            var info = await session.Schema.GetTableAsync(database, table, ct);
            var create = await session.Schema.GetCreateStatementAsync(database, table, DbObjectType.Table, ct);
            await using var conn = await session.OpenAsync(null, ct);
            bool noBackslash;
            await using (var cmd = new MySqlCommand("SELECT @@SESSION.sql_mode", conn))
                noBackslash = (Convert.ToString(await cmd.ExecuteScalarAsync(ct)) ?? "").Contains("NO_BACKSLASH_ESCAPES", StringComparison.OrdinalIgnoreCase);
            var collations = new List<string>();
            await using (var cmd = new MySqlCommand("SELECT COLLATION_NAME FROM information_schema.COLLATIONS ORDER BY COLLATION_NAME", conn))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) collations.Add(r.GetString(0));
            return new TableDdlContext { Table = info, CreateTable = create, NoBackslashEscapes = noBackslash, Collations = collations };
        }, ct);
}
