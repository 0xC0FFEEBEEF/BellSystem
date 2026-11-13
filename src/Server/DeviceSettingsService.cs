using Microsoft.Data.Sqlite;
using SchoolBell.Contracts;

namespace SchoolBell.Server;

/// Per-device settings chosen in /admin (PROTOCOL §13 device.settings): classroom speaker
/// (null = Windows default output), announcement level and cap, room, and group membership
/// (devices need their groups to match group-scoped bells in the cached manifest).
public sealed class DeviceSettingsService
{
    private readonly Db _db;
    private readonly OutboxService _outbox;
    private readonly TimeProvider _time;

    public DeviceSettingsService(Db db, OutboxService outbox, TimeProvider time)
    {
        _db = db; _outbox = outbox; _time = time;
    }

    public int BellNotificationSeconds => int.TryParse(_db.GetSetting("bell_notification_seconds"), out var seconds) ? Math.Clamp(seconds, 0, 300) : 12;

    public int DefaultAnnouncementVolume => int.TryParse(_db.GetSetting("default_announcement_volume"), out var v) ? Math.Clamp(v, 0, 100) : 80;
    public int DefaultVolumeCap => int.TryParse(_db.GetSetting("default_volume_cap"), out var v) ? Math.Clamp(v, 1, 100) : 100;

    public DeviceSettingsPayload For(string deviceId)
    {
        using var conn = _db.Open();
        return For(conn, deviceId);
    }

    private DeviceSettingsPayload For(SqliteConnection conn, string deviceId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT output_device, announcement_volume, volume_cap, room, settings_version FROM devices WHERE device_id = $d";
        cmd.Parameters.AddWithValue("$d", deviceId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return new DeviceSettingsPayload(0, null, DefaultAnnouncementVolume, DefaultVolumeCap, [], null, BellNotificationSeconds);
        var output = r.IsDBNull(0) ? null : r.GetString(0);
        int vol = r.IsDBNull(1) ? DefaultAnnouncementVolume : r.GetInt32(1);
        int cap = r.IsDBNull(2) ? DefaultVolumeCap : r.GetInt32(2);
        var room = r.IsDBNull(3) ? null : r.GetString(3);
        long version = r.GetInt64(4);
        r.Close();
        var groups = DevicesService.GroupsOf(conn, deviceId).Order().ToList();
        // settings_version is the Unix-ms time of the last change to anything in this payload
        // (settings, defaults, group membership), so it only ever grows.
        return new DeviceSettingsPayload(version, output, Math.Min(vol, cap), cap, groups, room, BellNotificationSeconds);
    }

    public ControlEnvelope Envelope(string deviceId) =>
        ControlEnvelope.Create(Protocol.TDeviceSettings, PayloadSerializer.Node(For(deviceId)));

    /// Queue the current settings for one device (durable: delivered on reconnect if offline).
    public void Push(string deviceId) => _outbox.Enqueue(deviceId, Envelope(deviceId));

    /// Group membership or defaults changed: bump the version and queue the new settings.
    public void Touch(string deviceId)
    {
        using (var conn = _db.Open())
            conn.Run("UPDATE devices SET settings_version = MAX(settings_version + 1, $now) WHERE device_id = $d",
                ("$now", _time.GetUtcNow().ToUnixTimeMilliseconds()), ("$d", deviceId));
        Push(deviceId);
    }

    public void PushAll()
    {
        foreach (var id in AllDeviceIds()) Touch(id);
    }

    private List<string> AllDeviceIds()
    {
        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT device_id FROM devices WHERE revoked = 0";
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public (int Status, string? Detail) Update(string actor, string deviceId, string? outputDevice, bool clearOutput,
        int? announcementVolume, int? volumeCap, string? room)
    {
        if (outputDevice is { Length: > 200 }) return (400, "output_device is too long");
        if (room is { Length: > 80 }) return (400, "room is too long");
        using var conn = _db.Open();
        if (!conn.Exists("SELECT COUNT(*) FROM devices WHERE device_id = $d", ("$d", deviceId))) return (404, "device not found");
        conn.Run("""
            UPDATE devices SET
              output_device = CASE WHEN $clr = 1 THEN NULL WHEN $o IS NULL THEN output_device ELSE $o END,
              announcement_volume = COALESCE($v, announcement_volume),
              volume_cap = COALESCE($c, volume_cap),
              room = COALESCE($r, room),
              settings_version = MAX(settings_version + 1, $now)
            WHERE device_id = $d
            """,
            ("$now", _time.GetUtcNow().ToUnixTimeMilliseconds()),
            ("$clr", clearOutput ? 1 : 0), ("$o", string.IsNullOrWhiteSpace(outputDevice) ? null : outputDevice.Trim()),
            ("$v", announcementVolume is null ? null : Math.Clamp(announcementVolume.Value, 0, 100)),
            ("$c", volumeCap is null ? null : Math.Clamp(volumeCap.Value, 1, 100)),
            ("$r", room), ("$d", deviceId));
        Audit.Add(conn, null, actor, "device.settings",
            $"device={deviceId} output={(clearOutput ? "(default)" : outputDevice ?? "(unchanged)")} volume={announcementVolume?.ToString() ?? "-"} cap={volumeCap?.ToString() ?? "-"}");
        conn.Dispose();
        Push(deviceId);
        return (200, null);
    }

    public void SetDefaults(string actor, int? announcementVolume, int? volumeCap, int? bellNotificationSeconds = null)
    {
        if (bellNotificationSeconds is not null) _db.SetSetting("bell_notification_seconds", Math.Clamp(bellNotificationSeconds.Value, 0, 300).ToString());
        if (announcementVolume is not null) _db.SetSetting("default_announcement_volume", Math.Clamp(announcementVolume.Value, 0, 100).ToString());
        if (volumeCap is not null) _db.SetSetting("default_volume_cap", Math.Clamp(volumeCap.Value, 1, 100).ToString());
        Audit.Add(_db, actor, "device.defaults", $"announcement_volume={announcementVolume} volume_cap={volumeCap} bell_notification_seconds={bellNotificationSeconds}");
        PushAll();
    }
}
