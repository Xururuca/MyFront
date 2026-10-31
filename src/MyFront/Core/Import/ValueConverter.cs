using System.Globalization;
using System.Text.RegularExpressions;
using MyFront.Core.Db;

namespace MyFront.Core.Import;

/// <summary>How numbers typed as text are written: "1.234,56" (Turkish) or "1,234.56".</summary>
public enum NumberFormat { Turkish, Dot }

public enum TargetKind { Integer, Decimal, Float, Date, DateTime, Time, Year, Text, Binary }

/// <summary>A destination column with what the converter needs to know about its type.</summary>
public sealed class TargetColumn
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required TargetKind Kind { get; init; }
    public bool Nullable { get; init; } = true;
    public int? MaxLength { get; init; }
    public bool IsKey { get; init; }

    public static TargetColumn From(TableColumn c) => From(c.Name, c.Type, c.Nullable, c.Key is "PRI" or "UNI");

    public static TargetColumn From(string name, string type, bool nullable = true, bool isKey = false)
    {
        var t = type.Trim().ToLowerInvariant();
        var kind = t switch
        {
            _ when StartsWithAny(t, "tinyint", "smallint", "mediumint", "int", "integer", "bigint", "bit", "bool", "serial") => TargetKind.Integer,
            _ when StartsWithAny(t, "decimal", "numeric", "dec", "fixed") => TargetKind.Decimal,
            _ when StartsWithAny(t, "float", "double", "real") => TargetKind.Float,
            _ when StartsWithAny(t, "datetime", "timestamp") => TargetKind.DateTime,
            _ when t.StartsWith("date", StringComparison.Ordinal) => TargetKind.Date,
            _ when t.StartsWith("time", StringComparison.Ordinal) => TargetKind.Time,
            _ when t.StartsWith("year", StringComparison.Ordinal) => TargetKind.Year,
            _ when StartsWithAny(t, "binary", "varbinary", "blob", "tinyblob", "mediumblob", "longblob") => TargetKind.Binary,
            _ => TargetKind.Text,
        };
        int? max = null;
        var m = Regex.Match(t, @"^(?:var)?char\s*\((\d+)\)");
        if (m.Success) max = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        else if (t is "tinytext") max = 255;
        return new TargetColumn { Name = name, Type = type, Kind = kind, Nullable = nullable, MaxLength = max, IsKey = isKey };
    }

    private static bool StartsWithAny(string t, params string[] prefixes) => prefixes.Any(p => t.StartsWith(p, StringComparison.Ordinal));
}

