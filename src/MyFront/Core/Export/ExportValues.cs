using System.Globalization;
using System.Text;
using MyFront.Core.Db;
using MySqlConnector;

namespace MyFront.Core.Export;

/// <summary>How DB values become Excel / CSV cells.</summary>
public static class ExportValues
{
    public const int ExcelMaxText = 32_767;           // Excel's per-cell character limit
    public const int ExcelMaxRows = 1_048_576;        // including the header row
    private const long MaxExactInteger = 999_999_999_999_999; // Excel keeps 15 significant digits
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Value for an Excel cell: numbers stay numbers, dates stay dates, NULL → null (empty cell).
    /// Values Excel would silently damage (16+ digit numbers, dates before 1900, zero dates) become text.
    /// </summary>
    public static object? ForExcel(object? v)
    {
        switch (v)
        {
            case null or DBNull:
                return null;
            case string s:
                return s.Length > ExcelMaxText ? s[..ExcelMaxText] : s;
            case int or short or sbyte or byte or ushort or bool or float or double:
                return v;
            case long l:
                return l is > MaxExactInteger or < -MaxExactInteger ? l.ToString(Inv) : v;
            case uint u:
                return (long)u;
            case ulong ul:
                return ul > MaxExactInteger ? ul.ToString(Inv) : (long)ul;
            case decimal d:
                return SignificantDigits(d) > 15 ? d.ToString(Inv) : v;
            case DateTime dt:
                return dt.Year < 1900 ? CellFormat.ToDisplay(dt, ColumnKind.DateTime) : dt;
            case TimeSpan ts:
                return ts < TimeSpan.Zero ? CellFormat.FormatTime(ts) : ts;
            case MySqlDateTime m:
                return CellFormat.ToDisplay(m, ColumnKind.DateTime);
            case byte[] b:
                return CellFormat.FormatBinary(b);
            case Guid g:
                return g.ToString();
            default:
                return Convert.ToString(v, Inv);
        }
    }

    private static int SignificantDigits(decimal d)
    {
        var s = Math.Abs(d).ToString(Inv);
        int digits = 0;
        bool leading = true;
        foreach (var c in s)
        {
            if (c is < '0' or > '9') continue;
            if (leading && c == '0') continue;
            leading = false;
            digits++;
        }
        // trailing zeros after the decimal point don't need precision
        if (s.Contains('.'))
            for (int i = s.Length - 1; i >= 0 && s[i] == '0'; i--) digits--;
        return digits;
    }

    /// <summary>Text for a CSV field (without quoting). NULL → empty.</summary>
    public static string ForCsv(object? v, ColumnKind kind, char decimalSeparator)
    {
        switch (v)
        {
            case null or DBNull:
                return "";
            case string s:
                return s;
            case decimal or double or float:
            {
                var text = v is double dbl ? dbl.ToString("R", Inv) : v is float f ? f.ToString("R", Inv) : ((decimal)v).ToString(Inv);
                return decimalSeparator == '.' ? text : text.Replace('.', decimalSeparator);
            }
            default:
                return CellFormat.ToDisplay(v, kind);
        }
    }

    /// <summary>Excel sheet name rules: max 31 chars, none of []:*?/\, unique (case-insensitive), not empty.</summary>
    public static string SheetName(string wanted, ISet<string> used)
    {
        var sb = new StringBuilder();
        foreach (var c in wanted)
            sb.Append(c is '[' or ']' or ':' or '*' or '?' or '/' or '\\' ? '_' : c);
        var name = sb.ToString().Trim().Trim('\'');
        if (name.Length == 0 || name.Equals("History", StringComparison.OrdinalIgnoreCase)) name = "Sheet";
        if (name.Length > 31) name = name[..31];
        var candidate = name;
        for (int n = 2; used.Contains(candidate); n++)
        {
            var suffix = $" ({n})";
            candidate = (name.Length + suffix.Length > 31 ? name[..(31 - suffix.Length)] : name) + suffix;
        }
        used.Add(candidate);
        return candidate;
    }

    /// <summary>Safe file name from a table or query name.</summary>
    public static string FileName(string wanted)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(wanted.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        if (name.Length > 80) name = name[..80];
        return name.Length == 0 ? "export" : name;
    }
}
