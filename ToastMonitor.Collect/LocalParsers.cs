using System.Text.Json;
using Microsoft.Data.Sqlite;
using ToastMonitor.Core;

namespace ToastMonitor.Collect;

/// <summary>Local JSONL/SQLite parsers, ported from Mac Collect/*.swift.</summary>
public static class ClaudeCodeParser
{
    public static string Root => SourceRoots.ClaudeProjects;
    public static string CoworkRoot => SourceRoots.ClaudeCoworkRoot;

    public static List<string> ListCoworkFiles(string? root = null)
    {
        root ??= CoworkRoot;
        var outList = new List<string>();
        if (!Directory.Exists(root)) return outList;
        foreach (var org in Directory.GetDirectories(root))
        foreach (var project in Directory.GetDirectories(org))
        foreach (var session in Directory.GetDirectories(project))
        {
            if (!Path.GetFileName(session).StartsWith("local_", StringComparison.Ordinal)) continue;
            var claudeProjects = Path.Combine(session, ".claude", "projects");
            if (Directory.Exists(claudeProjects)) outList.AddRange(FileScanner.ListFiles(claudeProjects, 2));
        }
        return outList;
    }

    public static (List<TurnRecord> Turns, List<SessionInfo> Sessions) Scan(
        IEnumerable<string> knownPaths, IParserStateStore database, Func<string, bool>? sourceIsRemote = null)
    {
        if (sourceIsRemote?.Invoke("claude") == true) return (new(), new());
        var turns = new List<TurnRecord>();
        var sessions = new List<SessionInfo>();
        var byEvent = new Dictionary<string, TurnRecord>();
        foreach (var file in knownPaths)
        {
            var st = FileScanner.FileStat(file);
            if (st is null) continue;
            var prev = database.ScanState(file);
            if (prev.Size == st.Size && prev.Mtime == st.Mtime && prev.Identity == st.Identity) continue;
            var pendingRewrite = JsonUtil.ContextNeedsFullRescan(prev.Context);
            var sameAppendOnly = FileScanner.IsAppendOnly(file, st, prev);
            var offset = sameAppendOnly ? prev.Size : 0;
            var objectCount = 0;
            var sessionId = Path.GetFileNameWithoutExtension(file);
            var project = FileScanner.LastComponentOfEncodedPath(
                Path.GetFileName(Path.GetDirectoryName(file) ?? ""));
            long firstTs = 0, lastTs = 0;
            string? sessionModel = null;
            var newOffset = FileScanner.ForEachNewJsonLine(file, offset, (itemOffset, obj) =>
            {
                objectCount++;
                var type = JsonUtil.GetString(obj, "type");
                if (type is null) return;
                var sid = JsonUtil.GetString(obj, "sessionId");
                if (!string.IsNullOrEmpty(sid)) sessionId = sid;
                if (type != "assistant") return;
                var usage = JsonUtil.Get(obj, "usage") ?? JsonUtil.Get(JsonUtil.Get(obj, "message") ?? default, "usage");
                if (usage is null) return;
                var usageEl = usage.Value;
                long ts = JsonUtil.ParseIso(JsonUtil.GetString(obj, "timestamp"))
                    ?? JsonUtil.ParseIso(JsonUtil.GetString(JsonUtil.Get(obj, "message") ?? default, "timestamp"))
                    ?? JsonUtil.NowSeconds();
                var model = JsonUtil.GetString(obj, "model")
                    ?? JsonUtil.GetString(JsonUtil.Get(obj, "message") ?? default, "model");
                var input = JsonUtil.GetLong(usageEl, "input_tokens");
                var output = JsonUtil.GetLong(usageEl, "output_tokens");
                var cacheRead = JsonUtil.GetLong(usageEl, "cache_read_input_tokens");
                var cacheWrite = JsonUtil.GetLong(usageEl, "cache_creation_input_tokens");
                if (input <= 0 && output <= 0 && cacheRead <= 0 && cacheWrite <= 0) return;
                var cost = Pricing.Estimate(model, input, output, cacheRead, cacheWrite);
                var messageEl = JsonUtil.Get(obj, "message");
                var messageId = messageEl is not null ? JsonUtil.GetString(messageEl.Value, "id") : null;
                var messageIdAlt = JsonUtil.GetString(obj, "messageId");
                var contentJson = messageEl is not null ? EventIdentity.CanonicalJson(JsonUtil.Get(messageEl.Value, "content") ?? default) : "";
                var eventId = EventIdentity.Claude(sessionId, messageId, messageIdAlt, itemOffset, model,
                    EventIdentity.CanonicalJson(usageEl), contentJson);
                var turn = new TurnRecord(ToolKind.Claude, sessionId, project, model, ts,
                    input, output, 0, cacheRead, cacheWrite, cost ?? 0,
                    eventId: eventId, costQuality: cost is null ? "unknown" : "estimated");
                // Audit rule: a streaming message id first arrives as an intermediate
                // and later as the final row. Within one scan, keep the MAX of each
                // field (the DB MAX upsert does the same across scans).
                if (byEvent.TryGetValue(eventId, out var existing))
                {
                    byEvent[eventId] = new TurnRecord(ToolKind.Claude, sessionId, project,
                        turn.Model ?? existing.Model, Math.Max(turn.Ts, existing.Ts),
                        Math.Max(turn.InputTokens, existing.InputTokens),
                        Math.Max(turn.OutputTokens, existing.OutputTokens), 0,
                        Math.Max(turn.CacheRead, existing.CacheRead),
                        Math.Max(turn.CacheWrite, existing.CacheWrite),
                        Math.Max(turn.Cost, existing.Cost),
                        eventId: eventId, costQuality: turn.CostQuality);
                }
                else byEvent[eventId] = turn;
                sessionModel ??= model;
                if (firstTs == 0 || ts < firstTs) firstTs = ts;
                if (ts > lastTs) lastTs = ts;
            });
            if (objectCount == 0)
            {
                var pending = st.Size < prev.Size || pendingRewrite;
                database.SetScanState(file, newOffset, st.Mtime, st.Identity,
                    JsonUtil.ContextWithFullRescan(prev.Context, pending));
                continue;
            }
            if (lastTs > 0)
                sessions.Add(new SessionInfo(ToolKind.Claude, sessionId, null, project, sessionModel, firstTs, lastTs));
            var pending2 = st.Size < prev.Size;
            database.SetScanState(file, newOffset, st.Mtime, st.Identity,
                JsonUtil.ContextWithFullRescan(prev.Context, pending2));
        }
        turns.AddRange(byEvent.Values);
        return (turns, sessions);
    }
}

