using ToastMonitor.Collect;

namespace ToastMonitor.ViewModels;

/// <summary>Per-source health for the UI (Mac SourceHealthHub equivalent, backed by Collect's record type).</summary>
public sealed class SourceHealthHub
{
    public const double StaleThresholdSeconds = 120;

    private readonly object _gate = new();
    private readonly Dictionary<string, SourceHealth> _sources = new(StringComparer.Ordinal);

    public event Action? Changed;

    public IReadOnlyList<SourceHealth> Sources
    {
        get { lock (_gate) return _sources.Values.OrderBy(s => s.Source).ToList(); }
    }

    public static SourceHealthStatus StatusOf(SourceHealth h, long nowUnix)
    {
        if (!string.IsNullOrEmpty(h.Message)) return SourceHealthStatus.Error;
        if (h.LastSuccessSeconds > 0 && nowUnix - h.LastSuccessSeconds > StaleThresholdSeconds) return SourceHealthStatus.Stale;
        return h.LastSuccessSeconds > 0 ? SourceHealthStatus.Ok : SourceHealthStatus.Disabled;
    }

    public void Record(string source, SourceHealthStatus status, string? message, long lastSuccessSeconds)
    {
        lock (_gate) _sources[source] = new SourceHealth(source, status, message, lastSuccessSeconds);
        Changed?.Invoke();
    }

    public void RecordIdle(string source, long nowUnix) => Record(source, SourceHealthStatus.Ok, null, nowUnix);

    public void RecordError(string source, string message, long nowUnix)
    {
        lock (_gate)
        {
            var prev = _sources.TryGetValue(source, out var h) ? h.LastSuccessSeconds : 0;
            _sources[source] = new SourceHealth(source, SourceHealthStatus.Error, message, prev);
        }
        Changed?.Invoke();
    }

    /// <summary>Sync from the engine's health list after a scan (engine uses the same record type).</summary>
    public void SyncFromEngine(IReadOnlyList<SourceHealth> engineHealth, long nowUnix)
    {
        lock (_gate)
        {
            foreach (var h in engineHealth)
            {
                var status = !string.IsNullOrEmpty(h.Message) ? SourceHealthStatus.Error
                    : h.LastSuccessSeconds > 0 && nowUnix - h.LastSuccessSeconds > StaleThresholdSeconds ? SourceHealthStatus.Stale
                    : SourceHealthStatus.Ok;
                _sources[h.Source] = new SourceHealth(h.Source, status, h.Message, h.LastSuccessSeconds);
            }
        }
        Changed?.Invoke();
    }

    public void PurgeRemote(IEnumerable<string> remoteSources)
    {
        lock (_gate)
        {
            foreach (var s in remoteSources.ToList()) _sources.Remove(s);
        }
        Changed?.Invoke();
    }
}
