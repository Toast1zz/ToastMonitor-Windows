using ToastMonitor.Core;

namespace ToastMonitor.Core.Tests;

public class FormatTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1234, "1.23k")]
    [InlineData(12345, "12.3k")]
    [InlineData(1234567, "1.23M")]
    [InlineData(999600000, "1.00B")]
    [InlineData(-1234, "-1.23k")]
    public void Compact_Cases(long n, string expected) => Assert.Equal(expected, Format.Compact(n));

    [Theory]
    [InlineData(1234567, "1,234,567")]
    public void Full_Groups(long n, string expected) => Assert.Equal(expected, Format.Full(n));

    [Theory]
    [InlineData(0, "$0.00")]
    [InlineData(173564, "$173,564")]
    [InlineData(123.45, "$123.45")]
    [InlineData(0.5, "$0.500")]
    [InlineData(0.005, "$0.0050")]
    [InlineData(-5, "-$5.00")]
    public void Money_Cases(double v, string expected) => Assert.Equal(expected, Format.Money(v));

    [Fact]
    public void Money_NonFinite_IsZero() => Assert.Equal("$0.00", Format.Money(double.NaN));

    [Theory]
    [InlineData(20260805, "2026-08-05")]
    [InlineData(20260105, "2026-01-05")]
    public void DayKeyString_Cases(long key, string expected) => Assert.Equal(expected, Format.DayKeyString(key));

    [Fact]
    public void ShortDayKey_IsEnglish() => Assert.Equal("Aug 5", Format.ShortDayKey(20260805));

    [Theory]
    [InlineData(45, "45s")]
    [InlineData(2700, "45m")]
    [InlineData(7560, "2.1h")]
    [InlineData(259200, "3.0d")]
    public void Remaining_Cases(long s, string expected) => Assert.Equal(expected, Format.Remaining(s));

    [Theory]
    [InlineData(0, "resetting soon")]
    [InlineData(2700, "in 45m")]
    [InlineData(3900, "1h 5m")]
    [InlineData(280800, "3d 6h")]
    public void Countdown_Cases(long s, string expected) => Assert.Equal(expected, Format.Countdown(s));
}

public class ModelNameTests
{
    [Theory]
    [InlineData("claude-opus-5-5", "Opus 5.5")]
    [InlineData("claude-3-5-sonnet", "Sonnet 3.5")]
    [InlineData("gpt-5.6", "GPT-5.6")]
    [InlineData("deepseek/deepseek-v4-flash", "DeepSeek V4 Flash")]
    [InlineData("qwen3.7-plus", "Qwen 3.7 Plus")]
    [InlineData("claude-opus-5-5-20250101", "Opus 5.5")]
    public void Display_Cases(string raw, string expected) => Assert.Equal(expected, ModelName.Display(raw));
}

public class BalanceRunwayTests
{
    [Fact]
    public void CumulativeDailySpend_UsesFirstAndLast()
    {
        // A cumulative usage counter rises: 10 → 20 over 2 days = 5/day.
        var samples = new[] { new BalanceSample(0, 10), new BalanceSample(86400 * 2, 20) };
        Assert.Equal(5.0, BalanceRunway.DailySpendFromCumulative(samples)!.Value, precision: 9);
    }

    [Fact]
    public void CumulativeDailySpend_TooShort_ReturnsNull()
    {
        var samples = new[] { new BalanceSample(0, 100), new BalanceSample(3600, 90) };
        Assert.Null(BalanceRunway.DailySpendFromCumulative(samples));
    }

    [Fact]
    public void BalanceDailySpend_TopUpsDoNotCountAsNegative()
    {
        var samples = new[] { new BalanceSample(0, 100), new BalanceSample(86400, 110), new BalanceSample(86400 * 2, 90) };
        // Drops: 0 (top-up) + 20 = 20 over 2 days = 10/day.
        Assert.Equal(10.0, BalanceRunway.DailySpendFromBalances(samples)!.Value, precision: 9);
    }

    [Fact]
    public void LowText_OnlyWhenRunningOutSoon()
    {
        Assert.Equal("~3.0d left", BalanceRunway.LowText(30, 10));
        Assert.Null(BalanceRunway.LowText(100, 10));
        Assert.Null(BalanceRunway.LowText(30, null));
        Assert.Null(BalanceRunway.LowText(30, 0.0001));
    }
}

public class EventIdentityTests
{
    [Fact]
    public void Digest_KnownVector()
    {
        // SHA-256 of "a" + U+001F + "b", verified with Python hashlib.
        Assert.Equal("f04cdced9736a69da6103f08a4daaf8c485dd481217d218a1b4993c8c3968e13",
            EventIdentity.Digest(new[] { "a", "b" }));
    }

    [Fact]
    public void Claude_MessageIdWins_FallbackIsStableAndOffsetAnchored()
    {
        Assert.Equal("claude:s1:msg-1", EventIdentity.Claude("s1", "msg-1", null, 10, "m", "{}", "{}"));
        var a = EventIdentity.Claude("s1", null, null, 10, "m", "{}", "{}");
        var b = EventIdentity.Claude("s1", null, null, 10, "m", "{}", "{}");
        var c = EventIdentity.Claude("s1", null, null, 20, "m", "{}", "{}");
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.StartsWith("claude:s1:fallback:", a);
    }

    [Fact]
    public void Codex_IncludesOffset()
    {
        var a = EventIdentity.Codex("s1", 1000, "gpt-5", "{}", 10);
        var b = EventIdentity.Codex("s1", 1000, "gpt-5", "{}", 20);
        Assert.NotEqual(a, b);
        Assert.StartsWith("codex:s1:", a);
    }

    [Fact]
    public void CanonicalJson_SortsKeys()
    {
        var json = System.Text.Json.JsonDocument.Parse("{\"b\":1,\"a\":{\"d\":2,\"c\":3}}").RootElement;
        Assert.Equal("{\"a\":{\"c\":3,\"d\":2},\"b\":1}", EventIdentity.CanonicalJson(json));
    }
}

public class ToolKindTests
{
    [Fact]
    public void TotalTokens_CodexExcludesCacheRead_OthersInclude()
    {
        Assert.Equal(300, ToolKind.Codex.TotalTokens(100, 200, 50));
        Assert.Equal(350, ToolKind.Claude.TotalTokens(100, 200, 50));
    }

    [Fact]
    public void TurnRecord_ClampsTokensAndCost()
    {
        var t = new TurnRecord(ToolKind.Claude, "s", null, "m", 1,
            -5, TurnRecord.MaxTokens + 100, 0, 0, 0, double.NaN);
        Assert.Equal(0, t.InputTokens);
        Assert.Equal(TurnRecord.MaxTokens, t.OutputTokens);
        Assert.Equal(0, t.Cost);
    }

    [Fact]
    public void TurnRecord_PricingVersion_NullWhenNotEstimated()
    {
        var t = new TurnRecord(ToolKind.Claude, "s", null, "m", 1,
            0, 0, 0, 0, 0, 1.0, costQuality: "actual");
        Assert.Null(t.PricingVersion);
        var e = new TurnRecord(ToolKind.Claude, "s", null, "m", 1,
            0, 0, 0, 0, 0, 1.0);
        Assert.Equal(Pricing.Version, e.PricingVersion);
    }
}
