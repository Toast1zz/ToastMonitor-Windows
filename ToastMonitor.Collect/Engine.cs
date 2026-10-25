using Microsoft.Data.Sqlite;
using ToastMonitor.Core;

namespace ToastMonitor.Collect;

/// <summary>Quota polling backoff (Mac QuotaPolling.RefreshBackoff): after a
/// failure the next attempt waits 1, 2, 4, 8, then capped 15 minutes; success
/// resets. Measured on a monotonic clock (Environment.TickCount64).</summary>
public sealed class RefreshBackoff
{
    private int _failures;
    private long _nextAllowedTickMs;
    private static readonly long[] DelaysMs = { 1000, 2000, 4000, 8000, 15 * 60 * 1000 };

    public void RecordSuccess()
    {
        _failures = 0;
        _nextAllowedTickMs = 0;
    }

    public void RecordFailure()
    {
        _failures++;
        var delay = DelaysMs[Math.Min(_failures - 1, DelaysMs.Length - 1)];
        _nextAllowedTickMs = Environment.TickCount64 + delay;
    }

    public bool CanAttempt(long nowTickMs = 0) =>
        (nowTickMs == 0 ? Environment.TickCount64 : nowTickMs) >= _nextAllowedTickMs;

    public int FailureCount => _failures;
}

/// <summary>Foreground/background poller (Mac ForegroundPoller): foreground
/// cadence while the popover/dashboard is visible, background otherwise,
/// immediate refresh on becoming visible.</summary>
public sealed class ForegroundPoller
{
    public TimeSpan ForegroundInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan BackgroundInterval { get; set; } = TimeSpan.FromSeconds(5);
    public bool IsVisible { get; private set; }
    public event Action? BecameVisible;

    public TimeSpan CurrentInterval => IsVisible ? ForegroundInterval : BackgroundInterval;

    public void SetVisible(bool visible)
    {
        var was = IsVisible;
        IsVisible = visible;
        if (visible && !was) BecameVisible?.Invoke();
    }
}

public enum SourceHealthStatus { Ok, Stale, Error, Disabled }

public sealed record SourceHealth(string Source, SourceHealthStatus Status, string? Message, long LastSuccessSeconds);

/// <summary>Quota alert badge + transition logic (Mac QuotaAlertManager, trigger
/// logic only — notification delivery is the platform stage).</summary>
public static class QuotaAlertPolicy
{
    public sealed record BadgeState(long? ResetAt, bool Triggered, bool Unread);

    /// <summary>Mac BadgeState.update: a new reset &gt;300s after the old one
    /// clears triggered/unread and adopts it; otherwise adopt only if nil;
    /// remaining above threshold clears unread (and triggered if reset is nil);
    /// at/below threshold sets triggered+unread when not already triggered.</summary>
    public static BadgeState Update(BadgeState state, double remaining, double threshold, long? resetAt, bool viewed)
    {
        var triggered = state.Triggered;
        var unread = state.Unread;
        var reset = state.ResetAt;
        if (resetAt is not null && state.ResetAt is not null && resetAt.Value - state.ResetAt.Value > 300)
        {
            triggered = false; unread = false; reset = resetAt;
        }
        else if (reset is null) reset = resetAt;
        if (remaining > threshold)
        {
            unread = false;
            if (reset is null) triggered = false;
        }
        else if (!triggered)
        {
            triggered = true; unread = true;
        }
        if (viewed) unread = false;
        return new BadgeState(reset, triggered, unread);
    }

    public enum Transition { Low, Recovered }

    /// <summary>Low when remaining &lt;= threshold; emits Low on entering low,
    /// Recovered on leaving, else null.</summary>
    public static Transition? TransitionOf(bool wasLow, double remaining, double threshold)
    {
        var isLow = remaining <= threshold;
        if (isLow && !wasLow) return Transition.Low;
        if (!isLow && wasLow) return Transition.Recovered;
        return null;
    }
}

public sealed class QuotaAlertManager
{
    private readonly Dictionary<string, QuotaAlertPolicy.BadgeState> _badges = new();
    public bool Enabled { get; set; } = true;
    public double Threshold { get; set; } = 20;

    public QuotaAlertPolicy.Transition? Check(string key, double remaining, long? resetAt, bool viewed = false)
    {
        if (!Enabled) return null;
        var prev = _badges.TryGetValue(key, out var b) ? b : new QuotaAlertPolicy.BadgeState(null, false, false);
        var wasLow = prev.Triggered;
        var next = QuotaAlertPolicy.Update(prev, remaining, Threshold, resetAt, viewed);
        _badges[key] = next;
        return QuotaAlertPolicy.TransitionOf(wasLow, remaining, Threshold);
    }

