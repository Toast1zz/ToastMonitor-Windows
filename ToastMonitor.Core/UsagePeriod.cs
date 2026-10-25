namespace ToastMonitor.Core;

public enum UsagePeriodMode { Recent, Calendar }
public enum UsagePeriodSlot { Today, Week, Month, All }
public enum UsageWeekStart { Monday, Sunday }

public readonly record struct UsagePeriodRange(long Start, long End);

/// <summary>
/// Usage-window configuration, ported from Mac
/// Sources/ToastMonitor/Core/UsagePeriod.swift (the pure Configuration
/// part). The settings-persistence wrapper (UsagePeriodSettings, which
/// reads/writes Database settings and posts notifications) is an App-layer
/// concern and is intentionally not in Core.
/// NOTE: Mac cache keys use the IANA time-zone identifier; Windows
/// TimeZoneInfo.Local.Id is a Windows zone name (e.g. "E. Africa Standard
/// Time"). The key is only an internal day-bucket cache key, so the
/// string differs but the function is the same.
/// </summary>
public sealed record UsagePeriodConfiguration(
    UsagePeriodMode Mode = UsagePeriodMode.Recent,
    UsageWeekStart WeekStart = UsageWeekStart.Monday)
{
    public string Label(UsagePeriodSlot slot) => slot switch
    {
        UsagePeriodSlot.Today => "Today",
        UsagePeriodSlot.Week => Mode == UsagePeriodMode.Calendar ? "Weekly" : "7 Days",
        UsagePeriodSlot.Month => Mode == UsagePeriodMode.Calendar ? "Monthly" : "30 Days",
        UsagePeriodSlot.All => "All Time",
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };

    public string Title(UsagePeriodSlot slot) => slot switch
    {
        UsagePeriodSlot.Today => "Today's Usage",
        UsagePeriodSlot.Week => Mode == UsagePeriodMode.Calendar ? "Weekly Usage" : "Last 7 Days",
        UsagePeriodSlot.Month => Mode == UsagePeriodMode.Calendar ? "Monthly Usage" : "Last 30 Days",
        UsagePeriodSlot.All => "All Time",
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };

    public DateTime StartOfConfiguredWeek(DateTime localDate)
    {
        var day = localDate.Date;
        var first = WeekStart == UsageWeekStart.Sunday ? DayOfWeek.Sunday : DayOfWeek.Monday;
        var offset = ((int)day.DayOfWeek - (int)first + 7) % 7;
        return day.AddDays(-offset);
    }

    public UsagePeriodRange Range(UsagePeriodSlot slot, DateTimeOffset now)
    {
        var localNow = now.LocalDateTime;
        var today = localNow.Date;
        var nowUnix = now.ToUnixTimeSeconds();
        if (slot == UsagePeriodSlot.All) return new UsagePeriodRange(0, nowUnix);
        DateTime start = slot switch
        {
            UsagePeriodSlot.Today => today,
            UsagePeriodSlot.Week => Mode == UsagePeriodMode.Calendar
                ? StartOfConfiguredWeek(localNow)
                : today.AddDays(-6),
            UsagePeriodSlot.Month => Mode == UsagePeriodMode.Calendar
                ? new DateTime(today.Year, today.Month, 1)
                : today.AddDays(-29),
            _ => throw new ArgumentOutOfRangeException(nameof(slot)),
        };
        // Convert from the local date via the system time zone (not
        // now's offset) so a DST change between start and now does not
        // shift the calendar-day boundary.
        var startUnix = TimeZoneInfo.ConvertTimeToUtc(start, TimeZoneInfo.Local)
            .Subtract(DateTime.UnixEpoch).Ticks / TimeSpan.TicksPerSecond;
        return new UsagePeriodRange(startUnix, nowUnix);
    }

    public string CacheKey(DateTimeOffset now)
    {
        var local = now.LocalDateTime;
        return string.Join("|",
            Mode == UsagePeriodMode.Recent ? "recent" : "calendar",
            WeekStart == UsageWeekStart.Monday ? "monday" : "sunday",
            TimeZoneInfo.Local.Id,
            $"{local.Year}-{local.Month}-{local.Day}");
    }
}
