using System.Text;

namespace MyFront.Core.Db;

/// <param name="Text">Statement text without the delimiter and without leading comments.</param>
/// <param name="Start">Offset of the first code character in the source.</param>
/// <param name="End">Offset just after the statement (before the delimiter).</param>
/// <param name="DelimiterEnd">Offset just after the delimiter (same as End for the last statement without one).</param>
/// <param name="Line">1-based line of <paramref name="Start"/>.</param>
public readonly record struct SqlStatement(string Text, int Start, int End, int DelimiterEnd, int Line);

/// <summary>
/// Splits SQL scripts into statements the way the mysql client does: honours quotes, backticks,
/// comments (/* */, #, -- ), executable comments (/*! */) and the DELIMITER command.
/// Works on a TextReader so multi-GB .sql files stream with bounded memory.
/// </summary>
public static class SqlSplitter
{
    private enum State { Normal, SingleQuote, DoubleQuote, Backtick, BlockComment }

    public static List<SqlStatement> Split(string sql) => Split(new StringReader(sql)).ToList();

    public static IEnumerable<SqlStatement> Split(TextReader reader)
    {
        var state = State.Normal;
        var delimiter = ";";
        var sb = new StringBuilder();
        bool hasCode = false, commentKept = false;
        int codeStart = 0, codeLine = 0, codeEnd = 0;
        int pos = 0, lineNo = 0;

        foreach (var line in ReadLines(reader))
        {
            lineNo++;
            int len = line.Length;

            if (state == State.Normal && !hasCode && TryParseDelimiterCommand(line, out var newDelimiter))
            {
                if (newDelimiter.Length > 0) delimiter = newDelimiter;
                pos += len;
                continue;
            }

            int i = 0;
            while (i < len)
            {
                char c = line[i];
                char next = i + 1 < len ? line[i + 1] : '\0';
                switch (state)
                {
                    case State.Normal:
                        if (c == '\'' || c == '"' || c == '`')
                        {
                            if (!hasCode) { hasCode = true; codeStart = pos + i; codeLine = lineNo; }
                            sb.Append(c);
                            state = c == '\'' ? State.SingleQuote : c == '"' ? State.DoubleQuote : State.Backtick;
                            i++;
                            continue;
                        }
                        if (c == '/' && next == '*')
                        {
                            bool executable = i + 2 < len && (line[i + 2] == '!' || (line[i + 2] == 'M' && i + 3 < len && line[i + 3] == '!'));
                            commentKept = executable || hasCode;
                            if (executable && !hasCode) { hasCode = true; codeStart = pos + i; codeLine = lineNo; }
                            if (commentKept) sb.Append("/*");
                            state = State.BlockComment;
                            i += 2;
                            continue;
                        }
                        if (c == '#' || (c == '-' && next == '-' && (i + 2 >= len || char.IsWhiteSpace(line[i + 2]) || char.IsControl(line[i + 2]))))
                        {
                            if (hasCode) sb.Append(line, i, len - i);
                            i = len;
                            continue;
                        }
                        if (string.Compare(line, i, delimiter, 0, delimiter.Length, StringComparison.OrdinalIgnoreCase) == 0
                            && i + delimiter.Length <= len)
                        {
                            if (hasCode)
                            {
                                yield return new SqlStatement(sb.ToString().TrimEnd(), codeStart, codeEnd, pos + i + delimiter.Length, codeLine);
                                sb.Clear();
                                hasCode = false;
                            }
                            i += delimiter.Length;
                            continue;
                        }
                        if (!char.IsWhiteSpace(c))
                        {
                            if (!hasCode) { hasCode = true; codeStart = pos + i; codeLine = lineNo; }
                            sb.Append(c);
                            codeEnd = pos + i + 1;
                        }
                        else if (hasCode)
                        {
                            sb.Append(c);
                        }
                        i++;
                        continue;

                    case State.SingleQuote:
                    case State.DoubleQuote:
                        sb.Append(c);
                        char quote = state == State.SingleQuote ? '\'' : '"';
                        if (c == '\\' && i + 1 < len)
                        {
                            sb.Append(next);
                            i += 2;
                            continue;
                        }
                        if (c == quote)
                        {
                            if (next == quote) { sb.Append(next); i += 2; continue; }
                            state = State.Normal;
                            codeEnd = pos + i + 1;
                        }
                        i++;
                        continue;

                    case State.Backtick:
                        sb.Append(c);
                        if (c == '`')
                        {
                            if (next == '`') { sb.Append(next); i += 2; continue; }
                            state = State.Normal;
                            codeEnd = pos + i + 1;
                        }
                        i++;
                        continue;

                    case State.BlockComment:
                        if (c == '*' && next == '/')
                        {
                            if (commentKept) { sb.Append("*/"); codeEnd = pos + i + 2; }
                            state = State.Normal;
                            i += 2;
                            continue;
                        }
                        if (commentKept) sb.Append(c);
                        i++;
                        continue;
                }
            }
            pos += len;
        }

        if (hasCode)
            yield return new SqlStatement(sb.ToString().TrimEnd(), codeStart, codeEnd, codeEnd, codeLine);
    }

    /// <summary>"DELIMITER $$" at the start of a line (mysql client command, not sent to the server).</summary>
    private static bool TryParseDelimiterCommand(string line, out string delimiter)
    {
        delimiter = "";
        int j = 0;
        while (j < line.Length && (line[j] == ' ' || line[j] == '\t')) j++;
        const string kw = "DELIMITER";
        if (line.Length - j <= kw.Length
            || string.Compare(line, j, kw, 0, kw.Length, StringComparison.OrdinalIgnoreCase) != 0
            || !char.IsWhiteSpace(line[j + kw.Length]))
            return false;
        var rest = line[(j + kw.Length)..].Trim();
        int sp = 0;
        while (sp < rest.Length && !char.IsWhiteSpace(rest[sp])) sp++;
        delimiter = rest[..sp];
        return true;
    }

    /// <summary>Reads lines including their line terminators so offsets stay exact.</summary>
    private static IEnumerable<string> ReadLines(TextReader reader)
    {
        var buf = new char[16384];
        var sb = new StringBuilder();
        int n;
        while ((n = reader.Read(buf, 0, buf.Length)) > 0)
        {
            int start = 0;
            for (int i = 0; i < n; i++)
            {
                if (buf[i] != '\n') continue;
                sb.Append(buf, start, i - start + 1);
                yield return sb.ToString();
                sb.Clear();
                start = i + 1;
            }
            if (start < n) sb.Append(buf, start, n - start);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    /// <summary>The statement under the caret: the one containing it, else the nearest one before it on whitespace.</summary>
    public static SqlStatement? StatementAt(IReadOnlyList<SqlStatement> statements, int caret)
    {
        SqlStatement? before = null;
        foreach (var s in statements)
        {
            if (caret >= s.Start && caret <= s.DelimiterEnd) return s;
            if (s.DelimiterEnd < caret) before = s;
            else return before ?? s;
        }
        return before;
    }
}
