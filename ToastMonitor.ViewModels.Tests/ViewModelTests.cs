using ToastMonitor.Collect;
using ToastMonitor.Core;
using ToastMonitor.ViewModels;

namespace ToastMonitor.ViewModels.Tests;

public sealed class ViewModelTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ToastDatabase _db;

    public ViewModelTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "tm-vm-test-" + Guid.NewGuid().ToString("N") + ".db");
        _db = new ToastDatabase(_dbPath);
        _db.Open();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { /* temp cleanup best effort */ }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    private long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private void SeedTurns()
    {
        var now = Now;
        var todayStart = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.Today)).ToUnixTimeSeconds();
        // Recent fixtures must remain in today's bucket just after midnight.
        var recentClaude = Math.Max(todayStart, now - 3600);
        var recentCodex = Math.Max(todayStart, now - 7200);
        var turns = new List<TurnRecord>
        {
            new(ToolKind.Claude, "s1", "proj", "claude-sonnet-4-5", recentClaude, 1000, 500, 0, 200, 0, 0.05),
            new(ToolKind.Codex, "s2", "proj", "gpt-5", recentCodex, 2000, 1000, 0, 0, 0, 0.10),
            new(ToolKind.Claude, "s3", "proj", "claude-sonnet-4-5", now - 40 * 86400, 500, 250, 0, 0, 0, 0.02),
        };
        Assert.True(_db.InsertTurns(turns));
        Assert.True(_db.UpsertSessions(new[]
        {
            new SessionInfo(ToolKind.Claude, "s1", "First", "proj", "claude-sonnet-4-5", now - 3600, now - 3600),
            new SessionInfo(ToolKind.Codex, "s2", "Second", "proj", "gpt-5", now - 7200, now - 7200),
        }));
    }

    [Fact]
    public void QuotaHeadline_RemainingAndUsed_DoNotMix()
    {
        var window = new QuotaWindowRow("Weekly", 70, null);
        Assert.Equal("30% of Weekly left", QuotaRowViewModel.Headline(window, true, "Weekly"));
        Assert.Equal("70% of Weekly used", QuotaRowViewModel.Headline(window, false, "Weekly"));
        Assert.Equal(30, window.RemainingPercent);
    }

    [Fact]
    public void QuotaResetText_PastReset_IsEmpty()
    {
        Assert.Equal("", QuotaRowViewModel.ResetText(Now - 60, Now));
        Assert.Equal("", QuotaRowViewModel.ResetText(null, Now));
        Assert.NotEqual("", QuotaRowViewModel.ResetText(Now + 3600, Now));
    }

    [Fact]
    public void QuotaWindow_Clamps_UsedPercent()
    {
        Assert.Equal(100, new QuotaWindowRow("W", 150, null).UsedPercentClamped);
        Assert.Equal(0, new QuotaWindowRow("W", -5, null).UsedPercentClamped);
    }

    [Fact]
    public async Task UsageQuery_TodayExcludesOldTurns()
    {
        SeedTurns();
        var svc = new UsageQueryService(_db);
        var snap = await svc.LoadLightAsync(new UsagePeriodConfiguration());
        // Today: claude 1000+500+200 cache + codex 2000+1000 = 4700 tokens (codex cache excluded, it is 0 here)
        Assert.Equal(4700, snap.Today.TotalTokens);
        Assert.Equal(2, snap.ByToolToday.Count);
        // All: + old claude 750 = 5450
        Assert.Equal(5450, snap.All.TotalTokens);
        Assert.Equal(0.15, snap.Today.Cost, 3);
    }

    [Fact]
    public async Task UsageQuery_Sessions_And_Turns_RoundTrip()
    {
        SeedTurns();
        var svc = new UsageQueryService(_db);
        var sessions = await svc.LoadSessionsAsync(null, 500);
        Assert.Equal(2, sessions.Count);
        Assert.Equal("s1", sessions[0].SessionId); // most recently updated first
        var turns = await svc.LoadTurnsAsync("claude", "s1");
        Assert.Single(turns);
        Assert.Equal(1000, turns[0].Input);
    }

    [Fact]
    public async Task UsageQuery_Daily_SumsMatchTotals()
    {
        SeedTurns();
        var svc = new UsageQueryService(_db);
        var daily = await svc.LoadDailyAsync(0, Now);
        Assert.True(daily.Count >= 2); // today + 40 days ago, in local days
        Assert.Equal(5450, daily.Sum(d => d.Tool == "codex" ? d.Input + d.Output : d.Input + d.Output + d.CacheRead));
    }

    [Fact]
    public async Task Popover_Refresh_PopulatesSummary_And_HidesQuotaRows()
    {
        SeedTurns();
        using var state = new AppState(_db, new QuotaService(new InMemoryCredentialProvider()));
        state.Settings.SetHideQuotaRow("claude", true);
        var vm = new PopoverViewModel(state);
        await vm.RefreshAsync(refreshQuota: false);
        Assert.Equal(2, vm.ByToolToday.Count);
        Assert.Contains("$", vm.TodayCostText);
        Assert.NotEmpty(vm.TodayTokensText);
        // Quota rows are empty without refresh; filtering logic itself:
        var rows = new List<QuotaRowViewModel>
        {
            new() { Key = "claude", Name = "Claude", Group = "quota" },
            new() { Key = "codex", Name = "Codex", Group = "quota" },
        };
        // Simulate a quota refresh result via the VM's backing list through reflection-free path:
        // PopoverViewModel exposes QuotaRows only after RefreshAsync(true); with no credentials
        // the service returns not-configured rows, and the claude row must be hidden.
        await vm.RefreshAsync(refreshQuota: true);
        Assert.DoesNotContain(vm.VisibleQuotaRows, r => r.Key == "claude");
        Assert.Empty(vm.VisibleQuotaRows);
        Assert.True(vm.QuotaEmpty);
    }

    [Fact]
    public void Settings_RoundTrip()
    {
        var s = new SettingsStore(_db);
        s.PollIntervalSeconds = 30;
        s.ShowRemaining = false;
        s.PeriodConfiguration = new UsagePeriodConfiguration(UsagePeriodMode.Calendar, UsageWeekStart.Sunday);
        s.TrayFontSize = 14;
        s.TrayTextMode = "tokens";
        s.SetHideQuotaRow("codex", true);
        s.SetSourceEnabled("claude", false);
        var s2 = new SettingsStore(_db);
        Assert.Equal(30, s2.PollIntervalSeconds);
        Assert.False(s2.ShowRemaining);
        Assert.Equal(UsagePeriodMode.Calendar, s2.PeriodConfiguration.Mode);
        Assert.Equal(UsageWeekStart.Sunday, s2.PeriodConfiguration.WeekStart);
        Assert.Equal(14, s2.TrayFontSize);
        Assert.Equal("tokens", s2.TrayTextMode);
        Assert.True(s2.GetHideQuotaRow("codex"));
        Assert.False(s2.GetSourceEnabled("claude"));
    }

    [Fact]
    public void HealthHub_Stale_And_Error_Rules()
    {
        var hub = new SourceHealthHub();
        var now = Now;
        hub.Record("claude", SourceHealthStatus.Ok, null, now - 200);
        var h = hub.Sources.Single();
        Assert.Equal(SourceHealthStatus.Stale, SourceHealthHub.StatusOf(h, now));
        hub.RecordError("claude", "boom", now);
        Assert.Equal(SourceHealthStatus.Error, SourceHealthHub.StatusOf(hub.Sources.Single(), now));
        hub.RecordIdle("claude", now);
        Assert.Equal(SourceHealthStatus.Ok, SourceHealthHub.StatusOf(hub.Sources.Single(), now));
    }

    [Fact]
    public void TraySummary_Modes()
    {
        var snap = new UsageQueryService.LightSnapshot(
            new UsageQueryService.ToolRow("all", "All", 1000, 500, 0, 0, 1.5, 1),
            new UsageQueryService.ToolRow("all", "All", 0, 0, 0, 0, 0, 0),
            new UsageQueryService.ToolRow("all", "All", 0, 0, 0, 0, 0, 0),
            new UsageQueryService.ToolRow("all", "All", 0, 0, 0, 0, 0, 0),
            new(), new(), new(), new(),
            new CostBreakdownRow(0, 0, 0), new CostBreakdownRow(0, 0, 0),
            new CostBreakdownRow(0, 0, 0), new CostBreakdownRow(0, 0, 0),
            0, 0, 0, 0, new(), 0, Now);
        Assert.Contains("1.50", AppState.TraySummary(snap, "tokens") + AppState.TraySummary(snap, "cost"));
        Assert.Equal("ToastMonitor", AppState.TraySummary(snap, "none"));
        var empty = snap with { Today = new UsageQueryService.ToolRow("all", "All", 0, 0, 0, 0, 0, 0) };
        Assert.Contains("no data", AppState.TraySummary(empty, "cost"));
    }

    [Fact]
    public async Task Dashboard_Filter_ByTool()
    {
        SeedTurns();
        using var state = new AppState(_db, new QuotaService(new InMemoryCredentialProvider()));
        var vm = new OverviewViewModel(state);
        await vm.RefreshAsync();
        Assert.Equal(2, vm.ByTool.Count);
        vm.Filter.Tool = "claude";
        await vm.RefreshAsync();
        Assert.Single(vm.ByTool);
        Assert.Equal("claude", vm.ByTool[0].Tool);
    }

    [Fact]
    public async Task Overview_PeriodChanges_KeepModelsAndTotalsInTheSameWindow()
    {
        SeedTurns();
        using var state = new AppState(_db, new QuotaService(new InMemoryCredentialProvider()));
        var vm = new OverviewViewModel(state);
        await vm.RefreshAsync();
        Assert.Equal(2, vm.Models.Sum(m => m.Count));
        Assert.Equal("2", vm.CallsText);
        vm.Filter.Slot = UsagePeriodSlot.All;
        await vm.RefreshAsync();
        Assert.Equal(3, vm.Models.Sum(m => m.Count));
        Assert.Equal("3", vm.CallsText);
        Assert.Equal(vm.ByTool.Sum(t => t.TotalTokens), vm.Models.Sum(m => m.Input + m.Output + (m.Tool == "codex" ? 0 : m.CacheRead)));
        // Old usage contributes to All Time totals, but never the last-30-day chart.
        Assert.Equal(30, vm.DailyBars.Count);
        Assert.Equal(4700L, vm.DailyBars.Sum(d => d.Tokens));
        var today = DateTime.Today;
        Assert.Equal((long)(today.Year * 10000 + today.Month * 100 + today.Day), vm.DailyBars[^1].Day);
        Assert.True(vm.DailyBars.Zip(vm.DailyBars.Skip(1)).All(p => p.First.Day < p.Second.Day));
    }

    [Fact]
    public async Task Overview_EmptyDatabase_ShowsRealZerosAndThirtyEmptyDays()
    {
        using var state = new AppState(_db, new QuotaService(new InMemoryCredentialProvider()));
        var vm = new OverviewViewModel(state);
        await vm.RefreshAsync();
        Assert.False(vm.HasAnyData);
        Assert.Empty(vm.ToolShares);
        Assert.Empty(vm.ModelShares);
        Assert.Equal("0", vm.TokensText);
        Assert.Equal("$0.00", vm.CostText);
        Assert.Equal(30, vm.DailyBars.Count);
        Assert.All(vm.DailyBars, day => { Assert.Equal(0L, day.Tokens); Assert.Equal(0, day.BarPercent); });
    }

    [Fact]
    public async Task Plans_Amortized_UsesSubscriptions()
    {
        SeedTurns();
        using var state = new AppState(_db, new QuotaService(new InMemoryCredentialProvider()));
        Assert.True(_db.UpsertSubscription(new Subscription(0, "Pro", "pro", Now - 86400, 0, "monthly", 20, "USD")));
        var vm = new PlansViewModel(state);
        await vm.RefreshAsync();
        Assert.Single(vm.Subscriptions);
        Assert.True(vm.AmortizedDaily > 0);
    }

    private static string Fixture(string name)
    {
        var here = AppContext.BaseDirectory;
        var p = Path.Combine(here, "Fixtures", name);
        if (File.Exists(p)) return p;
        var dir = new DirectoryInfo(here);
        while (dir is not null)
        {
            var cand = Path.Combine(dir.FullName, "ToastMonitor.Collect.Tests", "Fixtures", name);
            if (File.Exists(cand)) return cand;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("fixture not found: " + name);
    }

    /// <summary>End-to-end: Collect-stage Claude fixture -> DB -> UsageQueryService, Mac expectations preserved
    /// (fixture values fixed by Collect ParserTests: 2 turns, 812+901 input, 96+312 output, 120 cache read).</summary>
    [Fact]
    public async Task EndToEnd_ClaudeFixture_ThroughUsageQueryService()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tm-vm-e2e-" + Guid.NewGuid().ToString("N"));
        var projDir = Path.Combine(dir, "-Users-demo-dev-sample-project");
        Directory.CreateDirectory(projDir);
        var file = Path.Combine(projDir, "e1f5c1a2-0000-4000-8000-000000000001.jsonl");
        File.Copy(Fixture("claude-project-sample.jsonl"), file);
        var (turns, sessions) = ClaudeCodeParser.Scan(new[] { file }, new InMemoryParserStateStore());
        Assert.Equal(2, turns.Count);
        Assert.True(_db.InsertTurns(turns));
        Assert.True(_db.UpsertSessions(sessions));

        using var state = new AppState(_db, new QuotaService(new InMemoryCredentialProvider()));
        var svc = state.Queries;
        var from = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var to = new DateTimeOffset(2026, 8, 31, 23, 59, 59, TimeSpan.Zero).ToUnixTimeSeconds();
        var models = await svc.LoadModelsAsync(from, to);
        var row = Assert.Single(models);
        Assert.Equal("claude", row.Tool);
        Assert.Equal("claude-sonnet-4-6", row.Model);
        Assert.Equal(1713, row.Input);
        Assert.Equal(408, row.Output);
        Assert.Equal(120, row.CacheRead);
        var sessionRows = await svc.LoadSessionsAsync("claude");
        Assert.Contains(sessionRows, s => s.SessionId == "e1f5c1a2-0000-4000-8000-000000000001");
        var turnRows = await svc.LoadTurnsAsync("claude", "e1f5c1a2-0000-4000-8000-000000000001");
        Assert.Equal(2, turnRows.Count);
        Assert.Equal(2241, turnRows.Sum(t => t.Input + t.Output + t.CacheRead));
        try { Directory.Delete(dir, true); } catch { /* temp cleanup best effort */ }
    }

    [Fact]
    public async Task Popover_EmptyState_CollapsesSections()
    {
        using var state = new AppState(_db, new QuotaService(new InMemoryCredentialProvider()));
        var vm = new PopoverViewModel(state);
        await vm.RefreshAsync(refreshQuota: false);
        Assert.False(vm.HasByToolRows);
        Assert.True(vm.ByToolEmpty);
        Assert.False(vm.HasSessionRows);
        Assert.True(vm.SessionEmpty);
        Assert.Equal("尚未扫描", vm.LastScanText);
    }

    [Fact]
    public async Task Popover_SeededState_ExpandsSections()
    {
        SeedTurns();
        using var state = new AppState(_db, new QuotaService(new InMemoryCredentialProvider()));
        var vm = new PopoverViewModel(state);
        await vm.RefreshAsync(refreshQuota: false);
        Assert.True(vm.HasByToolRows);
        Assert.False(vm.ByToolEmpty);
        Assert.True(vm.HasSessionRows);
        Assert.False(vm.SessionEmpty);
    }
}
