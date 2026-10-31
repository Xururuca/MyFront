namespace MyFront.Core.Db;

/// <summary>
/// How the Data Browser finds the row it updates or deletes: by its primary / unique key, or — for a table without one
/// — by all of the row's original values, NULL-safe (&lt;=&gt;), with LIMIT 1. Values are parameters
/// @k0, @k1 … in the order of the columns given.
/// </summary>
public static class RowMatch
{
    private static readonly string[] Inexact = { "float", "double", "real", "json", "geometry", "point", "linestring", "polygon",
        "multipoint", "multilinestring", "multipolygon", "geometrycollection" };

    /// <summary>
    /// Whether a keyless row can be matched by this column: FLOAT / DOUBLE values don't round-trip exactly, JSON
    /// (MySQL) and spatial values don't compare as the text / bytes that were read.
    /// </summary>
    public static bool CanMatchBy(string columnType)
    {
        var t = columnType.TrimStart().ToLowerInvariant();
        int end = 0;
        while (end < t.Length && char.IsLetter(t[end])) end++;
        return end > 0 && !Inexact.Contains(t[..end]);
    }

    /// <summary>"`a` = @k0 AND `b` = @k1" for a key, "`a` &lt;=&gt; @k0 AND …" (NULL-safe) when matching all values.</summary>
    public static string Where(IReadOnlyList<string> columns, bool allValues)
    {
        if (columns.Count == 0) throw new ArgumentException("No columns to find the row by.");
        var op = allValues ? " <=> @k" : " = @k";
        return string.Join(" AND ", columns.Select((c, i) => Ident.Quote(c) + op + i));
    }
}