/// <summary>Turns a raw Excel/CSV value into the parameter value for a target column (null = SQL NULL).</summary>
public static class ValueConverter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly Regex TrThousands = new(@"^[+-]?\d{1,3}(\.\d{3})+$", RegexOptions.Compiled);
    private static readonly Regex DotThousands = new(@"^[+-]?\d{1,3}(,\d{3})+$", RegexOptions.Compiled);

    private static readonly string[] DateFormats =
    {
        "dd.MM.yyyy", "d.M.yyyy", "dd.MM.yyyy HH:mm", "d.M.yyyy H:mm", "dd.MM.yyyy HH:mm:ss", "d.M.yyyy H:mm:ss",
        "yyyy-MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFF", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFFK",
        "dd/MM/yyyy", "d/M/yyyy", "dd/MM/yyyy HH:mm", "dd/MM/yyyy HH:mm:ss", "yyyy/MM/dd", "yyyyMMdd",
        "dd-MM-yyyy", "d-M-yyyy",
    };

    public static object? Convert(object? raw, TargetColumn col, NumberFormat nf, bool trim, out string? error)
    {
        error = null;
        if (raw is null) return null;
        if (raw is string s)
        {
            if (trim) s = s.Trim();
            if (s.Length == 0) return null;
            raw = s;
        }

        switch (col.Kind)
        {
            case TargetKind.Integer:
            case TargetKind.Year:
            {
                decimal? d = raw switch
                {
                    bool b => b ? 1 : 0,
                    double dbl when double.IsFinite(dbl) => (decimal)dbl,
                    string str when TryParseNumber(str, nf, out var p) => p,
                    _ => null,
                };
                if (d is null) return Fail(raw, col, "is not a number", out error);
                if (d != decimal.Truncate(d.Value)) return Fail(raw, col, "is not a whole number", out error);
                return d is >= long.MinValue and <= long.MaxValue ? (object)(long)d.Value : d.Value;
            }
            case TargetKind.Decimal:
            case TargetKind.Float:
            {
                decimal? d = raw switch
                {
                    bool b => b ? 1 : 0,
                    double dbl when double.IsFinite(dbl) => System.Convert.ToDecimal(dbl), // 15 significant digits, no binary noise
                    string str when TryParseNumber(str, nf, out var p) => p,
                    _ => null,
                };
                if (d is null) return Fail(raw, col, "is not a number", out error);
                return col.Kind == TargetKind.Float ? (double)d.Value : d.Value;
            }
            case TargetKind.Date:
            case TargetKind.DateTime:
            {
                DateTime? dt = raw switch
                {
                    DateTime x => x,
                    double serial when serial is >= 1 and < 2958466 => DateTime.FromOADate(serial),
                    string str when TryParseDate(str, out var p) => p,
                    _ => null,
                };
                if (dt is null) return Fail(raw, col, "is not a date", out error);
                return col.Kind == TargetKind.Date ? dt.Value.Date : dt.Value;
            }
            case TargetKind.Time:
            {
                TimeSpan? t = raw switch
                {
                    TimeSpan x => x,
                    DateTime x => x.TimeOfDay,
                    double frac when frac is >= 0 and < 35 => TimeSpan.FromDays(frac),
                    string str when TimeSpan.TryParse(str, Inv, out var p) => p,
                    _ => null,
                };
                if (t is null) return Fail(raw, col, "is not a time", out error);
                return t.Value;
            }
            default:
            {
                var text = ToText(raw);
                if (col.MaxLength is { } max && text.Length > max)
                    return Fail(raw, col, $"is too long ({text.Length} > {max} characters)", out error);
                return text;
            }
        }
    }

    private static object? Fail(object raw, TargetColumn col, string what, out string error)
    {
        var shown = ToText(raw);
        if (shown.Length > 60) shown = shown[..60] + "…";
        error = $"{col.Name}: '{shown}' {what} ({col.Type})";
        return null;
    }

    /// <summary>Text form of a raw value; whole numbers never get ".0" or exponent notation.</summary>
    public static string ToText(object raw) => raw switch
    {
        string s => s,
        double d when d == Math.Truncate(d) && Math.Abs(d) < 1e15 => ((long)d).ToString(Inv),
        double d => d.ToString("G15", Inv),
        DateTime dt => dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd", Inv) : dt.ToString("yyyy-MM-dd HH:mm:ss", Inv),
        TimeSpan ts => CellFormat.FormatTime(ts),
        bool b => b ? "1" : "0",
        IFormattable f => f.ToString(null, Inv),
        _ => raw.ToString() ?? "",
    };

    /// <summary>"1.234,56" / "1,234.56" / "-12,5" / "1e3"; a lone thousands group ("1.234" Turkish) means 1234.</summary>
    public static bool TryParseNumber(string s, NumberFormat nf, out decimal value)
    {
        value = 0;
        s = s.Trim().Replace(" ", "").Replace(" ", "");
        if (s.Length == 0) return false;
        if (nf == NumberFormat.Turkish)
        {
            if (s.Contains(',')) s = s.Replace(".", "").Replace(',', '.');
            else if (TrThousands.IsMatch(s)) s = s.Replace(".", "");
        }
        else
        {
            if (s.Contains('.')) s = s.Replace(",", "");
            else if (DotThousands.IsMatch(s)) s = s.Replace(",", "");
            else s = s.Replace(',', '.');
        }
        return decimal.TryParse(s, NumberStyles.Float, Inv, out value);
    }

    public static bool TryParseDate(string s, out DateTime value) =>
        DateTime.TryParseExact(s.Trim(), DateFormats, Inv, DateTimeStyles.AllowWhiteSpaces, out value)
        || DateTime.TryParse(s, Tr, DateTimeStyles.AllowWhiteSpaces, out value);
}
