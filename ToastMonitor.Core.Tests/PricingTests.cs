using ToastMonitor.Core;

namespace ToastMonitor.Core.Tests;

public class PricingTests
{
    private static double? Cost(string? model, long input = 1_000_000, long output = 0, long cacheRead = 0, long cacheWrite = 0, bool includesCache = false)
        => Pricing.Estimate(model, input, output, cacheRead, cacheWrite, includesCache);

    [Fact]
    public void OpusRates_FollowMacTable()
    {
        // opus-4 and opus-4-1 share 15/75 in the Mac table; opus-5 is cheaper.
        Assert.Equal(15.0, Cost("claude-opus-4"));
        Assert.Equal(15.0, Cost("claude-opus-4-1"));
        Assert.Equal(5.0, Cost("claude-opus-5"));
        Assert.Equal(4.0, Cost("claude-opus-5-5"));
    }

    [Fact]
    public void HaikuDashedVariant_DoesNotFallToCatchAll()
    {
        // Without the dashed entries haiku would fall to the claude 3/15 rate.
        Assert.Equal(0.8, Cost("claude-3-5-haiku"));
        Assert.Equal(0.25, Cost("claude-3-haiku"));
    }

    [Fact]
    public void MiniVariant_BeatsBase_WhenPresent()
    {
        Assert.Equal(0.25, Cost("gpt-5.4-mini"));
        Assert.Equal(2.5, Cost("gpt-5.4"));
        Assert.Equal(0.4, Cost("gpt-4.1-mini"));
        Assert.Equal(2.0, Cost("gpt-4.1"));
    }

    [Fact]
    public void Gpt5Mini_FallsBackToGpt5Rate_AsOnMac()
    {
        // Deliberate Mac-faithful behaviour, NOT a fix: the Mac table has
        // no gpt-5-mini entry, so it matches gpt-5. See the stage-1 report.
        Assert.Equal(1.25, Cost("gpt-5-mini"));
        Assert.Equal(1.25, Cost("gpt-5-nano"));
    }

    [Fact]
    public void UnknownModel_ReturnsNull()
    {
        Assert.Null(Cost("some-unknown-model-xyz"));
        Assert.Null(Cost(null));
        Assert.Null(Cost(""));
    }

    [Fact]
    public void CacheAndOutput_ArePricedSeparately()
    {
        // claude-sonnet-4: input 3, output 15, cacheRead 0.3, cacheWrite 3.75
        var cost = Cost("claude-sonnet-4", input: 1_000_000, output: 1_000_000, cacheRead: 1_000_000, cacheWrite: 1_000_000);
        Assert.Equal(3 + 15 + 0.3 + 3.75, cost!.Value, precision: 9);
    }

    [Fact]
    public void CodexInputIncludesCache_SubtractsCacheRead()
    {
        // gpt-5: input 1.25, cacheRead 0.125. Input 2M incl. 1M cache.
        var withFlag = Cost("gpt-5", input: 2_000_000, cacheRead: 1_000_000, includesCache: true);
        var withoutFlag = Cost("gpt-5", input: 2_000_000, cacheRead: 1_000_000, includesCache: false);
        Assert.Equal(1 * 1.25 + 1 * 0.125, withFlag!.Value, precision: 9);
        Assert.Equal(2 * 1.25 + 1 * 0.125, withoutFlag!.Value, precision: 9);
    }

    [Fact]
    public void Matching_IsCaseInsensitive_AndProviderPrefixIsDropped()
    {
        Assert.Equal(0.28, Cost("DeepSeek/DeepSeek-Chat"));
        Assert.Equal(0.28, Cost("deepseek-chat"));
    }

    [Fact]
    public void ClaudeSuffixRules_LatestAndDate_Accepted_JunkRejected()
    {
        Assert.Equal(5.0, Cost("claude-opus-5-latest"));
        Assert.Equal(5.0, Cost("claude-opus-5-20250101"));
        // "claude-opus-5x" is not a valid suffix: no match at all (not even opus-4).
        Assert.Null(Cost("claude-opus-5x"));
    }
}
