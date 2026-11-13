using System.Text;
using SchoolBell.Contracts;

namespace SchoolBell.Server;

public sealed record ClientLogRow(long Id, string DeviceId, string Label, string Source, string FileId,
    long Offset, string Text, long ReceivedAt);

public sealed class ClientLogsService(Db db, TimeProvider time)
{
    public const int KeepDays = 14;

    public bool Authenticate(string device, string timestamp, string proof, byte[] body)
    {
        if (!long.TryParse(timestamp, out var at) || at < time.GetUtcNow().ToUnixTimeMilliseconds() - 600_000 ||
            at > time.GetUtcNow().ToUnixTimeMilliseconds() + 600_000) return false;
        using var c = db.Open();
        var secret = c.Scalar("SELECT secret FROM devices WHERE device_id = $d AND revoked = 0", ("$d", device)) as string;
        return secret is not null && AuthProof.Verify(secret, ClientLogAuth.Nonce(timestamp, body), device, proof);
    }

    public bool Ingest(string device, ClientLogChunk chunk)
    {
        if (string.IsNullOrWhiteSpace(chunk.Source) || chunk.Source.Length > 240 || chunk.Source.Any(char.IsControl) ||
            !Guid.TryParseExact(chunk.FileId, "N", out _) || chunk.Offset < 0 ||
            chunk.Encoding is not ("utf-8" or "utf-16" or "utf-16BE")) return false;
        byte[] data;
        try { data = Convert.FromBase64String(chunk.Data); } catch (FormatException) { return false; }
        if (data.Length is 0 or > ClientLogAuth.MaxChunkBytes) return false;
        var encoding = chunk.Encoding == "utf-16" ? Encoding.Unicode : chunk.Encoding == "utf-16BE" ? Encoding.BigEndianUnicode : Encoding.UTF8;
        var text = encoding.GetString(data);
        if (chunk.Offset == 0) text = text.TrimStart('\ufeff');
        using var c = db.Open();
        c.Run("""
            INSERT OR IGNORE INTO client_logs(device_id, source, file_id, byte_offset, text, received_at)
            VALUES ($d, $s, $f, $o, $text, $t)
            """, ("$d", device), ("$s", chunk.Source), ("$f", chunk.FileId), ("$o", chunk.Offset),
            ("$text", text), ("$t", time.GetUtcNow().ToUnixTimeMilliseconds()));
        return true;
    }

    public List<object> Endpoints()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT device_id, COALESCE(computer_name, name), active_user FROM devices ORDER BY COALESCE(computer_name, name) COLLATE NOCASE";
        using var r = cmd.ExecuteReader();
        var items = new List<object>();
        while (r.Read()) items.Add(new { device_id = r.GetString(0), label = r.IsDBNull(2) ? r.GetString(1) : r.GetString(2) + " (" + r.GetString(1) + ")" });
        return items;
    }

    public List<ClientLogRow> Read(string? device, string? q, int count, long? before = null, int days = KeepDays)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT l.id, l.device_id, COALESCE(d.computer_name, d.name), l.source, l.file_id,
                   l.byte_offset, l.text, l.received_at
            FROM client_logs l JOIN devices d ON d.device_id = l.device_id
            WHERE ($d IS NULL OR l.device_id = $d) AND ($before IS NULL OR l.id < $before)
              AND ($q IS NULL OR instr(lower(l.text), lower($q)) > 0 OR instr(lower(l.source), lower($q)) > 0)
              AND l.received_at >= $since
            ORDER BY l.id DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$d", string.IsNullOrEmpty(device) ? DBNull.Value : device);
        cmd.Parameters.AddWithValue("$q", string.IsNullOrEmpty(q) ? DBNull.Value : q);
        cmd.Parameters.AddWithValue("$before", (object?)before ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$since", time.GetUtcNow().ToUnixTimeMilliseconds() - Math.Clamp(days, 1, KeepDays) * 86_400_000L);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(count, 1, 500));
        var rows = new List<ClientLogRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
            r.GetString(4), r.GetInt64(5), r.GetString(6), r.GetInt64(7)));
        return rows;
    }

    public void Retain()
    {
        using var c = db.Open();
        c.Run("DELETE FROM client_logs WHERE received_at < $t", ("$t", time.GetUtcNow().ToUnixTimeMilliseconds() - KeepDays * 86_400_000L));
    }
}
