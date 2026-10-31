using System.Text.Json;
using System.Text.RegularExpressions;

namespace MyFront.Core.Editors;

public sealed class EditorTabState
{
    /// <summary>1 … 5 are the fixed tabs of a connection; 6+ were added with "New SQL Editor".</summary>
    public int Slot { get; set; }
    /// <summary>The .sql file the tab was opened from / saved to (its title); the text itself is always autosaved.</summary>
    public string? FilePath { get; set; }
    public int Caret { get; set; }
    public int FirstLine { get; set; }
}

public sealed class EditorSet
{
    public List<EditorTabState> Tabs { get; set; } = new();
    public int Selected { get; set; } = 1;
    /// <summary>The connection was closed while an editor tab was in front (it opens there again).</summary>
    public bool EditorActive { get; set; }
}

/// <summary>
/// The SQL editor tabs of one connection, kept across restarts without "save?" prompts:
/// %APPDATA%\MyFront\editors\{account id}\{slot}.sql plus tabs.json (order, titles, caret).
/// </summary>
public static partial class EditorStore
{
    public const int FixedTabs = 5;

    [GeneratedRegex(@"^(\d{1,4})\.sql$", RegexOptions.IgnoreCase)]
    private static partial Regex SlotFile();

    public static string Folder(string accountId) =>
        Path.Combine(AppPaths.DataDir, "editors", string.Concat(accountId.Where(char.IsAsciiLetterOrDigit)));

    private static string TextPath(string accountId, int slot) => Path.Combine(Folder(accountId), slot + ".sql");
    private static string IndexPath(string accountId) => Path.Combine(Folder(accountId), "tabs.json");

    /// <summary>The saved tabs: always slots 1 … 5, plus extra slots that have a text file.</summary>
    public static EditorSet Load(string accountId)
    {
        EditorSet set;
        try
        {
            set = File.Exists(IndexPath(accountId))
                ? JsonSerializer.Deserialize(File.ReadAllText(IndexPath(accountId)), CoreJson.Default.EditorSet) ?? new EditorSet()
                : new EditorSet();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            set = new EditorSet(); // the texts are separate files: rebuild the list from them
        }
        var bySlot = set.Tabs.Where(t => t.Slot > 0).GroupBy(t => t.Slot).ToDictionary(g => g.Key, g => g.First());
        // a tab whose text was autosaved but the app died before tabs.json was written
        if (Directory.Exists(Folder(accountId)))
            foreach (var f in Directory.EnumerateFiles(Folder(accountId), "*.sql"))
                if (SlotFile().Match(Path.GetFileName(f)) is { Success: true } m && int.Parse(m.Groups[1].Value) is var slot and > 0)
                    bySlot.TryAdd(slot, new EditorTabState { Slot = slot });
        for (int i = 1; i <= FixedTabs; i++) bySlot.TryAdd(i, new EditorTabState { Slot = i });
        // extra tabs only survive with text in them
        set.Tabs = bySlot.Values.Where(t => t.Slot <= FixedTabs || File.Exists(TextPath(accountId, t.Slot)))
            .OrderBy(t => t.Slot).ToList();
        if (set.Tabs.All(t => t.Slot != set.Selected)) set.Selected = 1;
        return set;
    }

    public static string ReadText(string accountId, int slot)
    {
        var path = TextPath(accountId, slot);
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    /// <summary>Writes the tab's text (atomically); an empty tab has no file.</summary>
    public static void WriteText(string accountId, int slot, string text)
    {
        var path = TextPath(accountId, slot);
        if (text.Length == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        Directory.CreateDirectory(Folder(accountId));
        AppPaths.WriteAllTextAtomic(path, text);
    }

    public static void SaveIndex(string accountId, EditorSet set)
    {
        Directory.CreateDirectory(Folder(accountId));
        AppPaths.WriteAllTextAtomic(IndexPath(accountId), JsonSerializer.Serialize(set, CoreJson.Default.EditorSet));
    }

    /// <summary>An extra tab closed for good.</summary>
    public static void Remove(string accountId, int slot)
    {
        var path = TextPath(accountId, slot);
        if (File.Exists(path)) File.Delete(path);
    }
}
