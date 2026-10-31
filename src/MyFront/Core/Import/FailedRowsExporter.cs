using MyFront.Core.Db;
using MyFront.Core.Export;

namespace MyFront.Core.Import;

/// <summary>Writes the rows that could not be imported (row number, error, original values) to a new .xlsx.</summary>
public static class FailedRowsExporter
{
    public static Task<ExportResult> SaveAsync(string path, ImportResult result, CancellationToken ct = default)
    {
        var columns = new List<ColumnInfo>
        {
            new() { Name = "Row", Kind = ColumnKind.Integer },
            new() { Name = "Error", Kind = ColumnKind.Text },
        };
        columns.AddRange(result.SourceHeaders.Select(h => new ColumnInfo { Name = h, Kind = ColumnKind.Text }));
        var store = new RowStore();
        foreach (var f in result.FailedRows)
        {
            var row = new object?[columns.Count];
            row[0] = f.RowNumber;
            row[1] = f.Error;
            for (int i = 0; i < result.SourceHeaders.Count && i < f.Values.Length; i++) row[i + 2] = f.Values[i];
            store.Add(row);
        }
        var table = ExportTable.FromResult("Failed rows", columns.ToArray(), store);
        return ExcelExporter.ExportAsync(path, new[] { table }, new ExportProgress(), ct);
    }
}
