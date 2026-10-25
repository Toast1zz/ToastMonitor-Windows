using ToastMonitor.Core;

namespace ToastMonitor.Collect;

/// <summary>Minimal persistence surface used by incremental parsers (Mac ParserStateStore).</summary>
public interface IParserStateStore
{
    (long Size, long Mtime, long Identity, string? Context) ScanState(string source);
    bool SetScanState(string source, long size, long mtime, long identity, string? context);
    Dictionary<string, SessionTotalsEntry> SessionTotals();
    bool SetSessionTotals(string key, string tool, long input, long output, long reasoning,
        long cacheRead, long cacheWrite, double cost, long updated);
    string? GetSetting(string key);
    bool SetSetting(string key, string? value);
}

/// <summary>In-memory store for tests and for parsers run outside the engine.</summary>
public sealed class InMemoryParserStateStore : IParserStateStore
{
    private readonly Dictionary<string, (long, long, long, string?)> _scan = new();
    private readonly Dictionary<string, SessionTotalsEntry> _totals = new();
    private readonly Dictionary<string, string?> _settings = new();

    public (long Size, long Mtime, long Identity, string? Context) ScanState(string source) =>
        _scan.TryGetValue(source, out var v) ? v : (0, 0, 0, null);

    public bool SetScanState(string source, long size, long mtime, long identity, string? context)
    {
        _scan[source] = (size, mtime, identity, context);
        return true;
    }

    public Dictionary<string, SessionTotalsEntry> SessionTotals() => new(_totals);

    public bool SetSessionTotals(string key, string tool, long input, long output, long reasoning,
        long cacheRead, long cacheWrite, double cost, long updated)
    {
        var sid = key.Contains('|') ? key[(key.IndexOf('|') + 1)..] : key;
        _totals[key] = new SessionTotalsEntry(tool, sid, input, output, reasoning, cacheRead, cacheWrite, cost, updated);
        return true;
    }

    public string? GetSetting(string key) => _settings.TryGetValue(key, out var v) ? v : null;

    public bool SetSetting(string key, string? value)
    {
        _settings[key] = value;
        return true;
    }
}

/// <summary>
/// Staged store (Mac StagedParserStateStore): reads come from the database,
/// cursor/baseline writes stay in memory until the engine commits them in one
/// transaction via ToastDatabase.ApplyScan. A mismatch found at commit time
/// discards the prepared result instead of writing it.
/// </summary>
public sealed class StagedParserStateStore : IParserStateStore
{
    private readonly ToastDatabase _database;
    private readonly IReadOnlyDictionary<string, FileScanner.Stat?> _sourceSnapshots;
    private readonly Dictionary<string, (long, long, long, string?)> _originalScan = new();
    private readonly Dictionary<string, string?> _originalSettings = new();
    private readonly Dictionary<string, (long, long, long, string?)> _scanUpdates = new();
    private readonly Dictionary<string, SessionTotalsEntry> _totalsUpdates = new();
    private readonly Dictionary<string, string?> _settingUpdates = new();
    private Dictionary<string, SessionTotalsEntry>? _originalTotals;
    public bool PreparationIsConsistent { get; private set; } = true;

