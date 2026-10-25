using ToastMonitor.Core;

namespace ToastMonitor.ViewModels;

/// <summary>Shared dashboard filter state (Mac DashboardKit filter equivalent).</summary>
public sealed class DashboardFilter : ViewModelBase
{
    private string? _tool;
    private UsagePeriodSlot _slot = UsagePeriodSlot.Week;

    public string? Tool { get => _tool; set => Set(ref _tool, value); }
    public UsagePeriodSlot Slot { get => _slot; set => Set(ref _slot, value); }
}

/// <summary>Period totals, ranked breakdowns and a continuous 30-day activity window.</summary>
public sealed class OverviewViewModel : ViewModelBase
{
    private readonly AppState _state;
    private UsageQueryService.FullSnapshot? _snapshot;
    private List<UsageQueryService.ToolRow> _byTool = new();
    private List<ModelAggRow> _models = new();
    private List<DayBar> _dailyBars = new();

    public OverviewViewModel(AppState state) => _state = state;
    public UsageQueryService.FullSnapshot? Snapshot { get => _snapshot; private set => Set(ref _snapshot, value); }
    public List<UsageQueryService.ToolRow> ByTool { get => _byTool; private set => Set(ref _byTool, value); }
    public List<ModelAggRow> Models { get => _models; private set => Set(ref _models, value); }
    public DashboardFilter Filter { get; } = new();
    public List<DayBar> DailyBars { get => _dailyBars; private set => Set(ref _dailyBars, value); }
    public sealed record DayBar(long Day, long Tokens, double Cost, double BarPercent)
    {
        public string DayText => Format.DayKeyString(Day);
    }
    public sealed record ShareRow(string Name, string Detail, string CostText, double Share);
    public List<ShareRow> ToolShares => ByTool.OrderByDescending(r => r.TotalTokens)
        .Select(r => new ShareRow(r.DisplayName, Format.Compact(r.TotalTokens) + " tokens", Format.Money(r.Cost),
            ByTool.Sum(t => t.TotalTokens) is > 0 and var total ? r.TotalTokens * 100.0 / total : 0)).ToList();
    public List<ShareRow> ModelShares => Models.GroupBy(m => m.Model ?? "未知模型")
        .Select(g => new { Name = g.Key, Tokens = g.Sum(m => ModelTokens(m)), Cost = g.Sum(m => m.Cost) })
        .OrderByDescending(m => m.Tokens)
        .Select(m => new ShareRow(m.Name, Format.Compact(m.Tokens) + " tokens", Format.Money(m.Cost),
            Models.Sum(ModelTokens) is > 0 and var total ? m.Tokens * 100.0 / total : 0)).ToList();
    private static long ModelTokens(ModelAggRow m) => m.Input + m.Output + (m.Tool == "codex" ? 0 : m.CacheRead);
    public string TokensText => Format.Compact(ByTool.Sum(t => t.TotalTokens));
    public string CallsText => Format.Full(ByTool.Sum(t => t.Count));
    public string CostText => Format.Money(ByTool.Sum(t => t.Cost));
    public string ApiValueText => Format.Money(Snapshot is not { } snap ? 0 : Filter.Slot switch
    {
        UsagePeriodSlot.Today => snap.Light.ApiValueToday,
        UsagePeriodSlot.Month => snap.Light.ApiValueMonth,
        UsagePeriodSlot.All => snap.Light.ApiValueAll,
        _ => snap.Light.ApiValueWeek,
    });
    private string Label(UsagePeriodSlot slot) => slot switch
    {
        UsagePeriodSlot.Today => "今日",
        UsagePeriodSlot.Week => _state.Settings.PeriodConfiguration.Mode == UsagePeriodMode.Calendar ? "本周" : "近 7 天",
        UsagePeriodSlot.Month => _state.Settings.PeriodConfiguration.Mode == UsagePeriodMode.Calendar ? "本月" : "近 30 天",
        _ => "全部",
    };
    public string PeriodLabel => Label(Filter.Slot);
    public string WeekLabel => Label(UsagePeriodSlot.Week);
    public string MonthLabel => Label(UsagePeriodSlot.Month);
    public bool HasAnyData => Snapshot?.Light.All.Count > 0;
    public string ScanStatus => Snapshot?.Light.LastScan is > 0 and var ts
        ? "已扫描 · " + Format.DateTime(ts) : _state.LastScanFinished > 0 ? "已扫描 · 暂无记录" : "等待首次扫描";
    public string ActivitySummary => $"{DailyBars.Count(d => d.Tokens > 0)} 个活跃日 · 最近 30 天";
    public string ActivityStart => DateTime.Today.AddDays(-29).ToString("MM/dd");

