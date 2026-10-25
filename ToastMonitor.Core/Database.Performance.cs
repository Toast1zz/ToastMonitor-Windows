namespace ToastMonitor.Core;

/// <summary>Mac Database.perfPoints: chart-ready timing for the last `days` days.</summary>
public sealed partial class ToastDatabase
{
    /// <summary>Named PerfChartPoints to avoid colliding with the raw-sample PerfPoints(long) query in Queries.cs.</summary>
    public List<PerfPoint> PerfChartPoints(int days)
    {
        var sinceMs = StartOfDayMs(DateTimeOffset.Now.AddDays(-Math.Max(days - 1, 0))) ;
        var points = new List<PerfPoint>();
        lock (_lock)
        {
            EnsureOpen();
            using var cmd = Command(@"
                SELECT first_ms, tool, COALESCE(NULLIF(model, ''), '(unknown)'),
                       request_ms, last_ms, output_tokens
                FROM perf_samples
                WHERE first_ms >= @p0 AND COALESCE(model, '') <> '<synthetic>';");
            cmd.Parameters.AddWithValue("@p0", sinceMs);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var firstMs = reader.GetInt64(0);
                var tool = reader.GetString(1);
                var model = reader.GetString(2);
                var requestMs = reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3);
                var lastMs = reader.IsDBNull(4) ? (long?)null : reader.GetInt64(4);
                var output = reader.GetInt64(5);
                double? ttft = null, tps = null;
                if (requestMs is not null && firstMs > requestMs.Value && firstMs - requestMs.Value < 300_000)
                    ttft = firstMs - requestMs.Value;
                if (lastMs is not null && output >= PerfStats.MinOutputTokens &&
                    lastMs.Value - firstMs >= PerfStats.MinGenerationMs)
                    tps = output * 1000.0 / (lastMs.Value - firstMs);
                if (ttft is null && tps is null) continue;
                var day = int.Parse(DateTimeOffset.FromUnixTimeMilliseconds(firstMs).ToLocalTime()
                    .ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));
                points.Add(new PerfPoint(day, tool, model, ttft, tps));
            }
        }
        return points;
    }

    private static long StartOfDayMs(DateTimeOffset day) =>
        new DateTimeOffset(day.Year, day.Month, day.Day, 0, 0, 0, day.Offset).ToUnixTimeMilliseconds();
}
