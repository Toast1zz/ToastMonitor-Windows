using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using ToastMonitor.ViewModels;

namespace ToastMonitor.WinUI;

/// <summary>
/// Tray-resident WinUI shell (WPF ToastMonitor.App equivalent).
/// No window is shown at launch: a hidden Dashboard window is created so
/// the app stays alive, the tray icon is the only visible presence.
/// Closing a window hides it to the tray; only the tray menu's Exit ends
/// the process. Engine ScanCompleted fires on a background thread, so all
/// UI refreshes go through the DispatcherQueue.
/// </summary>
public partial class App : Application
{
    private AppState? _state;
    private PopoverViewModel? _popoverVm;
    private TrayIcon? _tray;
    private DashboardWindow? _dashboard;
    private PopoverWindow? _popover;
    private SettingsWindow? _settings;
    private DispatcherQueue? _queue;
    private Mutex? _singleInstance;
    public bool IsExiting { get; private set; }

    public App()
    {
        CrashLog.Write("Stage", "App ctor entered");
        UnhandledException += (_, e) => CrashLog.Write("UnhandledException", e.Exception);
        InitializeComponent();
        CrashLog.Write("Stage", "App ctor InitializeComponent done");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        CrashLog.Write("Stage", "OnLaunched entered");
        try
        {
            // WinUI defers loading App.xaml until after the App constructor.
            // Application.Resources is available here, before any window uses it.
            Resources.Add("BoolToVis", new BoolToVisibilityConverter());
            Resources.Add("Fmt", new FormatConverter());
            Resources.Add("ItemsVis", new CollectionVisibilityConverter());
            CrashLog.Write("Stage", "Application resources registered");
            _queue = DispatcherQueue.GetForCurrentThread();
            CrashLog.Write("Stage", "OnLaunched DispatcherQueue done");
            try
            {
                _singleInstance = new Mutex(true, "ToastMonitorWinUI", out var createdNew);
                if (!createdNew)
                {
                    Exit();
                    return;
                }
            }
            catch
            {
                // A second instance is a nuisance, not a crash: continue.
            }

            _state = new AppState();
            _popoverVm = new PopoverViewModel(_state);
            _ = _state.MigrateCredentialsAsync().ContinueWith(t => { if (t.IsFaulted) { /* ignore */ } });
            _state.QuotaAlertFired += (title, body, transition) =>
                _tray?.ShowBalloon(title, body,
                    transition != ToastMonitor.Collect.QuotaAlertPolicy.Transition.Recovered);
            _state.StartEngine();
            _state.ScanCompleted += receipt => _queue?.TryEnqueue(async () =>
            {
                if (IsExiting) return;
                await RefreshTrayAsync();
                if (IsExiting) return;
                // Some sources report existing sessions on every scan even when usage is unchanged.
                if (_dashboard is not null && receipt.Turns > 0)
                    await _dashboard.RefreshIfVisibleAsync();
                if (_popover is not null && _popover.IsVisible && _popoverVm is not null)
                    await _popoverVm.RefreshAsync();
            });

            // Created hidden and never activated at launch; keeps the app alive.
            CrashLog.Write("Stage", "OnLaunched before DashboardWindow");
            _dashboard = new DashboardWindow(_state);
            CrashLog.Write("Stage", "OnLaunched after DashboardWindow");

            _tray = new TrayIcon();
            CrashLog.Write("Stage", "OnLaunched after TrayIcon");
            _tray.LeftClicked += ShowPopover;
            _tray.DoubleClicked += ShowDashboard;
            _tray.MenuClicked += id =>
            {
                switch (id)
                {
                    case TrayIcon.MenuPopover: ShowPopover(); break;
                    case TrayIcon.MenuDashboard: ShowDashboard(); break;
                    case TrayIcon.MenuSettings: ShowSettings(); break;
                    case TrayIcon.MenuExit: ExitApp(); break;
                }
            };
            _ = RefreshTrayAsync();
            CrashLog.Write("Stage", "OnLaunched done");
        }
        catch (Exception ex)
        {
            CrashLog.Write("OnLaunched", ex);
            throw;
        }
    }

    private async Task RefreshTrayAsync()
    {
        if (_state is null || _tray is null || _popoverVm is null) return;
        try
        {
            await _popoverVm.RefreshAsync(refreshQuota: false);
            if (_popoverVm.Snapshot is not null)
                _tray.SetTip(AppState.TraySummary(_popoverVm.Snapshot, _state.Settings.TrayTextMode));
        }
        catch
        {
            // Tray tip is best-effort; a failed refresh must not crash the shell.
        }
    }

    private void ShowPopover()
    {
        if (_state is null || _popoverVm is null) return;
        _state.SetVisible(true);
        _popover ??= new PopoverWindow(_state, _popoverVm, ShowDashboard, ShowSettings);
        _popover.ShowPopover();
    }

    private void ShowDashboard()
    {
        if (_state is null) return;
        _dashboard ??= new DashboardWindow(_state);
        _dashboard.ShowWindow();
    }

    internal void ShowSettings()
    {
        if (_state is null) return;
        _settings ??= new SettingsWindow(_state);
        _settings.ShowWindow();
    }

    internal void ShowSettings(int tab)
    {
        ShowSettings();
        _settings?.SelectTab(tab);
    }

    private void ExitApp()
    {
        if (IsExiting) return;
        IsExiting = true;
        CrashLog.Write("Stage", "Exiting application");
        _tray?.Dispose();
        _tray = null;
        _popover?.Close();
        _settings?.Close();
        _dashboard?.Close();
        _state?.Dispose();
        _singleInstance?.Dispose();
        Exit();
    }
}
