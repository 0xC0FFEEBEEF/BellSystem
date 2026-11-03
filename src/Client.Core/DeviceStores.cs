using SchoolBell.Contracts;

namespace SchoolBell.Client.Core;

/// Per-device settings pushed by the server (device.settings). Persisted so an offline restart
/// keeps the chosen output, volume and group membership (group-scoped bells need the groups).
public sealed class DeviceSettingsStore
{
    private readonly JsonStore<DeviceSettingsPayload> _store;
    private readonly object _gate = new();
    private DeviceSettingsPayload? _current;

    public DeviceSettingsStore(string dataDir)
    {
        _store = new JsonStore<DeviceSettingsPayload>(Path.Combine(dataDir, "settings.json"));
        _current = _store.Load();
    }

    public DeviceSettingsPayload? Current { get { lock (_gate) return _current; } }

    /// Accepts only a newer settings version; returns true when the stored settings changed.
    public bool Apply(DeviceSettingsPayload settings)
    {
        lock (_gate)
        {
            if (_current is not null && settings.SettingsVersion < _current.SettingsVersion) return false;
            if (_current is not null && _current == settings) return false;
            _current = settings with { Groups = settings.Groups ?? [] };
            _store.Save(_current);
            return true;
        }
    }
}

/// Bounded local tray history (newest first). Entries are replaced by announcement id so a
/// transcript update or correction overwrites the pending entry instead of duplicating it.
public sealed class HistoryStore
{
    private sealed class Data { public List<HistoryItemPayload> Items { get; set; } = []; }

    private readonly JsonStore<Data> _store;
    private readonly object _gate = new();
    private Data _data;

    public HistoryStore(string dataDir)
    {
        _store = new JsonStore<Data>(Path.Combine(dataDir, "history.json"));
        _data = _store.Load() ?? new Data();
    }

    public List<HistoryItemPayload> Snapshot() { lock (_gate) return _data.Items.ToList(); }

    public void Upsert(HistoryItemPayload item)
    {
        lock (_gate)
        {
            _data.Items.RemoveAll(i => i.AnnouncementId == item.AnnouncementId);
            _data.Items.Add(item);
            _data.Items = _data.Items.OrderByDescending(i => i.At).Take(Protocol.DeviceHistoryMax).ToList();
            _store.Save(_data);
        }
    }

    /// Identity change on a shared computer: drop targeted entries (they never carry text, but
    /// the next person should not even see that a targeted announcement happened).
    public bool DropTargeted()
    {
        lock (_gate)
        {
            int removed = _data.Items.RemoveAll(i => !i.Schoolwide);
            if (removed > 0) _store.Save(_data);
            return removed > 0;
        }
    }
}