public static class CodexParser
{
    public static string SessionsRoot => SourceRoots.CodexSessions;
    public static string StateDbPath => SourceRoots.CodexStateDb;

    public static string? SessionIdFromRolloutName(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var m = System.Text.RegularExpressions.Regex.Match(stem,
            @"^rollout-\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}-(.+)$");
        return m.Success ? m.Groups[1].Value : null;
    }

    public static Dictionary<string, (string? Model, string? Provider, string? Title, string? Cwd)> ReadThreads(string? dbPath = null)
    {
        var outMap = new Dictionary<string, (string?, string?, string?, string?)>();
        var path = dbPath ?? StateDbPath;
        if (!File.Exists(path)) return outMap;
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, model, model_provider, title, cwd FROM threads;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                outMap[reader.GetString(0)] = (
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4));
        }
        catch { /* missing/corrupt threads table: sessions still parse from rollouts */ }
        return outMap;
    }

    public static (List<TurnRecord> Turns, List<SessionInfo> Sessions) Scan(
        IParserStateStore database, string? sessionsRoot = null, string? stateDbPath = null)
    {
        var turns = new List<TurnRecord>();
        var sessions = new List<SessionInfo>();
        var root = sessionsRoot ?? SessionsRoot;
        if (!Directory.Exists(root)) return (turns, sessions);
        var threadMeta = ReadThreads(stateDbPath);
        var files = FileScanner.ListFiles(root, 4).Where(f => f.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var file in files)
        {
            var st = FileScanner.FileStat(file);
            if (st is null) continue;
            var prev = database.ScanState(file);
            if (prev.Size == st.Size && prev.Mtime == st.Mtime && prev.Identity == st.Identity) continue;
            var pendingRewrite = JsonUtil.ContextNeedsFullRescan(prev.Context);
            var sameAppendOnly = FileScanner.IsAppendOnly(file, st, prev);
            var offset = sameAppendOnly ? prev.Size : 0;
            // Pre-pass: last turn_context model in the new segment (Mac chunkModel).
            string? chunkModel = null;
            FileScanner.ForEachNewJsonLine(file, offset, (_, obj) =>
            {
                if (JsonUtil.GetString(obj, "type") == "turn_context"
                    && JsonUtil.Get(obj, "payload") is { } p
                    && !string.IsNullOrEmpty(JsonUtil.GetString(p, "model")))
                    chunkModel = JsonUtil.GetString(p, "model");
            });
            var ctx = JsonUtil.ParseContext(sameAppendOnly ? prev.Context : null);
            var sid = SessionIdFromRolloutName(Path.GetFileName(file)) ?? "";
            string? model = null, provider = null, cwd = null, title = null;
            long created = 0, updated = 0;
            if (ctx.TryGetValue("sid", out var cs) && !string.IsNullOrEmpty(cs)) sid = cs;
            if (ctx.TryGetValue("model", out var cm) && !string.IsNullOrEmpty(cm)) model = cm;
            if (ctx.TryGetValue("provider", out var cp) && !string.IsNullOrEmpty(cp)) provider = cp;
            if (ctx.TryGetValue("cwd", out var cc) && !string.IsNullOrEmpty(cc)) cwd = cc;
            if (ctx.TryGetValue("title", out var ct) && !string.IsNullOrEmpty(ct)) title = ct;
            if (ctx.TryGetValue("created", out var ccr) && long.TryParse(ccr, out var cr)) created = cr;
            if (ctx.TryGetValue("updated", out var cu) && long.TryParse(cu, out var up)) updated = up;
            var objectCount = 0;
            var newOffset = FileScanner.ForEachNewJsonLine(file, offset, (itemOffset, obj) =>
            {
                objectCount++;
                var type = JsonUtil.GetString(obj, "type");
                var payload = JsonUtil.Get(obj, "payload");
                if (type == "session_meta" && payload is not null)
                {
                    var p = payload.Value;
                    var s = JsonUtil.GetString(p, "session_id");
                    if (!string.IsNullOrEmpty(s)) sid = s;
                    var mp = JsonUtil.GetString(p, "model_provider");
                    if (!string.IsNullOrEmpty(mp)) provider = mp;
                    var c = JsonUtil.GetString(p, "cwd");
                    if (c is not null) cwd = c;
                    var ts = JsonUtil.ParseIso(JsonUtil.GetString(obj, "timestamp"));
                    if (ts is not null) { if (created == 0) created = ts.Value; updated = Math.Max(updated, ts.Value); }
                }
                if (type == "turn_context" && payload is not null)
                {
                    var m = JsonUtil.GetString(payload.Value, "model");
                    if (!string.IsNullOrEmpty(m)) model = m;
                }
                if (type == "event_msg" && payload is not null && JsonUtil.GetString(payload.Value, "type") == "token_count"
                    && JsonUtil.Get(payload.Value, "info") is { } infoEl)
                {
                    var info = infoEl;
                    var last = JsonUtil.Get(info, "last_token_usage");
                    if (last is null) return;
                    var lastEl = last.Value;
                    var input = JsonUtil.GetLong(lastEl, "input_tokens");
                    var output = JsonUtil.GetLong(lastEl, "output_tokens");
                    var reasoning = JsonUtil.GetLong(lastEl, "reasoning_output_tokens");
                    var cacheRead = JsonUtil.GetLong(lastEl, "cached_input_tokens");
                    var cacheWrite = JsonUtil.GetLong(lastEl, "cache_write_input_tokens");
                    // Audit S3: a rate-limit update re-sends the same token_count
                    // with the same total. Collapse repeats by total+last tuple.
                    var total = JsonUtil.Get(info, "total_token_usage");
                    var totalKey = total is null ? "" : string.Join(",", new[]
                    {
                        JsonUtil.GetLong(total.Value, "input_tokens"), JsonUtil.GetLong(total.Value, "cached_input_tokens"),
                        JsonUtil.GetLong(total.Value, "output_tokens"), JsonUtil.GetLong(total.Value, "cache_write_input_tokens"),
                    });
                    var lastKey = string.Join(",", new[] { input, output, reasoning, cacheRead, cacheWrite });
                    if (ctx.TryGetValue("codex_last_total", out var lt) && lt == totalKey
                        && ctx.TryGetValue("codex_last_usage", out var lu) && lu == lastKey)
                        return;
                    ctx["codex_last_total"] = totalKey;
                    ctx["codex_last_usage"] = lastKey;
                    var ts = JsonUtil.ParseIso(JsonUtil.GetString(obj, "timestamp")) ?? JsonUtil.NowSeconds();
                    var eventModel = model ?? chunkModel ?? threadMeta.GetValueOrDefault(sid).Model ?? threadMeta.GetValueOrDefault(sid).Provider;
                    var est = Pricing.Estimate(eventModel, input, output, cacheRead, cacheWrite, inputIncludesCache: true);
                    if (input + output + reasoning + cacheRead + cacheWrite > 0)
                    {
                        var eventId = EventIdentity.Codex(sid, ts, eventModel, EventIdentity.CanonicalJson(lastEl), itemOffset);
                        turns.Add(new TurnRecord(ToolKind.Codex, sid, null, eventModel, ts,
                            input, output, reasoning, cacheRead, cacheWrite, est ?? 0,
                            provider: provider ?? threadMeta.GetValueOrDefault(sid).Provider,
                            eventId: eventId, costQuality: est is null ? "unknown" : "estimated"));
                    }
                    updated = Math.Max(updated, ts);
                }
            });
            if (objectCount == 0)
            {
                var pending = st.Size < prev.Size || pendingRewrite;
                database.SetScanState(file, newOffset, st.Mtime, st.Identity,
                    JsonUtil.ContextWithFullRescan(prev.Context, pending));
                continue;
            }
            if (!string.IsNullOrEmpty(sid))
            {
                if (threadMeta.TryGetValue(sid, out var meta))
                {
                    if (model is null) model = !string.IsNullOrEmpty(meta.Model) ? meta.Model : meta.Provider;
                    title ??= meta.Title;
                    cwd ??= meta.Cwd;
                }
                var project = cwd is null ? null : Path.GetFileName(cwd);
                var sessionModel = model ?? (!string.IsNullOrEmpty(provider) ? provider : null);
                sessions.Add(new SessionInfo(ToolKind.Codex, sid, title, project, sessionModel, created, updated));
            }
            var newCtx = new Dictionary<string, string>
            {
                ["sid"] = sid, ["model"] = model ?? "", ["provider"] = provider ?? "",
                ["cwd"] = cwd ?? "", ["title"] = title ?? "",
                ["created"] = created.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["updated"] = updated.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            foreach (var k in new[] { "codex_last_total", "codex_last_usage" })
                if (ctx.TryGetValue(k, out var v)) newCtx[k] = v;
            if (st.Size < prev.Size) newCtx["_full_rescan"] = "true";
            database.SetScanState(file, newOffset, st.Mtime, st.Identity, JsonUtil.ContextJson(newCtx));
        }
        foreach (var (id, meta) in threadMeta)
            if (!sessions.Any(s => s.SessionId == id))
                sessions.Add(new SessionInfo(ToolKind.Codex, id, meta.Title,
                    meta.Cwd is null ? null : Path.GetFileName(meta.Cwd),
                    meta.Model ?? meta.Provider, 0, 0));
        return (turns, sessions);
    }
}

