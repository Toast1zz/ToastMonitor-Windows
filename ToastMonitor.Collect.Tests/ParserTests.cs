using System.Text.Json;
using Microsoft.Data.Sqlite;
using ToastMonitor.Collect;
using ToastMonitor.Core;
using Xunit;

namespace ToastMonitor.Collect.Tests;

public sealed class ParserTests
{
    private static string Fixture(string name)
    {
        var here = AppContext.BaseDirectory;
        var p = Path.Combine(here, "Fixtures", name);
        if (File.Exists(p)) return p;
        // Fallback: source-tree layout when run from a different working dir.
        var dir = new DirectoryInfo(here);
        while (dir is not null)
        {
            var cand = Path.Combine(dir.FullName, "ToastMonitor.Collect.Tests", "Fixtures", name);
            if (File.Exists(cand)) return cand;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("fixture not found: " + name);
    }

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "tm-collect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Claude_Fixture_TwoTurns_TokensMatch()
    {
        var dir = TempDir();
        var projDir = Path.Combine(dir, "-Users-demo-dev-sample-project");
        Directory.CreateDirectory(projDir);
        var file = Path.Combine(projDir, "e1f5c1a2-0000-4000-8000-000000000001.jsonl");
        File.Copy(Fixture("claude-project-sample.jsonl"), file);
        var store = new InMemoryParserStateStore();
        var (turns, sessions) = ClaudeCodeParser.Scan(new[] { file }, store);
        Assert.Equal(2, turns.Count);
        Assert.Equal(812, turns[0].InputTokens);
        Assert.Equal(96, turns[0].OutputTokens);
        Assert.Equal(901, turns[1].InputTokens);
        Assert.Equal(312, turns[1].OutputTokens);
        Assert.Equal(120, turns[1].CacheRead);
        Assert.Equal("claude-sonnet-4-6", turns[0].Model);
        Assert.Single(sessions);
        Assert.Equal("e1f5c1a2-0000-4000-8000-000000000001", sessions[0].SessionId);
    }

    [Fact]
    public void Claude_StreamingSameMessageId_CollapsesToMax()
    {
        var dir = TempDir();
        var file = Path.Combine(dir, "s.jsonl");
        File.WriteAllText(file,
            "{\"type\":\"assistant\",\"sessionId\":\"s1\",\"timestamp\":\"2026-08-04T10:00:02.000Z\",\"message\":{\"id\":\"m1\",\"model\":\"claude-sonnet-4-6\",\"usage\":{\"input_tokens\":100,\"output_tokens\":10}}}\n" +
            "{\"type\":\"assistant\",\"sessionId\":\"s1\",\"timestamp\":\"2026-08-04T10:00:05.000Z\",\"message\":{\"id\":\"m1\",\"model\":\"claude-sonnet-4-6\",\"usage\":{\"input_tokens\":100,\"output_tokens\":50}}}\n");
        var store = new InMemoryParserStateStore();
        var (turns, _) = ClaudeCodeParser.Scan(new[] { file }, store);
        Assert.Single(turns);
        Assert.Equal(50, turns[0].OutputTokens);
    }

    [Fact]
    public void Codex_Fixture_TwoTurns_TotalGrowthOnly()
    {
        var sessionsRoot = Path.Combine(TempDir(), "sessions");
        var root = Path.Combine(sessionsRoot, "2026", "08", "04");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "rollout-2026-08-04T09-22-33-019fcb70-2990-7ae3-897a-826135c6b1c5.jsonl");
        File.Copy(Fixture("codex-rollout-sample.jsonl"), file);
        var store = new InMemoryParserStateStore();
        var (turns, sessions) = CodexParser.Scan(store, sessionsRoot, Path.Combine(TempDir(), "no-such.sqlite"));
        Assert.Equal(2, turns.Count);
        Assert.Equal(4120, turns[0].InputTokens);
        Assert.Equal(318, turns[0].OutputTokens);
        Assert.Equal(2048, turns[0].CacheRead);
        Assert.Equal(3500, turns[1].InputTokens);
        Assert.Equal(421, turns[1].OutputTokens);
        Assert.Equal("gpt-5.6-sol", turns[0].Model);
        Assert.Single(sessions);
        Assert.Equal("019fcb70-2990-7ae3-897a-826135c6b1c5", sessions[0].SessionId);

        // Audit S3: re-sending the same total (rate-limit repeat) must not emit.
        var lastLine = File.ReadAllLines(file).Last();
        File.AppendAllText(file, lastLine + "\n");
        var (turns2, _) = CodexParser.Scan(store, sessionsRoot, Path.Combine(TempDir(), "no-such.sqlite"));
        Assert.Empty(turns2);
    }

