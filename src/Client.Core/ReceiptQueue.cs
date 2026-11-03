using Microsoft.Extensions.Logging;
using SchoolBell.Contracts;

namespace SchoolBell.Client.Core;

/// Durable receipt queue (PROTOCOL §8): cap 5000 (oldest dropped, count logged),
/// batched for resync (≤500 per device.resync) and sent live one-by-one.
public sealed class ReceiptQueue
{
    private sealed class QueueData { public List<ReceiptPayload> Items { get; set; } = []; }

    private readonly JsonStore<QueueData> _store;
    private readonly Microsoft.Extensions.Logging.ILogger? _log;
    private readonly object _gate = new();
    private QueueData _data;
    private long _totalDroppedOldest;

    public ReceiptQueue(string dataDir, Microsoft.Extensions.Logging.ILogger? log = null)
    {
        _log = log;
        _store = new JsonStore<QueueData>(System.IO.Path.Combine(dataDir, "receipts.json"));
        _data = _store.Load() ?? new QueueData();
    }

    public int Count { get { lock (_gate) return _data.Items.Count; } }
    public long TotalDroppedOldest { get { lock (_gate) return _totalDroppedOldest; } }

    public void Enqueue(ReceiptPayload receipt)
    {
        lock (_gate)
        {
            _data.Items.Add(receipt);
            if (_data.Items.Count > Protocol.ReceiptQueueMax)
            {
                var drop = _data.Items.Count - Protocol.ReceiptQueueMax;
                _data.Items.RemoveRange(0, drop);
                _totalDroppedOldest += drop;
                _log?.LogWarning("receipt queue full: dropped {Dropped} oldest receipt(s) (total {Total})",
                    drop, _totalDroppedOldest);
            }
            _store.Save(_data);
        }
    }

    /// Takes (and durably removes) up to Protocol.ResyncReceiptsMax receipts for sending.
    public List<ReceiptPayload> DequeueBatch(int max = Protocol.ResyncReceiptsMax)
    {
        lock (_gate)
        {
            max = Math.Min(max, Protocol.ResyncReceiptsMax);
            var take = Math.Min(max, _data.Items.Count);
            if (take == 0) return [];
            var batch = _data.Items.GetRange(0, take);
            _data.Items.RemoveRange(0, take);
            _store.Save(_data);
            return batch;
        }
    }

    /// Returns unsent receipts to the front (connection dropped mid-send).
    public void PrependBack(List<ReceiptPayload> batch)
    {
        lock (_gate)
        {
            if (batch.Count == 0) return;
            _data.Items.InsertRange(0, batch);
            if (_data.Items.Count > Protocol.ReceiptQueueMax)
            {
                // Oldest-first cap: drop the oldest NON-requeued receipts (just after the batch).
                var drop = Math.Min(_data.Items.Count - Protocol.ReceiptQueueMax, _data.Items.Count - batch.Count);
                _data.Items.RemoveRange(batch.Count, drop);
                _totalDroppedOldest += drop;
                if (_data.Items.Count > Protocol.ReceiptQueueMax) // extreme case: trim the requeued batch itself
                    _data.Items.RemoveRange(Protocol.ReceiptQueueMax, _data.Items.Count - Protocol.ReceiptQueueMax);
            }
            _store.Save(_data);
        }
    }
}
