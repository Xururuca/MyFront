using MyFront.Core.Db;
using MySqlConnector;

namespace MyFront.Core.Export;

/// <summary>A forward-only stream of rows to export.</summary>
public interface IExportRowSource : IDisposable
{
    ColumnInfo[] Columns { get; }
    /// <summary>Advances to the next row; false at the end.</summary>
    bool Read();
    /// <summary>Raw value of the current row (null for SQL NULL).</summary>
    object? GetValue(int column);
}

/// <summary>One thing to export (becomes one sheet, or several if it has more rows than a sheet holds).</summary>
public sealed class ExportTable
{
    public ExportTable(string name, Func<CancellationToken, IExportRowSource> open, long? estimatedRows = null)
    {
        Name = name;
        Open = open;
        EstimatedRows = estimatedRows;
    }

    public string Name { get; }
    /// <summary>Opened when the exporter reaches this table (called on the export thread).</summary>
    public Func<CancellationToken, IExportRowSource> Open { get; }
    public long? EstimatedRows { get; }

    /// <summary>SELECT * FROM table, streamed from the server on its own connection.</summary>
    public static ExportTable FromTable(ServerSession session, string database, string table, long? estimatedRows = null) =>
        new(table, ct => DbRowSource.Open(session, database, $"SELECT * FROM {Ident.Quote(database, table)}", ct), estimatedRows);

    /// <summary>Any SELECT, streamed (never loaded into a grid). Parameters are bound, never concatenated.</summary>
    public static ExportTable FromQuery(ServerSession session, string? database, string name, string sql,
        IReadOnlyList<(string Name, object? Value)>? parameters = null, long? estimatedRows = null) =>
        new(name, ct => DbRowSource.Open(session, database, sql, ct, parameters), estimatedRows);

    /// <summary>Rows already in a result grid, in display order (sort) and with the given column order.</summary>
    public static ExportTable FromResult(string name, ColumnInfo[] columns, IRowSource rows, int[]? columnOrder = null) =>
        new(name, _ => new StoreRowSource(columns, rows, columnOrder), rows.Count);
}

/// <summary>Streams a query on a dedicated connection; rows are never buffered.</summary>
public sealed class DbRowSource : IExportRowSource
{
    private readonly MySqlConnection _conn;
    private readonly bool _ownsConnection;
    private readonly MySqlCommand _cmd;
    private readonly MySqlDataReader _reader;
    private readonly CancellationTokenRegistration _reg;
    private readonly object?[] _values;

    private DbRowSource(MySqlConnection conn, MySqlCommand cmd, MySqlDataReader reader, CancellationToken ct, bool ownsConnection = true)
    {
        _conn = conn;
        _ownsConnection = ownsConnection;
        _cmd = cmd;
        _reader = reader;
        Columns = ColumnInfo.FromReader(reader);
        _values = new object?[reader.FieldCount];
        // KILL QUERY on cancel so a long table scan stops at the server too
        _reg = ct.Register(static c => { try { ((MySqlCommand)c!).Cancel(); } catch { } }, cmd);
    }

    public static DbRowSource Open(ServerSession session, string? database, string sql, CancellationToken ct,
        IReadOnlyList<(string Name, object? Value)>? parameters = null)
    {
        session.EnsureAllowed(sql);
        var conn = session.OpenDedicatedAsync(database, ct).GetAwaiter().GetResult();
        MySqlCommand? cmd = null;
        try
        {
            cmd = new MySqlCommand(sql, conn) { CommandTimeout = 0 };
            foreach (var (name, value) in parameters ?? Array.Empty<(string, object?)>())
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            var reader = cmd.ExecuteReader();
            if (reader.FieldCount == 0)
            {
                reader.Dispose();
                throw new InvalidOperationException("The statement returns no result set.");
            }
            return new DbRowSource(conn, cmd, reader, ct);
        }
        catch
        {
            cmd?.Dispose();
            conn.Dispose();
            throw;
        }
    }

    /// <summary>Opens a reader on a connection the caller owns (it stays open for the next statement).</summary>
    public static DbRowSource OpenOn(MySqlConnection conn, string sql, CancellationToken ct, IReadOnlyList<(string Name, object? Value)>? parameters = null)
    {
        var cmd = new MySqlCommand(sql, conn) { CommandTimeout = 0 };
        try
        {
            foreach (var (name, value) in parameters ?? Array.Empty<(string, object?)>())
                if (sql.Contains(name, StringComparison.Ordinal)) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            var reader = cmd.ExecuteReader();
            if (reader.FieldCount == 0)
            {
                reader.Dispose();
                throw new InvalidOperationException("The statement returns no result set.");
            }
            return new DbRowSource(conn, cmd, reader, ct, ownsConnection: false);
        }
        catch
        {
            cmd.Dispose();
            throw;
        }
    }

    public ColumnInfo[] Columns { get; }

    public bool Read()
    {
        if (!_reader.Read()) return false;
        _reader.GetValues(_values!);
        return true;
    }

    public object? GetValue(int column) => CellFormat.Normalize(_values[column]);

    public void Dispose()
    {
        _reg.Dispose();
        try { _reader.Dispose(); } catch (MySqlException) { /* cancelled */ }
        _cmd.Dispose();
        if (_ownsConnection) _conn.Dispose();
    }
}

/// <summary>Rows from an in-memory result (what the grid shows).</summary>
public sealed class StoreRowSource : IExportRowSource
{
    private readonly IRowSource _rows;
    private readonly int[] _order;
    private int _index = -1;

    public StoreRowSource(ColumnInfo[] columns, IRowSource rows, int[]? columnOrder)
    {
        _rows = rows;
        _order = columnOrder ?? Enumerable.Range(0, columns.Length).ToArray();
        Columns = _order.Select(i => columns[i]).ToArray();
    }

    public ColumnInfo[] Columns { get; }

    public bool Read() => ++_index < _rows.Count;

    public object? GetValue(int column) => _rows.GetRow(_index)[_order[column]];

    public void Dispose() { }
}

/// <summary>Thread-safe counters the UI polls while an export runs.</summary>
public sealed class ExportProgress
{
    private long _rows;
    public long Rows => Interlocked.Read(ref _rows);
    public void AddRow() => Interlocked.Increment(ref _rows);
    public volatile int Sheets;
    public volatile int Files;
    public volatile string? CurrentTable;
    public long? TotalRowsEstimate { get; set; }
}

public sealed class ExportResult
{
    public long Rows { get; init; }
    public int Sheets { get; init; }
    public List<string> Files { get; init; } = new();
    public TimeSpan Elapsed { get; init; }
    public bool HeaderBold { get; init; }
}
