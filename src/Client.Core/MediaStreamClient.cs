using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using SchoolBell.Contracts;

namespace SchoolBell.Client.Core;

/// Media WebSocket (/ws/media?announcement_id=&token=, PROTOCOL §7): authenticated by
/// the short-lived single-use media token issued on the control channel and bound to
/// (device_id, announcement_id). Binary frames are parsed via Contracts.MediaFrame.
public sealed class MediaStreamClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();

    public string AnnouncementId { get; }
    public WebSocketState State => _ws.State;

    private MediaStreamClient(string announcementId) => AnnouncementId = announcementId;

    public static async Task<MediaStreamClient> ConnectAsync(
        ClientConfig cfg, string announcementId, string token, TimeProvider time,
        ILogger log, CancellationToken ct)
    {
        var client = new MediaStreamClient(announcementId);
        client._ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(Protocol.HeartbeatSeconds);
        // ping/pong heartbeat: a server that stops answering for 30 s is treated as gone
        client._ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(Protocol.StaleAfterSeconds);
        using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, connectTimeout.Token);
        await client._ws.ConnectAsync(new Uri(cfg.MediaWsUrl(announcementId, token)), linked.Token);
        log.LogInformation("media channel open for announcement {AnnouncementId} (token not logged)", announcementId);
        return client;
    }

    /// Next SBMF frame; null on socket close. Malformed/oversized frames are skipped (counted via null-on-parse).
    public async Task<MediaFrame?> ReceiveFrameAsync(CancellationToken ct)
    {
        var buffer = new byte[Protocol.MaxMediaFrameBytes];
        var ms = new MemoryStream(Protocol.MaxMediaFrameBytes);
        while (true)
        {
            WebSocketReceiveResult res;
            try { res = await _ws.ReceiveAsync(buffer, ct); }
            catch (WebSocketException) { return null; }
            if (res.MessageType == WebSocketMessageType.Close) return null;
            if (res.MessageType != WebSocketMessageType.Binary) continue; // ignore non-media frames
            ms.Write(buffer, 0, res.Count);
            if (ms.Length > Protocol.MaxMediaFrameBytes) return null; // oversized: treat as close
            if (res.EndOfMessage) break;
        }
        return MediaFrame.TryParse(ms.ToArray());
    }

    public async Task CloseAsync(WebSocketCloseStatus status, string reason)
    {
        try
        {
            if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await _ws.CloseAsync(status, reason, CancellationToken.None);
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _ws.Dispose();
        await Task.CompletedTask;
    }
}
