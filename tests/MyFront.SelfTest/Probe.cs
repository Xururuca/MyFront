using MyFront.Core.Accounts;
using MyFront.Core.Db;
using MySqlConnector;

namespace MyFront.SelfTest;

/// <summary>Quick look at the test server: version, TLS per mode, certificate names, schema.</summary>
internal static class Probe
{
    public static async Task RunAsync(TestDb db)
    {
        foreach (var mode in Enum.GetValues<TlsMode>())
        {
            try
            {
                using var s = await ServerSession.ConnectAsync(db.ToAccount(mode), db.Password);
                Console.WriteLine($"{mode,-11} OK   {s.ServerVersion}  {s.TlsVersion} {s.TlsCipher}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{mode,-11} FAIL {ex.GetType().Name}: {ex.Message}");
            }
        }

        using var session = await ServerSession.ConnectAsync(db.ToAccount(TlsMode.Required), db.Password);
        await using var conn = await session.OpenAsync(db.Database);
        foreach (var sql in new[]
                 {
                     "SELECT @@version, @@character_set_server, @@collation_server, @@sql_mode, @@max_allowed_packet, @@have_ssl",
                     "SELECT TABLE_NAME, TABLE_TYPE, ENGINE, TABLE_ROWS, DATA_LENGTH FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE()",
                     "SHOW CREATE TABLE products",
                     "SHOW CREATE TABLE prices",
                     "SELECT * FROM prices LIMIT 3",
                     "SHOW GRANTS",
                 })
        {
            Console.WriteLine("\n> " + sql);
            await using var cmd = new MySqlCommand(sql, conn);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var vals = new object[r.FieldCount];
                r.GetValues(vals);
                Console.WriteLine("  " + string.Join(" | ", vals.Select(v => v is DBNull ? "NULL" : v.ToString())));
            }
        }
    }
}