    [Fact]
    public void OpenCode_JsonModelObject_Normalized_AndDelta()
    {
        var dir = TempDir();
        var dbPath = Path.Combine(dir, "opencode.db");
        using (var conn = new SqliteConnection("Data Source=" + dbPath))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"CREATE TABLE session (id TEXT, title TEXT, directory TEXT, model TEXT, cost REAL,
                tokens_input INTEGER, tokens_output INTEGER, tokens_reasoning INTEGER,
                tokens_cache_read INTEGER, tokens_cache_write INTEGER, time_created INTEGER, time_updated INTEGER);
                INSERT INTO session VALUES ('sess-1','t','/Users/demo/proj','{""id"":""gpt-5"",""providerID"":""openai""}',0.05,
                1000,200,50,300,20,1786628702000,1786628703000);";
            cmd.ExecuteNonQuery();
        }
        var store = new InMemoryParserStateStore();
        var (turns, sessions) = OpenCodeParser.Scan(store, dbPath);
        Assert.Single(turns);
        Assert.Equal("gpt-5", turns[0].Model);
        Assert.Equal("openai", turns[0].Provider);
        Assert.Equal("actual", turns[0].CostQuality);
        Assert.Equal(1000, turns[0].InputTokens);
        Assert.Single(sessions);
        // Second scan: no growth, no turn.
        var (turns2, _) = OpenCodeParser.Scan(store, dbPath);
        Assert.Empty(turns2);
        // Growth: delta only.
        using (var conn = new SqliteConnection("Data Source=" + dbPath))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE session SET tokens_output=250, time_updated=1786628704000 WHERE id='sess-1';";
            cmd.ExecuteNonQuery();
        }
        var (turns3, _) = OpenCodeParser.Scan(store, dbPath);
        Assert.Single(turns3);
        Assert.Equal(50, turns3[0].OutputTokens);
        Assert.Equal(0, turns3[0].InputTokens);
    }

    [Fact]
    public void Hermes_UsageBaseline_FirstThenDelta_AndRemoteSharesBaseline()
    {
        var dir = TempDir();
        var dbPath = Path.Combine(dir, "state.db");
        using (var conn = new SqliteConnection("Data Source=" + dbPath))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"CREATE TABLE session_model_usage (session_id TEXT, model TEXT, billing_provider TEXT,
                billing_base_url TEXT, input_tokens INTEGER, output_tokens INTEGER, reasoning_tokens INTEGER,
                cache_read_tokens INTEGER, cache_write_tokens INTEGER, first_seen REAL, last_seen REAL);
                INSERT INTO session_model_usage VALUES ('h1','m','p','https://x/',500,100,10,200,5,1786628702,1786628702);";
            cmd.ExecuteNonQuery();
        }
        var store = new InMemoryParserStateStore();
        var (turns, _) = HermesParser.Scan(store, new[] { dbPath });
        Assert.Single(turns);
        Assert.Equal(500, turns[0].InputTokens);
        Assert.Equal(100, turns[0].OutputTokens);
        Assert.Equal(200, turns[0].CacheRead);
        var (turnsAgain, _) = HermesParser.Scan(store, new[] { dbPath });
        Assert.Empty(turnsAgain);
        var current = new HermesUsageBaseline.Counters(500, 100, 10, 200, 5);
        var step = HermesUsageBaseline.Advance(current, HermesUsageBaseline.Merge(current, new[] { (long[]?)null }));
        Assert.True(step.IsFirst);
        Assert.Equal(500, step.Delta.Input);
        // Remote row with the same counters, after the local baseline is recorded,
        // must produce no delta (counted once).
        store.SetSetting(HermesUsageBaseline.Key("h1", "m", "p", "https://x/"), HermesUsageBaseline.Encode(step.Base));
        store.SetSessionTotals(HermesUsageBaseline.LocalTotalsKey(HermesUsageBaseline.Key("h1", "m", "p", "https://x/")),
            "hermes", 500, 100, 10, 200, 5, 0, 1786628702);
        var remote = HermesRemoteClient.ApplyRow(store, ("h1", "m", "p", "https://x/", current, 1786628702));
        Assert.Null(remote);
    }

    [Fact]
    public void Omp_Fixture_TwoTurns_ProviderCost()
    {
        var dir = TempDir();
        var file = Path.Combine(dir, "omp.jsonl");
        File.Copy(Fixture("omp-sample.jsonl"), file);
        var store = new InMemoryParserStateStore();
        var (turns, sessions) = OmpParser.Scan(new[] { file }, store);
        Assert.Equal(2, turns.Count);
        Assert.Equal(18585, turns[0].InputTokens);
        Assert.Equal(241, turns[0].OutputTokens);
        Assert.Equal(0.0027, turns[0].Cost, 6);
        Assert.Equal(18816, turns[1].CacheRead);
        Assert.Equal("019f0000-0000-7000-0000-000000000001", sessions[0].SessionId);
    }

    [Fact]
    public void Dsh_LogFixture_ThreeTurns_MessageReplacesChunkSample()
    {
        var dir = TempDir();
        var file = Path.Combine(dir, "session.jsonl");
        File.Copy(Fixture("dsh-sample.jsonl"), file);
        var store = new InMemoryParserStateStore();
        var (turns, sessions) = DshParser.ScanLogs(new[] { file }, store);
        Assert.Equal(3, turns.Count);
        Assert.Equal(8045, turns[0].InputTokens);
        Assert.Equal(399, turns[0].OutputTokens);
        Assert.Equal(895, turns[1].InputTokens);
        Assert.Equal(8320, turns[1].CacheRead);
        // Step 4: assistant/message usage (2600/420/9400) replaces the pending chunk sample.
        Assert.Equal(2600, turns[2].InputTokens);
        Assert.Equal(420, turns[2].OutputTokens);
        Assert.Equal(9400, turns[2].CacheRead);
        Assert.Equal("deepseek-v4-flash", turns[0].Model);
        Assert.Single(sessions);
    }

    [Fact]
    public void Dsh_CacheFixture_OneTurn_TwoSessions()
    {
        var dir = TempDir();
        var file = Path.Combine(dir, "session_projcache.json");
        File.Copy(Fixture("dsh-projcache.json"), file);
        var store = new InMemoryParserStateStore();
        var (turns, sessions) = DshParser.ScanProjCache(store, file);
        Assert.Single(turns);
        Assert.Equal(1000, turns[0].InputTokens);
        Assert.Equal(500, turns[0].OutputTokens);
        Assert.Equal(8000, turns[0].CacheRead);
        Assert.Equal(10, turns[0].CacheWrite);
        Assert.Equal(2, sessions.Count);
        // Second scan: baselines hold, no new turn.
        var (turns2, _) = DshParser.ScanProjCache(store, file);
        Assert.Empty(turns2);
    }

    [Fact]
    public void CommandCode_PlanCatalog_KnownAndUnknown()
    {
        Assert.Equal(30, CommandCodePlanCatalog.Allowance("individual-pro"));
        Assert.Null(CommandCodePlanCatalog.Allowance("individual-mystery"));
        Assert.Equal("PRO", CommandCodePlanCatalog.ShortName("individual-pro"));
    }

    [Fact]
    public void Zstd_RoundTrip_WhenAvailable()
    {
        if (!Zstd.IsAvailable) return; // missing package degrades gracefully, like Mac without the CLI
        var data = System.Text.Encoding.UTF8.GetBytes("hello zstd world, hello zstd world");
        byte[] compressed;
        using (var c = new ZstdSharp.Compressor())
            compressed = c.Wrap(data).ToArray();
        var back = Zstd.Decompress(compressed);
        Assert.NotNull(back);
        Assert.Equal(data, back);
    }

    [Fact]
    public void Dsh_ZstdFile_ScansLikePlainJsonl()
    {
        if (!Zstd.IsAvailable) return;
        var dir = TempDir();
        var file = Path.Combine(dir, "session.jsonl.zstd");
        var plain = File.ReadAllBytes(Fixture("dsh-sample.jsonl"));
        byte[] compressed;
        using (var c = new ZstdSharp.Compressor())
            compressed = c.Wrap(plain).ToArray();
        File.WriteAllBytes(file, compressed);
        var store = new InMemoryParserStateStore();
        var (turns, _) = DshParser.ScanLogs(new[] { file }, store);
        Assert.Equal(3, turns.Count);
        Assert.Equal(2600, turns[2].InputTokens);
    }

    [Fact]
    public void QuotaClients_ParseSamples()
    {
        var claude = ClaudeQuotaClient.Parse("{\"five_hour\":{\"utilization\":42.5,\"resets_at\":1786630000},\"seven_day\":{\"utilization\":10,\"resets_at\":1786700000}}");
        Assert.Equal(2, claude.Windows.Count);
        Assert.Equal("5h", claude.Windows[0].Name);
        Assert.Equal(42.5, claude.Windows[0].PercentUsed);
        var codex = CodexQuotaClient.Parse("{\"plan_type\":\"plus\",\"rate_limit\":{\"primary_window\":{\"used_percent\":30,\"limit_window_seconds\":604800,\"reset_at\":1786700000}}}");
        Assert.Equal("Plus", codex.Plan);
        Assert.Single(codex.Windows);
        var grok = GrokQuotaClient.ParseBilling("{\"prepaid_balance\":{\"amount\":12.5},\"products\":[{\"name\":\"SuperGrok\"}]}");
        Assert.Equal(12.5, grok.Balance);
        var or = OpenRouterClient.ParseUsage("{\"data\":{\"usage\":1.5,\"usage_daily\":0.4,\"usage_weekly\":2.0,\"usage_monthly\":5.0,\"limit\":10,\"limit_remaining\":8.5,\"is_free_tier\":false}}");
        Assert.Equal(0.4, or.UsageDaily);
        Assert.Equal(10, or.Limit);
        var (used, pct) = CommandCodeQuotaClient.ComputeUsed(20, 30);
        Assert.Equal(10, used);
        Assert.Equal(100.0 / 3.0, pct, 6);
        Assert.True(HermesRemoteClient.IsAllowedFeedUrl("https://example.com/feed.json"));
        Assert.True(HermesRemoteClient.IsAllowedFeedUrl("http://100.64.0.1:8080/feed"));
        Assert.False(HermesRemoteClient.IsAllowedFeedUrl("http://8.8.8.8/feed"));
    }
}
