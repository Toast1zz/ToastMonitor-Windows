using System.Globalization;

namespace ToastMonitor.Core;

/// <summary>
/// Compact human formatting for tokens and money, ported from Mac
/// Sources/ToastMonitor/Core/Format.swift. Money/full use en-US grouping
/// (the Mac code pins en_US_POSIX, which agrees with en-US for these).
/// </summary>
public static class Format
{
    private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Exactly three significant digits with the unit: 1.23k, 12.3M, 190M,
    /// 4.15B. Trailing zeros stay (1.00M, 20.0M). Rounding that reaches
    /// 1000 moves to the next unit (999.6M → 1.00B). Below 1,000 the raw
    /// count is shown.
    /// </summary>
    public static string Compact(long n)
    {
        ulong magnitude = n == long.MinValue ? (ulong)long.MaxValue + 1UL : (ulong)Math.Abs(n);
        if (magnitude < 1000) return n.ToString(Inv);
        var units = new[] { ("k", 1e3), ("M", 1e6), ("B", 1e9), ("T", 1e12) };
        var index = 0;
        for (var i = units.Length - 1; i >= 0; i--)
        {
            if (magnitude >= units[i].Item2) { index = i; break; }
        }
        var sign = n < 0 ? "-" : "";
        while (true)
        {
            var value = magnitude / units[index].Item2;
            foreach (var decimals in new[] { 2, 1, 0 })
            {
                var factor = Math.Pow(10, decimals);
                var rounded = Math.Round(value * factor, MidpointRounding.AwayFromZero) / factor;
                if (rounded < Math.Pow(10, 3 - decimals))
                    return sign + rounded.ToString("F" + decimals, Inv) + units[index].Item1;
            }
            if (index + 1 >= units.Length)
                return sign + Math.Round(value, MidpointRounding.AwayFromZero).ToString("F0", Inv) + units[index].Item1;
            index++;
        }
    }

    /// <summary>Full number with grouping: 1,234,567.</summary>
    public static string Full(long n) => n.ToString("N0", EnUs);

    private static string MoneyWholeString(double v) => v.ToString("#,##0.##", EnUs);

    /// <summary>
    /// Money in USD with adaptive precision. Non-finite values render as
    /// $0.00 (never "nan"); negatives format normally with a leading "-".
    /// </summary>
    public static string Money(double v)
    {
        if (!double.IsFinite(v)) return "$0.00";
        if (v == 0) return "$0.00";
        if (v < 0) return "-" + Money(-v);
        if (v >= 100) return "$" + MoneyWholeString(v);
        if (v >= 1) return "$" + v.ToString("F2", Inv);
        if (v >= 0.01) return "$" + v.ToString("F3", Inv);
        return "$" + v.ToString("F4", Inv);
    }

    public static string MoneyShort(double v)
    {
        if (v == 0) return "$0.00";
        if (v >= 1000) return "$" + (v / 1000).ToString("F1", Inv) + "k";
        if (v >= 1) return "$" + v.ToString("F2", Inv);
        if (v >= 0.01) return "$" + v.ToString("F2", Inv);
        return "$" + v.ToString("F3", Inv);
    }

    /// <summary>yyyymmdd key -> "2026-08-05".</summary>
    public static string DayKeyString(long key)
    {
        var y = (int)(key / 10_000);
        var m = (int)((key / 100) % 100);
        var d = (int)(key % 100);
        return $"{y:D4}-{m:D2}-{d:D2}";
    }

    /// <summary>yyyymmdd key -> English short date "Aug 5", regardless of system locale.</summary>
    public static string ShortDayKey(long key)
    {
        var y = (int)(key / 10_000);
        var m = (int)((key / 100) % 100);
        var d = (int)(key % 100);
        try
        {
            var date = new DateTime(y, m, d);
            return date.ToString("MMM d", EnUs);
        }
        catch
        {
            return DayKeyString(key);
        }
    }

    /// <summary>"2026-08-05 14:23" in local time (Mac format is "MM-dd HH:mm"; kept as-is).</summary>
    public static string DateTime(long ts)
    {
        var local = DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime;
        return local.ToString("MM-dd HH:mm", Inv);
    }

    public static string Day(long ts)
    {
        var local = DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime;
        return local.ToString("yyyy-MM-dd", Inv);
    }

    /// <summary>Count abbreviation (1.2k / 34), same units as compact.</summary>
    public static string Count(long n) => Compact(n);

    /// <summary>Relative remaining time: "2.1h" / "45m" / "3d".</summary>
    public static string Remaining(long seconds)
    {
        var s = Math.Max(0, seconds);
        if (s >= 86400) return (s / 86400.0).ToString("F1", Inv) + "d";
        if (s >= 3600) return (s / 3600.0).ToString("F1", Inv) + "h";
        if (s >= 60) return (s / 60) + "m";
        return s + "s";
    }

    /// <summary>
    /// File size, human units. Approximation of Mac ByteCountFormatter(.file):
    /// whole values print without a decimal ("128 KB"), others with one.
    /// Exact ByteCountFormatter output (1000- vs 1024-based thresholds and
    /// localisation) is not reproduced byte-for-byte; see the stage-1 report.
    /// </summary>
    public static string Bytes(long n)
    {
        if (n < 1024) return n + " bytes";
        string[] units = { "bytes", "KB", "MB", "GB", "TB" };
        double v = n;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        var text = Math.Abs(v - Math.Round(v)) < 1e-9
            ? Math.Round(v).ToString("F0", Inv)
            : v.ToString("F1", Inv);
        return text + " " + units[i];
    }

    /// <summary>Reset countdown: "3d 2h" / "1h 5m" / "in 45m".</summary>
    public static string Countdown(long secs)
    {
        if (secs <= 0) return "resetting soon";
        var d = secs / 86400;
        var h = (secs % 86400) / 3600;
        var m = (secs % 3600) / 60;
        if (d > 0) return $"{d}d {h}h";
        if (h > 0) return $"{h}h {m}m";
        return $"in {m}m";
    }
}
