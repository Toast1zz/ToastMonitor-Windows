namespace ToastMonitor.Core;

/// <summary>
/// Cycle math + value attribution for user-recorded subscriptions.
/// Ported from Mac Sources/ToastMonitor/Core/SubscriptionMath.swift.
/// CycleInfo and Amortized are ported in full. The plan-specific
/// CycleValue/Forecast entry points in the Mac code read live client
/// state (OpenCode Go quota %, OpenRouter balance, Database aggregates);
/// those are not pure Core, so stage 1 ports their arithmetic as
/// <see cref="Extrapolate"/> and leaves the live-state wiring to the
/// collector/App stage (see the stage-1 report).
/// </summary>
public static class SubscriptionMath
{
    public sealed record CycleInfoResult(long Start, long End, int DayOfCycle, int TotalDays, double Progress);

    /// <param name="start">Unix seconds of the subscription start.</param>
    /// <param name="end">Unix seconds of the subscription end, or 0 for active.</param>
    /// <param name="cycle">"yearly" | "monthly" | "weekly"; anything else returns null.</param>
    public static CycleInfoResult? CycleInfo(long start, long end, string cycle, long nowUnix)
    {
        if (start <= 0) return null;
        if (cycle != "yearly" && cycle != "monthly" && cycle != "weekly") return null;
        var startDate = DateTimeOffset.FromUnixTimeSeconds(start).LocalDateTime;
        DateTime? endDate = end > 0 ? DateTimeOffset.FromUnixTimeSeconds(end).LocalDateTime : null;
        var now = DateTimeOffset.FromUnixTimeSeconds(nowUnix).LocalDateTime;
        if (endDate is not null && now >= endDate.Value) return null; // ended subscription: no active cycle

        // Weekly advances by adding 7 days, not week-of-year: week-of-year
        // math misbehaves across year boundaries (and DST shifts).
        // Window boundaries keep the start time-of-day (as in the Mac
        // code); only the day counts are calendar-day counts.
        var windowStart = startDate;
        DateTime windowEnd;
        if (cycle == "yearly") windowEnd = windowStart.AddYears(1);
        else if (cycle == "weekly") windowEnd = windowStart.AddDays(7);
        else windowEnd = windowStart.AddMonths(1);

        var guardCount = 0;
        while (windowEnd <= now && guardCount < 1200)
        {
            var next = cycle == "yearly" ? windowEnd.AddYears(1)
                : cycle == "weekly" ? windowEnd.AddDays(7)
                : windowEnd.AddMonths(1);
            windowStart = windowEnd;
            windowEnd = next;
            guardCount++;
        }
        // An end date inside the window truncates it.
        if (endDate is not null && endDate.Value < windowEnd) windowEnd = endDate.Value;

        // Calendar-day counts (DateOnly day numbers), matching Mac
        // Calendar.dateComponents(.day) rather than 24-hour periods.
        var windowStartDay = DateOnly.FromDateTime(windowStart);
        var windowEndDay = DateOnly.FromDateTime(windowEnd);
        var nowDay = DateOnly.FromDateTime(now);
        var totalDays = Math.Max(windowEndDay.DayNumber - windowStartDay.DayNumber, 1);
        var elapsed = Math.Max(nowDay.DayNumber - windowStartDay.DayNumber, 0);
        var dayOfCycle = Math.Min(elapsed + 1, totalDays);
        var progress = Math.Min(Math.Max((double)elapsed / totalDays, 0), 1);
        var startUnix = TimeZoneInfo.ConvertTimeToUtc(windowStart, TimeZoneInfo.Local)
            .Subtract(DateTime.UnixEpoch).Ticks / TimeSpan.TicksPerSecond;
        var endUnix = TimeZoneInfo.ConvertTimeToUtc(windowEnd, TimeZoneInfo.Local)
            .Subtract(DateTime.UnixEpoch).Ticks / TimeSpan.TicksPerSecond;
        return new CycleInfoResult(startUnix, endUnix, dayOfCycle, totalDays, progress);
    }

    public static string DateStr(DateTimeOffset d) => d.LocalDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Subscription cost amortized as overlap days × daily price.
    /// Ended subscriptions count for the days they covered.
    /// </summary>
    public static double Amortized(int days, IReadOnlyList<Subscription> subscriptions, long nowUnix)
    {
        var total = 0.0;
        var now = nowUnix;
        var windowStart = now - (long)days * 86400;
        foreach (var sub in subscriptions)
        {
            var start = sub.StartDate;
            var end = sub.EndDate > 0 ? sub.EndDate : now;
            double daily;
            var info = CycleInfo(sub.StartDate, sub.EndDate, sub.Cycle, now);
            if (info is not null)
            {
                daily = sub.Price / info.TotalDays;
            }
            else if (sub.EndDate > 0)
            {
                var spanDays = Math.Max((end - start) / 86400.0, 1.0);
                daily = sub.Price / spanDays;
            }
            else
            {
                daily = sub.Price / (sub.Cycle == "yearly" ? 365 : 30);
            }
            var overlap = Math.Min(end, now) - Math.Max(start, windowStart);
            if (overlap > 0) total += overlap / 86400.0 * daily;
        }
        return total;
    }

    /// <summary>
    /// Pure extrapolation used by the Mac forecast: daily rate = used ÷
    /// days elapsed; projected = used + rate × days left; exhaust is the
    /// date the limit is crossed, or null when it is not crossed.
    /// Returns (projectedEnd, exhaustUnix).
    /// </summary>
    public static (double ProjectedEnd, long? ExhaustUnix) Extrapolate(
        double used, int daysElapsed, int daysLeft, double limit, long nowUnix, double minRate = 0)
    {
        var elapsed = Math.Max(daysElapsed, 1);
        var rate = used / elapsed;
        var projected = used + rate * Math.Max(daysLeft, 0);
        long? exhaust = null;
        if (rate > minRate && projected > limit)
        {
            var daysToExhaust = (limit - used) / rate;
            exhaust = nowUnix + (long)Math.Ceiling(daysToExhaust) * 86400;
        }
        return (projected, exhaust);
    }
}
