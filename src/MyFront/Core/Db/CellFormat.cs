using System.Globalization;
using MySqlConnector;

namespace MyFront.Core.Db;

/// <summary>Turns DB values into the text shown in grids (and later written to CSV).</summary>
public static class CellFormat
{
    public const string NullText = "(NULL)";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>DBNull → null, valid MySqlDateTime → DateTime. Called for every fetched value.</summary>
    public static object? Normalize(object? v) => v switch
    {
        DBNull => null,
        MySqlDateTime m when m.IsValidDateTime => m.GetDateTime(),
        _ => v,
    };

    public static string ToDisplay(object? v, ColumnKind kind)
    {
        switch (v)
        {
            case null:
                return NullText;
            case string s:
                return s;
            case DateTime d:
                if (kind == ColumnKind.Date) return d.ToString("yyyy-MM-dd", Inv);
                return d.Ticks % TimeSpan.TicksPerSecond == 0
                    ? d.ToString("yyyy-MM-dd HH:mm:ss", Inv)
                    : d.ToString("yyyy-MM-dd HH:mm:ss.ffffff", Inv).TrimEnd('0');
            case TimeSpan t:
                return FormatTime(t);
            case byte[] b:
                return FormatBinary(b);
            case MySqlDateTime m:
                return kind == ColumnKind.Date
                    ? $"{m.Year:0000}-{m.Month:00}-{m.Day:00}"
                    : $"{m.Year:0000}-{m.Month:00}-{m.Day:00} {m.Hour:00}:{m.Minute:00}:{m.Second:00}" + (m.Microsecond == 0 ? "" : "." + m.Microsecond.ToString("000000", Inv).TrimEnd('0'));
            case double dbl:
                return dbl.ToString("R", Inv);
            case float f:
                return f.ToString("R", Inv);
            case IFormattable fmt:
                return fmt.ToString(null, Inv);
            default:
                return v.ToString() ?? "";
        }
    }

    public static string FormatTime(TimeSpan t)
    {
        var sign = t < TimeSpan.Zero ? "-" : "";
        t = t.Duration();
        var s = $"{sign}{(long)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
        var frac = t.Ticks % TimeSpan.TicksPerSecond;
        return frac == 0 ? s : s + "." + (frac / 10).ToString("000000", Inv).TrimEnd('0');
    }

    public static string FormatBinary(byte[] b)
    {
        if (b.Length > 64) return $"(BLOB {b.Length:N0} bytes)";
        return "0x" + Convert.ToHexString(b);
    }

    public static string FormatBytes(long? bytes)
    {
        if (bytes is null) return "";
        double v = bytes.Value;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes:N0} B" : $"{v:0.0} {units[u]}";
    }

    public static string FormatDuration(TimeSpan t) =>
        t.TotalSeconds < 1 ? $"{t.TotalMilliseconds:0} ms" :
        t.TotalMinutes < 1 ? $"{t.TotalSeconds:0.00} s" :
        $"{(int)t.TotalMinutes}:{t.Seconds:00} min";
}
