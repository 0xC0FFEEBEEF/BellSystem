using System.Text.Json;
using SchoolBell.Client.Core;
using SchoolBell.Contracts;

namespace Client.Tests;

public class BellNotificationTests
{
    [Fact]
    public void Older_server_settings_default_to_twelve_seconds()
    {
        var settings = JsonSerializer.Deserialize<DeviceSettingsPayload>(
            """{"settings_version":1,"announcement_volume":80,"volume_cap":100,"groups":[]}""", ControlEnvelope.Json)!;
        Assert.Equal(12, settings.BellNotificationSeconds);
        var pipe = JsonSerializer.Deserialize<PipeSettings>("""{"announcement_volume":80,"volume_cap":100}""", ControlEnvelope.Json)!;
        Assert.Equal(12, pipe.BellNotificationSeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    public void Notification_duration_survives_offline_restart(int seconds)
    {
        var dir = TestDirs.Create("bell-notification");
        try
        {
            var store = new DeviceSettingsStore(dir);
            store.Apply(new(1, null, 80, 100, [], null, seconds));
            Assert.Equal(seconds, new DeviceSettingsStore(dir).Current!.BellNotificationSeconds);
        }
        finally { TestDirs.TryDelete(dir); }
    }
}
