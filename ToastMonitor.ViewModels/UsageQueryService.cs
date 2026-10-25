using ToastMonitor.Core;

namespace ToastMonitor.ViewModels;

/// <summary>
/// Off-UI-thread aggregation facade (Mac UsageQueryService equivalent).
/// All queries run on Task.Run; callers await on the UI thread so property
/// updates land back on the UI context. No caching by data version here:
/// Core has no dataVersionKey, so snapshots are recomputed per refresh
/// (the Mac cache exists to avoid a 371-day GROUP BY on a minute ticker;
/// the Windows daily query is bounded to the requested window).
/// </summary>
public sealed class UsageQueryService
{
    private readonly ToastDatabase _db;

    public UsageQueryService(ToastDatabase db) => _db = db;

    public sealed record ToolRow(string Tool, string DisplayName, long Input, long Output, long CacheRead, long CacheWrite, double Cost, long Count)
    {
        // Mac totalTokens: input + output + cache reads, except Codex whose
        // input already includes cache reads. For the "all" row, CacheRead
        // is pre-adjusted by Sum() to exclude Codex cache, so Input + Output
        // + CacheRead equals the per-tool sum.
        public long TotalTokens
        {
            get
            {
                var kind = Tool switch
                {
                    "claude" => ToolKind.Claude, "codex" => ToolKind.Codex, "opencode" => ToolKind.Opencode,
                    "hermes" => ToolKind.Hermes, "omp" => ToolKind.Omp, "dsh" => ToolKind.Dsh,
                    "openrouter" => ToolKind.Openrouter, _ => (ToolKind?)null,
                };
                if (Tool == "all") return Input + Output + CacheRead;
                return kind is null ? Input + Output : kind.Value.TotalTokens(Input, Output, CacheRead);
            }
        }
    }

    public sealed record LightSnapshot(
        ToolRow Today, ToolRow Week, ToolRow Month, ToolRow All,
        List<ToolRow> ByToolToday, List<ToolRow> ByToolWeek, List<ToolRow> ByToolMonth, List<ToolRow> ByToolAll,
        CostBreakdownRow CostToday, CostBreakdownRow CostWeek, CostBreakdownRow CostMonth, CostBreakdownRow CostAll,
        double ApiValueToday, double ApiValueWeek, double ApiValueMonth, double ApiValueAll,
        List<Subscription> Subscriptions, long LastScan, long FetchedAt);

    public sealed record FullSnapshot(
        LightSnapshot Light, List<ModelAggRow> ModelAggs, List<ModelAggRow> ModelAggsToday,
        List<ModelAggRow> ModelAggsMonth, List<ModelAggRow> ModelAggsAll,
        Dictionary<long, long> Heatmap, Dictionary<long, double> HeatmapCost);

    private static ToolRow Sum(IEnumerable<ToolTotalsRow> rows, string tool = "all")
    {
        var list = rows.ToList();
        // Exclude Codex cache reads from the aggregate cache column so the
        // "all" TotalTokens (Input + Output + CacheRead) matches Mac's
        // per-tool sum exactly.
        var cache = tool == "all" ? list.Where(r => r.Tool != "codex").Sum(r => r.CacheRead) : list.Sum(r => r.CacheRead);
        return new ToolRow(tool, tool == "all" ? "All" : tool,
            list.Sum(r => r.Input), list.Sum(r => r.Output), cache,
            list.Sum(r => r.CacheWrite), list.Sum(r => r.Cost), list.Sum(r => r.Count));
    }

    private static ToolRow ToRow(ToolTotalsRow r)
    {
        var display = r.Tool switch
        {
            "claude" => "Claude Code", "codex" => "Codex", "opencode" => "OpenCode", "hermes" => "Hermes",
            "omp" => "Oh My Pi", "dsh" => "DeepSeek Harness", "openrouter" => "OpenRouter", _ => r.Tool,
        };
        return new ToolRow(r.Tool, display, r.Input, r.Output, r.CacheRead, r.CacheWrite, r.Cost, r.Count);
    }

