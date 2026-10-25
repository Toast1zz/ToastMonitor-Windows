using Microsoft.Data.Sqlite;
using ToastMonitor.Core;

namespace ToastMonitor.Collect;

/// <summary>Hermes local parser (Mac HermesParser.swift). Schema varies by version; columns are introspected.</summary>
public static class HermesParser
{
    public static string Home => SourceRoots.HermesHome;
    public static string DbPath => SourceRoots.HermesDb;

    public static List<string> DbPaths
    {
        get
        {
            var paths = new List<string> { DbPath };
            var profiles = Path.Combine(Home, "profiles");
            if (Directory.Exists(profiles))
                foreach (var dir in Directory.GetDirectories(profiles).OrderBy(d => d, StringComparer.Ordinal))
                {
                    var name = Path.GetFileName(dir);
                    if (name.StartsWith('.')) continue;
                    paths.Add(Path.Combine(dir, "state.db"));
                }
            return paths.Where(File.Exists).ToList();
        }
    }

    public static (List<TurnRecord> Turns, List<SessionInfo> Sessions) Scan(IParserStateStore database) =>
        Scan(database, DbPaths);

    public static (List<TurnRecord> Turns, List<SessionInfo> Sessions) Scan(IParserStateStore database, IEnumerable<string> dbPaths)
    {
        var turns = new List<TurnRecord>();
        var sessions = new List<SessionInfo>();
        foreach (var path in dbPaths)
        {
            var (t, s) = ScanPath(path, database);
            turns.AddRange(t);
            sessions.AddRange(s);
        }
        return (turns, sessions);
    }

    private static (List<TurnRecord>, List<SessionInfo>) ScanPath(string path, IParserStateStore database)
    {
        var turns = new List<TurnRecord>();
        var sessions = new List<SessionInfo>();
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
            conn.Open();
            var tables = new HashSet<string>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
                using var r = cmd.ExecuteReader();
                while (r.Read()) tables.Add(r.GetString(0));
            }
            var hasMessages = tables.Contains("messages");
            var hasSessions = tables.Contains("sessions");
            var hasUsage = tables.Contains("session_model_usage");
            if (!hasMessages && !hasSessions && !hasUsage) return (turns, sessions);

            long Sec(long v) => v > 1_000_000_000_000L ? v / 1000 : v;

