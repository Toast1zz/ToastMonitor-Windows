using ToastMonitor.Core;

namespace ToastMonitor.Core.Tests;

public class Stage4Tests : IDisposable
{
    private readonly string _path;
    private readonly string _backupDir;
    private readonly ToastDatabase _db;

    public Stage4Tests()
    {
        _path = Path.Combine(Path.GetTempPath(), "tm-stage4-" + Guid.NewGuid().ToString("N") + ".db");
        _backupDir = Path.Combine(Path.GetTempPath(), "tm-stage4-bak-" + Guid.NewGuid().ToString("N"));
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
        try { Directory.Delete(_backupDir, recursive: true); } catch { /* best effort */ }
    }

    private static TurnRecord Turn(ToolKind tool, long ts, string? model, long input, long output,
        long cacheRead, double cost, string? provider = null, string quality = "estimated", string? eventId = null) =>
        new(tool, "s1", null, model, ts, input, output, 0, cacheRead, 0, cost, provider,
            costQuality: quality, eventId: eventId ?? Guid.NewGuid().ToString("N"));

    private void SeedMixedTurns()
    {
        _db.InsertTurns(new[]
        {
            Turn(ToolKind.Claude, 899, "claude-opus", 100, 50, 20, 0.5),
            Turn(ToolKind.Claude, 900, "claude-opus", 200, 100, 0, 1.0, quality: "actual"),
            Turn(ToolKind.Claude, 1799, null, 300, 150, 10, 1.5),
            Turn(ToolKind.Claude, 1800, "claude-opus", 400, 200, 0, 2.0),
            Turn(ToolKind.Codex, 1000, "gpt-5", 500, 250, 600, 3.0),
            Turn(ToolKind.Dsh, 1200, "deepseek-chat", 700, 350, 0, 0.7, provider: "deepseek", quality: "actual"),
        });
    }

    // MARK: - Rollup

    [Fact]
    public void RollupSpan_WholeBucketsOnly()
    {
        Assert.Equal((900L, 1800L), ToastDatabase.RollupSpan(900, 1799, true));
        Assert.Equal((2001L, 2001L), ToastDatabase.RollupSpan(1000, 2000, true));
        Assert.Equal((2001L, 2001L), ToastDatabase.RollupSpan(900, 2000, false));
        Assert.Equal((900L, 900L), ToastDatabase.RollupSpan(900, 899, true));
    }

    [Fact]
    public void Rollup_MismatchZero_AfterInsertsAndMerge()
    {
        SeedMixedTurns();
        Assert.Equal(0, _db.RollupMismatchCount());
        // Same event id re-inserted with larger values exercises the UPDATE trigger path.
        var first = Turn(ToolKind.Claude, 900, "claude-opus", 200, 100, 0, 1.0, quality: "actual", eventId: "evt-1");
        _db.InsertTurns(new[] { first });
        var bigger = Turn(ToolKind.Claude, 900, "claude-opus", 999, 100, 0, 9.0, quality: "actual", eventId: "evt-1");
        _db.InsertTurns(new[] { bigger });
        Assert.Equal(0, _db.RollupMismatchCount());
    }

    [Fact]
    public void Aggregates_RollupMatchesRaw_InBothModes()
    {
        SeedMixedTurns();
        _db.AggregatesUseRollup = true;
        var rollupTools = _db.TotalsByTool(0, 100000);
        var rollupModels = _db.ModelAggregates(0, 100000);
        var rollupDays = _db.DailyAggregates(0, 100000);
        var rollupBreakdown = _db.CostBreakdown(0, 100000);
        _db.AggregatesUseRollup = false;
        var rawTools = _db.TotalsByTool(0, 100000);
        var rawModels = _db.ModelAggregates(0, 100000);
        var rawDays = _db.DailyAggregates(0, 100000);
        var rawBreakdown = _db.CostBreakdown(0, 100000);
        Assert.Equal(rawTools, rollupTools);
        Assert.Equal(rawModels, rollupModels);
        Assert.Equal(rawDays, rollupDays);
        Assert.Equal(rawBreakdown.Estimated, rollupBreakdown.Estimated, precision: 6);
        Assert.Equal(rawBreakdown.Actual, rollupBreakdown.Actual, precision: 6);
        Assert.Equal(rawBreakdown.DeepSeekActual, rollupBreakdown.DeepSeekActual, precision: 6);
        // Partial-edge range (no whole bucket) must also match.
        _db.AggregatesUseRollup = true;
        var edgeRollup = _db.TotalsByTool(1000, 1700);
        _db.AggregatesUseRollup = false;
        Assert.Equal(_db.TotalsByTool(1000, 1700), edgeRollup);
    }

