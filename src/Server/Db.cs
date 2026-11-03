using Microsoft.Data.Sqlite;

namespace SchoolBell.Server;

/// SQLite access: WAL, foreign keys on, busy_timeout; one short-lived connection per unit of work
/// (single-writer discipline: writers are short transactions; WAL busy_timeout arbitrates).
public sealed class Db
{
    private readonly ServerConfig _cfg;
    private readonly string _connectionString;

    public Db(ServerConfig cfg)
    {
        _cfg = cfg;
        Directory.CreateDirectory(cfg.DataDir);
        _connectionString = $"Data Source={cfg.DbPath}";
    }

    public string DbPath => _cfg.DbPath;

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        Execute(conn, "PRAGMA journal_mode=WAL;");
        Execute(conn, "PRAGMA foreign_keys=ON;");
        Execute(conn, "PRAGMA busy_timeout=5000;");
        return conn;
    }

    public void Init()
    {
        using var conn = Open();
        Execute(conn, "CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);");
        Execute(conn, "CREATE TABLE IF NOT EXISTS schema_version(version INTEGER NOT NULL PRIMARY KEY);");

        int current;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version";
            current = Convert.ToInt32(cmd.ExecuteScalar());
        }

        for (int i = current; i < Migrations.Sql.Length; i++)
        {
            using var tx = conn.BeginTransaction();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = Migrations.Sql[i];
                cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO schema_version(version) VALUES ($v)";
                cmd.Parameters.AddWithValue("$v", i + 1);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public string? GetSetting(string key)
    {
        using var conn = Open();
        return GetSetting(conn, key);
    }

    public void SetSetting(string key, string value)
    {
        using var conn = Open();
        SetSetting(conn, key, value);
    }

    public static string? GetSetting(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public static void SetSetting(SqliteConnection conn, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO settings(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = $v";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }
}
