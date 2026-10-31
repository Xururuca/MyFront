using System.Text.Json;
using MyFront.Core.Accounts;

namespace MyFront.Core.Quick;

/// <summary>One button on the Quick Query bar.</summary>
public sealed class QuickQuery
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New query";
    /// <summary>"#RRGGBB" or null.</summary>
    public string? Color { get; set; }
    /// <summary>Glyph key (see QuickQueryIcons) or null.</summary>
    public string? Icon { get; set; }
    public string Sql { get; set; } = "";
    /// <summary>The connection the button belongs to: it is shown only while that connection is in front. null = on every connection.</summary>
    public string? AccountId { get; set; }
    /// <summary>Shown in the editor and used to re-link the button after importing it on another PC.</summary>
    public string? AccountName { get; set; }
    /// <summary>Target database; null = the connection's default / current database.</summary>
    public string? Database { get; set; }
    /// <summary>1..9 → Ctrl+1..Ctrl+9.</summary>
    public int? Hotkey { get; set; }
    /// <summary>Write the result straight to an .xlsx and open it, instead of a result tab.</summary>
    public bool ToExcel { get; set; }
    /// <summary>Ask before running SQL that contains UPDATE / DELETE / DROP / TRUNCATE / ALTER.</summary>
    public bool ConfirmDangerous { get; set; } = true;

    public QuickQuery Clone()
    {
        var c = (QuickQuery)MemberwiseClone();
        return c;
    }

    /// <summary>On the toolbar while this connection is in front (nothing shows without a connection).</summary>
    public bool ShowsOn(string? accountId) => accountId is not null && (AccountId is null || AccountId == accountId);

    /// <summary>Two buttons that can be on the toolbar together can't share a hotkey.</summary>
    public bool SharesToolbarWith(QuickQuery other) => AccountId is null || other.AccountId is null || AccountId == other.AccountId;

    public override string ToString() => Name;
}

/// <summary>The file format of quickqueries.json (and of exported files).</summary>
public sealed class QuickQueryFile
{
    public int Version { get; set; } = 1;
    public List<QuickQuery> Queries { get; set; } = new();
}

/// <summary>%APPDATA%\MyFront\quickqueries.json, plus import / export of that file.</summary>
public static class QuickQueryStore
{
    public static string FilePath => AppPaths.File("quickqueries.json");

    public static List<QuickQuery> Load()
    {
        var path = FilePath;
        if (!File.Exists(path)) return new List<QuickQuery>();
        return Read(path);
    }

    public static void Save(IEnumerable<QuickQuery> queries) => Write(FilePath, queries);

    public static void Export(string path, IEnumerable<QuickQuery> queries) => Write(path, queries);

    /// <summary>Reads an exported file; ids are kept so re-importing the same file replaces instead of duplicating.</summary>
    public static List<QuickQuery> Import(string path, IReadOnlyList<Account> accounts)
    {
        var list = Read(path);
        Relink(list, accounts);
        return list;
    }

    /// <summary>Points buttons at the local account with the same name when the saved id doesn't exist here.</summary>
    public static void Relink(IEnumerable<QuickQuery> queries, IReadOnlyList<Account> accounts)
    {
        foreach (var q in queries)
        {
            if (q.AccountId is null || accounts.Any(a => a.Id == q.AccountId)) continue;
            var byName = accounts.FirstOrDefault(a => string.Equals(a.Name, q.AccountName, StringComparison.OrdinalIgnoreCase));
            if (byName is not null) q.AccountId = byName.Id;
        }
    }

    private static List<QuickQuery> Read(string path)
    {
        try
        {
            var file = JsonSerializer.Deserialize(File.ReadAllText(path), CoreJson.Default.QuickQueryFile)
                       ?? throw new InvalidDataException("Empty file.");
            foreach (var q in file.Queries)
            {
                if (string.IsNullOrWhiteSpace(q.Id)) q.Id = Guid.NewGuid().ToString("N");
                if (q.Hotkey is < 1 or > 9) q.Hotkey = null;
                q.Name = string.IsNullOrWhiteSpace(q.Name) ? "Query" : q.Name.Trim();
                q.Sql ??= "";
            }
            return file.Queries;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} is not a quick query file: {ex.Message}", ex);
        }
    }

    private static void Write(string path, IEnumerable<QuickQuery> queries)
    {
        var json = JsonSerializer.Serialize(new QuickQueryFile { Queries = queries.ToList() }, CoreJson.Default.QuickQueryFile);
        AppPaths.WriteAllTextAtomic(path, json);
    }
}
