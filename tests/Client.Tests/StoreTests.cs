using SchoolBell.Client.Core;
using SchoolBell.Contracts;

namespace Client.Tests;

public class ClaimLedgerTests
{
    [Fact]
    public void First_claim_wins_duplicate_triggers_do_nothing()
    {
        var dir = TestDirs.Create("ledger");
        try
        {
            var ledger = new ClaimLedger(dir);
            Assert.True(ledger.TryClaim("occ-1", 1000));
            Assert.False(ledger.TryClaim("occ-1", 1001)); // duplicate: no second claim
            Assert.False(ledger.Has("occ-2"));
            Assert.True(ledger.TryClaim("occ-2", 1002));
        }
        finally { TestDirs.TryDelete(dir); }
    }

    [Fact]
    public void Reconnect_storm_hits_ledger_and_claims_once_survives_reload()
    {
        var dir = TestDirs.Create("ledger-storm");
        try
        {
            var ledger = new ClaimLedger(dir);
            int claimed = 0;
            // Simulate a reconnect storm: 50 triggers of the same occurrence.
            for (int i = 0; i < 50; i++)
                if (ledger.TryClaim("occ-x", 2000 + i)) claimed++;
            Assert.Equal(1, claimed);

            var reloaded = new ClaimLedger(dir); // durable across "service restart"
            Assert.True(reloaded.Has("occ-x"));
            Assert.False(reloaded.TryClaim("occ-x", 3000));
        }
        finally { TestDirs.TryDelete(dir); }
    }

    [Fact]
    public void Unterminated_claims_surface_as_unknown_on_startup()
    {
        var dir = TestDirs.Create("ledger-unknown");
        try
        {
            var ledger = new ClaimLedger(dir);
            ledger.TryClaim("crashed", 100);
            ledger.TryClaim("finished", 101);
            ledger.MarkTerminal("finished", ReceiptState.Completed);

            var reloaded = new ClaimLedger(dir);
            var unterminated = reloaded.UnterminalClaims();
            Assert.Single(unterminated);
            Assert.Equal("crashed", unterminated[0].OccurrenceId);

            reloaded.MarkTerminal("crashed", ReceiptState.Unknown);
            Assert.Empty(reloaded.UnterminalClaims());
        }
        finally { TestDirs.TryDelete(dir); }
    }

    [Fact]
    public void Prune_keeps_nonterminal_and_recent_terminal_claims()
    {
        var dir = TestDirs.Create("ledger-prune");
        try
        {
            var ledger = new ClaimLedger(dir);
            ledger.TryClaim("old-terminal", 100);
            ledger.MarkTerminal("old-terminal", ReceiptState.Completed);
            ledger.TryClaim("old-nonterminal", 100);
            ledger.TryClaim("new-terminal", 5_000);
            ledger.MarkTerminal("new-terminal", ReceiptState.Completed);

            ledger.PruneTerminalBefore(cutoffMs: 1_000);
            Assert.False(ledger.Has("old-terminal"));
            Assert.True(ledger.Has("old-nonterminal"));
            Assert.True(ledger.Has("new-terminal"));
        }
        finally { TestDirs.TryDelete(dir); }
    }
}

public class ReceiptQueueTests
{
    private static ReceiptPayload R(string id, string state = "expired") =>
        new(id, state, 1000, null);

    [Fact]
    public void Cap_is_5000_and_drops_oldest_with_count()
    {
        var dir = TestDirs.Create("receipts-cap");
        try
        {
            var log = new CapturingLogger();
            var q = new ReceiptQueue(dir, log);
            for (int i = 0; i < 5010; i++) q.Enqueue(R("occ-" + i));
            Assert.Equal(Protocol.ReceiptQueueMax, q.Count);          // 5000 cap
            Assert.Equal(10, q.TotalDroppedOldest);
            Assert.Contains("dropped 1 oldest", string.Join("\n", log.Messages));

            var first = q.DequeueBatch(1);
            Assert.Equal("occ-10", first[0].OccurrenceOrAnnouncementId); // oldest KEPT is #10
        }
        finally { TestDirs.TryDelete(dir); }
    }

    [Fact]
    public void Batch_is_capped_at_500_and_queue_survives_reload()
    {
        var dir = TestDirs.Create("receipts-batch");
        try
        {
            var q = new ReceiptQueue(dir);
            for (int i = 0; i < 1200; i++) q.Enqueue(R("occ-" + i));
            var batch = q.DequeueBatch(); // default max
            Assert.Equal(500, batch.Count);
            Assert.Equal(700, q.Count);

            var reloaded = new ReceiptQueue(dir);
            Assert.Equal(700, reloaded.Count);
        }
        finally { TestDirs.TryDelete(dir); }
    }

    [Fact]
    public void Failed_send_prepends_back_for_resync()
    {
        var dir = TestDirs.Create("receipts-prepend");
        try
        {
            var q = new ReceiptQueue(dir);
            q.Enqueue(R("a")); q.Enqueue(R("b")); q.Enqueue(R("c"));
            var batch = q.DequeueBatch(2);
            Assert.Equal(["a", "b"], batch.Select(x => x.OccurrenceOrAnnouncementId));
            q.PrependBack(batch); // connection died mid-send
            var again = q.DequeueBatch(10);
            Assert.Equal(["a", "b", "c"], again.Select(x => x.OccurrenceOrAnnouncementId));
        }
        finally { TestDirs.TryDelete(dir); }
    }
}

public class CursorStoreTests
{
    [Fact]
    public void Cursor_is_durable_and_advances()
    {
        var dir = TestDirs.Create("cursor");
        try
        {
            var store = new CursorStore(dir);
            Assert.Null(store.Cursor);
            store.Advance("event-1");
            var reloaded = new CursorStore(dir);
            Assert.Equal("event-1", reloaded.Cursor);
            reloaded.Advance("event-2");
            Assert.Equal("event-2", new CursorStore(dir).Cursor);
        }
        finally { TestDirs.TryDelete(dir); }
    }
}
