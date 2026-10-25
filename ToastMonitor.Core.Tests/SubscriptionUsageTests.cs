using ToastMonitor.Core;

namespace ToastMonitor.Core.Tests;

public class SubscriptionMathTests
{
    // Noon UTC timestamps: the local calendar date is the same in every
    // practical time zone, so these expectations do not depend on the
    // VM's time zone. January has no DST transition in the zones that
    // matter, so day counts are calendar days.
    private static long Unix(int y, int m, int d) =>
        new DateTimeOffset(y, m, d, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    [Fact]
    public void MonthlyCycle_DayAndProgress()
    {
        var info = SubscriptionMath.CycleInfo(Unix(2026, 1, 15), 0, "monthly", Unix(2026, 1, 20))!;
        Assert.Equal(31, info.TotalDays);
        Assert.Equal(6, info.DayOfCycle);
        Assert.Equal(5.0 / 31, info.Progress, precision: 9);
    }

    [Fact]
    public void WeeklyCycle_AdvancesBySevenDays()
    {
        var info = SubscriptionMath.CycleInfo(Unix(2026, 1, 5), 0, "weekly", Unix(2026, 1, 20))!;
        Assert.Equal(7, info.TotalDays);
        // Window Jan 19–26, elapsed Jan 19→20 = 1, day 2.
        Assert.Equal(2, info.DayOfCycle);
    }

    [Fact]
    public void UnknownCycle_OrZeroStart_ReturnsNull()
    {
        Assert.Null(SubscriptionMath.CycleInfo(Unix(2026, 1, 15), 0, "daily", Unix(2026, 1, 20)));
        Assert.Null(SubscriptionMath.CycleInfo(0, 0, "monthly", Unix(2026, 1, 20)));
    }

    [Fact]
    public void EndedSubscription_ReturnsNull()
    {
        Assert.Null(SubscriptionMath.CycleInfo(Unix(2026, 1, 15), Unix(2026, 1, 18), "monthly", Unix(2026, 1, 20)));
    }

    [Fact]
    public void Amortized_UsesCurrentCycleDailyRate_OverWholeOverlap()
    {
        var subs = new[]
        {
            new Subscription(0, "Pro", "claude", Unix(2026, 1, 1), 0, "monthly", 30, "USD"),
        };
        // Mac formula: daily = price / CURRENT cycle totalDays (Mar 1–Apr 1
        // = 31), overlap = Jan 1–Mar 1 = 59 days. So 59 * 30/31, not the
        // intuitive "two cycles = 60". This is Mac behaviour, faithfully
        // reproduced, not an exact per-cycle accounting.
        var value = SubscriptionMath.Amortized(60, subs, Unix(2026, 3, 1));
        Assert.Equal(59 * 30.0 / 31, value, precision: 9);
    }

    [Fact]
    public void Extrapolate_ProjectedAndExhaust()
    {
        var (projected, exhaust) = SubscriptionMath.Extrapolate(20, 10, 10, 100, Unix(2026, 1, 20));
        Assert.Equal(40, projected, precision: 9);
        Assert.Null(exhaust);
        var (projected2, exhaust2) = SubscriptionMath.Extrapolate(90, 10, 10, 100, Unix(2026, 1, 20));
        Assert.Equal(180, projected2, precision: 9);
        Assert.NotNull(exhaust2);
        // (100-90)/9 = 1.11 days → ceil = 2 days after now.
        Assert.Equal(Unix(2026, 1, 22), exhaust2!.Value);
    }
}

public class UsagePeriodTests
{
    private static DateTimeOffset Noon(int y, int m, int d) =>
        new DateTimeOffset(y, m, d, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Recent_WeekIsTodayMinusSix_MonthIsMinus29()
    {
        var cfg = new UsagePeriodConfiguration(UsagePeriodMode.Recent, UsageWeekStart.Monday);
        var now = Noon(2026, 10, 24);
        var week = cfg.Range(UsagePeriodSlot.Week, now);
        var month = cfg.Range(UsagePeriodSlot.Month, now);
        Assert.Equal(Noon(2026, 10, 18).LocalDateTime.Date, DateTimeOffset.FromUnixTimeSeconds(week.Start).LocalDateTime.Date);
        Assert.Equal(Noon(2026, 9, 25).LocalDateTime.Date, DateTimeOffset.FromUnixTimeSeconds(month.Start).LocalDateTime.Date);
        Assert.Equal(now.ToUnixTimeSeconds(), week.End);
    }

    [Fact]
    public void Calendar_WeekStartsMonday_ByDefault()
    {
        // 2026-10-24 is a Saturday; the Monday-start week begins 2026-10-19.
        var cfg = new UsagePeriodConfiguration(UsagePeriodMode.Calendar, UsageWeekStart.Monday);
        var range = cfg.Range(UsagePeriodSlot.Week, Noon(2026, 10, 24));
        Assert.Equal(new DateTime(2026, 10, 19), DateTimeOffset.FromUnixTimeSeconds(range.Start).LocalDateTime.Date);
    }

    [Fact]
    public void Calendar_WeekStartsSunday_WhenConfigured()
    {
        var cfg = new UsagePeriodConfiguration(UsagePeriodMode.Calendar, UsageWeekStart.Sunday);
        var range = cfg.Range(UsagePeriodSlot.Week, Noon(2026, 10, 24));
        Assert.Equal(new DateTime(2026, 10, 18), DateTimeOffset.FromUnixTimeSeconds(range.Start).LocalDateTime.Date);
    }

    [Fact]
    public void Calendar_MonthStartsFirst_AllStartsZero()
    {
        var cfg = new UsagePeriodConfiguration(UsagePeriodMode.Calendar, UsageWeekStart.Monday);
        var month = cfg.Range(UsagePeriodSlot.Month, Noon(2026, 10, 24));
        Assert.Equal(new DateTime(2026, 10, 1), DateTimeOffset.FromUnixTimeSeconds(month.Start).LocalDateTime.Date);
        var all = cfg.Range(UsagePeriodSlot.All, Noon(2026, 10, 24));
        Assert.Equal(0, all.Start);
    }

    [Fact]
    public void Labels_FollowMode()
    {
        Assert.Equal("7 Days", new UsagePeriodConfiguration(UsagePeriodMode.Recent).Label(UsagePeriodSlot.Week));
        Assert.Equal("Weekly", new UsagePeriodConfiguration(UsagePeriodMode.Calendar).Label(UsagePeriodSlot.Week));
        Assert.Equal("30 Days", new UsagePeriodConfiguration(UsagePeriodMode.Recent).Label(UsagePeriodSlot.Month));
        Assert.Equal("Monthly", new UsagePeriodConfiguration(UsagePeriodMode.Calendar).Label(UsagePeriodSlot.Month));
    }
}
