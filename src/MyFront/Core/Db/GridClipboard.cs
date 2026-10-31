using System.Text;

namespace MyFront.Core.Db;

/// <summary>
/// Grid cells as clipboard text, the way Excel writes and reads it: tabs between fields, a line break after each row,
/// and a field in double quotes (quotes doubled) when it holds a tab or a line break or starts with a quote.
/// </summary>
public static class GridClipboard
{
    /// <summary>One field for tab-separated text; null (NULL) = empty.</summary>
    public static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        bool quote = value[0] == '"' || value.AsSpan().IndexOfAny('\t', '\r', '\n') >= 0;
        return quote ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    /// <summary>Rows of fields → "a\tb\r\nc\td\r\n".</summary>
    public static string Format(IEnumerable<IReadOnlyList<string?>> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            for (int i = 0; i < row.Count; i++)
            {
                if (i > 0) sb.Append('\t');
                sb.Append(Field(row[i]));
            }
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Clipboard text → rows of fields: a tab between fields, CR LF / LF / CR between rows, "…" quoting as Excel writes it
    /// (a quote that is never closed is taken as it is). The line break after the last row is not an extra row.
    /// </summary>
    public static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        if (string.IsNullOrEmpty(text)) return rows;
        var row = new List<string>();
        var field = new StringBuilder();
        int i = 0, n = text.Length;
        while (true)
        {
            // one field
            field.Clear();
            if (i < n && text[i] == '"' && ClosingQuote(text, i + 1) is var close and >= 0)
            {
                field.Append(text, i + 1, close - i - 1).Replace("\"\"", "\"");
                i = close + 1;
            }
            while (i < n && text[i] is not ('\t' or '\r' or '\n')) field.Append(text[i++]);
            row.Add(field.ToString());

            if (i < n && text[i] == '\t') { i++; continue; }
            // end of a row
            rows.Add(row.ToArray());
            row.Clear();
            if (i < n && text[i] == '\r') i++;
            if (i < n && text[i] == '\n') i++;
            if (i >= n) break;
        }
        return rows;
    }

    /// <summary>Index of the quote that closes a quoted field starting at <paramref name="from"/>; -1 = none.</summary>
    private static int ClosingQuote(string text, int from)
    {
        for (int i = from; i < text.Length; i++)
        {
            if (text[i] != '"') continue;
            if (i + 1 < text.Length && text[i + 1] == '"') { i++; continue; }
            return i;
        }
        return -1;
    }

    /// <summary>
    /// Where pasted values go: the block from the current cell on, left to right and top down, clipped at the last row
    /// and column; a single value with several cells selected goes into every selected cell. Columns are positions
    /// in display order.
    /// </summary>
    public static List<(int Row, int Column, string Value)> PasteTargets(IReadOnlyList<string[]> block, int startRow, int startColumn,
        int rowCount, int columnCount, IEnumerable<(int Row, int Column)>? selection = null)
    {
        var targets = new List<(int Row, int Column, string Value)>();
        bool Inside(int r, int c) => r >= 0 && r < rowCount && c >= 0 && c < columnCount;
        if (block.Count == 1 && block[0].Length == 1 && selection is not null)
        {
            var cells = selection.Where(x => Inside(x.Row, x.Column)).Distinct().OrderBy(x => x.Row).ThenBy(x => x.Column).ToList();
            if (cells.Count > 1)
            {
                targets.AddRange(cells.Select(x => (x.Row, x.Column, block[0][0])));
                return targets;
            }
        }
        for (int r = 0; r < block.Count; r++)
            for (int c = 0; c < block[r].Length; c++)
                if (Inside(startRow + r, startColumn + c))
                    targets.Add((startRow + r, startColumn + c, block[r][c]));
        return targets;
    }
}