    [Fact]
    public void Rollup_StaysConsistent_AfterResetLocalUsage()
    {
        SeedMixedTurns();
        Assert.True(_db.ResetLocalUsage(new[] { ("claude", (IReadOnlyList<string>)Array.Empty<string>()) }));
        Assert.Equal(0, _db.RollupMismatchCount());
        Assert.Empty(_db.TotalsByTool(0, 100000).Where(r => r.Tool == "claude"));
        Assert.Single(_db.TotalsByTool(0, 100000).Where(r => r.Tool == "codex"));
    }

    // MARK: - Performance

    [Fact]
    public void PerfSample_Merge_FirstWins_LastGrows()
    {
        var a = new PerfSample(ToolKind.Claude, "k", "m", 1000, 1100, 2000, 60);
        var b = new PerfSample(ToolKind.Claude, "k", null, 999, 1050, 3000, 40);
        var merged = a.Merge(b);
        Assert.Equal(1000, merged.RequestMs);
        Assert.Equal(1100, merged.FirstMs);
        Assert.Equal(3000, merged.LastMs);
        Assert.Equal(60, merged.OutputTokens);
        var c = new PerfSample(ToolKind.Claude, "k", null, null, null, null, 0);
        var merged2 = c.Merge(b);
        Assert.Equal(999, merged2.RequestMs);
        Assert.Equal(1050, merged2.FirstMs);
    }

    [Fact]
    public void PerfStats_Median_EvenAndOdd()
    {
        Assert.Equal(2.0, PerfStats.Median(new[] { 1.0, 2.0, 3.0 }));
        Assert.Equal(2.5, PerfStats.Median(new[] { 1.0, 2.0, 3.0, 4.0 }));
        Assert.Null(PerfStats.Median(Array.Empty<double>()));
    }

