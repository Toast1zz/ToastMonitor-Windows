namespace ToastMonitor.Core;

/// <summary>
/// The AI tools ToastMonitor tracks, plus the cloud quota panel.
/// Ported from Mac Sources/ToastMonitor/Core/Tool.swift, minus the
/// SwiftUI-only parts (SF Symbol names and brand colours are UI concerns
/// and live in the App layer on Windows, not in Core).
/// </summary>
public enum ToolKind
{
    Claude,
    Codex,
    Opencode,
    Hermes,
    Omp,
    Dsh,
    Openrouter,
}

public static class ToolKindExtensions
{
    public static string RawValue(this ToolKind tool) => tool switch
    {
        ToolKind.Claude => "claude",
        ToolKind.Codex => "codex",
        ToolKind.Opencode => "opencode",
        ToolKind.Hermes => "hermes",
        ToolKind.Omp => "omp",
        ToolKind.Dsh => "dsh",
        ToolKind.Openrouter => "openrouter",
        _ => throw new ArgumentOutOfRangeException(nameof(tool)),
    };

    public static string DisplayName(this ToolKind tool) => tool switch
    {
        ToolKind.Claude => "Claude Code",
        ToolKind.Codex => "Codex",
        ToolKind.Opencode => "OpenCode",
        ToolKind.Hermes => "Hermes",
        ToolKind.Omp => "Oh My Pi",
        ToolKind.Dsh => "DeepSeek Harness",
        ToolKind.Openrouter => "OpenRouter",
        _ => throw new ArgumentOutOfRangeException(nameof(tool)),
    };

    /// <summary>Cache reads are real (cheaper) tokens and count toward the total, except for Codex, whose input already contains them.</summary>
    public static bool CacheIncludedInInput(this ToolKind tool) => tool == ToolKind.Codex;

    public static long TotalTokens(this ToolKind tool, long input, long output, long cacheRead) =>
        input + output + (tool.CacheIncludedInInput() ? 0 : cacheRead);
}

/// <summary>
/// One LLM turn (one model call) extracted from a tool's logs.
/// Token/cost clamping mirrors the Mac initializer: hostile/malformed logs
/// must not overflow Int64 sums or poison aggregates.
/// </summary>
public sealed record TurnRecord
{
    public const long MaxTokens = 9_000_000_000_000_000L;
    public const double MaxCost = 1_000_000_000;

    public ToolKind Tool { get; init; }
    public string SessionId { get; init; }
    public string? Project { get; init; }
    public string? Model { get; init; }
    public long Ts { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    /// <summary>Reasoning is recorded separately. Providers commonly include it in output_tokens, so aggregates must not add it a second time.</summary>
    public long ReasoningTokens { get; init; }
    public long CacheRead { get; init; }
    public long CacheWrite { get; init; }
    public double Cost { get; init; }
    public string? Provider { get; init; }
    /// <summary>Stable source namespace ("local", or a hash of the remote feed URL).</summary>
    public string SourceInstance { get; init; }
    /// <summary>Price-table revision used when CostQuality == "estimated".</summary>
    public string? PricingVersion { get; init; }
    /// <summary>Stable upstream or canonical content identity.</summary>
    public string? EventId { get; init; }
    /// <summary>'actual' | 'estimated' | 'unknown'.</summary>
    public string CostQuality { get; init; }

    public TurnRecord(
        ToolKind tool, string sessionId, string? project, string? model, long ts,
        long inputTokens, long outputTokens, long reasoningTokens, long cacheRead, long cacheWrite,
        double cost, string? provider = null, string sourceInstance = "local",
        string? pricingVersion = Pricing.Version, string? eventId = null,
        string costQuality = "estimated")
    {
        Tool = tool;
        SessionId = sessionId;
        Project = project;
        Model = model;
        Ts = ts;
        InputTokens = Clamp(inputTokens);
        OutputTokens = Clamp(outputTokens);
        ReasoningTokens = Clamp(reasoningTokens);
        CacheRead = Clamp(cacheRead);
        CacheWrite = Clamp(cacheWrite);
        Cost = double.IsFinite(cost) ? Math.Min(Math.Max(cost, 0), MaxCost) : 0;
        Provider = provider;
        SourceInstance = sourceInstance;
        CostQuality = costQuality;
        PricingVersion = costQuality == "estimated" ? pricingVersion : null;
        EventId = eventId;
    }

    private static long Clamp(long v) => Math.Min(Math.Max(v, 0), MaxTokens);
}

/// <summary>Session-level info upserted from sources that expose it.</summary>
public sealed record SessionInfo(
    ToolKind Tool, string SessionId, string? Title, string? Project, string? Model,
    long Created, long Updated);
