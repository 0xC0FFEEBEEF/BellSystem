using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using SchoolBell.Contracts;

namespace SchoolBell.Server;

/// One live device control connection with a bounded send queue.
/// All socket writes go through this single consumer, so frames never interleave.
/// A slow device never stalls others: when the queue is full events are counted as dropped and
/// remain in the durable outbox for resync; repeated drops close the connection.
/// Heartbeat pings/pongs are handled by Kestrel (WebSocketOptions.KeepAliveInterval = 10 s);
/// .NET's WebSocket type exposes no app-level Ping send.
public sealed class ControlConnection
{
    private const int SendQueueCapacity = 200;
    private const int MaxDropsBeforeClose = 100;

    private readonly WebSocket _socket;
    private readonly ILogger _log;
    private readonly Channel<string> _out = Channel.CreateBounded<string>(new BoundedChannelOptions(SendQueueCapacity)
    {
        FullMode = BoundedChannelFullMode.Wait, // TryWrite returns false when full — counted as a drop
        SingleReader = true,
        SingleWriter = false,
    });
    private int _dropped;
    private long _lastSeenMs;

    public string DeviceId { get; }
    public long LastSeenMs => Volatile.Read(ref _lastSeenMs);
    public bool Closing { get; private set; }

    public ControlConnection(string deviceId, WebSocket socket, long nowMs, ILogger log)
    {
        DeviceId = deviceId; _socket = socket; _lastSeenMs = nowMs; _log = log;
    }

    public void Touch(long nowMs) => Volatile.Write(ref _lastSeenMs, nowMs);

    public void Send(ControlEnvelope env) => SendRaw(env.ToJson());

    public void SendRaw(string json)
    {
        if (Closing) return;
        if (_out.Writer.TryWrite(json)) return;
        var dropped = Interlocked.Increment(ref _dropped);
        _log.LogWarning("device {DeviceId} send queue overflow (dropped count {Dropped}); events remain in the durable outbox",
            DeviceId, dropped);
        if (dropped > MaxDropsBeforeClose) Close(WebSocketCloseStatus.PolicyViolation, "receive_queue_overflow");
    }

    public void Close(WebSocketCloseStatus status, string reason)
    {
        if (Closing) return;
        Closing = true;
        _ = CloseAsync(status, reason);
    }

    private async Task CloseAsync(WebSocketCloseStatus status, string reason)
    {
        try
        {
            if (_socket.State == WebSocketState.Open || _socket.State == WebSocketState.CloseReceived)
                await _socket.CloseAsync(status, reason, CancellationToken.None);
        }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
    }

    public async Task RunSendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var msg in _out.Reader.ReadAllAsync(ct))
            {
                if (_socket.State != WebSocketState.Open) break;
                await _socket.SendAsync(System.Text.Encoding.UTF8.GetBytes(msg),
                    WebSocketMessageType.Text, true, ct);
            }
        }
        catch (WebSocketException) { Close(WebSocketCloseStatus.EndpointUnavailable, "socket error"); }
        catch (OperationCanceledException) { }
    }
}

/// Connection registry. Bound is configurable and visible in /api/diagnostics — no hidden cap.
public sealed class ControlRegistry
{
    private readonly ConcurrentDictionary<string, ControlConnection> _conns = new();
    private readonly ServerConfig _cfg;
    private readonly ILogger<ControlRegistry> _log;

    public ControlRegistry(ServerConfig cfg, ILogger<ControlRegistry> log)
    {
        _cfg = cfg; _log = log;
    }

    public int Count => _conns.Count;
    public int MaxConnections => _cfg.MaxConnections;

    public ControlConnection? Register(ControlConnection conn)
    {
        if (_conns.Count >= _cfg.MaxConnections && !_conns.ContainsKey(conn.DeviceId))
        {
            _log.LogWarning("connection limit reached ({Max}); rejecting {DeviceId} (limit is configurable, not a product cap)",
                _cfg.MaxConnections, conn.DeviceId);
            return null;
        }
        var added = false;
        _conns.AddOrUpdate(conn.DeviceId, c => { added = true; return conn; }, (_, existing) =>
        {
            existing.Close(WebSocketCloseStatus.PolicyViolation, "replaced by newer connection");
            return conn;
        });
        return added ? conn : conn; // duplicate device: new conn replaces old
    }

    public void Remove(string deviceId, ControlConnection conn) =>
        _conns.TryRemove(new KeyValuePair<string, ControlConnection>(deviceId, conn));

    public ControlConnection? Lookup(string deviceId) =>
        _conns.TryGetValue(deviceId, out var c) ? c : null;

    public void CloseDevice(string deviceId, string reason) =>
        Lookup(deviceId)?.Close(WebSocketCloseStatus.PolicyViolation, reason);

    public List<(string DeviceId, long LastSeenMs)> Snapshot() =>
        _conns.Values.Select(c => (c.DeviceId, c.LastSeenMs)).ToList();
}
