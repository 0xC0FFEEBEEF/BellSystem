using SchoolBell.Server;
namespace Server.Tests;

public class DbMigrationTests
{
    [Fact]
    public void Migrations_apply_in_order_and_are_idempotent()
    {
        using var env = new TestEnv();
        using (var conn = env.Database.Open())
        {
            var version = Convert.ToInt32(conn.ScalarForTests("SELECT MAX(version) FROM schema_version"));
            Assert.Equal(Migrations.Sql.Length, version);
            var wal = conn.ScalarForTests("PRAGMA journal_mode") as string;
            Assert.Equal("wal", wal!.ToLowerInvariant());
        }

        // re-running Init does nothing and keeps data
        using (var conn = env.Database.Open())
            conn.Run("INSERT INTO groups(name) VALUES ('keepme')");
        env.Database.Init();
        using (var conn2 = env.Database.Open())
        {
            var count = Convert.ToInt32(conn2.ScalarForTests("SELECT COUNT(*) FROM groups WHERE name='keepme'"));
            Assert.Equal(1, count);
            var version = Convert.ToInt32(conn2.ScalarForTests("SELECT MAX(version) FROM schema_version"));
            Assert.Equal(Migrations.Sql.Length, version);
        }
    }

    [Fact]
    public void Settings_and_foreign_keys_work()
    {
        using var env = new TestEnv();
        env.Database.SetSetting("k", "v");
        Assert.Equal("v", env.Database.GetSetting("k"));

        using var conn = env.Database.Open();
        // FK enforcement: group_members must reference an existing group
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() =>
            conn.Run("INSERT INTO group_members(device_id, group_id) VALUES ('d1', 999)"));
    }
}

internal static class ScalarForTestsExt
{
    public static object? ScalarForTests(this Microsoft.Data.Sqlite.SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
