using System.Text.Json;
using ToastMonitor.Core;

namespace ToastMonitor.Collect;

/// <summary>DeepSeek Harness parser (Mac DSHParser.swift). Cache mode is verbatim;
/// log mode handles plain .jsonl natively and .jsonl.zstd via ZstdSharp (whole-file
/// decompress; incremental .zstd appends reparse from 0 and rely on the DB event-id
/// dedupe, a documented deviation from Mac's frame-by-frame cursor).</summary>
public static class DshParser
{
    public static string Home => SourceRoots.DshHome;
    public static string SessionsRoot => SourceRoots.DshSessions;
    public static string ProjCachePath => SourceRoots.DshProjCache;
    public const string ModeSettingKey = "dsh_parse_mode";

    public enum Mode { Log, Cache }

    public static Mode ResolveMode(IParserStateStore store, bool? zstdAvailable = null, Func<long, long, string?, long>? totalsCount = null)
    {
        var hasZstd = zstdAvailable ?? Zstd.IsAvailable;
        var stored = store.GetSetting(ModeSettingKey);
        if (stored == "log") return Mode.Log;
        if (stored == "cache")
        {
            if (hasZstd && (totalsCount?.Invoke(0, JsonUtil.NowSeconds(), "dsh") ?? 1) == 0)
            {
                store.SetSetting(ModeSettingKey, "log");
                return Mode.Log;
            }
            return Mode.Cache;
        }
        var mode = hasZstd ? Mode.Log : Mode.Cache;
        store.SetSetting(ModeSettingKey, mode == Mode.Log ? "log" : "cache");
        return mode;
    }

