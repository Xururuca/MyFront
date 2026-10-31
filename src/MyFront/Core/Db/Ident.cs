namespace MyFront.Core.Db;

/// <summary>Identifier quoting. Every table/column/database name that goes into SQL text goes through here.</summary>
public static class Ident
{
    public static string Quote(string name) => "`" + name.Replace("`", "``") + "`";

    public static string Quote(string? database, string name) =>
        string.IsNullOrEmpty(database) ? Quote(name) : Quote(database) + "." + Quote(name);

    /// <summary>
    /// A name as it is typed into SQL: as it is when that is safe, in backticks when it has a character outside
    /// [A-Za-z0-9_$], starts with a digit, or is a reserved word of MySQL 8 / MariaDB (`order`, `rank`, `key` …).
    /// </summary>
    public static string QuoteIfNeeded(string name)
    {
        bool plain = name.Length > 0 && !char.IsAsciiDigit(name[0])
                     && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '$')
                     && !Reserved.Contains(name);
        return plain ? name : Quote(name);
    }

    /// <summary>Reserved words of MySQL 8.0 / 8.4 and MariaDB 10.x / 11.x (the union; a word that needs no quotes on one
    /// server is quoted anyway, which is always valid).</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "ACCESSIBLE", "ADD", "ALL", "ALTER", "ANALYZE", "AND", "AS", "ASC", "ASENSITIVE", "BEFORE", "BETWEEN", "BIGINT", "BINARY",
        "BLOB", "BOTH", "BY", "CALL", "CASCADE", "CASE", "CHANGE", "CHAR", "CHARACTER", "CHECK", "COLLATE", "COLUMN", "CONDITION",
        "CONSTRAINT", "CONTINUE", "CONVERT", "CREATE", "CROSS", "CUBE", "CUME_DIST", "CURRENT_DATE", "CURRENT_ROLE", "CURRENT_TIME",
        "CURRENT_TIMESTAMP", "CURRENT_USER", "CURSOR", "DATABASE", "DATABASES", "DAY_HOUR", "DAY_MICROSECOND", "DAY_MINUTE",
        "DAY_SECOND", "DEC", "DECIMAL", "DECLARE", "DEFAULT", "DELAYED", "DELETE", "DELETE_DOMAIN_ID", "DENSE_RANK", "DESC",
        "DESCRIBE", "DETERMINISTIC", "DISTINCT", "DISTINCTROW", "DIV", "DO_DOMAIN_IDS", "DOUBLE", "DROP", "DUAL", "EACH", "ELSE",
        "ELSEIF", "EMPTY", "ENCLOSED", "ESCAPED", "EXCEPT", "EXISTS", "EXIT", "EXPLAIN", "FALSE", "FETCH", "FIRST_VALUE", "FLOAT",
        "FLOAT4", "FLOAT8", "FOR", "FORCE", "FOREIGN", "FROM", "FULLTEXT", "FUNCTION", "GENERAL", "GENERATED", "GET", "GRANT",
        "GROUP", "GROUPING", "GROUPS", "HAVING", "HIGH_PRIORITY", "HOUR_MICROSECOND", "HOUR_MINUTE", "HOUR_SECOND", "IF", "IGNORE",
        "IGNORE_DOMAIN_IDS", "IGNORE_SERVER_IDS", "IN", "INDEX", "INFILE", "INNER", "INOUT", "INSENSITIVE", "INSERT", "INT", "INT1",
        "INT2", "INT3", "INT4", "INT8", "INTEGER", "INTERSECT", "INTERVAL", "INTO", "IO_AFTER_GTIDS", "IO_BEFORE_GTIDS", "IS",
        "ITERATE", "JOIN", "JSON_TABLE", "KEY", "KEYS", "KILL", "LAG", "LAST_VALUE", "LATERAL", "LEAD", "LEADING", "LEAVE", "LEFT",
        "LIKE", "LIMIT", "LINEAR", "LINES", "LOAD", "LOCALTIME", "LOCALTIMESTAMP", "LOCK", "LONG", "LONGBLOB", "LONGTEXT", "LOOP",
        "LOW_PRIORITY", "MANUAL", "MASTER_BIND", "MASTER_HEARTBEAT_PERIOD", "MASTER_SSL_VERIFY_SERVER_CERT", "MATCH", "MAXVALUE",
        "MEDIUMBLOB", "MEDIUMINT", "MEDIUMTEXT", "MIDDLEINT", "MINUTE_MICROSECOND", "MINUTE_SECOND", "MOD", "MODIFIES", "NATURAL",
        "NOT", "NO_WRITE_TO_BINLOG", "NTH_VALUE", "NTILE", "NULL", "NUMERIC", "OF", "OFFSET", "ON", "OPTIMIZE", "OPTIMIZER_COSTS",
        "OPTION", "OPTIONALLY", "OR", "ORDER", "OUT", "OUTER", "OUTFILE", "OVER", "PAGE_CHECKSUM", "PARALLEL", "PARSE_VCOL_EXPR",
        "PARTITION", "PERCENT_RANK", "POSITION", "PRECISION", "PRIMARY", "PROCEDURE", "PURGE", "QUALIFY", "RANGE", "RANK", "READ",
        "READS", "READ_WRITE", "REAL", "RECURSIVE", "REF_SYSTEM_ID", "REFERENCES", "REGEXP", "RELEASE", "RENAME", "REPEAT",
        "REPLACE", "REQUIRE", "RESIGNAL", "RESTRICT", "RETURN", "RETURNING", "REVOKE", "RIGHT", "RLIKE", "ROW", "ROWS",
        "ROW_NUMBER", "SCHEMA", "SCHEMAS", "SECOND_MICROSECOND", "SELECT", "SENSITIVE", "SEPARATOR", "SET", "SHOW", "SIGNAL",
        "SLOW", "SMALLINT", "SPATIAL", "SPECIFIC", "SQL", "SQLEXCEPTION", "SQLSTATE", "SQLWARNING", "SQL_BIG_RESULT",
        "SQL_CALC_FOUND_ROWS", "SQL_SMALL_RESULT", "SSL", "STARTING", "STATS_AUTO_RECALC", "STATS_PERSISTENT",
        "STATS_SAMPLE_PAGES", "STORED", "STRAIGHT_JOIN", "SYSTEM", "TABLE", "TABLESAMPLE", "TERMINATED", "THEN", "TINYBLOB",
        "TINYINT", "TINYTEXT", "TO", "TRAILING", "TRIGGER", "TRUE", "UNDO", "UNION", "UNIQUE", "UNLOCK", "UNSIGNED", "UPDATE",
        "USAGE", "USE", "USING", "UTC_DATE", "UTC_TIME", "UTC_TIMESTAMP", "VALUES", "VARBINARY", "VARCHAR", "VARCHARACTER",
        "VARYING", "VIRTUAL", "WHEN", "WHERE", "WHILE", "WINDOW", "WITH", "WRITE", "XOR", "YEAR_MONTH", "ZEROFILL",
    };

    /// <summary>Escapes LIKE wildcards so a user's search text matches literally (default escape char '\').</summary>
    public static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