public static class OpenCodeParser
{
    public static string DbPath => SourceRoots.OpenCodeDb;

    /// <summary>Model column is sometimes a JSON object {"id":...,"providerID":...}.</summary>
    public static string? NormalizeModel(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        if (raw.StartsWith('{'))
        {
            var el = JsonUtil.ParseLine(raw);
            if (el is not null) return JsonUtil.GetString(el.Value, "id");
        }
        return raw;
    }

    public static string? NormalizeProvider(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || !raw.StartsWith('{')) return null;
        var el = JsonUtil.ParseLine(raw);
        var p = el is not null ? JsonUtil.GetString(el.Value, "providerID") : null;
        return string.IsNullOrEmpty(p) ? null : p;
    }

    public static (List<TurnRecord> Turns, List<SessionInfo> Sessions) Scan(
        IParserStateStore database, string? dbPath = null)
    {
        var turns = new List<TurnRecord>();
        var sessions = new List<SessionInfo>();
        var path = dbPath ?? DbPath;
        if (!File.Exists(path)) return (turns, sessions);
        var prevTotals = database.SessionTotals();
        long Sec(long v) => v > 1_000_000_000_000L ? v / 1000 : v;
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT id, COALESCE(title,''), COALESCE(directory,''), model, cost,
                tokens_input, tokens_output, tokens_reasoning, tokens_cache_read, tokens_cache_write,
                time_created, time_updated FROM session;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var title = reader.GetString(1);
                var directory = reader.GetString(2);
                var rawModel = reader.IsDBNull(3) ? null : reader.GetString(3);
                var model = NormalizeModel(rawModel);
                var provider = NormalizeProvider(rawModel);
                var cost = reader.IsDBNull(4) ? 0 : reader.GetDouble(4);
                var hasActualCost = !reader.IsDBNull(4);
                var input = reader.GetInt64(5); var output = reader.GetInt64(6);
                var reasoning = reader.GetInt64(7); var cacheRead = reader.GetInt64(8); var cacheWrite = reader.GetInt64(9);
                var created = Sec(reader.GetInt64(10)); var updated = Sec(reader.GetInt64(11));
                var project = string.IsNullOrEmpty(directory) ? null : Path.GetFileName(directory);
                sessions.Add(new SessionInfo(ToolKind.Opencode, id, string.IsNullOrEmpty(title) ? null : title,
                    project, model, created, updated));
                var key = "opencode|" + id;
                var totalInput = input;
                if (prevTotals.TryGetValue(key, out var prev))
                {
                    var legacyCombined = prev.Reasoning == 0 && reasoning > 0 && prev.Input == input + reasoning;
                    var dInput = (legacyCombined ? input + reasoning : totalInput) - prev.Input;
                    var dOutput = output - prev.Output;
                    var dReasoning = legacyCombined ? 0 : reasoning - prev.Reasoning;
                    var dCacheRead = cacheRead - prev.CacheRead;
                    var dCacheWrite = cacheWrite - prev.CacheWrite;
                    var dCost = cost - prev.Cost;
                    var turn = new TurnRecord(ToolKind.Opencode, id, project, model, updated,
                        Math.Max(dInput, 0), Math.Max(dOutput, 0), Math.Max(dReasoning, 0),
                        Math.Max(dCacheRead, 0), Math.Max(dCacheWrite, 0), Math.Max(dCost, 0),
                        provider: provider,
                        eventId: $"opencode:{id}:{reader.GetInt64(11)}:{totalInput}:{output}:{reasoning}:{cacheRead}:{cacheWrite}",
                        costQuality: hasActualCost ? "actual" : "unknown");
                    if (turn.InputTokens + turn.OutputTokens + turn.ReasoningTokens + turn.CacheRead + turn.CacheWrite > 0)
                        turns.Add(turn);
                }
                else if (totalInput > 0 || output > 0 || reasoning > 0 || cacheRead > 0 || cacheWrite > 0)
                {
                    turns.Add(new TurnRecord(ToolKind.Opencode, id, project, model, updated,
                        totalInput, output, reasoning, cacheRead, cacheWrite, cost,
                        provider: provider,
                        eventId: $"opencode:{id}:{reader.GetInt64(11)}:{totalInput}:{output}:{reasoning}:{cacheRead}:{cacheWrite}",
                        costQuality: hasActualCost ? "actual" : "unknown"));
                }
                database.SetSessionTotals(key, "opencode",
                    Math.Max(prevTotals.TryGetValue(key, out var p0) ? p0.Input : 0, totalInput),
                    Math.Max(prevTotals.TryGetValue(key, out var p1) ? p1.Output : 0, output),
                    Math.Max(prevTotals.TryGetValue(key, out var p2) ? p2.Reasoning : 0, reasoning),
                    Math.Max(prevTotals.TryGetValue(key, out var p3) ? p3.CacheRead : 0, cacheRead),
                    Math.Max(prevTotals.TryGetValue(key, out var p4) ? p4.CacheWrite : 0, cacheWrite),
                    Math.Max(prevTotals.TryGetValue(key, out var p5) ? p5.Cost : 0, cost), updated);
            }
        }
        catch { return (turns, sessions); }
        return (turns, sessions);
    }
}

