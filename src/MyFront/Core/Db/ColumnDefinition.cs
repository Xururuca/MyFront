using System.Text;
using System.Text.RegularExpressions;

namespace MyFront.Core.Db;

public enum ColumnDefaultKind { None, Null, Value, CurrentTimestamp, Expression }

/// <summary>
/// One column as SHOW CREATE TABLE writes it, split into the parts the Edit field dialog changes (name, type, NULL,
/// default, auto increment, collation, comment) and the parts it keeps as they are (character set, generated
/// expression, ON UPDATE, CHECK, INVISIBLE and anything else). <see cref="ToSql"/> puts it back together.
/// </summary>
public sealed partial class ColumnDefinition
{
    public string Name { get; set; } = "";
    /// <summary>Data type with its modifiers: "varchar(100)", "int(10) unsigned", "enum('a','b')".</summary>
    public string Type { get; set; } = "";
    public bool Nullable { get; set; } = true;
    public ColumnDefaultKind DefaultKind { get; set; }
    /// <summary>Value: the value itself (it is quoted when written); Expression: SQL as written (uuid(), b'0', (a + 1)).</summary>
    public string? Default { get; set; }
    public bool AutoIncrement { get; set; }
    /// <summary>Explicit collation; empty = the table's default.</summary>
    public string Collation { get; set; } = "";
    public string Comment { get; set; } = "";

    /// <summary>Kept: written back while the collation is unchanged.</summary>
    public string? CharacterSet { get; private set; }
    /// <summary>The collation as it was read; a different <see cref="Collation"/> replaces the character set too.</summary>
    public string ParsedCollation { get; private set; } = "";
    /// <summary>Kept: "GENERATED ALWAYS AS (…) VIRTUAL" (no default, auto increment or ON UPDATE then).</summary>
    public string? Generated { get; private set; }
    /// <summary>Kept: "ON UPDATE current_timestamp()".</summary>
    public string? OnUpdate { get; private set; }
    /// <summary>Kept, in their order: INVISIBLE, /*!80023 INVISIBLE */, COLUMN_FORMAT …, SRID …</summary>
    public List<string> Other { get; } = new();
    /// <summary>Kept: CHECK (…) constraints, always written last.</summary>
    public List<string> Checks { get; } = new();

    public bool IsGenerated => Generated is not null;

