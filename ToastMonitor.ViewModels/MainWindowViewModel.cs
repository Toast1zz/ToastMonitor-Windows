namespace ToastMonitor.ViewModels;

/// <summary>Main (dashboard) window view model: aggregates the five page view models (Mac DashboardWindow equivalent).</summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly AppState _state;
    private bool _isRefreshing;
    private bool _refreshInProgress;

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
    public bool RefreshInProgress => _refreshInProgress;

    public async Task RefreshAllAsync(bool showIndicator = true)
    {
        if (_refreshInProgress) return;
        _refreshInProgress = true;
        if (showIndicator) IsRefreshing = true;
        try
        {
            await Overview.RefreshAsync();
            await Sessions.RefreshAsync();
            await Plans.RefreshAsync();
            await Analysis.RefreshAsync();
            await Performance.RefreshAsync();
        }
        finally
        {
            if (showIndicator) IsRefreshing = false;
            _refreshInProgress = false;
        }
    }
}