    public bool IsUnread(string key) => _badges.TryGetValue(key, out var b) && b.Unread;
}

/// <summary>Claude CLI refresher trigger logic only (Mac ClaudeCLIRefresher):
/// decide whether the background `claude` CLI refresh should run. The CLI is
/// never executed by this layer and never in tests.</summary>
public static class ClaudeCLIRefresher
{
    public static bool ShouldRefresh(bool claudeQuotaEnabled, bool tokenExpired, bool alreadyRunning, TimeSpan sinceLastAttempt)
    {
        if (!claudeQuotaEnabled) return false;
        if (!tokenExpired) return false;
        if (alreadyRunning) return false;
        return sinceLastAttempt >= TimeSpan.FromMinutes(5);
    }
}

/// <summary>Perf collector (Mac PerfCollector): at most once a minute; Claude
/// api-request/assistant lines merged by request key; OpenCode step-start
/// messages with a first text/reasoning part. Writers go through the Core
/// perf-sample APIs (Database.Collect.cs).</summary>
public static class PerfCollector
{
    public const long MinIntervalSeconds = 60;

    public static List<PerfSample> ParseClaudePerf(string path, long fromOffset, out long newOffset)
    {
        var samples = new Dictionary<string, PerfSample>();
        var newCursor = FileScanner.ForEachNewJsonLine(path, fromOffset, (offset, obj) =>
        {
            var type = JsonUtil.GetString(obj, "type");
            var ts = JsonUtil.ParseIso(JsonUtil.GetString(obj, "timestamp")) ?? 0;
            var tsMs = ts * 1000;
            if (type == "api-request")
            {
                var id = JsonUtil.GetString(obj, "id");
                if (string.IsNullOrEmpty(id)) return;
                var requestMs = JsonUtil.GetLong(obj, "durationMs");
                if (requestMs <= 0)
                {
                    var dur = JsonUtil.Get(obj, "duration");
                    if (dur is not null) requestMs = JsonUtil.GetLong(dur.Value, "ms");
                }
                samples[id!] = (samples.TryGetValue(id!, out var s) ? s : new PerfSample(ToolKind.Claude, id!, null, null, null, null, 0))
                    with { RequestMs = requestMs };
            }
            else if (type == "assistant")
            {
                var msg = JsonUtil.Get(obj, "message");
                var key = JsonUtil.GetString(obj, "requestRef") ?? JsonUtil.GetString(obj, "requestId")
                    ?? (msg is not null ? JsonUtil.GetString(msg.Value, "id") : null);
                if (string.IsNullOrEmpty(key)) return;
                var outputTokens = msg is not null
                    ? JsonUtil.GetLong(JsonUtil.Get(msg.Value, "usage") ?? default, "output_tokens") : 0;
                var thinkingMs = JsonUtil.GetLong(obj, "thinkingDurationMs");
                var apiBlockIndex = JsonUtil.GetLong(obj, "apiBlockIndex", -1);
                long? firstTokenMs = null;
                if (apiBlockIndex == 0 && thinkingMs > 0) firstTokenMs = tsMs - thinkingMs;
                var existing = samples.TryGetValue(key!, out var s) ? s : new PerfSample(ToolKind.Claude, key!, null, null, null, null, 0);
                samples[key!] = existing with
                {
                    LastMs = tsMs,
                    OutputTokens = Math.Max(existing.OutputTokens, outputTokens),
                    FirstMs = firstTokenMs ?? existing.FirstMs,
                };
            }
        });
        newOffset = newCursor;
        return samples.Values.ToList();
    }

    public static List<PerfSample> ParseOpenCodePerf(string dbPath, long watermarkMs, out long newWatermarkMs)
    {
        var samples = new List<PerfSample>();
        newWatermarkMs = watermarkMs;
        if (!File.Exists(dbPath)) return samples;
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT m.id, m.time_created, m.time_completed,
                       MIN(p.time_created) AS first_part, MAX(p.time_created) AS last_part
                FROM message m JOIN part p ON p.message_id = m.id
                WHERE m.time_updated > @wm AND m.role = 'assistant'
                GROUP BY m.id;";
            cmd.Parameters.AddWithValue("@wm", watermarkMs);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetString(0);
                var created = r.GetInt64(1);
                var completed = r.IsDBNull(2) ? 0 : r.GetInt64(2);
                var first = r.IsDBNull(3) ? 0 : r.GetInt64(3);
                var last = r.IsDBNull(4) ? 0 : r.GetInt64(4);
                var lastMs = Math.Max(completed, last);
                samples.Add(new PerfSample(ToolKind.Opencode, id, null, null, first > 0 ? first : null, lastMs, 0));
                newWatermarkMs = Math.Max(newWatermarkMs, created);
            }
        }
        catch { /* missing tables: no samples */ }
        return samples;
    }
}

