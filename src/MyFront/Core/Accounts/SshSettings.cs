using System.Text.Json.Serialization;

namespace MyFront.Core.Accounts;

public enum SshAuth { Password, PrivateKey }

/// <summary>Optional SSH tunnel for an account. Secrets are stored DPAPI-encrypted only.</summary>
public sealed class SshSettings
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string User { get; set; } = "";
    public SshAuth Auth { get; set; } = SshAuth.Password;
    public bool SaveSecret { get; set; } = true;
    /// <summary>DPAPI-encrypted SSH password (password auth).</summary>
    public string? PasswordEncrypted { get; set; }
    /// <summary>OpenSSH or PuTTY (.ppk) private key file.</summary>
    public string? KeyFile { get; set; }
    /// <summary>DPAPI-encrypted key passphrase (key auth), null if the key has none or it isn't saved.</summary>
    public string? PassphraseEncrypted { get; set; }
    /// <summary>The database host as seen from the SSH server (usually 127.0.0.1 when MariaDB runs there).</summary>
    public string DbHost { get; set; } = "127.0.0.1";
    /// <summary>The database port as seen from the SSH server; null = the account's port.</summary>
    public int? DbPort { get; set; }
    /// <summary>Trusted server host key, "ssh-ed25519 SHA256:…" (set the first time you confirm it).</summary>
    public string? HostKey { get; set; }

    /// <summary>Typed but not saved (e.g. while testing in the dialog); wins over the saved secret. Never serialized.</summary>
    [JsonIgnore] public string? PasswordOverride { get; set; }
    [JsonIgnore] public string? PassphraseOverride { get; set; }

    [JsonIgnore] public string? Password => PasswordOverride ?? (PasswordEncrypted is null ? null : Dpapi.Unprotect(PasswordEncrypted));
    [JsonIgnore] public string? Passphrase => PassphraseOverride ?? (PassphraseEncrypted is null ? null : Dpapi.Unprotect(PassphraseEncrypted));

    public void SetPassword(string? value) => PasswordEncrypted = SaveSecret && !string.IsNullOrEmpty(value) ? Dpapi.Protect(value) : null;
    public void SetPassphrase(string? value) => PassphraseEncrypted = SaveSecret && !string.IsNullOrEmpty(value) ? Dpapi.Protect(value) : null;

    public SshSettings Clone() => (SshSettings)MemberwiseClone();

    public override string ToString() => $"{User}@{Host}:{Port}";
}