public static class OmpParser
{
    public static string Root => SourceRoots.OmpSessions;

    public static (List<TurnRecord> Turns, List<SessionInfo> Sessions) Scan(
        IEnumerable<string> knownPaths, IParserStateStore database)
    {
        var turns = new List<TurnRecord>();
        var sessions = new List<SessionInfo>();
        foreach (var file in knownPaths)
        {
            var st = FileScanner.FileStat(file);
            if (st is null) continue;
            var prev = database.ScanState(file);
            if (prev.Size == st.Size && prev.Mtime == st.Mtime && prev.Identity == st.Identity) continue;
            var pendingRewrite = JsonUtil.ContextNeedsFullRescan(prev.Context);
            var sameAppendOnly = FileScanner.IsAppendOnly(file, st, prev);
            var offset = sameAppendOnly ? prev.Size : 0;
            var objectCount = 0;
            var fileName = Path.GetFileName(file);
            var ctx = JsonUtil.ParseContext(pendingRewrite ? null : prev.Context);
            var sessionId = ctx.TryGetValue("sid", out var cs) && !string.IsNullOrEmpty(cs)
                ? cs : Path.GetFileNameWithoutExtension(file);
            var foundSession = false;
            var newOffset = FileScanner.ForEachNewJsonLine(file, offset, (_, obj) =>
            {
                objectCount++;
                if (!foundSession && JsonUtil.GetString(obj, "type") == "session")
                {
                    var sid = JsonUtil.GetString(obj, "id");
                    if (!string.IsNullOrEmpty(sid)) { sessionId = sid; foundSession = true; }
                }
            });
            if (objectCount == 0)
            {
                var pending = st.Size < prev.Size || pendingRewrite;
                database.SetScanState(file, newOffset, st.Mtime, st.Identity,
                    JsonUtil.ContextWithFullRescan(prev.Context, pending));
                continue;
            }
            var project = FileScanner.LastComponentOfEncodedPath(
                Path.GetFileName(Path.GetDirectoryName(file) ?? ""));
            var relKey = file.StartsWith(Root, StringComparison.OrdinalIgnoreCase)
                ? file[Root.Length..] : fileName;
            long firstTs = 0, lastTs = 0;
            string? sessionModel = null;
            FileScanner.ForEachNewJsonLine(file, offset, (itemOffset, obj) =>
            {
                if (JsonUtil.GetString(obj, "type") != "message") return;
                var msg = JsonUtil.Get(obj, "message");
                if (msg is null || JsonUtil.GetString(msg.Value, "role") != "assistant") return;
                var usage = JsonUtil.Get(msg.Value, "usage");
                if (usage is null) return;
                var u = usage.Value;
                var input = JsonUtil.GetLong(u, "input");
                var output = JsonUtil.GetLong(u, "output");
                var cacheRead = JsonUtil.GetLong(u, "cacheRead");
                var cacheWrite = JsonUtil.GetLong(u, "cacheWrite");
                if (input + output + cacheRead + cacheWrite <= 0) return;
                var ts = JsonUtil.ParseIso(JsonUtil.GetString(obj, "timestamp")) ?? JsonUtil.NowSeconds();
                var model = JsonUtil.GetString(msg.Value, "model");
                var costEl = JsonUtil.Get(u, "cost");
                var cost = costEl is not null ? JsonUtil.GetDouble(costEl.Value, "total") : 0;
                var messageId = JsonUtil.GetString(obj, "id");
                var eventId = EventIdentity.Omp(relKey, sessionId, messageId,
                    EventIdentity.CanonicalJson(u), EventIdentity.CanonicalJson(msg.Value),
                    JsonUtil.GetString(obj, "timestamp") ?? "");
                turns.Add(new TurnRecord(ToolKind.Omp, sessionId, project, model, ts,
                    input, output, 0, cacheRead, cacheWrite, cost,
                    eventId: eventId, costQuality: "estimated"));
                if (firstTs == 0 || ts < firstTs) firstTs = ts;
                if (ts > lastTs) lastTs = ts;
                sessionModel ??= model;
            });
            if (lastTs > 0)
                sessions.Add(new SessionInfo(ToolKind.Omp, sessionId, null, project, sessionModel, firstTs, lastTs));
            var newCtx = new Dictionary<string, string> { ["sid"] = sessionId };
            if (st.Size < prev.Size) newCtx["_full_rescan"] = "true";
            database.SetScanState(file, newOffset, st.Mtime, st.Identity, JsonUtil.ContextJson(newCtx));
        }
        return (turns, sessions);
    }
}

/// <summary>Command Code plan catalog (Mac CommandCodePlanCatalog.swift, verbatim).</summary>
public static class CommandCodePlanCatalog
{
    public static readonly IReadOnlyDictionary<string, double> MonthlyAllowanceUsd = new Dictionary<string, double>
    {
        ["individual-go"] = 10, ["individual-pro"] = 30, ["individual-max"] = 150,
        ["individual-ultra"] = 300, ["individual-goat"] = 70,
    };

    public static double? Allowance(string planId) =>
        MonthlyAllowanceUsd.TryGetValue(planId, out var v) ? v : null;

    public static string ShortName(string planId)
    {
        var tier = planId.StartsWith("individual-", StringComparison.Ordinal)
            ? planId["individual-".Length..] : planId;
        return string.IsNullOrEmpty(tier) ? planId : tier.ToUpperInvariant();
    }
}
