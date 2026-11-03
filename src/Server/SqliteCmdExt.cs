using Microsoft.Data.Sqlite;

namespace SchoolBell.Server;

internal static class SqliteCmdExt
{
    private static object NullSafe(object? value) => value ?? DBNull.Value;

    public static int Run(this SqliteConnection conn, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, NullSafe(value));
        return cmd.ExecuteNonQuery();
    }

    public static int Run(this SqliteConnection conn, SqliteTransaction tx, string sql,
        params (string Name, object? Value)[] ps)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, NullSafe(value));
        return cmd.ExecuteNonQuery();
    }

    public static object? Scalar(this SqliteConnection conn, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, NullSafe(value));
        return cmd.ExecuteScalar();
    }

    public static int ScalarInt(this SqliteConnection conn, string sql, params (string Name, object? Value)[] ps)
        => Convert.ToInt32(conn.Scalar(sql, ps) ?? 0);

    public static bool Exists(this SqliteConnection conn, string countSql, params (string Name, object? Value)[] ps)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = countSql;
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, NullSafe(value));
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }
}
