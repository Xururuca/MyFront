using System.Text.Json;

namespace MyFront.Core.Accounts;

/// <summary>Loads and saves %APPDATA%\MyFront\accounts.json.</summary>
public static class AccountStore
{
    private static string FilePath => AppPaths.File("accounts.json");

    public static List<Account> Load()
    {
        var path = FilePath;
        if (!File.Exists(path))
            return new List<Account>();
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), CoreJson.Default.ListAccount) ?? new List<Account>();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"accounts.json is damaged: {ex.Message}", ex);
        }
    }

    public static void Save(IEnumerable<Account> accounts)
    {
        AppPaths.WriteAllTextAtomic(FilePath, JsonSerializer.Serialize(accounts.ToList(), CoreJson.Default.ListAccount));
    }

    /// <summary>Writes an open connection's hidden tables into its saved copy (a connection that isn't saved keeps them for now only).</summary>
    public static void SaveHiddenTables(Account account)
    {
        var list = Load();
        if (list.FirstOrDefault(a => a.Id == account.Id) is not { } saved) return;
        saved.HiddenTables = account.Clone().HiddenTables;
        Save(list);
    }

    public static void Upsert(Account account)
    {
        var list = Load();
        var i = list.FindIndex(a => a.Id == account.Id);
        if (i >= 0) list[i] = account; else list.Add(account);
        Save(list);
    }
}
