using ToastMonitor.Collect;
using ToastMonitor.Core;

namespace ToastMonitor.ViewModels;

/// <summary>Popover (Mac PopoverRootView/PopoverHomeView equivalent, data only).</summary>
public sealed class PopoverViewModel : ViewModelBase
{
    private readonly AppState _state;
    private UsageQueryService.LightSnapshot? _snapshot;
    private List<QuotaRowViewModel> _quotaRows = new();
    private List<UsageQueryService.ToolRow> _byToolToday = new();
    private List<SessionRow> _recentSessions = new();
    private string _todayCostText = "";
    private string _todayTokensText = "";
    private string _weekCostText = "";
    private string _lastScanText = "";
    private bool _isRefreshing;
    private bool _isScanning;
    private bool _hasScanned;
    private readonly HashSet<string> _configuredQuotaKeys = new();
    private long _quotaFetchedAt;

    public PopoverViewModel(AppState state)
    {
        _state = state;
        // A scan receipt proves a scan ran, even when it found no source
        // files (in which case scan_state stays empty and LastScan stays 0).
        // Only the flag is recorded here; no UI-bound property is touched
        // from the background thread — RefreshAsync recomputes the text.
        _state.ScanCompleted += _ => _hasScanned = true;
    }

    public UsageQueryService.LightSnapshot? Snapshot { get => _snapshot; private set => Set(ref _snapshot, value); }
    public List<QuotaRowViewModel> QuotaRows { get => _quotaRows; private set => Set(ref _quotaRows, value); }
    public List<UsageQueryService.ToolRow> ByToolToday { get => _byToolToday; private set => Set(ref _byToolToday, value); }
    public List<SessionRow> RecentSessions { get => _recentSessions; private set => Set(ref _recentSessions, value); }
    public string TodayCostText { get => _todayCostText; private set => Set(ref _todayCostText, value); }
    public string TodayTokensText { get => _todayTokensText; private set => Set(ref _todayTokensText, value); }
    public string WeekCostText { get => _weekCostText; private set => Set(ref _weekCostText, value); }
    public string WeekLabel => _state.Settings.PeriodConfiguration.Mode == UsagePeriodMode.Calendar ? "本周记录花费" : "近 7 天记录花费";
    public List<SessionRow> PreviewSessions => RecentSessions.Take(3).ToList();
    public string LastScanText { get => _lastScanText; private set => Set(ref _lastScanText, value); }
    public bool IsRefreshing { get => _isRefreshing; private set => Set(ref _isRefreshing, value); }
    public bool IsScanning { get => _isScanning; private set => Set(ref _isScanning, value); }

    /// <summary>Sections only render as cards when they have data; otherwise a single muted line stands in.</summary>
    public bool HasByToolRows => ByToolToday.Any(r => r.Count > 0);
    public bool ByToolEmpty => !HasByToolRows;
    public bool HasQuotaRows => VisibleQuotaRows.Count > 0;
    public bool QuotaEmpty => !HasQuotaRows;
    public bool HasSessionRows => RecentSessions.Count > 0;
    public bool SessionEmpty => !HasSessionRows;

    /// <summary>Quota rows after applying the hide_quota_row_* settings (Mac loadQuotaRowHidden).</summary>
    public List<QuotaRowViewModel> VisibleQuotaRows =>
        QuotaRows.Where(r => !_state.Settings.GetHideQuotaRow(r.Key) &&
            (r.Connected || r.Windows.Count > 0 || _configuredQuotaKeys.Contains(r.Key))).ToList();

    /// <summary>
    /// Popover-open refresh: trigger one engine scan, wait for its receipt
    /// (15s cap so a stuck scan never hangs the popover), then load the
    /// snapshot. This closes the old race where RefreshAsync ran before the
    /// first scan landed and the popover kept showing pre-scan state until
    /// it was closed and reopened. Collection logic itself is untouched.
    /// </summary>
    public async Task OpenRefreshAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        try
        {
            var tcs = new TaskCompletionSource();
            Action<ScanReceipt>? handler = null;
            handler = _ => tcs.TrySetResult();
            _state.ScanCompleted += handler;
            try
            {
                _state.Engine.ScheduleScan(force: true);
                await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            }
            finally { _state.ScanCompleted -= handler; }
            await RefreshAsync(refreshQuota: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _quotaFetchedAt
                >= Math.Max(1, _state.Settings.QuotaRefreshMinutes) * 60);
        }
        finally { IsScanning = false; }
    }

    public async Task RefreshAsync(bool refreshQuota = false)
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        try
        {
            var snap = await _state.Queries.LoadLightAsync(_state.Settings.PeriodConfiguration);
            Snapshot = snap;
            ByToolToday = snap.ByToolToday;
            TodayCostText = Format.Money(snap.Today.Cost);
            TodayTokensText = Format.Compact(snap.Today.TotalTokens);
            WeekCostText = Format.Money(snap.Week.Cost);
            LastScanText = snap.LastScan > 0
                ? "上次扫描 " + Format.DateTime(snap.LastScan)
                : _hasScanned ? "已扫描，暂无用量数据" : "尚未扫描";
            RecentSessions = await _state.Queries.LoadSessionsAsync(null, 10);
            if (refreshQuota)
            {
                _configuredQuotaKeys.Clear();
                foreach (var (row, credential) in new[] { ("claude", "claude"), ("codex", "codex"), ("grok", "grok"),
                    ("deepseek", "deepseek"), ("cc", "commandcode"), ("go", "opencodego") })
                    if (!string.IsNullOrWhiteSpace(await _state.Credentials.GetTokenAsync(credential))) _configuredQuotaKeys.Add(row);
                if (!string.IsNullOrWhiteSpace(_state.Settings.GetRaw("openrouter_keys"))) _configuredQuotaKeys.Add("openrouter");
                var q = await _state.Quotas.RefreshAllAsync(_state.Settings.ShowRemaining);
                QuotaRows = q.Rows;
                _quotaFetchedAt = q.FetchedAt;
                Raise(nameof(VisibleQuotaRows));
                _state.EvaluateQuotaAlerts(q.Rows);
            }
            Raise(nameof(PreviewSessions));
            Raise(nameof(WeekLabel));
            Raise(nameof(HasByToolRows));
            Raise(nameof(ByToolEmpty));
            Raise(nameof(HasQuotaRows));
            Raise(nameof(QuotaEmpty));
            Raise(nameof(HasSessionRows));
            Raise(nameof(SessionEmpty));
        }
        finally { IsRefreshing = false; }
    }
}
