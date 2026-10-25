namespace ToastMonitor.Core;

/// <summary>Per-tool aggregate row (Mac Database.ToolTotals).</summary>
public sealed record ToolTotalsRow(
    string Tool, long Input, long Output, long CacheRead, long CacheWrite, double Cost, long Count);

/// <summary>Per-tool/model aggregate row (Mac Database.ModelAgg).</summary>
public sealed record ModelAggRow(
    string Tool, string? Model, long Input, long Output, long CacheRead, long CacheWrite, double Cost, long Count);

/// <summary>Per-tool/day aggregate row (Mac Database.DayAgg, day = local yyyymmdd).</summary>
public sealed record DayAggRow(
    long Day, string Tool, long Input, long Output, long CacheRead, long CacheWrite, double Cost, long Count);

/// <summary>One session row joined with its session_totals (Mac Database.SessionRow).</summary>
public sealed record SessionRow(
    string Tool, string SessionId, string? Title, string? Project, string? Model,
    long Created, long Updated, long Input, long Output, long CacheRead, long CacheWrite, double Cost);

/// <summary>One turn row (Mac Database turns query).</summary>
public sealed record TurnRow(
    long Ts, string? Model, long Input, long Output, long CacheRead, long CacheWrite, double Cost);

/// <summary>One perf point (Mac PerfPoint).</summary>
public sealed record PerfPointRow(
    string Tool, string Key, string? Model, long? RequestMs, long? FirstMs, long? LastMs, long OutputTokens);

/// <summary>Estimated/actual cost split (Mac UsageQueryService.CostQuality).</summary>
public sealed record CostBreakdownRow(double Estimated, double Actual, double DeepSeekActual);

/// <summary>
/// Stage-3 read queries for the App layer. These live in Core (partial class)
/// so they can reuse the database lock and Command() helper; no existing
/// Core file is modified. All methods take the same lock as the writers.
/// </summary>
public sealed partial class ToastDatabase
{
    // Stage-4 rollup switch: these aggregates read Mac usageSource (whole
    // buckets from turn_rollup + partial edges from raw turns). With
    // AggregatesUseRollup=false the same query degrades to raw turns, so
    // both modes are provably identical (see RollupTests). The Mac ApiValue
    // grouping (tool='codex' AND input_tokens < cache_read) is not a
    // rollup key and is not used by any Windows query, so it does not
    // block this switch.
    private const string AggSums =
        "COALESCE(SUM(input_tokens),0), COALESCE(SUM(output_tokens),0), " +
        "COALESCE(SUM(cache_read),0), COALESCE(SUM(cache_write),0), COALESCE(SUM(cost),0), COALESCE(SUM(n),0)";

