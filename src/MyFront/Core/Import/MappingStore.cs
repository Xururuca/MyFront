using System.Text.Json;
using System.Text.RegularExpressions;

namespace MyFront.Core.Import;

/// <summary>One remembered import: which sheet, which table, which column went where, and how.</summary>
public sealed class SavedImport
{
    public string FileKey { get; set; } = "";
    public string Database { get; set; } = "";
    public string Table { get; set; } = "";
    public string? Sheet { get; set; }
    public bool HeaderRow { get; set; } = true;
    /// <summary>Source header → target column (null = skip).</summary>
    public Dictionary<string, string?> Columns { get; set; } = new();
    public ImportMode Mode { get; set; }
    public bool RealTruncate { get; set; }
    public bool ContinueOnError { get; set; }
    public NumberFormat Numbers { get; set; }
    public DateTime SavedAt { get; set; }
}

/// <summary>%APPDATA%\MyFront\import-mappings.json — so a repeated import is "pick the file, click Import".</summary>
public static partial class MappingStore
{
    private static string FilePath => AppPaths.File("import-mappings.json");

    [GeneratedRegex(@"(\s*\(\d+\)|\s*-\s*(copy|kopya)(\s*\(\d+\))?)$", RegexOptions.IgnoreCase)]
    private static partial Regex CopySuffix();

    /// <summary>
    /// The file name without extension and without download/copy suffixes, so
    /// "TRADE_STATS (11).xlsx" and "TRADE_STATS (12).xlsx" share their mapping.
    /// </summary>
    public static string FileKey(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).Trim();
        string previous;
        do
        {
            previous = name;
            name = CopySuffix().Replace(name, "").Trim();
        } while (name != previous && name.Length > 0);
        return (name.Length == 0 ? previous : name).ToLowerInvariant();
    }

    public static List<SavedImport> Load()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return new List<SavedImport>();
            return JsonSerializer.Deserialize(File.ReadAllText(path), CoreJson.Default.ListSavedImport) ?? new List<SavedImport>();
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return new List<SavedImport>(); }
    }

    /// <summary>The mapping for this file (and table, if given), newest first.</summary>
    public static SavedImport? Find(string path, string? database = null, string? table = null)
    {
        var key = FileKey(path);
        return Load().Where(s => s.FileKey == key && (database is null || s.Database == database) && (table is null || s.Table == table))
            .OrderByDescending(s => s.SavedAt).FirstOrDefault();
    }

    public static void Save(SavedImport entry)
    {
        var all = Load();
        all.RemoveAll(s => s.FileKey == entry.FileKey && s.Database == entry.Database && s.Table == entry.Table);
        entry.SavedAt = DateTime.Now;
        all.Add(entry);
        if (all.Count > 500) all = all.OrderByDescending(s => s.SavedAt).Take(500).ToList();
        AppPaths.WriteAllTextAtomic(FilePath, JsonSerializer.Serialize(all, CoreJson.Default.ListSavedImport));
    }
}
