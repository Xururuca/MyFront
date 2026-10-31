using System.Text;

namespace MyFront.Core.Db;

/// <summary>
/// Formats SQL for reading: one clause per line (FROM, WHERE, GROUP BY …), JOINs and AND / OR on their own
/// indented lines, long column lists one per line, subqueries indented, reserved words in capitals.
/// Only whitespace and the case of reserved words change: strings, `names`, comments and {{parameters}} stay
/// exactly as they are, and a name before ( keeps its spacing (COUNT(*) must not become COUNT (*)).
/// SELECT / WITH / INSERT / REPLACE / UPDATE / DELETE statements are formatted; everything else (CREATE, ALTER,
/// procedures …) and the text between statements (delimiters, comments, blank lines) are left as they are.
/// </summary>
public static class SqlFormatter
{
    private const string Tab = "    ";
    /// <summary>A comma list longer than this goes one item per line.</summary>
    private const int MaxInline = 60;

    /// <summary>Formats every statement of a script; <paramref name="newLine"/> is the editor's line ending.</summary>
    public static string Format(string script, string newLine = "\n")
    {
        var sb = new StringBuilder(script.Length + script.Length / 4);
        int pos = 0;
        bool lineComment = false;
        foreach (var st in SqlSplitter.Split(script))
        {
            var gap = Gap(script[pos..st.Start], newLine, lineComment);
            // two statements on one line: the second starts a line of its own
            if (pos > 0 && !gap.Contains('\n')) gap = gap.TrimEnd() + newLine;
            sb.Append(gap);
            sb.Append(FormatStatement(script[st.Start..st.End], newLine, out lineComment));
            pos = st.End;
        }
        sb.Append(Gap(script[pos..], newLine, lineComment));
        return sb.ToString();
    }

    /// <summary>The text after a statement: no blank before its ";", and after a line comment the rest starts a new line (or the ; would be inside the comment).</summary>
    private static string Gap(string gap, string newLine, bool afterLineComment)
    {
        if (afterLineComment)
            return gap.Length == 0 || gap.StartsWith('\n') || gap.StartsWith("\r\n") ? gap : newLine + gap.TrimStart(' ', '\t');
        var trimmed = gap.TrimStart(' ', '\t');
        return trimmed.StartsWith(';') ? trimmed : gap;
    }

    /// <summary>Where the caret goes after formatting: at the same code character (or just after it, when it was on a blank).</summary>
    public static int MapPosition(string before, string after, int position)
    {
        position = Math.Clamp(position, 0, before.Length);
        int count = before.Take(position).Count(c => !char.IsWhiteSpace(c));
        bool onCode = position < before.Length && !char.IsWhiteSpace(before[position]);
        if (count == 0 && !onCode) return 0;
        int seen = 0;
        for (int i = 0; i < after.Length; i++)
        {
            if (char.IsWhiteSpace(after[i])) continue;
            if (onCode && seen == count) return i;
            if (++seen == count && !onCode) return i + 1;
        }
        return after.Length;
    }

    // ---- tokens ----------------------------------------------------------------------------------

    private enum K { Word, Quoted, Ident, Number, Op, Open, Close, Comma, Dot, Semi, LineComment, BlockComment }

    /// <param name="Space">Whitespace came before it in the source.</param>
    /// <param name="Newline">A line break came before it in the source.</param>
    private readonly record struct Tok(K Kind, string Text, bool Space, bool Newline)
    {
        /// <summary>A word in capitals ("" for anything else).</summary>
        public string Upper { get; } = Kind == K.Word && !Text.StartsWith('{') ? Text.ToUpperInvariant() : "";
        public bool IsComment => Kind is K.LineComment or K.BlockComment;
    }

    private static readonly string[] Ops3 = { "<=>", "->>" };
    private static readonly string[] Ops2 = { "<=", ">=", "<>", "!=", ":=", "||", "&&", "<<", ">>", "->" };