    public static List<string> ListSessionFiles()
    {
        var outList = new List<string>();
        if (!Directory.Exists(SessionsRoot)) return outList;
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((SessionsRoot, 0));
        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            if (depth >= 3) continue;
            foreach (var entry in Directory.GetFileSystemEntries(dir))
            {
                var name = Path.GetFileName(entry);
                if (name.StartsWith('.')) continue;
                if (Directory.Exists(entry)) stack.Push((entry, depth + 1));
                else if (name == "session.jsonl.zstd" || name == "session.jsonl") outList.Add(entry);
            }
        }
        return outList;
    }

    public static (List<TurnRecord> Turns, List<SessionInfo> Sessions) ScanLogs(
        IEnumerable<string> knownPaths, IParserStateStore database,
        Func<byte[], byte[]?>? decompress = null)
    {
        decompress ??= Zstd.Decompress;
        var turns = new List<TurnRecord>();
        var sessions = new List<SessionInfo>();
        foreach (var file in knownPaths)
        {
            var st = FileScanner.FileStat(file);
            if (st is null) continue;
            var prev = database.ScanState(file);
            if (prev.Size == st.Size && prev.Mtime == st.Mtime && prev.Identity == st.Identity) continue;
            var pendingRewrite = JsonUtil.ContextNeedsFullRescan(prev.Context);
            var sameAppendOnly = FileScanner.IsAppendOnly(file, st, prev, checkLineBoundary: !file.EndsWith(".zstd", StringComparison.OrdinalIgnoreCase));
            var offset = sameAppendOnly ? prev.Size : 0;
            var ctx = JsonUtil.ParseContext(prev.Context);
            string? sessionId = ctx.TryGetValue("sid", out var s) ? s : null;
            string? project = ctx.TryGetValue("cwd", out var c) ? c : null;
            long headerCreated = 0;
            var objects = new List<(long Offset, JsonElement Obj)>();
            if (file.EndsWith(".zstd", StringComparison.OrdinalIgnoreCase))
            {
                // Whole-file decompress (deviation documented above); cursor still tracks compressed size.
                var raw = File.ReadAllBytes(file);
                var decoded = decompress(raw);
                if (decoded is not null)
                {
                    var text = System.Text.Encoding.UTF8.GetString(decoded);
                    long pos = 0;
                    foreach (var line in text.Split('\n'))
                    {
                        var lineParsed = JsonUtil.ParseLine(line);
                        if (lineParsed is not null) objects.Add((pos, lineParsed.Value));
                        pos += line.Length + 1;
                    }
                }
            }
            else
            {
                FileScanner.ForEachNewJsonLine(file, offset, (o, obj) => objects.Add((o, obj)));
            }
            if (offset == 0 || sessionId is null)
            {
                foreach (var (_, obj) in objects)
                {
                    if (JsonUtil.GetString(obj, "type") != "session") continue;
                    sessionId = JsonUtil.GetString(obj, "id") ?? sessionId;
                    project = JsonUtil.GetString(obj, "cwd") ?? project;
                    headerCreated = JsonUtil.Sec(JsonUtil.GetLong(obj, "createdAt"));
                    break;
                }
            }
            var newOffset = file.EndsWith(".zstd", StringComparison.OrdinalIgnoreCase)
                ? st.Size
                : (objects.Count > 0 ? st.Size : offset);
            // For plain jsonl, compute the cursor from the scanner (consumed bytes), not file size,
            // so a trailing partial line stays pending. Re-run the scanner cursor logic:
            if (!file.EndsWith(".zstd", StringComparison.OrdinalIgnoreCase))
            {
                // The objects list was built from offset; the last object's end is not tracked,
                // so use the scanner's own cursor by re-invoking with a no-op is wasteful.
                // Instead: if the file ends with a newline, cursor == size; otherwise the
                // scanner above already skipped an unparseable tail, and size is still safe
                // because a reparse from size on next scan would miss nothing only when the
                // tail later completes AND grows past size (then IsAppendOnly fails the
                // line-boundary check and we rescan from 0, deduped by event id).
                newOffset = st.Size;
            }
            if (objects.Count == 0)
            {
                database.SetScanState(file, newOffset, st.Mtime, st.Identity,
                    JsonUtil.ContextWithFullRescan(prev.Context, st.Size < prev.Size || pendingRewrite));
                continue;
            }
            if (string.IsNullOrEmpty(sessionId))
            {
                database.SetScanState(file, newOffset, st.Mtime, st.Identity,
                    JsonUtil.ContextWithStallCount(prev.Context, 0));
                continue;
            }
            var parsed = ParseLogEvents(objects, sessionId!, project);
            turns.AddRange(parsed.Turns);
            if (parsed.LastTs > 0 || parsed.Turns.Count > 0)
                sessions.Add(new SessionInfo(ToolKind.Dsh, sessionId!, null, project, parsed.Model,
                    headerCreated, JsonUtil.Sec(parsed.LastTs > 0 ? parsed.LastTs : headerCreated)));
            var newCtx = new Dictionary<string, string> { ["sid"] = sessionId! };
            if (project is not null) newCtx["cwd"] = project;
            if (st.Size < prev.Size) newCtx["_full_rescan"] = "true";
            database.SetScanState(file, newOffset, st.Mtime, st.Identity, JsonUtil.ContextJson(newCtx));
        }
        return (turns, sessions);
    }

    public static (List<TurnRecord> Turns, long FirstTs, long LastTs, string? Model) ParseLogEvents(
        IReadOnlyList<(long Offset, JsonElement Obj)> objects, string sessionId, string? project)
    {
        var turns = new List<TurnRecord>();
        var pending = new Dictionary<string, (JsonElement Usage, long Seq, long Time)>();
        string? headerProvider = null, headerModel = null;
        long firstTs = 0, lastTs = 0;
        string? firstModel = null;

        void Emit(JsonElement usage, long seq, long time, string? provider, string? model)
        {
            var input = JsonUtil.GetLong(usage, "inputTokens");
            var output = JsonUtil.GetLong(usage, "outputTokens");
            var cacheRead = JsonUtil.GetLong(usage, "cacheReadTokens");
            var cacheWrite = JsonUtil.GetLong(usage, "cacheWriteTokens");
            if (input + output + cacheRead + cacheWrite <= 0) return;
            var ts = JsonUtil.Sec(time > 0 ? time : JsonUtil.NowSeconds() * 1000);
            var cost = Pricing.Estimate(model, input, output, cacheRead, cacheWrite);
            turns.Add(new TurnRecord(ToolKind.Dsh, sessionId, project, model, ts,
                input, output, 0, cacheRead, cacheWrite, cost ?? 0, provider: provider,
                eventId: $"dsh-log:{sessionId}:{seq}", costQuality: cost is null ? "unknown" : "estimated"));
            if (firstTs == 0 || time < firstTs) firstTs = time;
            if (time > lastTs) lastTs = time;
            firstModel ??= !string.IsNullOrEmpty(model) ? model : null;
        }

        foreach (var (_, obj) in objects)
        {
            var type = JsonUtil.GetString(obj, "type");
            switch (type)
            {
                case "request/header":
                    var header = JsonUtil.Get(JsonUtil.Get(obj, "data") ?? default, "header");
                    var config = header is not null ? JsonUtil.Get(header.Value, "config") : null;
                    if (config is not null)
                    {
                        headerProvider = JsonUtil.GetString(config.Value, "provider") ?? headerProvider;
                        headerModel = JsonUtil.GetString(config.Value, "model") ?? headerModel;
                    }
                    break;
                case "assistant/chunk":
                    var d = JsonUtil.Get(obj, "data");
                    var chunk = d is not null ? JsonUtil.Get(d.Value, "chunk") : null;
                    if (d is null || chunk is null) break;
                    var key = $"{JsonUtil.GetLong(d.Value, "turn")}:{JsonUtil.GetLong(d.Value, "step")}";
                    var chunkType = JsonUtil.GetString(chunk.Value, "type");
                    if (chunkType == "usage")
                    {
                        var usage = JsonUtil.Get(chunk.Value, "usage");
                        if (usage is not null)
                            pending[key] = (usage.Value, JsonUtil.GetLong(obj, "seq"), JsonUtil.GetLong(obj, "time"));
                    }
                    else if (chunkType == "finish")
                    {
                        var rs = JsonUtil.Get(chunk.Value, "replayState");
                        var provider = rs is not null ? JsonUtil.GetString(rs.Value, "provider") : null;
                        var model = rs is not null ? JsonUtil.GetString(rs.Value, "model") : null;
                        if (pending.TryGetValue(key, out var p))
                        {
                            pending.Remove(key);
                            Emit(p.Usage, p.Seq, p.Time, provider ?? headerProvider, model ?? headerModel);
                        }
                    }
                    break;
                case "assistant/message":
                    var dm = JsonUtil.Get(obj, "data");
                    var msg = dm is not null ? JsonUtil.Get(dm.Value, "message") : null;
                    var usageM = msg is not null ? JsonUtil.Get(msg.Value, "usage") : null;
                    if (dm is null || msg is null || usageM is null) break;
                    var keyM = $"{JsonUtil.GetLong(dm.Value, "turn")}:{JsonUtil.GetLong(dm.Value, "step")}";
                    var providerM = JsonUtil.GetString(msg.Value, "provider") ?? headerProvider;
                    var modelM = JsonUtil.GetString(msg.Value, "model") ?? headerModel;
                    if (pending.TryGetValue(keyM, out var pm))
                    {
                        pending.Remove(keyM);
                        Emit(usageM.Value, JsonUtil.GetLong(obj, "seq") != 0 ? JsonUtil.GetLong(obj, "seq") : pm.Seq,
                            JsonUtil.GetLong(obj, "time") != 0 ? JsonUtil.GetLong(obj, "time") : pm.Time, providerM, modelM);
                    }
                    else Emit(usageM.Value, JsonUtil.GetLong(obj, "seq"), JsonUtil.GetLong(obj, "time"), providerM, modelM);
                    break;
            }
        }
        foreach (var p in pending.Values)
            Emit(p.Usage, p.Seq, p.Time, headerProvider, headerModel);
        return (turns, firstTs, lastTs, firstModel);
    }

    public static (List<TurnRecord> Turns, List<SessionInfo> Sessions) ScanProjCache(IParserStateStore database, string? projCachePath = null)
    {
        var turns = new List<TurnRecord>();
        var sessionInfos = new List<SessionInfo>();
        var path = projCachePath ?? ProjCachePath;
        if (!File.Exists(path)) return (turns, sessionInfos);
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            root = doc.RootElement.Clone();
        }
        catch { return (turns, sessionInfos); }
        var tables = JsonUtil.Get(root, "tables");
        if (tables is null) return (turns, sessionInfos);
        var sessionsObj = JsonUtil.Get(tables.Value, "sessions");
        if (sessionsObj is null) return (turns, sessionInfos);
        var prevTotals = database.SessionTotals();
        var now = JsonUtil.NowSeconds();
        foreach (var sessionProp in sessionsObj.Value.EnumerateObject())
        {
            var sid = sessionProp.Name;
            var entry = sessionProp.Value;
            var identity = JsonUtil.Get(entry, "identity");
            var rows = JsonUtil.Get(entry, "rows");
            var tu = rows is not null ? JsonUtil.Get(rows.Value, "tokenUsage") : null;
            var tuVal = tu is not null ? JsonUtil.Get(tu.Value, "val") : null;
            var totals = tuVal is not null ? JsonUtil.Get(tuVal.Value, "totals") : null;
            if (identity is null || totals is null) continue;
            var totalsEl = totals.Value;
            var input = JsonUtil.GetLong(totalsEl, "uncachedInputTokens");
            var output = JsonUtil.GetLong(totalsEl, "outputTokens");
            var cacheRead = JsonUtil.GetLong(totalsEl, "cacheReadTokens");
            var cacheWrite = JsonUtil.GetLong(totalsEl, "cacheWriteTokens");
            var seq = tu is not null ? JsonUtil.GetLong(tu.Value, "seq") : 0;
            var createdAt = JsonUtil.Sec(JsonUtil.GetLong(identity.Value, "createdAt"));
            var project = JsonUtil.GetString(identity.Value, "cwd");
            string? title = null;
            long lastPromptAt = 0;
            var titleRow = rows is not null ? JsonUtil.Get(rows.Value, "title") : null;
            if (titleRow is not null)
            {
                var t = JsonUtil.GetString(titleRow.Value, "val");
                if (!string.IsNullOrEmpty(t)) title = t;
            }
            var meta = rows is not null ? JsonUtil.Get(rows.Value, "sessionListMetadata") : null;
            var metaVal = meta is not null ? JsonUtil.Get(meta.Value, "val") : null;
            if (metaVal is not null) lastPromptAt = JsonUtil.Sec(JsonUtil.GetLong(metaVal.Value, "lastPromptAt"));
            var ts = lastPromptAt > 0 ? lastPromptAt : (createdAt > 0 ? createdAt : now);
            var key = "dsh|" + sid;
            var prev = prevTotals.TryGetValue(key, out var pv) ? pv : null;
            var dIn = prev is null ? input : Math.Max(input - prev.Input, 0);
            var dOut = prev is null ? output : Math.Max(output - prev.Output, 0);
            var dCr = prev is null ? cacheRead : Math.Max(cacheRead - prev.CacheRead, 0);
            var dCw = prev is null ? cacheWrite : Math.Max(cacheWrite - prev.CacheWrite, 0);
            database.SetSessionTotals(key, "dsh",
                Math.Max(prev?.Input ?? 0, input), Math.Max(prev?.Output ?? 0, output), 0,
                Math.Max(prev?.CacheRead ?? 0, cacheRead), Math.Max(prev?.CacheWrite ?? 0, cacheWrite), 0, ts);
            if (dIn + dOut + dCr + dCw > 0)
                turns.Add(new TurnRecord(ToolKind.Dsh, sid, project, null, ts,
                    dIn, dOut, 0, dCr, dCw, 0,
                    eventId: $"dsh-cache:{sid}:{seq}:{input}:{output}:{cacheRead}:{cacheWrite}",
                    costQuality: "unknown"));
            sessionInfos.Add(new SessionInfo(ToolKind.Dsh, sid, title, project, null, createdAt, ts));
        }
        return (turns, sessionInfos);
    }
}
