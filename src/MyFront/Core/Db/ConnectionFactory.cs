using MyFront.Core.Accounts;
using MySqlConnector;

namespace MyFront.Core.Db;

public enum ConnectionPurpose
{
    /// <summary>Short-lived, pooled: tree, object browser, data browser.</summary>
    Pooled,
    /// <summary>One long-lived connection per SQL editor tab; keeps session state (USE, @vars, transactions).</summary>
    Dedicated,
}

public static class ConnectionFactory
{
    public static MySqlConnectionStringBuilder Build(Account a, string? password, string? database, ConnectionPurpose purpose,
        string? hostOverride = null, int? portOverride = null)
    {
        var b = new MySqlConnectionStringBuilder
        {
            Server = hostOverride ?? a.Host,
            Port = (uint)(portOverride ?? a.Port),
            UserID = a.User,
            Password = password ?? "",
            Database = database ?? "",
            ApplicationName = AppPaths.AppName,
            ConnectionTimeout = (uint)Math.Clamp(a.ConnectTimeoutSeconds, 1, 300),
            DefaultCommandTimeout = 0,      // queries run until done or cancelled by the user
            Keepalive = 60,                 // remote servers behind NAT/firewalls
            AllowUserVariables = true,      // SQL editor scripts use @vars
            AllowZeroDateTime = true,       // show '0000-00-00' instead of throwing
            TreatTinyAsBoolean = false,     // show TINYINT(1) as 0/1 like the server does
            GuidFormat = MySqlGuidFormat.None, // CHAR(36)/BINARY(16) stay strings/bytes
            AllowLoadLocalInfile = false,
            AllowPublicKeyRetrieval = false,
            Pooling = purpose == ConnectionPurpose.Pooled,
            UseAffectedRows = purpose == ConnectionPurpose.Dedicated,
            CharacterSet = "utf8mb4",
        };
        if (purpose == ConnectionPurpose.Pooled)
        {
            b.MinimumPoolSize = 0;
            b.MaximumPoolSize = 4; // servers often cap connections per user (the test account allows 10)
            b.ConnectionIdleTimeout = 120;
        }

        b.SslMode = a.Tls switch
        {
            TlsMode.Required => MySqlSslMode.Required,
            TlsMode.VerifyCA => MySqlSslMode.VerifyCA,
            // through an SSH tunnel the host name is 127.0.0.1; the certificate chain is still checked
            // and the SSH host key (trusted on first use) vouches for the server
            TlsMode.VerifyFull => hostOverride is null ? MySqlSslMode.VerifyFull : MySqlSslMode.VerifyCA,
            _ => MySqlSslMode.Preferred,
        };
        if (a.Tls is TlsMode.VerifyCA or TlsMode.VerifyFull)
        {
            if (string.IsNullOrWhiteSpace(a.CaFile))
                throw new InvalidOperationException("Verify CA / Verify Full needs a CA certificate file.");
            if (!File.Exists(a.CaFile))
                throw new FileNotFoundException("CA certificate file not found.", a.CaFile);
            b.SslCa = a.CaFile;
        }
        else if (!string.IsNullOrWhiteSpace(a.CaFile) && File.Exists(a.CaFile))
        {
            b.SslCa = a.CaFile;
        }
        return b;
    }
}
