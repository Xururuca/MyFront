using System.Data.Common;
using MySqlConnector;

namespace MyFront.Core.Db;

public enum ColumnKind { Text, Integer, Decimal, Float, Date, DateTime, Time, Binary }

/// <summary>Result-set column description.</summary>
public sealed class ColumnInfo
{
    public string Name { get; init; } = "";
    public string DataTypeName { get; init; } = "";
    public ColumnKind Kind { get; init; }
    public string? BaseSchema { get; init; }
    public string? BaseTable { get; init; }
    public string? BaseColumn { get; init; }
    /// <summary>The table as the query names it (its alias): tells two uses of one table (a self-join) apart.</summary>
    public string? TableAlias { get; init; }

    public bool IsNumeric => Kind is ColumnKind.Integer or ColumnKind.Decimal or ColumnKind.Float;

    public static ColumnInfo[] FromReader(MySqlDataReader reader)
    {
        var schema = reader.GetColumnSchema();
        var cols = new ColumnInfo[schema.Count];
        for (int i = 0; i < schema.Count; i++)
        {
            var c = schema[i];
            var type = c is MySqlDbColumn m ? m.ProviderType : MySqlDbType.VarString;
            cols[i] = new ColumnInfo
            {
                Name = c.ColumnName,
                DataTypeName = c.DataTypeName ?? "",
                Kind = KindOf(type),
                BaseSchema = c.BaseSchemaName,
                BaseTable = c.BaseTableName,
                BaseColumn = c.BaseColumnName,
                TableAlias = (c as MySqlDbColumn)?.TableName,
            };
        }
        return cols;
    }

    public static ColumnKind KindOf(MySqlDbType t) => t switch
    {
        MySqlDbType.Bit or MySqlDbType.Byte or MySqlDbType.UByte or MySqlDbType.Int16 or MySqlDbType.UInt16
            or MySqlDbType.Int24 or MySqlDbType.UInt24 or MySqlDbType.Int32 or MySqlDbType.UInt32
            or MySqlDbType.Int64 or MySqlDbType.UInt64 or MySqlDbType.Year => ColumnKind.Integer,
        MySqlDbType.Decimal or MySqlDbType.NewDecimal => ColumnKind.Decimal,
        MySqlDbType.Float or MySqlDbType.Double => ColumnKind.Float,
        MySqlDbType.Date or MySqlDbType.Newdate => ColumnKind.Date,
        MySqlDbType.DateTime or MySqlDbType.Timestamp => ColumnKind.DateTime,
        MySqlDbType.Time => ColumnKind.Time,
        MySqlDbType.Blob or MySqlDbType.TinyBlob or MySqlDbType.MediumBlob or MySqlDbType.LongBlob
            or MySqlDbType.Binary or MySqlDbType.VarBinary or MySqlDbType.Geometry => ColumnKind.Binary,
        _ => ColumnKind.Text,
    };
}
