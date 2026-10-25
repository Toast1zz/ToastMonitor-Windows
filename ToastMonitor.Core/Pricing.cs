using System.Text.RegularExpressions;

namespace ToastMonitor.Core;

/// <summary>
/// Approximate per-1M-token pricing (USD) for common models.
/// Ported verbatim (table order and matching rules) from Mac
/// Sources/ToastMonitor/Core/Pricing.swift, version 2026-10-03.
/// Unknown models fall back to null cost (tokens still shown).
/// </summary>
public sealed record ModelPrice(double Input, double Output, double CacheRead, double CacheWrite);

public static class Pricing
{
    public const string Version = "2026-10-03";

    // Order matters in the Mac original: first matching pattern wins.
    // The table is written longest/most-specific first for the families
    // that have one, and is copied here in exactly that order so that
    // behaviour matches the Mac build rather than an idealised
    // longest-prefix rule. In particular there is NO gpt-5-mini,
    // gpt-5-nano or gpt-5.1-codex-mini entry in the Mac table, so those
    // models fall through to the gpt-5 rate below (see the stage-1
    // report: this reproduces Mac behaviour and is a known overpricing
    // issue flagged by the Mac audit, not a Windows invention).
    public static readonly IReadOnlyList<(string Pattern, ModelPrice Price)> Table = new[]
    {
        ("claude-fable-5-1", new ModelPrice(10, 50, 0.25, 12.5)),
        ("claude-fable-5", new ModelPrice(10, 50, 1, 12.5)),
        ("claude-mythos-5-1", new ModelPrice(10, 50, 0.25, 12.5)),
        ("claude-mythos-5", new ModelPrice(10, 50, 1, 12.5)),
        ("claude-opus-5-5", new ModelPrice(4, 20, 0.2, 5)),
        ("claude-opus-5", new ModelPrice(5, 25, 0.5, 6.25)),
        ("claude-opus-4-8", new ModelPrice(5, 25, 0.5, 6.25)),
        ("claude-opus-4-7", new ModelPrice(5, 25, 0.5, 6.25)),
        ("claude-opus-4-6", new ModelPrice(5, 25, 0.5, 6.25)),
        ("claude-opus-4-5", new ModelPrice(5, 25, 0.5, 6.25)),
        ("claude-opus-4-1", new ModelPrice(15, 75, 1.5, 18.75)),
        ("claude-opus-4", new ModelPrice(15, 75, 1.5, 18.75)),
        ("claude-sonnet-5-5", new ModelPrice(2, 10, 0.2, 2.5)),
        ("claude-sonnet-5", new ModelPrice(2, 10, 0.2, 2.5)),
        ("claude-sonnet-4-6", new ModelPrice(3, 15, 0.3, 3.75)),
        ("claude-sonnet-4-5", new ModelPrice(3, 15, 0.3, 3.75)),
        ("claude-sonnet-4", new ModelPrice(3, 15, 0.3, 3.75)),
        ("claude-3-7-sonnet", new ModelPrice(3, 15, 0.3, 3.75)),
        ("claude-3-5-haiku", new ModelPrice(0.8, 4, 0.08, 1)),
        ("claude-3-5-opus", new ModelPrice(15, 75, 1.5, 18.75)),
        ("claude-3-5-sonnet", new ModelPrice(3, 15, 0.3, 3.75)),
        ("claude-3-haiku", new ModelPrice(0.25, 1.25, 0.03, 0.3)),
        ("claude-haiku-4-5", new ModelPrice(1, 5, 0.1, 1.25)),
        ("gpt-5.6-luna", new ModelPrice(1.25, 10, 0.125, 1.875)),
        ("gpt-5.6", new ModelPrice(1.25, 10, 0.125, 1.875)),
        ("gpt-5.4-mini", new ModelPrice(0.25, 2, 0.025, 0.375)),
        ("gpt-5.4", new ModelPrice(2.5, 15, 0.25, 1.875)),
        ("gpt-5", new ModelPrice(1.25, 10, 0.125, 1.875)),
        ("gpt-4.1-mini", new ModelPrice(0.4, 1.6, 0.05, 0.6)),
        ("gpt-4.1", new ModelPrice(2, 8, 0.5, 3)),
        ("gpt-4o-mini", new ModelPrice(0.15, 0.6, 0.075, 0.15)),
        ("gpt-4o", new ModelPrice(2.5, 10, 1.25, 2.5)),
        ("gpt-4", new ModelPrice(2.5, 10, 1.25, 2.5)),
        ("deepseek-v4-flash", new ModelPrice(0.28, 0.42, 0.028, 0.28)),
        ("deepseek-v4", new ModelPrice(0.28, 0.42, 0.028, 0.28)),
        ("deepseek-chat", new ModelPrice(0.28, 0.42, 0.028, 0.28)),
        ("deepseek-r1", new ModelPrice(0.55, 2.19, 0.055, 0.55)),
        ("deepseek-reasoner", new ModelPrice(0.56, 1.68, 0.056, 0.56)),
        ("deepseek", new ModelPrice(0.28, 0.42, 0.028, 0.28)),
        ("gemini-2.5", new ModelPrice(1.25, 10, 0.0625, 1.875)),
        ("gemini-2.0", new ModelPrice(1.25, 10, 0.0625, 1.875)),
        ("gemini", new ModelPrice(1.25, 10, 0.0625, 1.875)),
        ("qwen3", new ModelPrice(0.5, 2, 0.05, 0.75)),
        ("qwen", new ModelPrice(0.5, 2, 0.05, 0.75)),
        ("kimi", new ModelPrice(0.6, 2.5, 0.06, 0.9)),
        ("glm-4", new ModelPrice(0.1, 0.1, 0.01, 0.05)),
        ("glm", new ModelPrice(0.1, 0.1, 0.01, 0.05)),
        ("o3-mini", new ModelPrice(1.1, 4.4, 0.55, 1.65)),
        ("o3", new ModelPrice(2, 8, 0.5, 3)),
        ("o4", new ModelPrice(2, 8, 0.5, 3)),
    };

    /// <summary>Estimated cost in USD for a turn, or null when the model is unknown.</summary>
    public static double? Estimate(
        string? model, long input, long output, long cacheRead, long cacheWrite,
        bool inputIncludesCache = false)
    {
        if (string.IsNullOrEmpty(model)) return null;
        var lower = model.ToLowerInvariant();
        if (lower.Length == 0) return null;
        foreach (var (pattern, price) in Table)
        {
            if (!Matches(lower, pattern)) continue;
            var billableInput = inputIncludesCache ? Math.Max(input - cacheRead, 0) : input;
            return billableInput / 1e6 * price.Input
                + output / 1e6 * price.Output
                + cacheRead / 1e6 * price.CacheRead
                + cacheWrite / 1e6 * price.CacheWrite;
        }
        return null;
    }

    private static bool Matches(string model, string pattern)
    {
        if (!pattern.StartsWith("claude-", StringComparison.Ordinal))
            return model.Contains(pattern, StringComparison.Ordinal);
        // Claude: dots become dashes, provider prefix (before last '/') is dropped.
        var normalized = model.Replace(".", "-");
        var slash = normalized.LastIndexOf('/');
        if (slash >= 0) normalized = normalized[(slash + 1)..];
        if (!normalized.StartsWith(pattern, StringComparison.Ordinal)) return false;
        var suffix = normalized[pattern.Length..];
        return suffix.Length == 0
            || suffix == "-latest"
            || Regex.IsMatch(suffix, "^-[0-9]{8}$");
    }
}
