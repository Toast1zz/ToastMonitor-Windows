namespace ToastMonitor.Core;

/// <summary>
/// How long a prepaid balance lasts at the recent spending pace. Only shown
/// when it is running out ("~3.2d left"); a healthy balance says nothing.
/// Ported from Mac Sources/ToastMonitor/Core/BalanceRunway.swift.
/// </summary>
public readonly record struct BalanceSample(long Ts, double Value);

public static class BalanceRunway
{
    /// <summary>History looked at for the pace.</summary>
    public const long Lookback = 30 * 86_400;
    /// <summary>Less history than this is too noisy to extrapolate from.</summary>
    public const long MinCoverage = 86_400;
    /// <summary>Below this daily spend the balance is effectively not being used.</summary>
    public const double IdleDailySpend = 0.0005;
    /// <summary>Under this many days left, a balance needs attention.</summary>
    public const double WarnDays = 7.0;

    /// <summary>Daily spend from a cumulative usage counter: last minus first over the time between them.</summary>
    public static double? DailySpendFromCumulative(IReadOnlyList<BalanceSample> samples)
    {
        if (samples.Count == 0) return null;
        var first = samples[0];
        var last = samples[^1];
        if (last.Ts - first.Ts < MinCoverage) return null;
        return Math.Max(last.Value - first.Value, 0) / (last.Ts - first.Ts) * 86_400;
    }

    /// <summary>Daily spend from balance readings. Only drops count, so a top-up does not read as negative spend.</summary>
    public static double? DailySpendFromBalances(IReadOnlyList<BalanceSample> samples)
    {
        if (samples.Count == 0) return null;
        var first = samples[0];
        var last = samples[^1];
        if (last.Ts - first.Ts < MinCoverage) return null;
        var spent = 0.0;
        for (var i = 0; i + 1 < samples.Count; i++)
            spent += Math.Max(samples[i].Value - samples[i + 1].Value, 0);
        return spent / (last.Ts - first.Ts) * 86_400;
    }

    /// <summary>"~3.2d left" when the balance runs out within WarnDays at the recent pace; null otherwise.</summary>
    public static string? LowText(double balance, double? dailySpend)
    {
        if (dailySpend is null || !double.IsFinite(dailySpend.Value) || dailySpend.Value < IdleDailySpend
            || !double.IsFinite(balance) || balance <= 0) return null;
        var days = balance / dailySpend.Value;
        if (days >= WarnDays) return null;
        return $"~{Format.Remaining((long)(days * 86_400))} left";
    }
}
