using System.Data.Common;

namespace MyFront.Core.Db;

/// <summary>One row of EXPLAIN's table output (MariaDB and MySQL; extra columns such as MySQL's "filtered" are ignored).</summary>
public sealed record ExplainRow(string? Id, string? SelectType, string? Table, string? Type, string? PossibleKeys,
    string? Key, string? KeyLen, string? Ref, long? Rows, string? Extra);

/// <summary>
/// Runs nothing itself: turns EXPLAIN's rows into plain findings — which index each table is read by, full table / index
/// scans, sorting without an index (filesort), temporary tables, joins without an index.
/// </summary>
public static class ExplainAnalyzer
{
    /// <summary>Statements EXPLAIN takes without running them.</summary>
    public static bool CanExplain(string sql) =>
        SqlClassifier.FirstKeyword(sql) is "SELECT" or "WITH" or "UPDATE" or "DELETE" or "INSERT" or "REPLACE" or "TABLE" or "VALUES";

    public static async Task<List<ExplainRow>> ReadAsync(DbDataReader r, CancellationToken ct = default)
    {
        int Col(string name)
        {
            for (int i = 0; i < r.FieldCount; i++)
                if (string.Equals(r.GetName(i), name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
        int id = Col("id"), selectType = Col("select_type"), table = Col("table"), type = Col("type"), possible = Col("possible_keys"),
            key = Col("key"), keyLen = Col("key_len"), refCol = Col("ref"), rows = Col("rows"), extra = Col("Extra");
        string? S(int i) => i < 0 || r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
        var list = new List<ExplainRow>();
        while (await r.ReadAsync(ct))
        {
            long? n = long.TryParse(S(rows), out var v) ? v : null;
            list.Add(new ExplainRow(S(id), S(selectType), S(table), S(type), S(possible), S(key), S(keyLen), S(refCol), n, S(extra)));
        }
        return list;
    }

    /// <summary>Findings, most important first; "⚠" = worth a look, "✓" = fine, "ℹ" = information.</summary>
    public static List<string> Findings(IReadOnlyList<ExplainRow> rows)
    {
        var bad = new List<string>();
        var good = new List<string>();
        var info = new List<string>();
        foreach (var r in rows)
        {
            var extra = r.Extra ?? "";
            if (extra.Contains("Impossible WHERE", StringComparison.OrdinalIgnoreCase))
                info.Add("ℹ The WHERE can never be true: no rows are read.");
            if (string.IsNullOrEmpty(r.Table) || r.Table.StartsWith('<')) // derived tables / unions: their parts have rows of their own
            {
                if (extra.Contains("No tables used", StringComparison.OrdinalIgnoreCase)) info.Add("ℹ No table is read.");
                continue;
            }
            var t = r.Table;
            var rowsText = r.Rows is { } n ? $"≈{n:N0} rows" : "rows unknown";
            switch ((r.Type ?? "").ToLowerInvariant())
            {
                case "all":
                    bad.Add($"⚠ {t}: full table scan ({rowsText}) — no index is used." + (string.IsNullOrEmpty(r.PossibleKeys)
                        ? " An index on the columns it is filtered or joined on would help."
                        : $" Possible keys ({r.PossibleKeys}) weren't chosen."));
                    break;
                case "index":
                    bad.Add($"⚠ {t}: reads the whole index {r.Key} ({rowsText}) instead of looking rows up.");
                    break;
                case "":
                    break;
                default:
                    good.Add($"✓ {t}: index {r.Key ?? "?"} ({r.Type}, {rowsText}" + (string.IsNullOrEmpty(r.Ref) ? ")" : $", compared with {r.Ref})"));
                    break;
            }
            if (extra.Contains("Using filesort", StringComparison.OrdinalIgnoreCase))
                bad.Add($"⚠ {t}: rows are sorted without an index (filesort) — an index in ORDER BY order would avoid it.");
            if (extra.Contains("Using temporary", StringComparison.OrdinalIgnoreCase))
                bad.Add($"⚠ {t}: needs a temporary table (GROUP BY / DISTINCT / ORDER BY on other columns).");
            if (extra.Contains("Using join buffer", StringComparison.OrdinalIgnoreCase))
                bad.Add($"⚠ {t}: joined without an index (join buffer) — index the join column.");
            if (System.Text.RegularExpressions.Regex.IsMatch(extra, @"Using index(?!\s+condition)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                good.Add($"✓ {t}: answered from the index alone (covering index, no row lookups).");
        }
        var result = new List<string>();
        // real tables only: <derived2>, <union1,2> … are results of the query's own parts
        var real = rows.Where(r => !string.IsNullOrEmpty(r.Table) && !r.Table.StartsWith('<')).ToList();
        int scans = real.Count(r => string.Equals(r.Type, "ALL", StringComparison.OrdinalIgnoreCase));
        if (real.Count > 0)
            result.Add($"{real.Count} table read(s) · {scans} full table scan(s) · ≈{real.Sum(r => r.Rows ?? 0):N0} rows examined (estimate)");
        result.AddRange(bad);
        result.AddRange(good);
        result.AddRange(info.Distinct());
        if (result.Count == 0) result.Add("ℹ Nothing to report.");
        return result;
    }
}
