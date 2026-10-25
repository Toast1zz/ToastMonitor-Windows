using ToastMonitor.Collect;
using ToastMonitor.Core;
using ToastMonitor.Platform;

namespace ToastMonitor.ViewModels;

/// <summary>
/// App-level state (Mac AppState equivalent, minus SwiftUI/Combine specifics).
/// Owns the database, collector engine, query service, health hub and quota
/// service. RefreshAsync runs queries off the UI thread and updates
/// properties on the caller's context (UI thread when called from WPF).
/// Engine ScanCompleted fires on a background thread; subscribers that
/// touch UI must dispatch — Popover/Dashboard view models do that via
/// DispatcherHelper in the App project, while this class only raises the
/// plain ScanCompleted event.
/// </summary>
public sealed class AppState : IDisposable
{
    public ToastDatabase Database { get; }
    public CollectorEngine Engine { get; }
    public UsageQueryService Queries { get; }
    public SourceHealthHub Health { get; }
    public SettingsStore Settings { get; }
    public QuotaService Quotas { get; }

    /// <summary>Stage-4 platform services. Credentials is the DPAPI store by default (Mac Keychain equivalent); auth files are only a migration source.</summary>
    public ICredentialProvider Credentials { get; }
    public DpapiCredentialProvider? DpapiStore => Credentials as DpapiCredentialProvider;
    public IAutoStartService AutoStart { get; }
    public DeepSeekManualService DeepSeekManual { get; }
    public UpdateChecker UpdateChecker { get; }
    public TrayAlertNotifier Alerts { get; }

    public event Action<ScanReceipt>? ScanCompleted;
    /// <summary>Fired when a quota alert transition occurs; App shows it as a tray balloon (title, body, transition).</summary>
    public event Action<string, string, QuotaAlertPolicy.Transition>? QuotaAlertFired;

    private bool _engineStarted;
    private long _lastScanFinished;
    public long LastScanFinished => Interlocked.Read(ref _lastScanFinished);

    public AppState(ToastDatabase? db = null, QuotaService? quotas = null,
        ICredentialProvider? credentials = null, IAutoStartService? autoStart = null)
    {
        Database = db ?? ToastDatabase.CreateDefault();
        Database.Open();
        Engine = new CollectorEngine(Database);
        Queries = new UsageQueryService(Database);
        Health = new SourceHealthHub();
        Settings = new SettingsStore(Database);
        Credentials = credentials ?? new DpapiCredentialProvider();
        Quotas = quotas ?? new QuotaService(Credentials,
            openRouterKeys: key => Database.GetSetting(key),
            settings: key => Database.GetSetting(key));
        AutoStart = autoStart ?? new AutoStartService();
        DeepSeekManual = new DeepSeekManualService(Credentials);
        UpdateChecker = new UpdateChecker();
        Alerts = new TrayAlertNotifier((title, body, transition) => QuotaAlertFired?.Invoke(title, body, transition));
        Engine.ScanCompleted += receipt =>
        {
            Interlocked.Exchange(ref _lastScanFinished, receipt.FinishedAt);
            Health.SyncFromEngine(Engine.Health, receipt.FinishedAt);
            // Mac maybeCreateRoutineBackupIfDue: invoked unconditionally on
            // every scan tick; the 7-day throttle inside keeps actual work
            // rare. Failures must never break scan completion.
            try { DataMaintenance.MaybeCreateRoutineBackupIfDue(Database); } catch { /* best effort */ }
            ScanCompleted?.Invoke(receipt);
        };
    }

    /// <summary>Start background polling. Foreground interval is 1s while a window is visible, background 5s otherwise (Mac ForegroundPoller cadence); the engine itself coalesces to a 2s minimum between scans, so the 1s timer does not spin scans.</summary>
    public void StartEngine()
    {
        if (_engineStarted) return;
        _engineStarted = true;
        Engine.Poller.ForegroundInterval = TimeSpan.FromSeconds(1);
        Engine.Poller.BackgroundInterval = TimeSpan.FromSeconds(Settings.PollIntervalSeconds);
        Engine.Start();
    }

    /// <summary>One-time migration from tool auth files into the DPAPI store (fills only unstored keys; never deletes sources, never writes plaintext).</summary>
    public Task<IReadOnlyList<string>> MigrateCredentialsAsync(CancellationToken ct = default) =>
        DpapiStore is not null ? DpapiStore.MigrateFromAuthFilesAsync(ct)
            : Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    /// <summary>Feed a quota refresh into the alert policy. Reset epochs are not carried by quota rows, so null is passed (Low/Recovered transitions still hold; only the new-window &gt;300s badge clear loses precision).</summary>
    public void EvaluateQuotaAlerts(IReadOnlyList<QuotaRowViewModel> rows)
    {
        foreach (var row in rows)
        {
            if (row.PrimaryRemainingPercent is null) continue;
            Alerts.Evaluate(row.Key, row.PrimaryRemainingPercent.Value, null);
        }
    }

    /// <summary>Current app version for update comparison (assembly version; 1.0.0 until a release versioning scheme exists).</summary>
    public static string CurrentVersion =>
        typeof(AppState).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public void SetVisible(bool visible) => Engine.Poller.SetVisible(visible);

    public void Dispose()
    {
        Engine.Dispose();
        Database.Dispose();
    }

    /// <summary>Tray tooltip summary (Mac menu-bar text equivalent): today cost + today tokens, or "No data yet" before the first scan.</summary>
    public static string TraySummary(UsageQueryService.LightSnapshot snap, string trayTextMode)
    {
        if (snap.Today.Count == 0) return "ToastMonitor — no data yet";
        return trayTextMode switch
        {
            "tokens" => $"ToastMonitor — today {Format.Compact(snap.Today.TotalTokens)} tokens",
            "none" => "ToastMonitor",
            _ => $"ToastMonitor — today {Format.Money(snap.Today.Cost)}",
        };
    }
}
