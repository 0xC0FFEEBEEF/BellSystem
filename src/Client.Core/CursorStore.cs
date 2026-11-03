namespace SchoolBell.Client.Core;

/// Durable resync cursor: last control event_id durably processed (PROTOCOL §3).
/// Distinct from playback occurrence ids.
public sealed class CursorStore
{
    private sealed record CursorState(string? Cursor);
    private readonly JsonStore<CursorState> _store;
    private string? _cursor;

    public CursorStore(string dataDir)
    {
        _store = new JsonStore<CursorState>(System.IO.Path.Combine(dataDir, "cursor.json"));
        _cursor = _store.Load()?.Cursor;
    }

    public string? Cursor => _cursor;

    /// Advances the cursor only forward (replay of older events must not move it back).
    public void Advance(string? eventId)
    {
        if (string.IsNullOrEmpty(eventId)) return;
        _cursor = eventId;
        _store.Save(new CursorState(_cursor));
    }
}