    private static List<Tok> Tokenize(string s)
    {
        var list = new List<Tok>();
        bool space = false, newline = false;
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c))
            {
                space = true;
                if (c == '\n') newline = true;
                i++;
                continue;
            }
            int start = i;
            char next = i + 1 < s.Length ? s[i + 1] : '\0';
            K kind;
            if (c is '\'' or '"' or '`')
            {
                i = SkipQuoted(s, i, c);
                kind = c == '`' ? K.Ident : K.Quoted;
            }
            else if (c == '#' || (c == '-' && next == '-' && (i + 2 >= s.Length || char.IsWhiteSpace(s[i + 2]))))
            {
                i = s.IndexOf('\n', i);
                if (i < 0) i = s.Length;
                kind = K.LineComment;
            }
            else if (c == '/' && next == '*')
            {
                int e = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = e < 0 ? s.Length : e + 2;
                kind = K.BlockComment;
            }
            else if (c == '{' && next == '{')
            {
                int e = s.IndexOf("}}", i + 2, StringComparison.Ordinal);
                i = e < 0 ? s.Length : e + 2;
                kind = K.Word; // a {{parameter}}: kept as it is
            }
            else if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(next) && (list.Count == 0 || list[^1].Kind is not (K.Word or K.Ident or K.Close))))
            {
                bool hex = c == '0' && next is 'x' or 'X';
                i++;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '.' or '_'
                                        || (!hex && s[i] is '+' or '-' && s[i - 1] is 'e' or 'E' && char.IsAsciiDigit(s[start]))))
                    i++;
                kind = K.Number;
            }
            else if (IsWordChar(c) || c == '@')
            {
                i++;
                while (i < s.Length && (IsWordChar(s[i]) || s[i] == '@')) i++;
                kind = K.Word;
            }
            else
            {
                kind = c switch { '(' => K.Open, ')' => K.Close, ',' => K.Comma, '.' => K.Dot, ';' => K.Semi, _ => K.Op };
                i += kind != K.Op ? 1
                    : Ops3.Any(o => string.CompareOrdinal(s, i, o, 0, 3) == 0) ? 3
                    : Ops2.Any(o => string.CompareOrdinal(s, i, o, 0, 2) == 0) ? 2 : 1;
            }
            var text = s[start..i];
            if (kind == K.LineComment) text = text.TrimEnd('\r');
            list.Add(new Tok(kind, text, space, newline));
            space = newline = false;
        }
        return list;
    }

    /// <summary>The next word that isn't a comment, in capitals ("" when there is none).</summary>
    private static string NextCode(List<Tok> toks, int from)
    {
        for (int j = from; j < toks.Count; j++)
            if (!toks[j].IsComment) return toks[j].Upper;
        return "";
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    private static int SkipQuoted(string s, int i, char quote)
    {
        for (i++; i < s.Length; i++)
        {
            if (s[i] == '\\' && quote != '`') { i++; continue; }
            if (s[i] != quote) continue;
            if (i + 1 < s.Length && s[i + 1] == quote) { i++; continue; }
            return i + 1;
        }
        return s.Length;
    }

    // ---- words -----------------------------------------------------------------------------------

    /// <summary>Reserved words (MariaDB and MySQL): they can't be unquoted names, so capitals never change a name.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "ADD", "ALL", "ALTER", "ANALYZE", "AND", "AS", "ASC", "BETWEEN", "BIGINT", "BINARY", "BLOB", "BOTH", "BY", "CALL",
        "CASCADE", "CASE", "CHANGE", "CHAR", "CHARACTER", "CHECK", "COLLATE", "COLUMN", "CONSTRAINT", "CONVERT", "CREATE",
        "CROSS", "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP", "CURRENT_USER", "DATABASE", "DECIMAL", "DEFAULT",
        "DELAYED", "DELETE", "DESC", "DESCRIBE", "DISTINCT", "DISTINCTROW", "DIV", "DOUBLE", "DROP", "DUAL", "ELSE",
        "ELSEIF", "EXCEPT", "EXISTS", "EXPLAIN", "FALSE", "FLOAT", "FOR", "FORCE", "FOREIGN", "FROM", "FULLTEXT", "GROUP",
        "HAVING", "HIGH_PRIORITY", "IF", "IGNORE", "IN", "INDEX", "INNER", "INSERT", "INT", "INTEGER", "INTERSECT",
        "INTERVAL", "INTO", "IS", "JOIN", "KEY", "KEYS", "LEADING", "LEFT", "LIKE", "LIMIT", "LOCALTIME", "LOCALTIMESTAMP",
        "LOCK", "LOW_PRIORITY", "MATCH", "MOD", "NATURAL", "NOT", "NULL", "NUMERIC", "ON", "OR", "ORDER", "OUTER",
        "OUTFILE", "PARTITION", "PRIMARY", "RANGE", "RECURSIVE", "REFERENCES", "REGEXP", "RENAME", "REPLACE", "RIGHT",
        "RLIKE", "SCHEMA", "SELECT", "SEPARATOR", "SET", "SHOW", "SMALLINT", "SQL_BIG_RESULT", "SQL_CALC_FOUND_ROWS",
        "SQL_SMALL_RESULT", "STRAIGHT_JOIN", "TABLE", "THEN", "TINYINT", "TO", "TRAILING", "TRUE", "UNION", "UNIQUE",
        "UNSIGNED", "UPDATE", "USE", "USING", "UTC_DATE", "UTC_TIME", "UTC_TIMESTAMP", "VALUES", "VARBINARY", "VARCHAR",
        "WHEN", "WHERE", "WITH", "XOR",
    };

    /// <summary>Common functions: in capitals only when written directly before ( (so never a table or column name).</summary>
    private static readonly HashSet<string> Functions = new(StringComparer.Ordinal)
    {
        "ABS", "AVG", "CAST", "CEIL", "CEILING", "CHAR_LENGTH", "COALESCE", "CONCAT", "CONCAT_WS", "COUNT", "CURDATE",
        "CURTIME", "DATE", "DATEDIFF", "DATE_ADD", "DATE_FORMAT", "DATE_SUB", "DAY", "DENSE_RANK", "EXTRACT", "FIELD",
        "FIND_IN_SET", "FIRST_VALUE", "FLOOR", "FORMAT", "FROM_UNIXTIME", "GREATEST", "GROUP_CONCAT", "HOUR", "IFNULL",
        "INSTR", "JSON_ARRAY", "JSON_EXTRACT", "JSON_OBJECT", "JSON_UNQUOTE", "LAG", "LAST_INSERT_ID", "LAST_VALUE",
        "LCASE", "LEAD", "LEAST", "LENGTH", "LOCATE", "LOWER", "LPAD", "LTRIM", "MAX", "MD5", "MIN", "MINUTE", "MONTH",
        "NOW", "NTILE", "NULLIF", "POSITION", "RAND", "RANK", "ROUND", "ROW_NUMBER", "RPAD", "RTRIM", "SECOND", "SHA1",
        "SHA2", "STR_TO_DATE", "SUBSTR", "SUBSTRING", "SUBSTRING_INDEX", "SUM", "TIME", "TIMESTAMP", "TIMESTAMPDIFF",
        "TRIM", "UCASE", "UNIX_TIMESTAMP", "UPPER", "UUID", "WEEK", "YEAR",
    };

    /// <summary>A table name follows these (INSERT INTO date(…) must stay as it is).</summary>
    private static readonly HashSet<string> TableBefore = new(StringComparer.Ordinal) { "INTO", "TABLE", "FROM", "JOIN", "UPDATE" };

    /// <summary>Words that stay on the SELECT line before a one-per-line column list.</summary>
    private static readonly HashSet<string> SelectModifiers = new(StringComparer.Ordinal)
    {
        "ALL", "DISTINCT", "DISTINCTROW", "HIGH_PRIORITY", "STRAIGHT_JOIN", "SQL_SMALL_RESULT", "SQL_BIG_RESULT",
        "SQL_BUFFER_RESULT", "SQL_CACHE", "SQL_NO_CACHE", "SQL_CALC_FOUND_ROWS",
    };

    private static readonly HashSet<string> Listed = new(StringComparer.Ordinal)
    {
        "SELECT", "FROM", "GROUP BY", "ORDER BY", "SET", "VALUES", "ON DUPLICATE KEY UPDATE",
    };

    private static readonly HashSet<string> Formatted = new(StringComparer.Ordinal) { "SELECT", "WITH", "INSERT", "REPLACE", "UPDATE", "DELETE" };

    // ---- layout ----------------------------------------------------------------------------------

    /// <summary>A pair of parentheses; <see cref="Sub"/> = a subquery, whose clauses start lines.</summary>
    private sealed class Frame
    {
        public bool Sub;
        /// <summary>Indent of its clauses.</summary>
        public int Base;
        /// <summary>Indent of the line its ( is on (where a subquery's ) goes).</summary>
        public int CloseIndent;
        public string Clause = "";
        /// <summary>The clause's comma list goes one item per line.</summary>
        public bool List;
        /// <summary>Between JOIN and the next clause: AND / OR of the ON condition go one level deeper.</summary>
        public bool Join;
        /// <summary>BETWEEN seen: its AND stays on the line.</summary>
        public bool Between;
        public int Cases;
    }

    private static string FormatStatement(string sql, string nl, out bool endsWithLineComment)
    {
        endsWithLineComment = false;
        var toks = Tokenize(sql);
        int first = toks.FindIndex(t => !t.IsComment);
        if (first < 0) return sql;
        var lead = toks[first];
        bool subStart = lead.Kind == K.Open && NextCode(toks, first + 1) is "SELECT" or "WITH";
        if (!Formatted.Contains(lead.Upper) && !subStart) return sql;
        var statement = lead.Upper;

        var o = new StringBuilder(sql.Length + sql.Length / 4);
        int lineIndent = 0;
        bool lineEmpty = true;
        var frames = new Stack<Frame>();
        frames.Push(new Frame { Sub = true });
        Tok? prev = null;
        bool prevUnary = false, pendingList = false;

        void NewLine(int indent)
        {
            if (lineEmpty)
            {
                // nothing written on this line yet: just move it
                int cut = o.Length;
                while (cut > 0 && o[cut - 1] == ' ') cut--;
                o.Length = cut;
            }
            else
            {
                while (o.Length > 0 && o[^1] == ' ') o.Length--;
                o.Append(nl);
            }
            for (int k = 0; k < indent; k++) o.Append(Tab);
            lineIndent = indent;
            lineEmpty = true;
        }

        void Write(string text, bool space)
        {
            if (!lineEmpty && space) o.Append(' ');
            o.Append(text);
            lineEmpty = false;
        }

        string Upper(int i) => i < toks.Count ? toks[i].Upper : "";

        // how many tokens the clause keyword at i has (0 = none)
        int ClauseAt(int i, Frame f, out string name)
        {
            name = Upper(i);
            string before = i > 0 ? toks[i - 1].Upper : "";
            bool dotted = i > 0 && toks[i - 1].Kind == K.Dot || i + 1 < toks.Count && toks[i + 1].Kind == K.Dot;
            if (dotted) return 0;
            switch (name)
            {
                case "SELECT" or "WHERE" or "HAVING" or "LIMIT":
                    return 1;
                case "WITH" when i > 0 && toks[i - 1].Kind == K.Open: // (WITH … in a subquery, not GROUP BY … WITH ROLLUP
                    return 1;
                case "FROM":
                    return before is "DELETE" or "LOW_PRIORITY" or "QUICK" or "IGNORE" ? 0 : 1;
                case "GROUP" or "ORDER" when Upper(i + 1) == "BY":
                    name += " BY";
                    return 2;
                case "UNION" or "EXCEPT" or "INTERSECT":
                    if (Upper(i + 1) is "ALL" or "DISTINCT") { name += " " + Upper(i + 1); return 2; }
                    return 1;
                case "VALUES":
                    // VALUES(col) in ON DUPLICATE KEY UPDATE is a function
                    return f.Clause is "ON DUPLICATE KEY UPDATE" or "SET" ? 0 : 1;
                case "SET":
                    return statement is "UPDATE" or "INSERT" or "REPLACE" && before is not ("CHARACTER" or "CHARSET") ? 1 : 0;
                case "ON" when Upper(i + 1) == "DUPLICATE" && Upper(i + 2) == "KEY" && Upper(i + 3) == "UPDATE":
                    name = "ON DUPLICATE KEY UPDATE";
                    return 4;
                default:
                    return 0;
            }
        }

        // how many tokens the JOIN keyword at i has (0 = none): [NATURAL] [LEFT|RIGHT|FULL|INNER|CROSS] [OUTER] JOIN, STRAIGHT_JOIN
        int JoinAt(int i)
        {
            int j = i;
            if (Upper(j) == "STRAIGHT_JOIN") return 1;
            if (Upper(j) == "NATURAL") j++;
            if (Upper(j) is "LEFT" or "RIGHT" or "FULL" or "INNER" or "CROSS")
            {
                if (j + 1 < toks.Count && toks[j + 1].Kind == K.Open) return 0; // LEFT(…) is a function
                j++;
            }
            if (Upper(j) == "OUTER") j++;
            return Upper(j) == "JOIN" ? j - i + 1 : 0;
        }

        // whether the clause body from i is a comma list too long for one line
        bool LongList(int i, Frame f)
        {
            int depth = 0, commas = 0, length = 0;
            for (int j = i; j < toks.Count; j++)
            {
                var t = toks[j];
                if (t.Kind == K.Open) depth++;
                else if (t.Kind == K.Close && --depth < 0) break;
                else if (depth == 0)
                {
                    if (t.Kind == K.Semi || t.Kind == K.Word && (ClauseAt(j, f, out _) > 0 || JoinAt(j) > 0)) break;
                    if (t.Kind == K.Comma) commas++;
                    if (t.Kind == K.LineComment) length += MaxInline;
                }
                length += t.Text.Length + 1;
            }
            return commas > 0 && length > MaxInline;
        }

        string Cased(int i)
        {
            var t = toks[i];
            if (t.Kind != K.Word || t.Text.StartsWith('{')) return t.Text;
            if (i > 0 && toks[i - 1].Kind == K.Dot || i + 1 < toks.Count && toks[i + 1].Kind == K.Dot) return t.Text; // part of a name
            var u = t.Upper;
            if (Reserved.Contains(u)) return u;
            bool call = i + 1 < toks.Count && toks[i + 1].Kind == K.Open && !toks[i + 1].Space;
            if (call && Functions.Contains(u) && !(i > 0 && TableBefore.Contains(toks[i - 1].Upper))) return u;
            var f = frames.Peek();
            if (u == "END" && f.Cases > 0) return u;
            if (u == "OFFSET" && f.Clause == "LIMIT") return u;
            if (u == "ROLLUP" && i > 0 && toks[i - 1].Upper == "WITH") return u;
            if (u is "DUPLICATE" && Upper(i + 1) == "KEY") return u;
            return t.Text;
        }

        bool SpaceBefore(Tok t)
        {
            if (prev is not { } p) return false;
            if (t.Kind is K.Comma or K.Semi or K.Close or K.Dot || p.Kind is K.Dot or K.Open || prevUnary) return false;
            if (t.Kind == K.Open) return t.Space || p.Kind is not (K.Word or K.Ident);
            if (t.Kind == K.Quoted && p.Kind == K.Word && !t.Space) return false; // _utf8'…', X'…'
            return true;
        }

        bool lastWasLineComment = false;
        for (int i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            var f = frames.Peek();
            lastWasLineComment = false;

            if (t.IsComment)
            {
                if (t.Newline && !lineEmpty) NewLine(lineIndent);
                Write(t.Text, true);
                if (t.Kind == K.LineComment)
                {
                    NewLine(lineIndent);
                    lastWasLineComment = true;
                }
                continue; // prev stays the last code token
            }

            if (pendingList && !(t.Kind == K.Word && SelectModifiers.Contains(t.Upper)))
            {
                NewLine(f.Base + 1);
                pendingList = false;
            }

            if (f.Sub && t.Kind == K.Word)
            {
                int n = ClauseAt(i, f, out var clause);
                if (n > 0)
                {
                    f.Clause = clause;
                    f.Join = f.Between = false;
                    if (!lineEmpty || lineIndent != f.Base) NewLine(f.Base);
                    Write(string.Join(" ", Enumerable.Range(i, n).Select(Cased)), true);
                    f.List = Listed.Contains(clause) && LongList(i + n, f);
                    pendingList = f.List;
                    prev = toks[i + n - 1];
                    prevUnary = false;
                    i += n - 1;
                    continue;
                }
                n = JoinAt(i);
                if (n > 0)
                {
                    f.Join = true;
                    f.List = false;
                    NewLine(f.Base + 1);
                    Write(string.Join(" ", Enumerable.Range(i, n).Select(Cased)), true);
                    prev = toks[i + n - 1];
                    prevUnary = false;
                    i += n - 1;
                    continue;
                }
                if (t.Upper is "AND" or "OR" or "XOR" && f.Cases == 0 && (f.Clause is "WHERE" or "HAVING" || f.Join))
                {
                    if (f.Between) f.Between = false;
                    else NewLine(f.Base + (f.Join ? 2 : 1));
                }
            }

            switch (t.Kind)
            {
                case K.Open:
                {
                    bool sub = NextCode(toks, i + 1) is "SELECT" or "WITH";
                    Write("(", SpaceBefore(t));
                    frames.Push(sub
                        ? new Frame { Sub = true, Base = lineIndent + 1, CloseIndent = lineIndent }
                        : new Frame { Base = f.Base, CloseIndent = lineIndent });
                    break;
                }
                case K.Close:
                    if (frames.Count > 1 && frames.Pop() is { Sub: true } closed) NewLine(closed.CloseIndent);
                    Write(")", SpaceBefore(t));
                    break;
                case K.Comma:
                    Write(",", false);
                    if (f.Sub && f.List) NewLine(f.Base + 1);
                    break;
                case K.Semi:
                    Write(";", false);
                    NewLine(0);
                    while (frames.Count > 1) frames.Pop();
                    frames.Peek().Clause = "";
                    break;
                default:
                    if (t.Kind == K.Word)
                    {
                        if (t.Upper == "CASE") f.Cases++;
                        else if (t.Upper == "END" && f.Cases > 0) { Write(Cased(i), SpaceBefore(t)); f.Cases--; break; }
                        else if (t.Upper == "BETWEEN") f.Between = true;
                    }
                    Write(Cased(i), SpaceBefore(t));
                    break;
            }
            prevUnary = t.Kind == K.Op && t.Text is "-" or "+" or "~" or "!"
                        && (prev is not { } p || p.Kind is K.Op or K.Open or K.Comma
                            || p.Kind == K.Word && Reserved.Contains(p.Upper) && p.Upper is not ("NULL" or "TRUE" or "FALSE"));
            prev = t;
        }

        endsWithLineComment = lastWasLineComment;
        return o.ToString().TrimEnd();
    }
}
