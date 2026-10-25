namespace ToastMonitor.Core;

/// <summary>Mac Database Claude quota-sample queries feeding ClaudeNonLocalEstimator.</summary>
public sealed partial class ToastDatabase
{
    public const long ClaudeQuotaSampleRetention = 400L * 86400;

    /// <summary>Cheap change marker for the samples table (count + newest ts).</summary>
    public string ClaudeQuotaSampleMarker()
    {
        lock (_lock)
        {
            EnsureOpen();
            using var cmd = Command("SELECT COUNT(*), COALESCE(MAX(ts),0) FROM claude_quota_samples;");
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return "";
            return reader.GetInt64(0) + ":" + reader.GetInt64(1);
        }
    }

    public List<ClaudeNonLocalEstimator.Sample> ClaudeQuotaSamples(long since)
    {
        lock (_lock)
        {
            EnsureOpen();
            using var cmd = Command(
                "SELECT ts, weekly_pct, weekly_reset, five_hour_pct, five_hour_reset " +
                "FROM claude_quota_samples WHERE ts >= @p0 ORDER BY ts;");
            cmd.Parameters.AddWithValue("@p0", since);
            var outList = new List<ClaudeNonLocalEstimator.Sample>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                outList.Add(new ClaudeNonLocalEstimator.Sample(
                    reader.GetInt64(0), (int)reader.GetInt64(1),
                    reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : (int)reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4)));
            return outList;
        }
    }

    /// <summary>
    /// Aggregates every Claude turn once into adjacent sample intervals.
    /// Caller supplies timestamp-ordered samples. Fresh tokens are spread
    /// over [turnTs, turnTs + serverLag] via a difference array; total
    /// tokens land in the interval containing the turn.
    /// </summary>
    public List<ClaudeNonLocalEstimator.LocalIntervalTotals> ClaudeLocalIntervalTotals(
        IReadOnlyList<ClaudeNonLocalEstimator.Sample> samples)
    {
        if (samples.Count <= 1) return new List<ClaudeNonLocalEstimator.LocalIntervalTotals>();
        lock (_lock)
        {
            EnsureOpen();
            var intervalCount = samples.Count - 1;
            var freshDiff = new long[intervalCount + 1];
            var totalByInterval = new long[intervalCount];
            using var cmd = Command(
                "SELECT ts, input_tokens + output_tokens + cache_write, " +
                "input_tokens + output_tokens + cache_read FROM turns " +
                "WHERE tool = 'claude' AND ts >= @p0 AND ts <= @p1 ORDER BY ts;");
            cmd.Parameters.AddWithValue("@p0", samples[0].Ts - ClaudeNonLocalEstimator.ServerLagSeconds);
            cmd.Parameters.AddWithValue("@p1", samples[intervalCount].Ts);
            int lowerBoundEnd(long ts)
            {
                var lo = 1;
                var hi = samples.Count;
                while (lo < hi)
                {
                    var mid = (lo + hi) / 2;
                    if (samples[mid].Ts < ts) lo = mid + 1; else hi = mid;
                }
                return lo - 1;
            }
            int upperBoundStart(long ts)
            {
                var lo = 0;
                var hi = intervalCount;
                while (lo < hi)
                {
                    var mid = (lo + hi) / 2;
                    if (samples[mid].Ts <= ts) lo = mid + 1; else hi = mid;
                }
                return lo;
            }
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var ts = reader.GetInt64(0);
                var fresh = Math.Max(reader.GetInt64(1), 0);
                var total = Math.Max(reader.GetInt64(2), 0);
                var firstFreshInterval = lowerBoundEnd(ts);
                var afterLastFreshInterval = upperBoundStart(ts + ClaudeNonLocalEstimator.ServerLagSeconds);
                if (firstFreshInterval < intervalCount && firstFreshInterval < afterLastFreshInterval)
                {
                    freshDiff[firstFreshInterval] += fresh;
                    freshDiff[afterLastFreshInterval] -= fresh;
                }
                var totalInterval = lowerBoundEnd(ts);
                if (totalInterval < intervalCount && samples[totalInterval].Ts < ts)
                    totalByInterval[totalInterval] += total;
            }
            var outList = new List<ClaudeNonLocalEstimator.LocalIntervalTotals>(intervalCount);
            long freshRunning = 0;
            for (var i = 0; i < intervalCount; i++)
            {
                freshRunning += freshDiff[i];
                outList.Add(new ClaudeNonLocalEstimator.LocalIntervalTotals(freshRunning, totalByInterval[i]));
            }
            return outList;
        }
    }
}
