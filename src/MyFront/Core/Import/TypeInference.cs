using System.Globalization;

namespace MyFront.Core.Import;

/// <summary>A column of a new table as suggested from the data (editable in the wizard).</summary>
public sealed class InferredColumn
{
    public required string Source { get; init; }
    public required int SourceIndex { get; init; }
    public string Name { get; set; } = "";
    public string SqlType { get; set; } = "VARCHAR(255)";
    public bool Include { get; set; } = true;
    public long NonEmpty { get; init; }
    public int MaxLength { get; init; }
}

/// <summary>Suggests INT / BIGINT / DECIMAL(p,s) / DATE / DATETIME / VARCHAR(n) / TEXT per column from the rows.</summary>
public static class TypeInference
{
    private const int MaxRowBytes = 60_000; // InnoDB row limit is 65,535 bytes; utf8mb4 VARCHAR(n) counts 4n

    private sealed class Stats
    {
        public long NonEmpty;
        public bool AllNumeric = true, AllInteger = true, AllDate = true, AllBool = true, AnyTime;
        public decimal MaxAbs;
        public int MaxScale, MaxIntDigits, MaxLength;
    }

    /// <param name="maxRows">Rows to look at (all rows for typical files).</param>
    public static List<InferredColumn> Infer(IImportReader reader, NumberFormat nf, int maxRows, CancellationToken ct, Action<int>? progress = null)
    {
        var stats = reader.Headers.Select(_ => new Stats()).ToArray();
        int rows = 0;
        while (rows < maxRows && reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            var row = reader.Current;
            if (ImportFile.IsEmptyRow(row)) continue;
            rows++;
            if (rows % 5000 == 0) progress?.Invoke(rows);
            for (int c = 0; c < stats.Length && c < row.Length; c++) Observe(stats[c], row[c], nf);
        }
        progress?.Invoke(rows);

        var cols = new List<InferredColumn>();
        for (int c = 0; c < stats.Length; c++)
        {
            var s = stats[c];
            cols.Add(new InferredColumn
            {
                Source = reader.Headers[c],
                SourceIndex = c,
                Name = reader.Headers[c].Trim(),
                SqlType = Decide(s),
                NonEmpty = s.NonEmpty,
                MaxLength = s.MaxLength,
            });
        }
        FitRowSize(cols);
        return cols;
    }

    private static void Observe(Stats s, object? v, NumberFormat nf)
    {
        if (v is null || (v is string e && e.Trim().Length == 0)) return;
        if (v is string str) v = str.Trim();
        s.NonEmpty++;
        var text = ValueConverter.ToText(v);
        s.MaxLength = Math.Max(s.MaxLength, text.Length);
        if (v is not bool) s.AllBool = false;

        // numbers
        decimal? number = null;
        switch (v)
        {
            case double d when double.IsFinite(d):
                number = Convert.ToDecimal(d);
                break;
            case bool b:
                number = b ? 1 : 0;
                break;
            case string t:
                // codes with leading zeros ("00123") must stay text
                bool leadingZero = t.Length > 1 && t[0] == '0' && t[1] is not ('.' or ',');
                if (!leadingZero && ValueConverter.TryParseNumber(t, nf, out var p)) number = p;
                break;
        }
        if (number is { } n)
        {
            var abs = Math.Abs(n);
            if (abs > s.MaxAbs) s.MaxAbs = abs;
            if (n != decimal.Truncate(n)) s.AllInteger = false;
            var g = abs.ToString(CultureInfo.InvariantCulture);
            int dot = g.IndexOf('.');
            int scale = dot < 0 ? 0 : g.TrimEnd('0').Length - dot - 1;
            int intDigits = dot < 0 ? g.Length : dot;
            s.MaxScale = Math.Max(s.MaxScale, Math.Max(0, scale));
            s.MaxIntDigits = Math.Max(s.MaxIntDigits, intDigits);
        }
        else s.AllNumeric = false;

        // dates
        switch (v)
        {
            case DateTime dt:
                if (dt.TimeOfDay != TimeSpan.Zero) s.AnyTime = true;
                break;
            case string t when ValueConverter.TryParseDate(t, out var p) && !t.All(char.IsDigit):
                if (p.TimeOfDay != TimeSpan.Zero) s.AnyTime = true;
                break;
            default:
                s.AllDate = false;
                break;
        }
    }

    private static string Decide(Stats s)
    {
        if (s.NonEmpty == 0) return "VARCHAR(255)";
        if (s.AllBool) return "TINYINT(1)";
        if (s.AllNumeric)
        {
            if (s.AllInteger)
            {
                // headroom: amounts that already reach 100 million can pass INT's 2.1 billion next month
                if (s.MaxAbs <= 100_000_000) return "INT";
                if (s.MaxAbs <= long.MaxValue) return "BIGINT";
                return $"DECIMAL({Math.Min(65, s.MaxIntDigits)},0)";
            }
            int scale = Math.Min(s.MaxScale, 10);
            int precision = Math.Min(65, Math.Max(s.MaxIntDigits + scale, scale + 1));
            return $"DECIMAL({precision},{scale})";
        }
        if (s.AllDate) return s.AnyTime ? "DATETIME" : "DATE";
        // 25% headroom so slightly longer texts in the next file still fit
        return TextType((int)Math.Ceiling(s.MaxLength * 1.25));
    }

    internal static string TextType(int maxLength)
    {
        if (maxLength > 16_000) return "MEDIUMTEXT"; // TEXT holds 65,535 bytes ≈ 16,383 utf8mb4 characters
        int[] steps = { 10, 20, 30, 50, 100, 150, 200, 255 };
        foreach (var step in steps)
            if (maxLength <= step) return $"VARCHAR({step})";
        if (maxLength <= 1000) return $"VARCHAR({(maxLength + 99) / 100 * 100})";
        if (maxLength <= 4000) return $"VARCHAR({(maxLength + 499) / 500 * 500})";
        return "TEXT";
    }

    /// <summary>Turns the widest VARCHARs into TEXT until the row fits InnoDB's 65,535-byte limit.</summary>
    private static void FitRowSize(List<InferredColumn> cols)
    {
        static int Bytes(string type)
        {
            if (type.StartsWith("VARCHAR(", StringComparison.Ordinal))
                return int.Parse(type[8..^1], CultureInfo.InvariantCulture) * 4 + 2;
            return type.Contains("TEXT") ? 12 : 16;
        }
        while (cols.Where(c => c.Include).Sum(c => Bytes(c.SqlType)) > MaxRowBytes)
        {
            var widest = cols.Where(c => c.Include && c.SqlType.StartsWith("VARCHAR(", StringComparison.Ordinal))
                .OrderByDescending(c => Bytes(c.SqlType)).FirstOrDefault();
            if (widest is null) break;
            widest.SqlType = "TEXT";
        }
    }
}
