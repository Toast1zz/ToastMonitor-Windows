namespace ToastMonitor.Core;

/// <summary>
/// Estimates Claude tokens used somewhere this machine cannot see — Cowork
/// sessions that run server-side, claude.ai chat, Claude Code on another
/// machine. Those leave no local transcript, so their tokens can never be
/// counted; only their effect on the shared quota shows.
/// Method: every successful quota fetch is a sample. A quota rise between
/// two samples is attributed to non-local use only when this machine
/// recorded essentially no Claude Code tokens in that interval, then
/// converted to tokens at the rate local activity consumes quota. An
/// interval with both local and non-local use counts as local, so the
/// result is a lower bound.
/// </summary>
public static class ClaudeNonLocalEstimator
{
    public sealed record Sample(long Ts, int WeeklyPct, long? WeeklyReset, int? FiveHourPct, long? FiveHourReset);

    /// <summary>Aggregated local activity for one adjacent sample interval.</summary>
    public sealed record LocalIntervalTotals(long FreshTokens, long TotalTokens);

    /// <summary>Tokens attributed to non-local use, placed at the middle of the idle interval they were inferred from.</summary>
    public sealed record TokenEvent(long Ts, long Tokens);

    public sealed record TokenEstimate(double TokensPerPoint, IReadOnlyList<TokenEvent> Events)
    {
        public long Tokens(long start, long end) =>
            Events.Where(e => e.Ts >= start && e.Ts < end).Sum(e => e.Tokens);
    }

    /// <summary>Label of the synthetic per-tool row carrying the estimate.</summary>
    public const string EstimateToolLabel = "Cowork & web";

    /// <summary>"Basically no local activity": below one or two ordinary turns.</summary>
    public const long IdleTokenThreshold = 5_000;

    /// <summary>The usage endpoint aggregates with a delay, so local turns shortly *before* an interval can still move the quota inside it.</summary>
    public const long ServerLagSeconds = 10 * 60;

    /// <summary>The collector ingests transcripts on a short delay; an interval that ended moments ago may not have its local turns in the store yet.</summary>
    public const long IngestMarginSeconds = 3 * 60;

    /// <summary>Two reset timestamps within this distance are the same window (the endpoint's reset time can jitter).</summary>
    public const long SameWindowTolerance = 3600;

    /// <summary>Quota points of local activity needed before a tokens-per-point rate is trusted.</summary>
    public const int MinCalibrationPoints = 5;

    /// <summary>
    /// Converts non-local quota rises into tokens using per-interval local
    /// totals. The 5h window is preferred because its percentage moves ~20x
    /// finer than the weekly one; weekly data is the fallback.
    /// </summary>
    public static TokenEstimate? TokenEstimateFor(IReadOnlyList<Sample> samples,
        IReadOnlyList<LocalIntervalTotals> totals, long now)
    {
        var series = new (long Length, Func<Sample, (int Pct, long Reset)?>)[]
        {
            (5 * 3600, s => s.FiveHourPct is not null && s.FiveHourReset is not null
                ? (s.FiveHourPct.Value, s.FiveHourReset.Value) : null),
            (7 * 86400, s => s.WeeklyReset is not null ? (s.WeeklyPct, s.WeeklyReset.Value) : null),
        };
        foreach (var (length, extract) in series)
        {
            var estimate = TokenEstimateFor(samples, totals, now, length, extract);
            if (estimate is not null) return estimate;
        }
        return null;
    }

    private static TokenEstimate? TokenEstimateFor(IReadOnlyList<Sample> samples,
        IReadOnlyList<LocalIntervalTotals> totals, long now, long windowLength,
        Func<Sample, (int Pct, long Reset)?> series)
    {
        if (totals.Count != Math.Max(samples.Count - 1, 0)) return null;
        long calibrationTokens = 0;
        var calibrationPoints = 0;
        var idleRises = new List<(long Mid, int Points)>();
        for (var index = 0; index < totals.Count; index++)
        {
            var a = samples[index];
            var b = samples[index + 1];
            var intervalTotals = totals[index];
            if (b.Ts > now - IngestMarginSeconds) continue;
            var wa = series(a);
            var wb = series(b);
            if (wa is null || wb is null) continue;
            var idle = intervalTotals.FreshTokens < IdleTokenThreshold;
            int delta;
            if (Math.Abs(wa.Value.Reset - wb.Value.Reset) <= SameWindowTolerance)
            {
                delta = wb.Value.Pct - wa.Value.Pct;
                if (delta < 0) continue;
            }
            else
            {
                // A new window opened inside an idle interval: everything it
                // shows was used in the interval (the old window's tail is
                // unknown, so this stays a lower bound). Mixed intervals are
                // skipped — their local tokens straddle two windows.
                var opened = wb.Value.Reset - windowLength;
                if (!idle || opened < a.Ts || opened > b.Ts ||
                    opened < wa.Value.Reset - SameWindowTolerance) continue;
                delta = wb.Value.Pct;
            }
            if (idle)
            {
                if (delta > 0) idleRises.Add((a.Ts + (b.Ts - a.Ts) / 2, delta));
            }
            else
            {
                calibrationTokens += intervalTotals.TotalTokens;
                calibrationPoints += delta;
            }
        }
        if (calibrationPoints < MinCalibrationPoints || calibrationTokens <= 0) return null;
        var rate = (double)calibrationTokens / calibrationPoints;
        return new TokenEstimate(rate,
            idleRises.Select(r => new TokenEvent(r.Mid, (long)Math.Round(r.Points * rate))).ToList());
    }
}