public sealed record ScanReceipt(string RequestId, long StartedAt, long FinishedAt, int Turns, int Sessions, List<string> FailedSources)
{
    public bool Changed => Turns > 0 || Sessions > 0;
}

/// <summary>Collector engine (Mac CollectorEngine): staged scan + one-transaction
/// commit, per-source preflight, failure recording, 1s/5s cadence. UI wiring
/// (popover visibility, notifications, backup) is a later stage; this class
/// exposes ScanOnce + a timer so the WPF shell can drive it.</summary>
public sealed class CollectorEngine : IDisposable
{
    private readonly ToastDatabase _db;
    private readonly ForegroundPoller _poller = new();
    private System.Threading.Timer? _timer;
    private readonly object _gate = new();
    private DateTimeOffset _lastScan = DateTimeOffset.MinValue;
    private bool _scanPending;

    public ForegroundPoller Poller => _poller;
    public List<SourceHealth> Health { get; } = new();
    public event Action<ScanReceipt>? ScanCompleted;

    public CollectorEngine(ToastDatabase db) => _db = db;

    public void Start()
    {
        _poller.BecameVisible += () => ScheduleScan(force: true);
        _timer = new System.Threading.Timer(_ => ScheduleScan(force: false),
            null, TimeSpan.Zero, TimeSpan.FromMilliseconds(500));
    }

    public void ScheduleScan(bool force)
    {
        lock (_gate)
        {
            if (force) { _ = ScanOnceAsync(); return; }
            if (_scanPending) return;
            if (DateTimeOffset.UtcNow - _lastScan < TimeSpan.FromSeconds(2)) return;
            _scanPending = true;
        }
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            lock (_gate) _scanPending = false;
            await ScanOnceAsync();
        });
    }

    public async Task<ScanReceipt> ScanOnceAsync()
    {
        var started = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var requestId = Guid.NewGuid().ToString("N");
        var failed = new List<string>();
        var turns = new List<TurnRecord>();
        var sessions = new List<SessionInfo>();
        await Task.Run(() =>
        {
            var sources = new List<string>();
            sources.AddRange(FileScanner.ListFiles(SourceRoots.ClaudeProjects, 2));
            sources.AddRange(ClaudeCodeParser.ListCoworkFiles());
            sources.AddRange(FileScanner.ListFiles(SourceRoots.CodexSessions, 4));
            sources.AddRange(FileScanner.ListFiles(SourceRoots.OmpSessions, 3));
            sources.AddRange(DshParser.ListSessionFiles());
            var staged = new StagedParserStateStore(_db, sources);
            try
            {
                if (!SourceRoots.SourceIsRemote(staged, "claude"))
                {
                    var (t, s) = ClaudeCodeParser.Scan(sources.Where(f => f.Contains(".claude", StringComparison.OrdinalIgnoreCase)), staged);
                    turns.AddRange(t); sessions.AddRange(s);
                }
                var (ct, cs) = CodexParser.Scan(staged);
                turns.AddRange(ct); sessions.AddRange(cs);
                var (ot, os) = OpenCodeParser.Scan(staged);
                turns.AddRange(ot); sessions.AddRange(os);
                var (ht, hs) = HermesParser.Scan(staged);
                turns.AddRange(ht); sessions.AddRange(hs);
                var (mt, ms) = OmpParser.Scan(sources.Where(f => f.Contains(".omp", StringComparison.OrdinalIgnoreCase)), staged);
                turns.AddRange(mt); sessions.AddRange(ms);
                var mode = DshParser.ResolveMode(staged);
                if (mode == DshParser.Mode.Log)
                {
                    var (dt, ds) = DshParser.ScanLogs(DshParser.ListSessionFiles(), staged);
                    turns.AddRange(dt); sessions.AddRange(ds);
                }
                else
                {
                    var (dt, ds) = DshParser.ScanProjCache(staged);
                    turns.AddRange(dt); sessions.AddRange(ds);
                }
                if (staged.ValidateForCommit())
                {
                    var payload = staged.CommitPayload();
                    _db.ApplyScan(turns, sessions, payload.Scan, payload.Totals, payload.Settings);
                }
                else failed.Add("commit validation failed — replaying next tick");
            }
            catch (Exception ex) { failed.Add(ex.Message); }
        });
        _lastScan = DateTimeOffset.UtcNow;
        var receipt = new ScanReceipt(requestId, started, DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            turns.Count, sessions.Count, failed);
        ScanCompleted?.Invoke(receipt);
        return receipt;
    }

    public void Dispose() => _timer?.Dispose();
}
