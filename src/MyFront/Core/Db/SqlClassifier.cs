namespace MyFront.Core.Db;

/// <summary>
/// Lightweight keyword scanner (skips strings, quoted identifiers and comments) used for the
/// read-only guard and the "dangerous statement" confirmation.
/// </summary>
public static class SqlClassifier
{
    private static readonly HashSet<string> DmlWords = new(StringComparer.Ordinal) { "INSERT", "UPDATE", "DELETE", "REPLACE" };
    private static readonly HashSet<string> DangerWords = new(StringComparer.Ordinal) { "UPDATE", "DELETE", "DROP", "TRUNCATE", "ALTER" };

    public static string FirstKeyword(string sql)
    {
        foreach (var t in Tokens(sql))
            if (t != "(") return t;
        return "";
    }

    /// <summary>True when the SQL contains UPDATE/DELETE/DROP/TRUNCATE/ALTER as a keyword ("SELECT ... FOR UPDATE" excluded).</summary>
    public static bool ContainsDangerous(string sql)
    {
        string prev = "";
        foreach (var t in Tokens(sql))
        {
            if (DangerWords.Contains(t) && !(t == "UPDATE" && prev == "FOR"))
                return true;
            prev = t;
        }
        return false;
    }

    /// <summary>True if the statement(s) cannot modify data or server state.</summary>
    public static bool IsReadOnlySafe(string sql)
    {
        var segment = new List<string>();
        foreach (var t in Tokens(sql))
        {
            if (t == ";")
            {
                if (segment.Count > 0 && !IsSegmentSafe(segment)) return false;
                segment.Clear();
            }
            else segment.Add(t);
        }
        return segment.Count == 0 || IsSegmentSafe(segment);
    }

    private static bool IsSegmentSafe(List<string> t)
    {
        int first = 0;
        while (first < t.Count && t[first] == "(") first++;
        if (first >= t.Count) return true;
        string kw = t[first];
        string second = first + 1 < t.Count ? t[first + 1] : "";

        switch (kw)
        {
            case "SELECT":
            case "VALUES":
            case "TABLE":
                return !t.Contains("OUTFILE") && !t.Contains("DUMPFILE");
            case "WITH":
                return !ContainsDml(t) && !t.Contains("OUTFILE") && !t.Contains("DUMPFILE");
            case "SHOW":
            case "HELP":
            case "USE":
                return true;
            case "EXPLAIN":
            case "DESCRIBE":
            case "DESC":
                // EXPLAIN ANALYZE <dml> executes the statement
                return !(t.Contains("ANALYZE") && ContainsDml(t));
            case "ANALYZE":
                // MariaDB "ANALYZE SELECT" runs the query; "ANALYZE TABLE" writes statistics
                return t.Contains("SELECT") && !t.Contains("TABLE") && !ContainsDml(t);
            case "SET":
                return IsSetSafe(t, first);
            case "COMMIT":
            case "ROLLBACK":
            case "SAVEPOINT":
            case "RELEASE":
            case "UNLOCK":
                return true;
            case "BEGIN":
                return t.Count == first + 1 || (second == "WORK" && t.Count == first + 2);
            case "START":
                return second == "TRANSACTION" && !t.Contains("WRITE");
            default:
                return false;
        }
    }

    private static bool IsSetSafe(List<string> t, int first)
    {
        // MariaDB: SET STATEMENT var=val [, ...] FOR <statement>
        if (first + 1 < t.Count && t[first + 1] == "STATEMENT")
        {
            int f = t.IndexOf("FOR", first);
            if (f < 0) return false;
            return AssignmentsSafe(t, first + 2, f) && IsSegmentSafe(t.GetRange(f + 1, t.Count - f - 1));
        }
        if (first + 1 < t.Count && t[first + 1] == "DEFAULT") return false; // SET DEFAULT ROLE
        return AssignmentsSafe(t, first + 1, t.Count);
    }

    private static bool AssignmentsSafe(List<string> t, int from, int to)
    {
        for (int i = from; i < to; i++)
        {
            var w = t[i];
            if (w is "GLOBAL" or "PERSIST" or "PERSIST_ONLY" or "PASSWORD" or "WRITE")
                return false;
            if (w.StartsWith("@@GLOBAL.", StringComparison.Ordinal) || w.StartsWith("@@PERSIST", StringComparison.Ordinal))
                return false;
            if (w.Contains("READ_ONLY", StringComparison.Ordinal)) return false; // transaction_read_only / tx_read_only
        }
        return true;
    }

    private static bool ContainsDml(List<string> t)
    {
        for (int i = 0; i < t.Count; i++)
        {
            if (!DmlWords.Contains(t[i])) continue;
            bool isFunction = (t[i] is "REPLACE" or "INSERT") && i + 1 < t.Count && t[i + 1] == "(";
            if (!isFunction) return true;
        }
        return false;
    }

    /// <summary>
    /// Upper-cased words, "@var"/"@@var" tokens, "(" and ";". Strings, `identifiers` (as "`") and comments are skipped;
    /// the body of executable comments (/*!40101 ... */) is scanned like normal code.
    /// </summary>
    public static IEnumerable<string> Tokens(string sql)
    {
        int i = 0, n = sql.Length;
        int execDepth = 0;
        while (i < n)
        {
            char c = sql[i];
            char next = i + 1 < n ? sql[i + 1] : '\0';
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '\'' || c == '"')
            {
                i++;
                while (i < n)
                {
                    if (sql[i] == '\\') { i += 2; continue; }
                    if (sql[i] == c)
                    {
                        if (i + 1 < n && sql[i + 1] == c) { i += 2; continue; }
                        break;
                    }
                    i++;
                }
                i++;
                yield return "'";
                continue;
            }
            if (c == '`')
            {
                i++;
                while (i < n)
                {
                    if (sql[i] == '`')
                    {
                        if (i + 1 < n && sql[i + 1] == '`') { i += 2; continue; }
                        break;
                    }
                    i++;
                }
                i++;
                yield return "`";
                continue;
            }
            if (c == '/' && next == '*')
            {
                if (i + 2 < n && (sql[i + 2] == '!' || (sql[i + 2] == 'M' && i + 3 < n && sql[i + 3] == '!')))
                {
                    i += sql[i + 2] == '!' ? 3 : 4;
                    while (i < n && char.IsDigit(sql[i])) i++;
                    execDepth++;
                    continue;
                }
                int end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? n : end + 2;
                continue;
            }
            if (c == '*' && next == '/' && execDepth > 0) { execDepth--; i += 2; continue; }
            if (c == '#' || (c == '-' && next == '-' && (i + 2 >= n || char.IsWhiteSpace(sql[i + 2]) || char.IsControl(sql[i + 2]))))
            {
                int end = sql.IndexOf('\n', i);
                i = end < 0 ? n : end + 1;
                continue;
            }
            if (c == '(') { i++; yield return "("; continue; }
            if (c == ';') { i++; yield return ";"; continue; }
            if (c == '@')
            {
                int s = i;
                while (i < n && sql[i] == '@') i++;
                while (i < n && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$' or '.')) i++;
                yield return sql[s..i].ToUpperInvariant();
                continue;
            }
            if (char.IsLetterOrDigit(c) || c == '_' || c == '$')
            {
                int s = i;
                while (i < n && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$')) i++;
                yield return sql[s..i].ToUpperInvariant();
                continue;
            }
            i++;
        }
    }
}
