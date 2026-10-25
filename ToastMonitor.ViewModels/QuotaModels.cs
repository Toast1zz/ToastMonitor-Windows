using ToastMonitor.Core;

namespace ToastMonitor.ViewModels;

/// <summary>One quota window bar (Mac QuotaWindow equivalent). UsedPercent is the consumed share; the bar grows rightward as quota is consumed (Mac rule).</summary>
public sealed record QuotaWindowRow(string Label, double UsedPercent, string? ResetText)
{
    public double UsedPercentClamped => Math.Min(Math.Max(UsedPercent, 0), 100);
    public double RemainingPercent => 100 - UsedPercentClamped;
}

/// <summary>One quota/balance row for the popover. StatusText and the bar must agree (Mac M13 lesson): when ShowRemaining is on, the headline number is remaining and the bar still shows used — the label must say which one it is.</summary>
public sealed class QuotaRowViewModel : ViewModelBase
{
    private string _statusText = "";
    private string? _error;
    private bool _connected;
    private bool _critical;

    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Group { get; init; } // "quota" | "balance"
    public List<QuotaWindowRow> Windows { get; init; } = new();

    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public string? Error { get => _error; set => Set(ref _error, value); }
    public bool Connected { get => _connected; set => Set(ref _connected, value); }
    public bool Critical { get => _critical; set => Set(ref _critical, value); }

    /// <summary>Headline remaining percent from the primary window, if any.</summary>
    public double? PrimaryRemainingPercent => Windows.Count == 0 ? null : Windows[0].RemainingPercent;

    /// <summary>Headline text honouring the show-remaining toggle. When showing used, the number is used percent; when showing remaining, it is remaining percent — never mix them.</summary>
    public static string Headline(QuotaWindowRow window, bool showRemaining, string windowLabel)
    {
        var value = showRemaining ? window.RemainingPercent : window.UsedPercentClamped;
        var word = showRemaining ? "left" : "used";
        return $"{value:F0}% of {windowLabel} {word}";
    }

    public static string ResetText(long? resetAtSeconds, long nowSeconds)
    {
        if (resetAtSeconds is null) return "";
        var diff = resetAtSeconds.Value - nowSeconds;
        if (diff <= 0) return "";
        return "resets in " + Format.Remaining(diff);
    }
}
