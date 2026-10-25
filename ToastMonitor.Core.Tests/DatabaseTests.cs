using ToastMonitor.Core;

namespace ToastMonitor.Core.Tests;

public class DatabaseTests : IDisposable
{
    private readonly string _path;
    private readonly ToastDatabase _db;

    public DatabaseTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "tm-stage1-" + Guid.NewGuid().ToString("N") + ".db");
        _db = new ToastDatabase(_path);
        _db.Open();
    }

    public void Dispose()
    {
        _db.Close();
        _db.Dispose();
        try { File.Delete(_path); } catch { /* best effort */ }
        try { File.Delete(_path + "-wal"); } catch { /* best effort */ }
        try { File.Delete(_path + "-shm"); } catch { /* best effort */ }
    }

    [Fact]
    public void FreshDatabase_IsSchemaVersion6()
    {
        Assert.Equal(6, _db.UserVersion());
    }

    [Fact]
    public void Settings_RoundTrip_AndMissingIsNull()
    {
        Assert.Null(_db.GetSetting("nope"));
        Assert.True(_db.SetSetting("k", "v"));
        Assert.Equal("v", _db.GetSetting("k"));
        Assert.True(_db.SetSetting("k", "v2"));
        Assert.Equal("v2", _db.GetSetting("k"));
    }

    [Fact]
    public void Subscriptions_Validation_AndRoundTrip()
    {
        var badPrice = new Subscription(0, "X", "claude", 1000, 0, "monthly", -1, "USD");
        Assert.False(_db.UpsertSubscription(badPrice));
        var badCycle = new Subscription(0, "X", "claude", 1000, 0, "daily", 10, "USD");
        Assert.False(_db.UpsertSubscription(badCycle));
        var badEnd = new Subscription(0, "X", "claude", 2000, 1000, "monthly", 10, "USD");
        Assert.False(_db.UpsertSubscription(badEnd));

        var good = new Subscription(0, "Pro", "claude", 1000, 0, "weekly", 20, "USD");
        Assert.True(_db.UpsertSubscription(good));
        var all = _db.GetSubscriptions();
        Assert.Single(all);
        Assert.Equal("Pro", all[0].Name);
        Assert.Equal("weekly", all[0].Cycle);
        Assert.True(_db.DeleteSubscription(all[0].Id));
        Assert.Empty(_db.GetSubscriptions());
    }

    [Fact]
    public void InsertTurns_SameEventId_CountsOnce_AndTakesMax()
    {
        var first = new TurnRecord(ToolKind.Claude, "s1", null, "claude-sonnet-4", 1000,
            100, 50, 0, 10, 5, 0.01, eventId: "claude:s1:msg-1");
        var second = new TurnRecord(ToolKind.Claude, "s1", null, "claude-sonnet-4", 1000,
            200, 40, 0, 20, 5, 0.02, eventId: "claude:s1:msg-1");
        Assert.True(_db.InsertTurns(new[] { first }));
        Assert.True(_db.InsertTurns(new[] { second }));
        Assert.Equal(1, _db.TurnCount());
        var totals = _db.GetTotals(0, 2000);
        // MAX per field (audit S1 fix), not first-wins and not summed.
        Assert.Equal(200, totals.Input);
        Assert.Equal(50, totals.Output);
        Assert.Equal(20, totals.CacheRead);
    }

    [Fact]
    public void InsertTurns_DerivedRow_IsReplacedByRealEventId()
    {
        var derived = new TurnRecord(ToolKind.Claude, "s1", null, "claude-sonnet-4", 1000,
            100, 50, 0, 10, 5, 0.01, eventId: null);
        var real = new TurnRecord(ToolKind.Claude, "s1", null, "claude-sonnet-4", 1000,
            100, 50, 0, 10, 5, 0.01, eventId: "claude:s1:msg-9");
        Assert.True(_db.InsertTurns(new[] { derived }));
        Assert.Equal(1, _db.TurnCount());
        Assert.True(_db.InsertTurns(new[] { real }));
        Assert.Equal(1, _db.TurnCount());
    }

    [Fact]
    public void ApiValue_RepricesAtListPrices_GroupedByModel()
    {
        var t1 = new TurnRecord(ToolKind.Claude, "s1", null, "claude-sonnet-4", 1000,
            1_000_000, 0, 0, 0, 0, 0);
        var t2 = new TurnRecord(ToolKind.Claude, "s1", null, "claude-opus-5", 1000,
            1_000_000, 0, 0, 0, 0, 0);
        Assert.True(_db.InsertTurns(new[] { t1, t2 }));
        Assert.Equal(3.0 + 5.0, _db.ApiValue(0, 2000), precision: 9);
        Assert.Equal(3.0 + 5.0, _db.ApiValue(0, 2000, "claude"), precision: 9);
        Assert.Equal(0, _db.ApiValue(0, 2000, "codex"), precision: 9);
    }

    [Fact]
    public void OrSpendSince_SubtractsPreCycleBaseline_FromFirstDayOnly()
    {
        // Baseline: 10 on day before start. Start day: max 25 → 15 counts.
        // Next day: max 7 → 7 counts. Total 22.
        _db.InsertOpenRouterSnapshot(86400 - 3600, 10);
        _db.InsertOpenRouterSnapshot(86400 + 3600, 25);
        _db.InsertOpenRouterSnapshot(86400 * 2 + 3600, 7);
        Assert.Equal(22.0, _db.OrSpendSince(86400), precision: 9);
    }
}
