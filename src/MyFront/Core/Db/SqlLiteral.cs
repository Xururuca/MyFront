using System.Text;

namespace MyFront.Core.Db;

/// <summary>
/// String literals for DDL, where values can't be parameters (column defaults, comments).
/// Quotes are doubled; backslashes are doubled too unless the server runs with NO_BACKSLASH_ESCAPES.
/// </summary>
public static class SqlLiteral
{
    public static string Quote(string value, bool noBackslashEscapes)
    {
        var s = noBackslashEscapes ? value : value.Replace("\\", "\\\\");
        return "'" + s.Replace("'", "''") + "'";
    }

    /// <summary>
    /// The value of a literal as SHOW CREATE TABLE prints it: '…' or "…" with doubled quotes and backslash escapes,
    /// optionally with a character set introducer (_utf8mb4'…').
    /// </summary>
    public static string Unquote(string literal)
    {
        int start = 0;
        if (literal.StartsWith('_'))
            while (start < literal.Length && literal[start] is not ('\'' or '"')) start++;
        if (start >= literal.Length || literal.Length - start < 2) return literal;
        char q = literal[start];
        var sb = new StringBuilder(literal.Length);
        for (int i = start + 1; i < literal.Length; i++)
        {
            char c = literal[i];
            if (c == q)
            {
                if (i + 1 < literal.Length && literal[i + 1] == q) { sb.Append(q); i++; continue; }
                break;
            }
            if (c == '\\' && i + 1 < literal.Length)
            {
                char n = literal[++i];
                sb.Append(n switch
                {
                    '0' => "\0", 'n' => "\n", 'r' => "\r", 't' => "\t", 'b' => "\b", 'Z' => "\u001A",
                    '%' => "\\%", '_' => "\\_", // LIKE escapes keep their backslash
                    _ => n.ToString(),
                });
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