            if (hasSessions)
            {
                var cols = Columns(conn, "sessions");
                var idCol = cols.Contains("id") ? "id" : (cols.Contains("session_id") ? "session_id" : "");
                if (!string.IsNullOrEmpty(idCol))
                {
                    var titleCol = cols.Contains("title") ? "title" : "NULL";
                    var createdCol = cols.Contains("created_at") ? "created_at" : "0";
                    var updatedCol = cols.Contains("updated_at") ? "updated_at" : "0";
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT {idCol}, {titleCol}, {createdCol}, {updatedCol} FROM sessions;";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        sessions.Add(new SessionInfo(ToolKind.Hermes, r.GetString(0),
                            r.IsDBNull(1) ? null : r.GetString(1), null, null,
                            Sec(r.GetInt64(2)), Sec(r.GetInt64(3))));
                }
            }

            if (hasUsage)
            {
                turns.AddRange(ScanUsage(conn, database, Sec));
                return (turns, sessions);
            }

            if (hasMessages)
            {
                var cols = Columns(conn, "messages");
                var tokenCols = new Dictionary<string, string>();
                string? sessionCol = null;
                var timeCols = new List<string>();
                foreach (var c in cols)
                {
                    switch (c)
                    {
                        case "token_in": case "input_tokens": case "tokens_in": case "prompt_tokens": tokenCols["input"] = c; break;
                        case "token_out": case "output_tokens": case "tokens_out": case "completion_tokens": tokenCols["output"] = c; break;
                        case "session_id": case "session": sessionCol = c; break;
                        case "created_at": case "timestamp": case "ts": timeCols.Add(c); break;
                    }
                }
                if (tokenCols.TryGetValue("input", out var inCol) && tokenCols.TryGetValue("output", out var outCol)
                    && sessionCol is not null && timeCols.Count > 0)
                {
                    var timeCol = timeCols[0];
                    var perSession = new Dictionary<string, (long In, long Out, long LastTs)>();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = $"SELECT {sessionCol}, {inCol}, {outCol}, {timeCol} FROM messages WHERE ({inCol} IS NOT NULL AND {inCol} > 0) OR ({outCol} IS NOT NULL AND {outCol} > 0);";
                        using var r = cmd.ExecuteReader();
                        while (r.Read())
                        {
                            var sid = r.GetString(0);
                            var agg = perSession.TryGetValue(sid, out var a) ? a : (In: 0L, Out: 0L, LastTs: 0L);
                            var ts = r.IsDBNull(3) ? 0 : Sec(r.GetInt64(3));
                            perSession[sid] = (agg.In + Math.Max(r.GetInt64(1), 0), agg.Out + Math.Max(r.GetInt64(2), 0), Math.Max(agg.LastTs, ts));
                        }
                    }
                    var prevTotals = database.SessionTotals();
                    foreach (var (sid, agg) in perSession)
                    {
                        var key = "hermes|" + sid;
                        var prev = prevTotals.TryGetValue(key, out var p) ? p : null;
                        if (prev is not null)
                        {
                            var dIn = Math.Max(agg.In - prev.Input, 0);
                            var dOut = Math.Max(agg.Out - prev.Output, 0);
                            if (dIn > 0 || dOut > 0)
                                turns.Add(new TurnRecord(ToolKind.Hermes, sid, null, null,
                                    agg.LastTs > 0 ? agg.LastTs : JsonUtil.NowSeconds(),
                                    dIn, dOut, 0, 0, 0, 0,
                                    eventId: $"hermes-local:{sid}:{agg.LastTs}:{agg.In}:{agg.Out}", costQuality: "unknown"));
                        }
                        else if (agg.In > 0 || agg.Out > 0)
                        {
                            turns.Add(new TurnRecord(ToolKind.Hermes, sid, null, null,
                                agg.LastTs > 0 ? agg.LastTs : JsonUtil.NowSeconds(),
                                agg.In, agg.Out, 0, 0, 0, 0,
                                eventId: $"hermes-local:{sid}:{agg.LastTs}:{agg.In}:{agg.Out}", costQuality: "unknown"));
                        }
                        database.SetSessionTotals(key, "hermes",
                            Math.Max(prev?.Input ?? 0, agg.In), Math.Max(prev?.Output ?? 0, agg.Out),
                            0, 0, 0, 0, agg.LastTs);
                    }
                }
            }
        }
        catch { /* missing/corrupt store: no turns, like the Mac parser */ }
        return (turns, sessions);
    }

    private static List<TurnRecord> ScanUsage(SqliteConnection conn, IParserStateStore database, Func<long, long> sec)
    {
        var turns = new List<TurnRecord>();
        var cols = new HashSet<string>(Columns(conn, "session_model_usage"));
        if (!new[] { "session_id", "input_tokens", "output_tokens", "last_seen" }.All(cols.Contains)) return turns;
        string Col(string name, string fallback) => cols.Contains(name) ? name : fallback;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT session_id, {Col("model", "NULL")}, {Col("billing_provider", "NULL")},
                   {Col("billing_base_url", "NULL")}, input_tokens, output_tokens,
                   {Col("reasoning_tokens", "0")}, {Col("cache_read_tokens", "0")},
                   {Col("cache_write_tokens", "0")}, {Col("first_seen", "last_seen")}, last_seen
            FROM session_model_usage;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var sid = r.GetString(0);
            if (string.IsNullOrEmpty(sid)) continue;
            var model = r.IsDBNull(1) ? null : r.GetString(1);
            var provider = r.IsDBNull(2) ? null : r.GetString(2);
            var baseUrl = r.IsDBNull(3) ? null : r.GetString(3);
            var current = new HermesUsageBaseline.Counters(
                Math.Max(r.GetInt64(4), 0), Math.Max(r.GetInt64(5), 0), Math.Max(r.GetInt64(6), 0),
                Math.Max(r.GetInt64(7), 0), Math.Max(r.GetInt64(8), 0));
            double Seconds(int i)
            {
                var v = r.GetDouble(i);
                if (!double.IsFinite(v) || v <= 0) return 0;
                return v > 1_000_000_000_000 ? v / 1000 : v;
            }
            var lastSeen = (long)Seconds(10);
            var firstSeen = Math.Min((long)Seconds(9), lastSeen);
            var key = HermesUsageBaseline.Key(sid, model, provider, baseUrl);
            var localKey = HermesUsageBaseline.LocalTotalsKey(key);
            var totals = database.SessionTotals();
            var local = totals.TryGetValue(localKey, out var lt)
                ? new long[] { lt.Input, lt.Output, lt.Reasoning, lt.CacheRead, lt.CacheWrite, 0 } : null;
            var prev = HermesUsageBaseline.Merge(current, new[] { local, HermesUsageBaseline.Parse(database.GetSetting(key)) });
            var step = HermesUsageBaseline.Advance(current, prev);
            var d = step.Delta;
            if (d.Input > 0 || d.Output > 0 || d.Reasoning > 0 || d.CacheRead > 0 || d.CacheWrite > 0)
            {
                var ts = step.IsFirst && firstSeen > 0 ? firstSeen : lastSeen;
                if (ts < 1_000_000_000L)
                    ts = HermesParser.SessionStart(sid) ?? JsonUtil.NowSeconds();
                turns.Add(new TurnRecord(ToolKind.Hermes, sid, null, model, ts,
                    d.Input, d.Output, d.Reasoning, d.CacheRead, d.CacheWrite, 0,
                    provider: provider,
                    eventId: $"hermes-local:{key}:{current.Input}:{current.Output}:{current.Reasoning}:{current.CacheRead}:{current.CacheWrite}",
                    costQuality: "unknown"));
            }
            var b = step.Base;
            database.SetSetting(key, HermesUsageBaseline.Encode(b));
            database.SetSessionTotals(localKey, "hermes", b.Input, b.Output, b.Reasoning, b.CacheRead, b.CacheWrite, 0, lastSeen);
        }
        return turns;
    }

    public static long? SessionStart(string id)
    {
        var parts = id.Split('_');
        if (parts.Length < 2 || parts[0].Length != 8 || parts[1].Length != 6) return null;
        if (DateTime.TryParseExact(parts[0] + parts[1], "yyyyMMddHHmmss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dt))
            return new DateTimeOffset(dt).ToUnixTimeSeconds();
        return null;
    }

    private static List<string> Columns(SqliteConnection conn, string table)
    {
        var cols = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var r = cmd.ExecuteReader();
        while (r.Read()) cols.Add(r.GetString(1));
        return cols;
    }
}

/// <summary>Shared high-water baseline for Hermes local/remote (Mac HermesUsageBaseline, verbatim).</summary>
public static class HermesUsageBaseline
{
    public sealed record Counters(long Input, long Output, long Reasoning, long CacheRead, long CacheWrite);

    public static string Key(string session, string? model, string? provider, string? baseUrl) =>
        "hm_d|" + session + "|" + (model ?? "") + "|" + (provider ?? "") + "|" + (baseUrl ?? "").Trim('/');

    public static string LocalTotalsKey(string key) => "hermes|usage|" + key;

    public static long[] Parse(string? stored) =>
        (stored ?? "").Split(',').Select(s => long.TryParse(s, out var v) ? v : 0).ToArray();

    public static string Encode(Counters b) =>
        string.Join(",", new[] { b.Input, b.Output, b.Reasoning, b.CacheRead, b.CacheWrite, 0L });

    public static long[] Merge(Counters current, IEnumerable<long[]?> baselines)
    {
        var normalized = new List<long[]>();
        foreach (var values in baselines)
        {
            if (values is null) continue;
            if (values.Length == 6) normalized.Add(values);
            else if (values.Length == 5)
            {
                var reasoning = Math.Min(current.Reasoning, Math.Max(values[0], 0));
                normalized.Add(new[] { Math.Max(values[0] - reasoning, 0), values[1], reasoning, values[2], values[3], 0L });
            }
        }
        if (normalized.Count == 0) return Array.Empty<long>();
        var merged = normalized[0];
        foreach (var values in normalized.Skip(1))
            merged = merged.Zip(values, Math.Max).ToArray();
        return merged;
    }

    public static (Counters Delta, Counters Base, bool IsFirst) Advance(Counters c, long[] prev)
    {
        switch (prev.Length)
        {
            case 6:
                return (new Counters(
                    Math.Max(c.Input - prev[0], 0), Math.Max(c.Output - prev[1], 0), Math.Max(c.Reasoning - prev[2], 0),
                    Math.Max(c.CacheRead - prev[3], 0), Math.Max(c.CacheWrite - prev[4], 0)),
                    new Counters(Math.Max(c.Input, prev[0]), Math.Max(c.Output, prev[1]), Math.Max(c.Reasoning, prev[2]),
                        Math.Max(c.CacheRead, prev[3]), Math.Max(c.CacheWrite, prev[4])), false);
            case 5:
                return (new Counters(
                    Math.Max(c.Input + c.Reasoning - prev[0], 0), Math.Max(c.Output - prev[1], 0), 0,
                    Math.Max(c.CacheRead - prev[2], 0), Math.Max(c.CacheWrite - prev[3], 0)), c, false);
            default:
                return (c, c, true);
        }
    }
}