    public List<ToolTotalsRow> TotalsByTool(long from, long to)
    {
        lock (_lock)
        {
            EnsureOpen();
            using var cmd = UsageCommand("tool, " + AggSums, "tool", "tool", from, to);
            var rows = new List<ToolTotalsRow>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                rows.Add(new ToolTotalsRow(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2),
                    reader.GetInt64(3), reader.GetInt64(4), reader.GetDouble(5), reader.GetInt64(6)));
            return rows;
        }
    }

    public List<ModelAggRow> ModelAggregates(long from, long to)
    {
        lock (_lock)
        {
            EnsureOpen();
            using var cmd = UsageCommand("tool, model, " + AggSums, "tool, model", "SUM(cost) DESC", from, to);
            var rows = new List<ModelAggRow>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                rows.Add(new ModelAggRow(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
                    reader.GetDouble(6), reader.GetInt64(7)));
            return rows;
        }
    }

    public List<DayAggRow> DailyAggregates(long from, long to)
    {
        lock (_lock)
        {
            EnsureOpen();
            // Local calendar day, matching the Mac DayAgg yyyymmdd key.
            // Buckets are 900s-aligned so a bucket never straddles a local day.
            using var cmd = UsageCommand(
                "CAST(strftime('%Y%m%d', bucket, 'unixepoch', 'localtime') AS INTEGER), tool, " + AggSums,
                "1, tool", "1, tool", from, to);
            var rows = new List<DayAggRow>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                rows.Add(new DayAggRow(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2),
                    reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetDouble(6), reader.GetInt64(7)));
            return rows;
        }
    }

    public List<SessionRow> Sessions(string? tool, int limit)
    {
        lock (_lock)
        {
            EnsureOpen();
            var sql = tool is null
                ? "SELECT s.tool, s.session_id, s.title, s.project, s.model, s.created, s.updated, " +
                  "COALESCE(t.input,0), COALESCE(t.output,0), COALESCE(t.cache_read,0), COALESCE(t.cache_write,0), COALESCE(t.cost,0) " +
                  "FROM sessions s LEFT JOIN session_totals t ON t.tool = s.tool AND t.session_id = s.session_id " +
                  "ORDER BY s.updated DESC LIMIT @p0;"
                : "SELECT s.tool, s.session_id, s.title, s.project, s.model, s.created, s.updated, " +
                  "COALESCE(t.input,0), COALESCE(t.output,0), COALESCE(t.cache_read,0), COALESCE(t.cache_write,0), COALESCE(t.cost,0) " +
                  "FROM sessions s LEFT JOIN session_totals t ON t.tool = s.tool AND t.session_id = s.session_id " +
                  "WHERE s.tool = @p1 ORDER BY s.updated DESC LIMIT @p0;";
            using var cmd = Command(sql);
            cmd.Parameters.AddWithValue("@p0", limit);
            if (tool is not null) cmd.Parameters.AddWithValue("@p1", tool);
            var rows = new List<SessionRow>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                rows.Add(new SessionRow(reader.GetString(0), reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8),
                    reader.GetInt64(9), reader.GetInt64(10), reader.GetDouble(11)));
            return rows;
        }
    }

    public List<TurnRow> Turns(string tool, string sessionId, int limit)
    {
        lock (_lock)
        {
            EnsureOpen();
            using var cmd = Command(
                "SELECT ts, model, input_tokens, output_tokens, cache_read, cache_write, cost " +
                "FROM turns WHERE tool = @p0 AND session_id = @p1 ORDER BY ts ASC LIMIT @p2;");
            cmd.Parameters.AddWithValue("@p0", tool);
            cmd.Parameters.AddWithValue("@p1", sessionId);
            cmd.Parameters.AddWithValue("@p2", limit);
            var rows = new List<TurnRow>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                rows.Add(new TurnRow(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetDouble(6)));
            return rows;
        }
    }

    public List<PerfPointRow> PerfPoints(long from)
    {
        lock (_lock)
        {
            EnsureOpen();
            using var cmd = Command(
                "SELECT tool, key, model, request_ms, first_ms, last_ms, output_tokens " +
                "FROM perf_samples WHERE first_ms IS NOT NULL AND first_ms >= @p0 ORDER BY first_ms DESC LIMIT 5000;");
            cmd.Parameters.AddWithValue("@p0", from);
            var rows = new List<PerfPointRow>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                rows.Add(new PerfPointRow(reader.GetString(0), reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4),
                    reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    reader.GetInt64(6)));
            return rows;
        }
    }

    public long LastScanTime()
    {
        lock (_lock)
        {
            EnsureOpen();
            using var cmd = Command("SELECT COALESCE(MAX(last_scan),0) FROM scan_state;");
            var v = cmd.ExecuteScalar();
            return v is null or DBNull ? 0 : Convert.ToInt64(v);
        }
    }

    public CostBreakdownRow CostBreakdown(long from, long to)
    {
        lock (_lock)
        {
            EnsureOpen();
            double estimated = 0, actual = 0, deepseek = 0;
            // Cost-quality split via the rollup source; the DeepSeek part
            // keeps its Windows definition (tool='dsh') and stays raw, since
            // the Mac deepseek flag is provider-based, not tool-based.
            using (var cmd = UsageCommand("cost_quality, COALESCE(SUM(cost),0)", "cost_quality", null, from, to))
            {
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var q = reader.GetString(0);
                    var v = reader.GetDouble(1);
                    if (q == "actual") actual += v; else estimated += v;
                }
            }
            using (var cmd = Command(
                "SELECT COALESCE(SUM(cost),0) FROM turns WHERE ts >= @p0 AND ts <= @p1 AND tool = 'dsh';"))
            {
                cmd.Parameters.AddWithValue("@p0", from);
                cmd.Parameters.AddWithValue("@p1", to);
                var v = cmd.ExecuteScalar();
                deepseek = v is null or DBNull ? 0 : Convert.ToDouble(v);
            }
            return new CostBreakdownRow(estimated, actual, deepseek);
        }
    }

    public (long Ts, int WeeklyPct, long? WeeklyReset, int? FiveHourPct, long? FiveHourReset)? LatestClaudeQuotaSample()
    {
        lock (_lock)
        {
            EnsureOpen();
            using var cmd = Command(
                "SELECT ts, weekly_pct, weekly_reset, five_hour_pct, five_hour_reset FROM claude_quota_samples ORDER BY ts DESC LIMIT 1;");
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return (reader.GetInt64(0), reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4));
        }
    }

    public (long Ts, double? RollingPct, long? RollingReset, double? WeeklyPct, long? WeeklyReset, double? MonthlyPct, long? MonthlyReset)? LatestOpencodeGoSnapshot()
    {
        lock (_lock)
        {
            EnsureOpen();
            using var cmd = Command(
                "SELECT ts, rolling_pct, rolling_reset, weekly_pct, weekly_reset, monthly_pct, monthly_reset FROM opencodego_snapshots ORDER BY ts DESC LIMIT 1;");
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            double? D(int i) => reader.IsDBNull(i) ? null : reader.GetDouble(i);
            long? L(int i) => reader.IsDBNull(i) ? null : reader.GetInt64(i);
            return (reader.GetInt64(0), D(1), L(2), D(3), L(4), D(5), L(6));
        }
    }

    public (long Ts, double UsageDaily, double UsageWeekly, double UsageMonthly)? LatestOpenRouterSnapshot()
    {
        lock (_lock)
        {
            EnsureOpen();
            using var cmd = Command(
                "SELECT ts, usage_daily, usage_weekly, usage_monthly FROM openrouter_snapshots ORDER BY ts DESC LIMIT 1;");
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return (reader.GetInt64(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3));
        }
    }

    public (long Ts, string Currency, double Amount)? LatestDeepSeekBalance()
    {
        lock (_lock)
        {
            EnsureOpen();
            using var cmd = Command(
                "SELECT ts, currency, amount FROM deepseek_balance_samples ORDER BY ts DESC LIMIT 1;");
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return (reader.GetInt64(0), reader.GetString(1), reader.GetDouble(2));
        }
    }
}
