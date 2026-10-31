using System.Text.Json.Serialization;

namespace MyFront.Core.Accounts;

public enum TlsMode
{
    Preferred,
    Required,
    VerifyCA,
    VerifyFull,
}

/// <summary>A saved connection. Secrets are only ever stored DPAPI-encrypted.</summary>
public sealed class Account
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 3306;
    public string User { get; set; } = "root";
    public bool SavePassword { get; set; } = true;

    /// <summary>DPAPI-encrypted password ("dpapi1:..."), null when not saved.</summary>
    public string? PasswordEncrypted { get; set; }

    /// <summary>The database to work in; when set, the tree shows only this one.</summary>
    public string? DefaultDatabase { get; set; }
    public TlsMode Tls { get; set; } = TlsMode.Preferred;
    public string? CaFile { get; set; }
    public bool ReadOnly { get; set; }
    public int ConnectTimeoutSeconds { get; set; } = 15;
    public DateTime? LastLogin { get; set; }
    /// <summary>Optional SSH tunnel (null = direct connection).</summary>
    public SshSettings? Ssh { get; set; }
    /// <summary>Tables and views hidden in the tree (database → names): they sit in the database's closed "Hidden" folder.</summary>
    public Dictionary<string, List<string>>? HiddenTables { get; set; }

    [JsonIgnore]
    public bool UsesSsh => Ssh is { Enabled: true };

    /// <summary>In the tree and the Object Browser's database list: all of them, or only the connection's database.</summary>
    public bool ShowsDatabase(string name) =>
        string.IsNullOrEmpty(DefaultDatabase) || string.Equals(name, DefaultDatabase, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool HasSavedPassword => SavePassword && !string.IsNullOrEmpty(PasswordEncrypted);

    public string? GetSavedPassword() =>
        HasSavedPassword ? Dpapi.Unprotect(PasswordEncrypted!) : null;

    public void SetPassword(string? password)
    {
        PasswordEncrypted = SavePassword && password is not null ? Dpapi.Protect(password) : null;
    }

    public bool IsHidden(string database, string name) => HiddenTables?.GetValueOrDefault(database)?.Contains(name) == true;

    /// <summary>Hide / Unhide in the tree (names as the server spells them; nothing left → null).</summary>
    public void SetHidden(string database, string name, bool hidden)
    {
        HiddenTables ??= new Dictionary<string, List<string>>();
        if (!HiddenTables.TryGetValue(database, out var names)) HiddenTables[database] = names = new List<string>();
        names.Remove(name);
        if (hidden) names.Add(name);
        if (names.Count == 0) HiddenTables.Remove(database);
        if (HiddenTables.Count == 0) HiddenTables = null;
    }

    /// <summary>A hidden table renamed stays hidden under its new name.</summary>
    public void RenameHidden(string database, string name, string newName)
    {
        if (!IsHidden(database, name)) return;
        SetHidden(database, name, false);
        SetHidden(database, newName, true);
    }

    public Account Clone()
    {
        var copy = (Account)MemberwiseClone();
        copy.Ssh = Ssh?.Clone();
        copy.HiddenTables = HiddenTables?.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        return copy;
    }

    public override string ToString() => Name;
}