    public StagedParserStateStore(ToastDatabase database, IEnumerable<string> sourcePaths)
    {
        _database = database;
        _sourceSnapshots = sourcePaths.Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p => p, p => FileScanner.FileStat(p), StringComparer.OrdinalIgnoreCase);
    }

    public (long Size, long Mtime, long Identity, string? Context) ScanState(string source)
    {
        if (_scanUpdates.TryGetValue(source, out var u)) return u;
        if (_originalScan.TryGetValue(source, out var o)) return o;
        var db = _database.GetScanState(source);
        var tuple = (db.Size, db.Mtime, db.Identity, db.Context);
        _originalScan[source] = tuple;
        return tuple;
    }

    public bool SetScanState(string source, long size, long mtime, long identity, string? context)
    {
        if (!_originalScan.ContainsKey(source)) _ = ScanState(source);
        var current = FileScanner.FileStat(source);
        if (current is null || current.Mtime != mtime || current.Identity != identity || current.Size < size)
        {
            PreparationIsConsistent = false;
            return false;
        }
        _scanUpdates[source] = (size, mtime, identity, context);
        return true;
    }

    public string? GetSetting(string key)
    {
        if (_settingUpdates.TryGetValue(key, out var u)) return u;
        if (_originalSettings.TryGetValue(key, out var o)) return o;
        var v = _database.GetSetting(key);
        _originalSettings[key] = v;
        return v;
    }

    public bool SetSetting(string key, string? value)
    {
        _ = GetSetting(key);
        _settingUpdates[key] = value;
        return true;
    }

    public Dictionary<string, SessionTotalsEntry> SessionTotals()
    {
        if (_originalTotals is null) _originalTotals = _database.GetSessionTotals();
        var result = new Dictionary<string, SessionTotalsEntry>(_originalTotals);
        foreach (var (k, v) in _totalsUpdates) result[k] = v;
        return result;
    }

    public bool SetSessionTotals(string key, string tool, long input, long output, long reasoning,
        long cacheRead, long cacheWrite, double cost, long updated)
    {
        // Keys are "tool|sessionId" split on the FIRST pipe; session ids (notably
        // Hermes usage keys "hermes|usage|hm_d|...") may themselves contain pipes.
        if (!key.Contains('|')) { PreparationIsConsistent = false; return false; }
        var sid = key[(key.IndexOf('|') + 1)..];
        _totalsUpdates[key] = new SessionTotalsEntry(tool, sid, input, output, reasoning, cacheRead, cacheWrite, cost, updated);
        return true;
    }

    public bool ValidateForCommit()
    {
        if (!PreparationIsConsistent) return false;
        foreach (var (k, original) in _originalSettings)
            if (_database.GetSetting(k) != original) return false;
        foreach (var (path, snapshot) in _sourceSnapshots)
        {
            var current = FileScanner.FileStat(path);
            if (!SameStat(current, snapshot)) return false;
        }
        foreach (var (source, update) in _scanUpdates)
        {
            var current = FileScanner.FileStat(source);
            if (current is null || current.Mtime != update.Item2 || current.Identity != update.Item3) return false;
            var db = _database.GetScanState(source);
            if (!_originalScan.TryGetValue(source, out var orig)
                || db.Size != orig.Item1 || db.Mtime != orig.Item2 || db.Identity != orig.Item3 || db.Context != orig.Item4)
                return false;
        }
        if (_originalTotals is not null)
        {
            var current = _database.GetSessionTotals();
            foreach (var key in _totalsUpdates.Keys)
                if (!SameTotals(current.TryGetValue(key, out var c) ? c : null, _originalTotals.TryGetValue(key, out var o) ? o : null))
                    return false;
        }
        return true;
    }

    public (Dictionary<string, Core.ScanState> Scan, Dictionary<string, SessionTotalsEntry> Totals, Dictionary<string, string?> Settings) CommitPayload()
    {
        var scan = _scanUpdates.ToDictionary(kv => kv.Key,
            kv => new Core.ScanState(kv.Value.Item1, kv.Value.Item2, kv.Value.Item3, kv.Value.Item4));
        return (scan, new Dictionary<string, SessionTotalsEntry>(_totalsUpdates), new Dictionary<string, string?>(_settingUpdates));
    }

    private static bool SameStat(FileScanner.Stat? a, FileScanner.Stat? b) =>
        (a, b) switch
        {
            (null, null) => true,
            (not null, not null) => a.Size == b.Size && a.Mtime == b.Mtime && a.Identity == b.Identity,
            _ => false,
        };

    private static bool SameTotals(SessionTotalsEntry? a, SessionTotalsEntry? b) =>
        (a, b) switch
        {
            (null, null) => true,
            (not null, not null) => a.Tool == b.Tool && a.Input == b.Input && a.Output == b.Output
                && a.Reasoning == b.Reasoning && a.CacheRead == b.CacheRead && a.CacheWrite == b.CacheWrite && a.Cost == b.Cost,
            _ => false,
        };
}
