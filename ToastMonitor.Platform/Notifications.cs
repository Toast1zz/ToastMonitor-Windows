using ToastMonitor.Collect;

namespace ToastMonitor.Platform;

/// <summary>
/// Quota-alert delivery (Mac UNUserNotificationCenter equivalent).
/// Policy lives in Collect (QuotaAlertPolicy/QuotaAlertManager): Low on
/// entering low, Recovered on leaving, still-low does not repeat, a new
/// reset window &gt;300s clears the badge. This layer only turns a fired
/// transition into a native tray balloon via NotifyIcon.ShowBalloonTip —
/// no WinRT toast dependency. The balloon sink is injectable so tests
/// never touch a real tray icon.
/// </summary>
public sealed class TrayAlertNotifier
{
    private readonly Action<string, string, QuotaAlertPolicy.Transition> _balloon;
    private readonly QuotaAlertManager _manager;

    /// <param name="balloon">Sink receiving (title, body, transition); App wires NotifyIcon.ShowBalloonTip here.</param>
    public TrayAlertNotifier(Action<string, string, QuotaAlertPolicy.Transition>? balloon = null, QuotaAlertManager? manager = null)
    {
        _balloon = balloon ?? ((_, _, _) => { });
        _manager = manager ?? new QuotaAlertManager();
    }

    /// <summary>Feed one quota refresh; returns the transition fired, if any.</summary>
    public QuotaAlertPolicy.Transition? Evaluate(string key, double remainingPercent, long? resetAtSeconds)
    {
        var transition = _manager.Check(key, remainingPercent, resetAtSeconds);
        if (transition is null) return null;
        _balloon(transition.Value switch
        {
            QuotaAlertPolicy.Transition.Low => "Quota running low",
            QuotaAlertPolicy.Transition.Recovered => "Quota recovered",
            _ => "Quota alert",
        }, transition.Value switch
        {
            QuotaAlertPolicy.Transition.Low => $"{key}: {remainingPercent:F0}% remaining.",
            QuotaAlertPolicy.Transition.Recovered => $"{key} is back above the low threshold.",
            _ => key,
        }, transition.Value);
        return transition;
    }

    /// <summary>Feed a whole refresh result (key -> remaining % + reset epoch).</summary>
    public IReadOnlyList<(string Key, QuotaAlertPolicy.Transition Transition)> EvaluateAll(
        IReadOnlyDictionary<string, (double RemainingPercent, long? ResetAtSeconds)> rows)
    {
        var fired = new List<(string, QuotaAlertPolicy.Transition)>();
        foreach (var (key, row) in rows)
        {
            var t = Evaluate(key, row.RemainingPercent, row.ResetAtSeconds);
            if (t is not null) fired.Add((key, t.Value));
        }
        return fired;
    }
}