    public async Task RefreshAsync()
    {
        var snap = await _state.Queries.LoadFullAsync(_state.Settings.PeriodConfiguration);
        Snapshot = snap;
        ByTool = Filter.Slot switch
        {
            UsagePeriodSlot.Today => snap.Light.ByToolToday,
            UsagePeriodSlot.Month => snap.Light.ByToolMonth,
            UsagePeriodSlot.All => snap.Light.ByToolAll,
            _ => snap.Light.ByToolWeek,
        };
        Models = Filter.Slot switch
        {
            UsagePeriodSlot.Today => snap.ModelAggsToday,
            UsagePeriodSlot.Month => snap.ModelAggsMonth,
            UsagePeriodSlot.All => snap.ModelAggsAll,
            _ => snap.ModelAggs,
        };
        if (Filter.Tool is not null)
        {
            ByTool = ByTool.Where(r => r.Tool == Filter.Tool).ToList();
            Models = Models.Where(m => m.Tool == Filter.Tool).ToList();
        }
        // Fill actual calendar dates, including zero-use days. Taking the last
        // 30 database rows would include old activity and skip empty days.
        var today = DateTime.Today;
        var days = Enumerable.Range(0, 30).Select(i => today.AddDays(i - 29))
            .Select(d => (long)(d.Year * 10000 + d.Month * 100 + d.Day)).ToList();
        var max = days.Select(d => snap.Heatmap.GetValueOrDefault(d)).DefaultIfEmpty(0).Max();
        DailyBars = days.Select(d => new DayBar(d, snap.Heatmap.GetValueOrDefault(d),
            snap.HeatmapCost.GetValueOrDefault(d), max == 0 ? 0 : snap.Heatmap.GetValueOrDefault(d) * 100.0 / max)).ToList();
        foreach (var name in new[] { nameof(ToolShares), nameof(ModelShares), nameof(TokensText), nameof(CallsText),
            nameof(CostText), nameof(ApiValueText), nameof(PeriodLabel), nameof(WeekLabel), nameof(MonthLabel),
            nameof(HasAnyData), nameof(ScanStatus), nameof(ActivitySummary), nameof(ActivityStart) }) Raise(name);
    }
}

/// <summary>Sessions page (Mac SessionsView).</summary>
public sealed class SessionsViewModel : ViewModelBase
{
    private readonly AppState _state;
    private List<SessionRow> _sessions = new();
    private SessionRow? _selected;
    private List<TurnRow> _turns = new();

    public SessionsViewModel(AppState state) => _state = state;
    public List<SessionRow> Sessions { get => _sessions; private set => Set(ref _sessions, value); }
    public SessionRow? Selected { get => _selected; private set => Set(ref _selected, value); }
    public List<TurnRow> Turns { get => _turns; private set => Set(ref _turns, value); }
    public DashboardFilter Filter { get; } = new();

    public async Task RefreshAsync()
    {
        Sessions = await _state.Queries.LoadSessionsAsync(Filter.Tool, 500);
        if (Selected is not null) await SelectAsync(Selected);
    }

    public async Task SelectAsync(SessionRow row)
    {
        Selected = row;
        Turns = await _state.Queries.LoadTurnsAsync(row.Tool, row.SessionId, 1000);
    }
}

/// <summary>Plans page (Mac PlansView): subscriptions + amortized cost + runway, via Core SubscriptionMath/BalanceRunway.</summary>
public sealed class PlansViewModel : ViewModelBase
{
    private readonly AppState _state;
    private List<Subscription> _subscriptions = new();
    private double _amortizedDaily;
    private string _runwayText = "";

    public PlansViewModel(AppState state) => _state = state;
    public List<Subscription> Subscriptions { get => _subscriptions; private set => Set(ref _subscriptions, value); }
    public double AmortizedDaily { get => _amortizedDaily; private set => Set(ref _amortizedDaily, value); }
    public string RunwayText { get => _runwayText; private set => Set(ref _runwayText, value); }