    /// <summary>The definition of one column taken from the whole CREATE TABLE statement.</summary>
    public static ColumnDefinition FromCreateTable(string createTable, string column)
    {
        // one line per column; keys and constraints start with a keyword, not a backtick
        var columns = createTable.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith('`'))
            .Select(l => Tokenize(l.TrimEnd(','))).Where(t => t.Count > 1).ToList();
        var match = columns.FirstOrDefault(t => UnquoteIdent(t[0]) == column)
                    ?? columns.FirstOrDefault(t => UnquoteIdent(t[0]).Equals(column, StringComparison.OrdinalIgnoreCase)) // names ignore case
                    ?? throw new InvalidOperationException($"Column {column} not found in the table definition.");
        return Parse(match);
    }

    /// <summary>One column line of SHOW CREATE TABLE ("`name` type …", the trailing comma is ignored).</summary>
    public static ColumnDefinition Parse(string line) => Parse(Tokenize(line.Trim().TrimEnd(',')));

    private static ColumnDefinition Parse(List<string> t)
    {
        if (t.Count < 2) throw new FormatException("Not a column definition: " + string.Join(" ", t));
        var d = new ColumnDefinition { Name = UnquoteIdent(t[0]), Type = t[1] };
        int i = 2;
        while (i < t.Count && Up(t[i]) is "UNSIGNED" or "SIGNED" or "ZEROFILL") d.Type += " " + t[i++];
        string Next(int k) => i + k < t.Count ? t[i + k] : "";
        while (i < t.Count)
        {
            switch (Up(t[i]))
            {
                case "CHARACTER" when Up(Next(1)) == "SET":
                    d.CharacterSet = Next(2);
                    i += 3;
                    break;
                case "CHARSET":
                    d.CharacterSet = Next(1);
                    i += 2;
                    break;
                case "COLLATE":
                    d.Collation = d.ParsedCollation = Next(1);
                    i += 2;
                    break;
                case "GENERATED":
                case "AS":
                {
                    var parts = new List<string>();
                    while (i < t.Count && Up(t[i]) is "GENERATED" or "ALWAYS") parts.Add(t[i++]);
                    if (i < t.Count && Up(t[i]) == "AS") parts.Add(t[i++]);
                    if (i < t.Count && t[i].StartsWith('(')) parts.Add(t[i++]);
                    if (i < t.Count && Up(t[i]) is "VIRTUAL" or "STORED" or "PERSISTENT") parts.Add(t[i++]);
                    d.Generated = string.Join(" ", parts);
                    break;
                }
                case "NOT" when Up(Next(1)) == "NULL":
                    d.Nullable = false;
                    i += 2;
                    break;
                case "NULL":
                    d.Nullable = true;
                    i++;
                    break;
                case "DEFAULT":
                    (d.DefaultKind, d.Default) = ClassifyDefault(Next(1));
                    i += 2;
                    break;
                case "ON" when Up(Next(1)) == "UPDATE":
                    d.OnUpdate = "ON UPDATE " + Next(2);
                    i += 3;
                    break;
                case "AUTO_INCREMENT":
                    d.AutoIncrement = true;
                    i++;
                    break;
                case "COMMENT":
                    d.Comment = SqlLiteral.Unquote(Next(1));
                    i += 2;
                    break;
                case "CHECK":
                    d.Checks.Add("CHECK " + Next(1));
                    i += 2;
                    break;
                case "CONSTRAINT" when Up(Next(2)) == "CHECK":
                    d.Checks.Add($"CONSTRAINT {Next(1)} CHECK {Next(3)}");
                    i += 4;
                    break;
                case "COLUMN_FORMAT" or "STORAGE" or "SRID" or "REF_SYSTEM_ID":
                    d.Other.Add(t[i] + " " + Next(1));
                    i += 2;
                    break;
                case "WITH" or "WITHOUT" when Up(Next(1)) == "SYSTEM":
                    d.Other.Add($"{t[i]} {Next(1)} {Next(2)}");
                    i += 3;
                    break;
                default:
                    d.Other.Add(t[i]);
                    i++;
                    break;
            }
        }
        return d;
    }

    private static string Up(string s) => s.ToUpperInvariant();

    [GeneratedRegex(@"^[+-]?(\d+(\.\d*)?|\.\d+)([eE][+-]?\d+)?$")]
    private static partial Regex NumberLiteral();

    [GeneratedRegex(@"^(CURRENT_TIMESTAMP|NOW|LOCALTIME|LOCALTIMESTAMP)(\(\d*\))?$", RegexOptions.IgnoreCase)]
    private static partial Regex CurrentTimestamp();

    [GeneratedRegex(@"^(datetime|timestamp)\s*\((\d)\)", RegexOptions.IgnoreCase)]
    private static partial Regex FractionalTime();

    [GeneratedRegex(@"^[A-Za-z0-9_]+$")]
    private static partial Regex PlainName();

    private static (ColumnDefaultKind, string?) ClassifyDefault(string raw)
    {
        if (raw.Length == 0) return (ColumnDefaultKind.None, null);
        if (Up(raw) == "NULL") return (ColumnDefaultKind.Null, null);
        if (raw[0] is '\'' or '"') return (ColumnDefaultKind.Value, SqlLiteral.Unquote(raw));
        if (NumberLiteral().IsMatch(raw)) return (ColumnDefaultKind.Value, raw);
        if (CurrentTimestamp().IsMatch(raw)) return (ColumnDefaultKind.CurrentTimestamp, null);
        return (ColumnDefaultKind.Expression, raw); // uuid(), b'0', 0x00, (a + 1) …
    }

    private static string FirstWord(string type)
    {
        int end = 0;
        while (end < type.Length && (char.IsLetterOrDigit(type[end]) || type[end] == '_')) end++;
        return type[..end].ToLowerInvariant();
    }

    /// <summary>Types that have a character set and collation.</summary>
    public static bool IsTextType(string type) =>
        FirstWord(type) is "char" or "varchar" or "tinytext" or "text" or "mediumtext" or "longtext" or "enum" or "set";

    /// <summary>Types a key can use with a prefix length (text and binary strings).</summary>
    public static bool IsPrefixType(string type) =>
        IsTextType(type) || FirstWord(type) is "binary" or "varbinary" or "tinyblob" or "blob" or "mediumblob" or "longblob";

    /// <summary>TEXT / BLOB: a (non-fulltext) key on them needs a prefix length.</summary>
    public static bool NeedsPrefixLength(string type) =>
        FirstWord(type) is "tinytext" or "text" or "mediumtext" or "longtext" or "tinyblob" or "blob" or "mediumblob" or "longblob";

    /// <summary>Fields a FULLTEXT key can use.</summary>
    public static bool IsFulltextType(string type) =>
        FirstWord(type) is "char" or "varchar" or "tinytext" or "text" or "mediumtext" or "longtext";

    private static bool IsNumericType(string type) =>
        FirstWord(type) is "tinyint" or "smallint" or "mediumint" or "int" or "integer" or "bigint" or "decimal" or "dec"
            or "numeric" or "fixed" or "float" or "double" or "real";

    /// <summary>"`name` type … COMMENT '…'" as it goes into ALTER TABLE … CHANGE COLUMN.</summary>
    /// <exception cref="ArgumentException">Missing name or type, or a value that isn't plain text where it must be.</exception>
    public string ToSql(bool noBackslashEscapes)
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("Enter a name for the field.");
        var type = Type.Trim();
        if (type.Length == 0) throw new ArgumentException("Enter a type for the field.");
        var collation = Collation.Trim();
        if (collation.Length > 0 && !PlainName().IsMatch(collation)) throw new ArgumentException($"\"{collation}\" is not a collation name.");

        var sb = new StringBuilder(Ident.Quote(Name.Trim())).Append(' ').Append(type);
        if (IsTextType(type))
        {
            if (collation == ParsedCollation)
            {
                if (CharacterSet is not null) sb.Append(" CHARACTER SET ").Append(CharacterSet);
                if (collation.Length > 0) sb.Append(" COLLATE ").Append(collation);
            }
            else if (collation.Length > 0)
            {
                sb.Append(" COLLATE ").Append(collation); // the collation names its character set
            }
        }
        if (Generated is not null)
        {
            sb.Append(' ').Append(Generated);
            if (!Nullable) sb.Append(" NOT NULL");
        }
        else
        {
            sb.Append(Nullable ? " NULL" : " NOT NULL");
            switch (DefaultKind)
            {
                case ColumnDefaultKind.Null:
                    sb.Append(" DEFAULT NULL");
                    break;
                case ColumnDefaultKind.Value:
                    var v = Default ?? "";
                    sb.Append(" DEFAULT ").Append(IsNumericType(type) && NumberLiteral().IsMatch(v) ? v : SqlLiteral.Quote(v, noBackslashEscapes));
                    break;
                case ColumnDefaultKind.CurrentTimestamp:
                    var fsp = FractionalTime().Match(type);
                    sb.Append(" DEFAULT CURRENT_TIMESTAMP").Append(fsp.Success ? $"({fsp.Groups[2].Value})" : "");
                    break;
                case ColumnDefaultKind.Expression:
                    if (string.IsNullOrWhiteSpace(Default)) throw new ArgumentException("Enter the default expression.");
                    sb.Append(" DEFAULT ").Append(Default.Trim());
                    break;
            }
            if (OnUpdate is not null) sb.Append(' ').Append(OnUpdate);
            if (AutoIncrement) sb.Append(" AUTO_INCREMENT");
        }
        foreach (var o in Other) sb.Append(' ').Append(o);
        if (Comment.Length > 0) sb.Append(" COMMENT ").Append(SqlLiteral.Quote(Comment, noBackslashEscapes));
        foreach (var c in Checks) sb.Append(' ').Append(c);
        var sql = sb.ToString();
        // type and default expression are typed by the user: one column definition, never a second statement
        if (SqlClassifier.Tokens(sql).Contains(";")) throw new ArgumentException("A field definition can't contain ';'.");
        return sql;
    }

    internal static string UnquoteIdent(string token) =>
        token.Length >= 2 && token[0] == '`' && token[^1] == '`' ? token[1..^1].Replace("``", "`") : token;

    /// <summary>
    /// Splits a column line into `identifiers`, 'strings', (groups), /*comments*/ and words; a word directly followed
    /// by a group or a quote stays one token: varchar(100), current_timestamp(), enum('a','b'), b'0', _utf8mb4'x'.
    /// </summary>
    internal static List<string> Tokenize(string s)
    {
        var list = new List<string>();
        int i = 0, n = s.Length;
        while (i < n)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c) || c == ',') { i++; continue; }
            int start = i;
            if (c == '/' && i + 1 < n && s[i + 1] == '*')
            {
                int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? n : end + 2;
            }
            else if (c is '`' or '\'' or '"') i = SkipQuoted(s, i);
            else if (c == '(') i = SkipGroup(s, i);
            else
            {
                while (i < n && !char.IsWhiteSpace(s[i]) && s[i] is not ('(' or ')' or ',' or '`' or '\'' or '"')) i++;
                while (i < n && (s[i] == '(' || s[i] is '\'' or '"'))
                    i = s[i] == '(' ? SkipGroup(s, i) : SkipQuoted(s, i);
            }
            if (i == start) i++; // a stray ')'
            list.Add(s[start..i]);
        }
        return list;
    }

    private static int SkipQuoted(string s, int i)
    {
        char q = s[i++];
        while (i < s.Length)
        {
            if (s[i] == '\\' && q != '`') { i += 2; continue; }
            if (s[i] == q)
            {
                if (i + 1 < s.Length && s[i + 1] == q) { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return s.Length;
    }

    private static int SkipGroup(string s, int i)
    {
        int depth = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c is '`' or '\'' or '"') { i = SkipQuoted(s, i); continue; }
            if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return i + 1;
            i++;
        }
        return s.Length;
    }
}