    [Fact]
    public void PerfChartPoints_Filters_AndComputes()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var dayStart = new DateTimeOffset(DateTimeOffset.UtcNow.Date, TimeSpan.Zero).ToUnixTimeMilliseconds();
        _db.UpsertPerfSamples(new[]
        {
            // TTFT 500, TPS 100*1000/1000 = 100.
            new PerfSample(ToolKind.Claude, "a", "m1", now - 5000, now - 4500, now - 3500, 100),
            // Too fast to count for TPS (100ms < 250) but TTFT counts.
            new PerfSample(ToolKind.Claude, "b", "m1", now - 5000, now - 4500, now - 4400, 100),
            // Synthetic model excluded.
            new PerfSample(ToolKind.Claude, "c", "<synthetic>", now - 5000, now - 4500, now - 3500, 100),
            // TTFT too long (>= 300000) and TPS too small (10 < 50): dropped entirely.
            new PerfSample(ToolKind.Claude, "d", "m1", now - 500000, now - 1000, now - 500, 10),
        });
        var points = _db.PerfChartPoints(2);
        Assert.Equal(2, points.Count);
        var a = points.First(p => p.Model == "m1" && p.TtftMs == 500 && p.TokensPerSecond == 100);
        Assert.NotNull(a);
        Assert.Contains(points, p => p.TtftMs == 500 && p.TokensPerSecond is null);
        Assert.All(points, p => Assert.True(p.Day >= 20000000));
        _ = dayStart;
    }

    // MARK: - Estimator

    private static ClaudeNonLocalEstimator.Sample S(long ts, int fivePct, long fiveReset) =>
        new(ts, 10, 100000, fivePct, fiveReset);

    [Fact]
    public void Estimator_IdleRise_UsesCalibrationRate()
    {
        // 5 intervals: 4 calibration (2 pts each, 10000 tokens each) + 1 idle rise of 4 pts.
        var samples = new[]
        {
            S(0, 10, 100000), S(3600, 12, 100000), S(7200, 14, 100000),
            S(10800, 16, 100000), S(14400, 18, 100000), S(18000, 22, 100000),
        };
        var totals = new[]
        {
            new ClaudeNonLocalEstimator.LocalIntervalTotals(10000, 10000),
            new ClaudeNonLocalEstimator.LocalIntervalTotals(10000, 10000),
            new ClaudeNonLocalEstimator.LocalIntervalTotals(10000, 10000),
            new ClaudeNonLocalEstimator.LocalIntervalTotals(10000, 10000),
            new ClaudeNonLocalEstimator.LocalIntervalTotals(0, 0),
        };
        var est = ClaudeNonLocalEstimator.TokenEstimateFor(samples, totals, now: 100000);
        Assert.NotNull(est);
        Assert.Equal(5000.0, est.TokensPerPoint);
        Assert.Single(est.Events);
        Assert.Equal(16200, est.Events[0].Ts); // midpoint of [14400, 18000)
        Assert.Equal(20000, est.Events[0].Tokens);
        Assert.Equal(20000, est.Tokens(14400, 18000));
        Assert.Equal(0, est.Tokens(0, 14400));
    }

    [Fact]
    public void Estimator_TooFewCalibrationPoints_ReturnsNull()
    {
        var samples = new[] { S(0, 10, 100000), S(3600, 11, 100000), S(7200, 15, 100000) };
        var totals = new[]
        {
            new ClaudeNonLocalEstimator.LocalIntervalTotals(10000, 10000),
            new ClaudeNonLocalEstimator.LocalIntervalTotals(0, 0),
        };
        Assert.Null(ClaudeNonLocalEstimator.TokenEstimateFor(samples, totals, now: 100000));
    }

    [Fact]
    public void Estimator_WeeklyFallback_WhenFiveHourMissing()
    {
        var samples = new[]
        {
            new ClaudeNonLocalEstimator.Sample(0, 10, 100000, null, null),
            new ClaudeNonLocalEstimator.Sample(3600, 12, 100000, null, null),
            new ClaudeNonLocalEstimator.Sample(7200, 14, 100000, null, null),
            new ClaudeNonLocalEstimator.Sample(10800, 16, 100000, null, null),
            new ClaudeNonLocalEstimator.Sample(14400, 18, 100000, null, null),
            new ClaudeNonLocalEstimator.Sample(18000, 22, 100000, null, null),
        };
        var totals = new[]
        {
            new ClaudeNonLocalEstimator.LocalIntervalTotals(10000, 10000),
            new ClaudeNonLocalEstimator.LocalIntervalTotals(10000, 10000),
            new ClaudeNonLocalEstimator.LocalIntervalTotals(10000, 10000),
            new ClaudeNonLocalEstimator.LocalIntervalTotals(10000, 10000),
            new ClaudeNonLocalEstimator.LocalIntervalTotals(0, 0),
        };
        var est = ClaudeNonLocalEstimator.TokenEstimateFor(samples, totals, now: 100000);
        Assert.NotNull(est);
        Assert.Single(est.Events);
    }

    // MARK: - DebugLog

    [Fact]
    public void DebugLog_Gate_ReadsEnv()
    {
        var prev = Environment.GetEnvironmentVariable("TM_DEBUG");
        try
        {
            Environment.SetEnvironmentVariable("TM_DEBUG", "1");
            Assert.True(DebugLog.Enabled);
            Environment.SetEnvironmentVariable("TM_DEBUG", "0");
            Assert.False(DebugLog.Enabled);
            Environment.SetEnvironmentVariable("TM_DEBUG", null);
            Assert.False(DebugLog.Enabled);
        }
        finally { Environment.SetEnvironmentVariable("TM_DEBUG", prev); }
    }

    // MARK: - Maintenance

    [Fact]
    public void Maintenance_Backup_Restore_Clear_Export()
    {
        SeedMixedTurns();
        _db.SetSetting("openrouter_keys", "sk-test-value");
        _db.SetSetting("keep_me", "yes");

        var backup = DataMaintenance.CreateBackup("manual", _db, _backupDir);
        Assert.NotNull(backup);
        Assert.Matches(@"^toastmonitor-manual-\d{8}-\d{6}-[a-f0-9]{8}\.db$", Path.GetFileName(backup));
        Assert.True(DataMaintenance.IsSQLiteFile(backup));
        Assert.Contains(backup, DataMaintenance.AvailableBackups(_backupDir));

        // More turns after the backup, then restore -> back to backup state.
        _db.InsertTurns(new[] { Turn(ToolKind.Claude, 5000, "x", 1, 1, 0, 0.1) });
        Assert.True(DataMaintenance.Restore(backup, _db, _backupDir));
        Assert.Equal(6, _db.TotalsByTool(0, 100000).Sum(r => r.Count));

        // Non-managed path and non-SQLite managed file are refused; data intact.
        var outside = Path.Combine(Path.GetTempPath(), "tm-outside-" + Guid.NewGuid().ToString("N") + ".db");
        File.Copy(backup, outside);
        try
        {
            Assert.False(DataMaintenance.Restore(outside, _db, _backupDir));
            var fake = Path.Combine(_backupDir, "toastmonitor-manual-20200101-000000-deadbeef.db");
            File.WriteAllText(fake, "not sqlite at all, but non-empty");
            Assert.False(DataMaintenance.Restore(fake, _db, _backupDir));
            Assert.Equal(6, _db.TotalsByTool(0, 100000).Sum(r => r.Count));
        }
        finally { try { File.Delete(outside); } catch { /* best effort */ } }

        // Export redacts credential-bearing keys; live DB keeps them.
        var exportPath = Path.Combine(Path.GetTempPath(), "tm-export-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            Assert.True(DataMaintenance.ExportDatabase(exportPath, _db));
            using var exported = new ToastDatabase(exportPath);
            exported.Open();
            Assert.Null(exported.GetSetting("openrouter_keys"));
            Assert.Equal("yes", exported.GetSetting("keep_me"));
            Assert.Equal("sk-test-value", _db.GetSetting("openrouter_keys"));
            Assert.False(DataMaintenance.ExportDatabase(_path, _db)); // destination == live path refused
        }
        finally { try { File.Delete(exportPath); } catch { /* best effort */ } }

        // Clear keeps settings, drops usage, returns a backup.
        var preClear = DataMaintenance.ClearAllData(_db, _backupDir);
        Assert.True(File.Exists(preClear));
        Assert.Equal("yes", _db.GetSetting("keep_me"));
        Assert.Empty(_db.TotalsByTool(0, 100000));
        Assert.Equal(0, _db.RollupMismatchCount());
    }

    [Fact]
    public void Maintenance_Prune_ReservesRoutineSlot()
    {
        SeedMixedTurns();
        var stamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        string? weekly = null;
        for (var i = 0; i < 7; i++)
            Assert.NotNull(DataMaintenance.CreateBackup("manual", _db, _backupDir, stamp));
        weekly = DataMaintenance.CreateBackup("weekly", _db, _backupDir, stamp);
        Assert.NotNull(weekly);
        var kept = DataMaintenance.AvailableBackups(_backupDir);
        Assert.Equal(7, kept.Count);
        Assert.Contains(weekly, kept);
        // One more manual must not evict the routine snapshot.
        Assert.NotNull(DataMaintenance.CreateBackup("manual", _db, _backupDir, stamp));
        kept = DataMaintenance.AvailableBackups(_backupDir);
        Assert.Equal(7, kept.Count);
        Assert.Contains(weekly, kept);
    }

    [Fact]
    public void Maintenance_Repair_RemovesLocalClaudeUsage()
    {
        SeedMixedTurns();
        _db.UpsertSessions(new[] { new SessionInfo(ToolKind.Claude, "s1", null, null, null, 0, 0) });
        _db.SetScanState("claude-src-1", 10, 20, 30, null);
        var receipt = DataMaintenance.Repair(_db,
            new[] { ("claude", (IReadOnlyList<string>)new[] { "claude-src" }) }, _backupDir);
        Assert.True(File.Exists(receipt.BackupPath));
        Assert.Equal(4, receipt.RemovedTurns);
        Assert.Equal(1, receipt.RemovedSessions);
        Assert.Empty(_db.TotalsByTool(0, 100000).Where(r => r.Tool == "claude"));
        Assert.Equal(new ScanState(0, 0, 0, null), _db.GetScanState("claude-src-1"));
        Assert.Equal(0, _db.RollupMismatchCount());
    }

    [Fact]
    public void Maintenance_RoutineBackup_ThrottledToSevenDays()
    {
        SeedMixedTurns();
        DataMaintenance.MaybeCreateRoutineBackupIfDue(_db, _backupDir);
        Assert.Single(DataMaintenance.AvailableBackups(_backupDir));
        DataMaintenance.MaybeCreateRoutineBackupIfDue(_db, _backupDir);
        Assert.Single(DataMaintenance.AvailableBackups(_backupDir));
        _db.SetSetting("routine_backup_last",
            (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 8 * 86400).ToString(System.Globalization.CultureInfo.InvariantCulture));
        DataMaintenance.MaybeCreateRoutineBackupIfDue(_db, _backupDir);
        Assert.Equal(2, DataMaintenance.AvailableBackups(_backupDir).Count);
    }
}
