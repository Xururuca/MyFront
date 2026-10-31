using MyFront.Core.Db;

namespace MyFront.SelfTest;

/// <summary>Copy and paste of grid cells: the clipboard text (as Excel writes and reads it) and where pasted values go.</summary>
internal static class GridClipboardTests
{
    private static string Show(List<string[]> rows) => string.Join(" | ", rows.Select(r => string.Join(" · ", r.Select(f => $"[{f}]"))));

    public static void Offline(TestRunner t)
    {
        t.Check("clipboard: copied cells are tab-separated rows; NULL is an empty field; tabs, line breaks and a leading quote are quoted", () =>
        {
            var text = GridClipboard.Format(new[]
            {
                new string?[] { "İstanbul", null, "12.50" },
                new string?[] { "satır\r\nikinci", "a\tb", "\"tırnak\" baş" },
                new string?[] { "5\" disk", "", "son" },
            });
            Assert.Equal("İstanbul\t\t12.50\r\n\"satır\r\nikinci\"\t\"a\tb\"\t\"\"\"tırnak\"\" baş\"\r\n5\" disk\t\tson\r\n", text, "text");
        });

        t.Check("clipboard: Excel's text parses back into the same cells (quotes, line breaks in a cell, empty cells, ragged rows)", () =>
        {
            var cells = new[]
            {
                new string?[] { "İstanbul", null, "12.50" },
                new string?[] { "satır\r\nikinci", "a\tb", "\"tırnak\" baş" },
                new string?[] { "5\" disk", "", "son" },
            };
            var back = GridClipboard.Parse(GridClipboard.Format(cells));
            Assert.Equal("[İstanbul] · [] · [12.50] | [satır\r\nikinci] · [a\tb] · [\"tırnak\" baş] | [5\" disk] · [] · [son]", Show(back), "round trip");
            Assert.Equal("[Ankara]", Show(GridClipboard.Parse("Ankara")), "one value, no line break");
            Assert.Equal("[Ankara]", Show(GridClipboard.Parse("Ankara\r\n")), "one value as Excel copies it");
            Assert.Equal("[a] · [b] | [c] · [d]", Show(GridClipboard.Parse("a\tb\nc\td\n")), "LF only");
            Assert.Equal("[a] | []", Show(GridClipboard.Parse("a\r\n\r\n")), "an empty cell under a value");
            Assert.Equal("[x] · [] · [y]", Show(GridClipboard.Parse("x\t\ty")), "empty field in the middle");
            Assert.Equal("[\"açık tırnak] · [z]", Show(GridClipboard.Parse("\"açık tırnak\tz")), "a quote never closed is taken as it is");
            Assert.True(GridClipboard.Parse("").Count == 0, "nothing on the clipboard");
        });

        t.Check("paste: block from the current cell on, left to right and top down, clipped at the last row and column", () =>
        {
            var block = GridClipboard.Parse("a\tb\tc\r\nd\te\tf\r\ng\th\ti\r\n");
            var targets = GridClipboard.PasteTargets(block, startRow: 3, startColumn: 1, rowCount: 5, columnCount: 3);
            Assert.Equal("3,1=a 3,2=b 4,1=d 4,2=e", string.Join(" ", targets.Select(x => $"{x.Row},{x.Column}={x.Value}")), "clipped");
        });

        t.Check("paste: one value into several selected cells fills each of them; with one cell selected it goes to the current cell", () =>
        {
            var one = GridClipboard.Parse("Çorum\r\n");
            var many = GridClipboard.PasteTargets(one, 0, 0, 10, 4, new[] { (2, 1), (0, 3), (2, 1), (9, 0), (12, 0) });
            Assert.Equal("0,3 2,1 9,0", string.Join(" ", many.Select(x => $"{x.Row},{x.Column}")), "every selected cell inside the grid, once");
            Assert.True(many.All(x => x.Value == "Çorum"), "same value");
            var single = GridClipboard.PasteTargets(one, 4, 2, 10, 4, new[] { (7, 1) });
            Assert.Equal("4,2", string.Join(" ", single.Select(x => $"{x.Row},{x.Column}")), "the current cell");
            var block = GridClipboard.PasteTargets(GridClipboard.Parse("a\tb"), 0, 0, 10, 4, new[] { (1, 1), (2, 2) });
            Assert.Equal("0,0 0,1", string.Join(" ", block.Select(x => $"{x.Row},{x.Column}")), "a block ignores the selection");
        });
    }
}