    public async Task RefreshAsync()
    {
        var snap = await _state.Queries.LoadLightAsync(_state.Settings.PeriodConfiguration);
        Subscriptions = snap.Subscriptions;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        AmortizedDaily = SubscriptionMath.Amortized(30, Subscriptions, now) / 30;
        RunwayText = Subscriptions.Count == 0 ? "No subscriptions recorded"
            : $"Amortized {Format.Money(AmortizedDaily)}/day across {Subscriptions.Count} subscription(s)";
    }

    public bool AddSubscription(Subscription s) => _state.Database.UpsertSubscription(s);
    public bool DeleteSubscription(long id) => _state.Database.DeleteSubscription(id);
}

/// <summary>Usage analysis page (Mac UsageAnalysisView): model + daily breakdowns for the selected window.</summary>
public sealed class UsageAnalysisViewModel : ViewModelBase
{
    private readonly AppState _state;
    private List<ModelAggRow> _models = new();
    private List<DayAggRow> _daily = new();

    public UsageAnalysisViewModel(AppState state) => _state = state;
    public List<ModelAggRow> Models { get => _models; private set => Set(ref _models, value); }
    public List<DayAggRow> Daily { get => _daily; private set => Set(ref _daily, value); }
    public DashboardFilter Filter { get; } = new();
    public string PeriodHeading => _state.Settings.PeriodConfiguration.Mode == UsagePeriodMode.Calendar ? "模型用量 · 本周" : "模型用量 · 近 7 天";

    public async Task RefreshAsync()
    {
        Raise(nameof(PeriodHeading));
        var config = _state.Settings.PeriodConfiguration;
        var at = DateTimeOffset.UtcNow;
        var range = config.Range(Filter.Slot, at);
        var models = await _state.Queries.LoadModelsAsync(range.Start, range.End);
        Models = Filter.Tool is null ? models : models.Where(m => m.Tool == Filter.Tool).ToList();
        var daily = await _state.Queries.LoadDailyAsync(range.Start, range.End);
        Daily = Filter.Tool is null ? daily : daily.Where(d => d.Tool == Filter.Tool).ToList();
    }
}

/// <summary>Performance page (Mac PerformanceView): per-model median speed / time-to-first-token, plus raw samples.
/// Mac renders one line per model (daily medians, chart limited to 6 models); the Windows page shows the same
/// medians as a table plus the raw sample list, without a chart library.</summary>
public sealed class PerformanceViewModel : ViewModelBase
{
    private readonly AppState _state;
    private List<PerfPointRow> _points = new();
    private double? _avgFirstMs;
    private List<ModelPerfRow> _modelRows = new();

    public PerformanceViewModel(AppState state) => _state = state;
    public List<PerfPointRow> Points { get => _points; private set => Set(ref _points, value); }
    public double? AvgFirstMs { get => _avgFirstMs; private set => Set(ref _avgFirstMs, value); }
    public List<ModelPerfRow> ModelRows { get => _modelRows; private set => Set(ref _modelRows, value); }

    public sealed record ModelPerfRow(string Model, int Calls, double? MedianSpeedTokensPerSec, double? MedianFirstMs);

    public static double? Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    public async Task RefreshAsync()
    {
        var from = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds();
        Points = await _state.Queries.LoadPerfAsync(from);
        var firsts = Points.Where(p => p.FirstMs is not null).Select(p => (double)p.FirstMs!.Value).ToList();
        AvgFirstMs = firsts.Count == 0 ? null : firsts.Average();
        ModelRows = Points.GroupBy(p => p.Model ?? "(unknown)").Select(g =>
        {
            var speeds = g.Where(p => p.FirstMs is not null && p.LastMs is not null && p.LastMs.Value > p.FirstMs.Value)
                .Select(p => p.OutputTokens / ((p.LastMs!.Value - p.FirstMs!.Value) / 1000.0)).ToList();
            var ttfts = g.Where(p => p.FirstMs is not null).Select(p => (double)p.FirstMs!.Value).ToList();
            return new ModelPerfRow(g.Key, g.Count(), Median(speeds), Median(ttfts));
        }).OrderByDescending(r => r.Calls).ToList();
    }
}
