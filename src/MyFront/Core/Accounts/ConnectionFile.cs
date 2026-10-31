using System.Text.Json;
using MyFront.Core.Editors;
using MyFront.Core.Quick;

namespace MyFront.Core.Accounts;

/// <summary>One SQL editor tab's text in a connection file.</summary>
public sealed class ConnectionFileEditor
{
    /// <summary>1 … 5 = SQL Editor 1 … 5; 6+ = tabs added with "New SQL Editor".</summary>
    public int Slot { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>
/// A connection and what is kept for it on this PC — its settings, its SQL editor texts and its own quick query
/// buttons — in one .json file, to take it to another PC (or keep a copy). Saved passwords are never in it.
/// </summary>
public sealed class ConnectionFile
{
    public const string FormatName = "MyFront connection";

    /// <summary>"MyFront connection" (anything else is not a connection file).</summary>
    public string? Format { get; set; }
    public int Version { get; set; } = 1;
    public DateTime Exported { get; set; }
    public Account Account { get; set; } = new();
    public List<ConnectionFileEditor> Editors { get; set; } = new();
    public List<QuickQuery> QuickQueries { get; set; } = new();

    /// <summary>Writes the connection without its saved passwords, its editor texts and the quick query buttons that belong to it.</summary>
    public static ConnectionFile Export(string path, Account account, IEnumerable<QuickQuery> quickQueries)
    {
        var file = new ConnectionFile
        {
            Format = FormatName,
            Exported = DateTime.Now,
            Account = WithoutSecrets(account),
            Editors = EditorStore.Load(account.Id).Tabs
                .Select(t => new ConnectionFileEditor { Slot = t.Slot, Text = EditorStore.ReadText(account.Id, t.Slot) })
                .Where(e => e.Text.Length > 0).ToList(),
            QuickQueries = quickQueries.Where(q => q.AccountId == account.Id).Select(q => q.Clone()).ToList(),
        };
        AppPaths.WriteAllTextAtomic(path, JsonSerializer.Serialize(file, CoreJson.Default.ConnectionFile));
        return file;
    }

    public static ConnectionFile Read(string path)
    {
        ConnectionFile? file;
        try
        {
            file = JsonSerializer.Deserialize(File.ReadAllText(path), CoreJson.Default.ConnectionFile);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} is not a MyFront connection file: {ex.Message}", ex);
        }
        if (file is null || file.Format != FormatName || file.Account is null)
            throw new InvalidDataException($"{Path.GetFileName(path)} is not a MyFront connection file.");
        return file;
    }

    /// <summary>
    /// Adds the file's connection as a new one — never replacing one: a new id, and "name (2)" when the name is taken —
    /// writes its editor texts and adds its quick query buttons (a hotkey already used on its toolbar is dropped).
    /// A button still here from a deleted connection (same id, its connection gone) is taken over, not added twice.
    /// The caller saves <paramref name="accounts"/> and <paramref name="quickQueries"/>.
    /// </summary>
    public Account AddTo(List<Account> accounts, List<QuickQuery> quickQueries)
    {
        var a = WithoutSecrets(Account);
        a.Id = Guid.NewGuid().ToString("N");
        a.Name = UniqueName(string.IsNullOrWhiteSpace(a.Name) ? a.Host : a.Name.Trim(), accounts);
        foreach (var e in Editors.Where(e => e.Slot is >= 1 and <= 9999 && !string.IsNullOrEmpty(e.Text)))
            EditorStore.WriteText(a.Id, e.Slot, e.Text);
        foreach (var q in QuickQueries)
        {
            var copy = q.Clone();
            copy.AccountId = a.Id;
            copy.AccountName = a.Name;
            copy.Name = string.IsNullOrWhiteSpace(copy.Name) ? "Query" : copy.Name.Trim();
            copy.Sql ??= "";
            // export → delete the connection → import: its buttons stayed and would re-link to it by name — use those places
            int left = quickQueries.FindIndex(x => x.Id == q.Id && x.AccountId is not null && accounts.All(y => y.Id != x.AccountId));
            if (left < 0) copy.Id = Guid.NewGuid().ToString("N");
            var others = quickQueries.Where((_, i) => i != left);
            if (copy.Hotkey is < 1 or > 9 || others.Any(x => x.Hotkey == copy.Hotkey && x.SharesToolbarWith(copy))) copy.Hotkey = null;
            if (left < 0) quickQueries.Add(copy);
            else quickQueries[left] = copy;
        }
        accounts.Add(a);
        return a;
    }

    private static Account WithoutSecrets(Account account)
    {
        var a = account.Clone();
        a.PasswordEncrypted = null;
        a.LastLogin = null;
        if (a.Ssh is { } ssh)
        {
            ssh.PasswordEncrypted = ssh.PassphraseEncrypted = null;
            ssh.PasswordOverride = ssh.PassphraseOverride = null;
        }
        return a;
    }

    private static string UniqueName(string name, List<Account> accounts)
    {
        bool Taken(string n) => accounts.Any(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase));
        if (!Taken(name)) return name;
        for (int i = 2; ; i++)
            if (!Taken($"{name} ({i})")) return $"{name} ({i})";
    }
}