    public async Task<LightSnapshot> LoadLightAsync(UsagePeriodConfiguration config, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        return await Task.Run(() =>
        {
            var today = config.Range(UsagePeriodSlot.Today, at);
            var week = config.Range(UsagePeriodSlot.Week, at);
            var month = config.Range(UsagePeriodSlot.Month, at);
            var all = config.Range(UsagePeriodSlot.All, at);
            List<ToolRow> ByTool(UsagePeriodRange r) => _db.TotalsByTool(r.Start, r.End).Select(ToRow).ToList();
            var bt = ByTool(today); var bw = ByTool(week); var bm = ByTool(month); var ba = ByTool(all);
            return new LightSnapshot(
                Sum(_db.TotalsByTool(today.Start, today.End)), Sum(_db.TotalsByTool(week.Start, week.End)),
                Sum(_db.TotalsByTool(month.Start, month.End)), Sum(_db.TotalsByTool(all.Start, all.End)),
                bt, bw, bm, ba,
                _db.CostBreakdown(today.Start, today.End), _db.CostBreakdown(week.Start, week.End),
                _db.CostBreakdown(month.Start, month.End), _db.CostBreakdown(all.Start, all.End),
                _db.ApiValue(today.Start, today.End), _db.ApiValue(week.Start, week.End),
                _db.ApiValue(month.Start, month.End), _db.ApiValue(all.Start, all.End),
                _db.GetSubscriptions(), _db.LastScanTime(), at.ToUnixTimeSeconds());
        });
    }

    public async Task<FullSnapshot> LoadFullAsync(UsagePeriodConfiguration config, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var light = await LoadLightAsync(config, at);
        return await Task.Run(() =>
        {
            var today = config.Range(UsagePeriodSlot.Today, at);
            var week = config.Range(UsagePeriodSlot.Week, at);
            var month = config.Range(UsagePeriodSlot.Month, at);
            var all = config.Range(UsagePeriodSlot.All, at);
            var annualStart = DateTimeOffset.UtcNow.AddDays(-371).ToUnixTimeSeconds();
            var daily = _db.DailyAggregates(annualStart, at.ToUnixTimeSeconds());
            var heat = new Dictionary<long, long>();
            var heatCost = new Dictionary<long, double>();
            foreach (var d in daily)
            {
                var kind = d.Tool switch
                {
                    "claude" => ToolKind.Claude, "codex" => ToolKind.Codex, "opencode" => ToolKind.Opencode,
                    "hermes" => ToolKind.Hermes, "omp" => ToolKind.Omp, "dsh" => ToolKind.Dsh,
                    "openrouter" => ToolKind.Openrouter, _ => (ToolKind?)null,
                };
                var tokens = kind is null ? d.Input + d.Output : kind.Value.TotalTokens(d.Input, d.Output, d.CacheRead);
                heat[d.Day] = heat.GetValueOrDefault(d.Day) + tokens;
                heatCost[d.Day] = heatCost.GetValueOrDefault(d.Day) + d.Cost;
            }
            return new FullSnapshot(light,
                _db.ModelAggregates(week.Start, week.End), _db.ModelAggregates(today.Start, today.End),
                _db.ModelAggregates(month.Start, month.End), _db.ModelAggregates(all.Start, all.End),
                heat, heatCost);
        });
    }

    public Task<List<DayAggRow>> LoadDailyAsync(long from, long to) => Task.Run(() => _db.DailyAggregates(from, to));
    public Task<List<SessionRow>> LoadSessionsAsync(string? tool, int limit = 500) => Task.Run(() => _db.Sessions(tool, limit));
    public Task<List<TurnRow>> LoadTurnsAsync(string tool, string sessionId, int limit = 1000) => Task.Run(() => _db.Turns(tool, sessionId, limit));
    public Task<List<PerfPointRow>> LoadPerfAsync(long from) => Task.Run(() => _db.PerfPoints(from));
    public Task<List<ModelAggRow>> LoadModelsAsync(long from, long to) => Task.Run(() => _db.ModelAggregates(from, to));
}
