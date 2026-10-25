using ToastMonitor.Collect;
using ToastMonitor.Core;
using Xunit;

namespace ToastMonitor.Collect.Tests;

public sealed class EngineTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "tm-engine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static ToastDatabase TempDb(string dir)
    {
        var path = Path.Combine(dir, "test.db");
        return new ToastDatabase(path);
    }

    [Fact]
    public void IncrementalCursor_SecondScan_NoDoubleCount()
    {
        var dir = TempDir();
        using var db = TempDb(dir);
        var file = Path.Combine(dir, "c.jsonl");
        File.WriteAllText(file,
            "{\"type\":\"assistant\",\"sessionId\":\"s1\",\"timestamp\":\"2026-08-04T10:00:02.000Z\",\"message\":{\"id\":\"m1\",\"model\":\"claude-sonnet-4-6\",\"usage\":{\"input_tokens\":100,\"output_tokens\":10}}}\n");
        var staged1 = new StagedParserStateStore(db, new[] { file });
        var (turns1, sessions1) = ClaudeCodeParser.Scan(new[] { file }, staged1);
        Assert.Single(turns1);
        Assert.True(staged1.ValidateForCommit());
        var payload1 = staged1.CommitPayload();
        Assert.True(db.ApplyScan(turns1, sessions1, payload1.Scan, payload1.Totals, payload1.Settings));
        Assert.Equal(1, db.TurnCount());

        // Append one more turn: only the new row is emitted, DB count grows by 1.
        File.AppendAllText(file,
            "{\"type\":\"assistant\",\"sessionId\":\"s1\",\"timestamp\":\"2026-08-04T10:00:05.000Z\",\"message\":{\"id\":\"m2\",\"model\":\"claude-sonnet-4-6\",\"usage\":{\"input_tokens\":200,\"output_tokens\":20}}}\n");
        var staged2 = new StagedParserStateStore(db, new[] { file });
        var (turns2, sessions2) = ClaudeCodeParser.Scan(new[] { file }, staged2);
        Assert.Single(turns2);
        Assert.Equal(200, turns2[0].InputTokens);
        Assert.Equal(20, turns2[0].OutputTokens);
        Assert.True(staged2.ValidateForCommit());
        var payload2 = staged2.CommitPayload();
        Assert.True(db.ApplyScan(turns2, sessions2, payload2.Scan, payload2.Totals, payload2.Settings));
        Assert.Equal(2, db.TurnCount());

        // Third scan with no change: nothing emitted.
        var staged3 = new StagedParserStateStore(db, new[] { file });
        var (turns3, _) = ClaudeCodeParser.Scan(new[] { file }, staged3);
        Assert.Empty(turns3);
    }

    [Fact]
    public void FileChangeRewrite_FullRescan_DedupedByEventId()
    {
        var dir = TempDir();
        using var db = TempDb(dir);
        var file = Path.Combine(dir, "c.jsonl");
        var line1 = "{\"type\":\"assistant\",\"sessionId\":\"s1\",\"timestamp\":\"2026-08-04T10:00:02.000Z\",\"message\":{\"id\":\"m1\",\"model\":\"claude-sonnet-4-6\",\"usage\":{\"input_tokens\":100,\"output_tokens\":10}}}\n";
        var line2 = "{\"type\":\"assistant\",\"sessionId\":\"s1\",\"timestamp\":\"2026-08-04T10:00:05.000Z\",\"message\":{\"id\":\"m2\",\"model\":\"claude-sonnet-4-6\",\"usage\":{\"input_tokens\":200,\"output_tokens\":20}}}\n";
        File.WriteAllText(file, line1 + line2);
        var staged1 = new StagedParserStateStore(db, new[] { file });
        var (turns1, sessions1) = ClaudeCodeParser.Scan(new[] { file }, staged1);
        var payload1 = staged1.CommitPayload();
        Assert.True(db.ApplyScan(turns1, sessions1, payload1.Scan, payload1.Totals, payload1.Settings));
        Assert.Equal(2, db.TurnCount());

        // Rewrite the file in place with only line1 (shrink): parser rescans from 0,
        // re-emits line1, and the DB MAX-upsert keeps the count at 2 (no duplicate).
        File.WriteAllText(file, line1);
        var staged2 = new StagedParserStateStore(db, new[] { file });
        var (turns2, sessions2) = ClaudeCodeParser.Scan(new[] { file }, staged2);
        Assert.Single(turns2);
        var payload2 = staged2.CommitPayload();
        Assert.True(db.ApplyScan(turns2, sessions2, payload2.Scan, payload2.Totals, payload2.Settings));
        Assert.Equal(2, db.TurnCount());
    }

    [Fact]
    public void Backoff_GrowsThenResets()
    {
        var backoff = new RefreshBackoff();
        Assert.True(backoff.CanAttempt());
        backoff.RecordFailure();
        Assert.False(backoff.CanAttempt());
        Assert.Equal(1, backoff.FailureCount);
        backoff.RecordFailure();
        backoff.RecordFailure();
        Assert.Equal(3, backoff.FailureCount);
        backoff.RecordSuccess();
        Assert.True(backoff.CanAttempt());
        Assert.Equal(0, backoff.FailureCount);
    }

    [Fact]
    public void Poller_ForegroundBackgroundIntervals_AndBecameVisible()
    {
        var poller = new ForegroundPoller();
        Assert.Equal(TimeSpan.FromSeconds(5), poller.CurrentInterval);
        var fired = false;
        poller.BecameVisible += () => fired = true;
        poller.SetVisible(true);
        Assert.True(fired);
        Assert.Equal(TimeSpan.FromSeconds(1), poller.CurrentInterval);
        poller.SetVisible(false);
        Assert.Equal(TimeSpan.FromSeconds(5), poller.CurrentInterval);
    }

    [Fact]
    public void QuotaAlert_ThresholdTransitions()
    {
        var mgr = new QuotaAlertManager { Enabled = true, Threshold = 20 };
        Assert.Null(mgr.Check("claude-5h", 50, 1786700000));
        Assert.Equal(QuotaAlertPolicy.Transition.Low, mgr.Check("claude-5h", 15, 1786700000));
        Assert.True(mgr.IsUnread("claude-5h"));
        Assert.Null(mgr.Check("claude-5h", 10, 1786700000)); // still low, no repeat
        Assert.Equal(QuotaAlertPolicy.Transition.Recovered, mgr.Check("claude-5h", 60, 1786700000));
        Assert.False(mgr.IsUnread("claude-5h"));
        // New reset window far in the future clears the badge state.
        var state = new QuotaAlertPolicy.BadgeState(1786700000, true, true);
        var next = QuotaAlertPolicy.Update(state, 10, 20, 1786700000 + 10000, viewed: false);
        Assert.True(next.Triggered); // re-triggers in the new window (low again)
        Assert.True(next.Unread);
        Assert.Equal(1786710000, next.ResetAt);
    }

    [Fact]
    public void ClaudeCLIRefresher_TriggerLogicOnly()
    {
        Assert.True(ClaudeCLIRefresher.ShouldRefresh(true, true, false, TimeSpan.FromMinutes(6)));
        Assert.False(ClaudeCLIRefresher.ShouldRefresh(false, true, false, TimeSpan.FromMinutes(6)));
        Assert.False(ClaudeCLIRefresher.ShouldRefresh(true, false, false, TimeSpan.FromMinutes(6)));
        Assert.False(ClaudeCLIRefresher.ShouldRefresh(true, true, true, TimeSpan.FromMinutes(6)));
        Assert.False(ClaudeCLIRefresher.ShouldRefresh(true, true, false, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void PerfCollector_ClaudeLines_MergeByRequestKey()
    {
        var dir = TempDir();
        var file = Path.Combine(dir, "perf.jsonl");
        File.WriteAllText(file,
            "{\"type\":\"api-request\",\"id\":\"r1\",\"timestamp\":\"2026-08-04T10:00:00.000Z\",\"durationMs\":1200}\n" +
            "{\"type\":\"assistant\",\"timestamp\":\"2026-08-04T10:00:01.000Z\",\"requestRef\":\"r1\",\"apiBlockIndex\":0,\"thinkingDurationMs\":200,\"message\":{\"id\":\"mm1\",\"usage\":{\"output_tokens\":42}}}\n");
        var samples = PerfCollector.ParseClaudePerf(file, 0, out var cursor);
        Assert.Single(samples);
        Assert.Equal(1200, samples[0].RequestMs);
        Assert.Equal(42, samples[0].OutputTokens);
        Assert.Equal(samples[0].LastMs - 200, samples[0].FirstMs);
        Assert.True(cursor > 0);
    }

    [Fact]
    public void SourceRoots_MappingTable()
    {
        Assert.EndsWith(".claude" + Path.DirectorySeparatorChar + "projects", SourceRoots.ClaudeProjects);
        Assert.EndsWith(".codex" + Path.DirectorySeparatorChar + "sessions", SourceRoots.CodexSessions);
        Assert.EndsWith(".omp" + Path.DirectorySeparatorChar + "agent" + Path.DirectorySeparatorChar + "sessions", SourceRoots.OmpSessions);
        Assert.EndsWith(".dsh" + Path.DirectorySeparatorChar + "sessions", SourceRoots.DshSessions);
        Assert.EndsWith(".grok" + Path.DirectorySeparatorChar + "auth.json", SourceRoots.GrokAuthFile);
        Assert.Contains("Claude", SourceRoots.ClaudeCoworkRoot);
    }
}
