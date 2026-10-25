namespace ToastMonitor.Core;

/// <summary>One measured call, ready for charting. Either figure may be missing (Mac PerfPoint).</summary>
public sealed record PerfPoint(long Day, string Tool, string Model, double? TtftMs, double? TokensPerSecond);

/// <summary>Mac PerfStats: calls shorter than these are dominated by write/clock granularity.</summary>
public static class PerfStats
{
    public const long MinOutputTokens = 50;
    public const long MinGenerationMs = 250;

    public static double? Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(v => v).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
