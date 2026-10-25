namespace ToastMonitor.ViewModels;

/// <summary>Main (dashboard) window view model: aggregates the five page view models (Mac DashboardWindow equivalent).</summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly AppState _state;
    private bool _isRefreshing;

    public MainWindowViewModel(AppState state)
    {
        _state = state;
        Overview = new OverviewViewModel(state);
        Sessions = new SessionsViewModel(state);
        Plans = new PlansViewModel(state);
        Analysis = new UsageAnalysisViewModel(state);
        Performance = new PerformanceViewModel(state);
    }

    public OverviewViewModel Overview { get; }
    public SessionsViewModel Sessions { get; }
    public PlansViewModel Plans { get; }
    public UsageAnalysisViewModel Analysis { get; }
    public PerformanceViewModel Performance { get; }
    public bool IsRefreshing { get => _isRefreshing; private set => Set(ref _isRefreshing, value); }

    public async Task RefreshAllAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        try
        {
            await Overview.RefreshAsync();
            await Sessions.RefreshAsync();
            await Plans.RefreshAsync();
            await Analysis.RefreshAsync();
            await Performance.RefreshAsync();
        }
        finally { IsRefreshing = false; }
    }
}
