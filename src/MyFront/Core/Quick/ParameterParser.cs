using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MyFront.Core.Import;

namespace MyFront.Core.Quick;

public enum ParamType { Text, Int, Number, Date, DateTime }

/// <summary>A {{name}} or {{name:type}} placeholder.</summary>
public sealed record Placeholder(string Name, ParamType Type);

public sealed class ParameterException : Exception
{
    public ParameterException(string message) : base(message) { }
}

/// <summary>
/// {{name}} placeholders → real query parameters (never text concatenation).
/// <list type="bullet">
/// <item>{{date}} or '{{date}}' → @qp0 (quotes around a lone placeholder are dropped: it is a value, not text)</item>
/// <item>{{n:int}}, {{x:number}}, {{d:date}}, {{t:datetime}}, {{s:text}} pick the input and value type;
///   names containing "date"/"tarih" default to date</item>
/// <item>placeholders in comments are ignored; inside a longer literal ('%{{x}}%') they are an error —
///   write CONCAT('%', {{x}}, '%') instead</item>
/// </list>
/// </summary>
public static partial class ParameterParser
{
    [GeneratedRegex(@"\{\{\s*([\p{L}_][\p{L}\p{N}_]*)\s*(?::\s*([A-Za-z]+)\s*)?\}\}")]
    private static partial Regex PlaceholderRegex();

    public static bool HasPlaceholders(string sql) => Find(sql).Count > 0;

    /// <summary>Distinct placeholders in order of first appearance.</summary>
    public static List<Placeholder> Find(string sql)
    {
        var list = new List<Placeholder>();
        Scan(sql, (name, type) =>
        {
            int i = list.FindIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (i < 0) list.Add(new Placeholder(name, type ?? DefaultType(name)));
            else if (type is not null && list[i].Type != type) list[i] = list[i] with { Type = type.Value };
            return "";
        }, quotedOnlyErrors: false);
        return list;
    }

    /// <summary>The SQL with @qpN markers and the parameter values to bind.</summary>
    public static (string Sql, List<(string Name, object? Value)> Parameters) Bind(string sql, IReadOnlyDictionary<string, object?> values)
    {
        var binder = new ParameterBinder(values);
        var bound = binder.Bind(sql);
        return (bound, binder.Parameters);
    }

    /// <summary>Binds several statements with one shared set of parameters (same name → same @qpN).</summary>
    public sealed class ParameterBinder
    {
        private readonly IReadOnlyDictionary<string, object?> _values;
        private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

        public ParameterBinder(IReadOnlyDictionary<string, object?> values) => _values = values;

        public List<(string Name, object? Value)> Parameters { get; } = new();

        public string Bind(string sql) => Scan(sql, (name, _) =>
        {
            if (!_names.TryGetValue(name, out var p))
            {
                if (!_values.TryGetValue(name, out var value))
                    throw new ParameterException($"No value for {{{{{name}}}}}.");
                p = "@qp" + _names.Count;
                _names[name] = p;
                Parameters.Add((p, value));
            }
            return p;
        }, quotedOnlyErrors: true);
    }

    private static ParamType DefaultType(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("date") || n.Contains("tarih") ? ParamType.Date : ParamType.Text;
    }

    private static ParamType? ParseType(string? t) => t?.ToLowerInvariant() switch
    {
        null or "" => null,
        "int" or "integer" or "tamsayi" => ParamType.Int,
        "number" or "num" or "decimal" or "sayi" => ParamType.Number,
        "date" or "tarih" => ParamType.Date,
        "datetime" or "timestamp" => ParamType.DateTime,
        "text" or "string" or "metin" => ParamType.Text,
        _ => throw new ParameterException($"Unknown placeholder type \"{t}\" (use int, number, date, datetime or text)."),
    };

    /// <summary>Walks the SQL like the splitter does and lets <paramref name="replace"/> decide each placeholder's text.</summary>
    private static string Scan(string sql, Func<string, ParamType?, string> replace, bool quotedOnlyErrors)
    {
        var sb = new StringBuilder(sql.Length);
        int i = 0, n = sql.Length;
        var rx = PlaceholderRegex();
        while (i < n)
        {
            char c = sql[i];
            char next = i + 1 < n ? sql[i + 1] : '\0';
            if (c == '{' && next == '{')
            {
                var m = rx.Match(sql, i);
                if (m.Success && m.Index == i)
                {
                    sb.Append(replace(m.Groups[1].Value, ParseType(m.Groups[2].Value)));
                    i += m.Length;
                    continue;
                }
            }
            if (c is '\'' or '"' or '`')
            {
                int end = EndOfQuoted(sql, i);
                var literal = sql[i..end];
                var inner = literal.Length >= 2 ? literal[1..^1] : "";
                var whole = rx.Match(inner);
                if (c != '`' && whole.Success && inner.Trim() == whole.Value)
                    sb.Append(replace(whole.Groups[1].Value, ParseType(whole.Groups[2].Value))); // '{{x}}' is the value itself
                else if (whole.Success && c != '`')
                {
                    if (quotedOnlyErrors)
                        throw new ParameterException($"{whole.Value} is inside the text {literal}. Values can't be glued into text; " +
                                                     $"write it outside the quotes, e.g. CONCAT('%', {whole.Value}, '%').");
                    replace(whole.Groups[1].Value, ParseType(whole.Groups[2].Value)); // still list it in Find
                    sb.Append(literal);
                }
                else sb.Append(literal);
                i = end;
                continue;
            }
            if (c == '/' && next == '*')
            {
                int end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? n : end + 2;
                sb.Append(sql, i, end - i);
                i = end;
                continue;
            }
            if (c == '#' || (c == '-' && next == '-' && (i + 2 >= n || char.IsWhiteSpace(sql[i + 2]))))
            {
                int end = sql.IndexOf('\n', i);
                end = end < 0 ? n : end;
                sb.Append(sql, i, end - i);
                i = end;
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    private static int EndOfQuoted(string sql, int start)
    {
        char q = sql[start];
        int i = start + 1;
        while (i < sql.Length)
        {
            if (sql[i] == '\\' && q != '`') { i += 2; continue; }
            if (sql[i] == q)
            {
                if (i + 1 < sql.Length && sql[i + 1] == q) { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return sql.Length;
    }

    /// <summary>Text typed in the parameter dialog → the value to bind for that type.</summary>
    public static object? ConvertInput(string? text, ParamType type)
    {
        if (text is null) return null;
        switch (type)
        {
            case ParamType.Int:
                if (long.TryParse(text.Trim().Replace(".", "").Replace(" ", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
                throw new ParameterException($"'{text}' is not a whole number.");
            case ParamType.Number:
                if (ValueConverter.TryParseNumber(text, NumberFormat.Turkish, out var d)) return d;
                throw new ParameterException($"'{text}' is not a number.");
            case ParamType.Date:
            case ParamType.DateTime:
                if (ValueConverter.TryParseDate(text, out var dt)) return type == ParamType.Date ? dt.Date : dt;
                throw new ParameterException($"'{text}' is not a date (use 29.02.2024 or 2024-02-29).");
            default:
                return text;
        }
    }
}
