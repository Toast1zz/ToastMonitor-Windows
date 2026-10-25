using Microsoft.UI.Windowing;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using ToastMonitor.Core;
using ToastMonitor.ViewModels;
using Windows.Graphics;

namespace ToastMonitor.WinUI;

/// <summary>The dashboard presents snapshots; collection and aggregation remain in the shared view models.</summary>
public sealed partial class DashboardWindow : Window
{
    private readonly MainWindowViewModel _vm;
    private bool _loaded;
    private bool _refreshPending;
    private NavigationViewItem? _selectedPage;

    public DashboardWindow(AppState state)
    {
        _vm = new MainWindowViewModel(state);
        InitializeComponent();
        RootGrid.DataContext = _vm;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        RootGrid.ActualThemeChanged += (_, _) =>
        {
            UpdateCaptionColors();
            if (_loaded) { UpdatePeriodButtons(); DrawActivity(); }
        };
        UpdateCaptionColors();
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        if (scale <= 0) scale = 1;
        var width = Math.Min((int)Math.Round(1100 * scale), area.Width - (int)Math.Round(48 * scale));
        var height = Math.Min((int)Math.Round(790 * scale), area.Height - (int)Math.Round(48 * scale));
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        Navigation.SelectedItem = OverviewNav;
        _vm.Overview.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(OverviewViewModel.DailyBars)) DrawActivity(); };
        AppWindow.Closing += (_, args) =>
        {
            if (Application.Current is App { IsExiting: true }) return;
            args.Cancel = true;
            AppWindow.Hide();
        };
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    private void UpdateCaptionColors()
    {
        var dark = RootGrid.ActualTheme == ElementTheme.Dark;
        AppWindow.TitleBar.ButtonForegroundColor = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = Microsoft.UI.Colors.Gray;
    }

    public void ShowWindow()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        AppWindow.Show();
        Activate();
        if (_loaded) _ = RefreshAsync();
    }

    public Task RefreshIfVisibleAsync() => Application.Current is not App { IsExiting: true } && AppWindow.IsVisible && _loaded ? RefreshAsync() : Task.CompletedTask;

    private async Task RefreshAsync()
    {
        if (Application.Current is App { IsExiting: true }) return;
        if (_vm.IsRefreshing) { _refreshPending = true; return; }
        try
        {
            do
            {
                _refreshPending = false;
                await _vm.RefreshAllAsync();
            } while (_refreshPending);
            RefreshError.IsOpen = false;
        }
        catch (Exception ex)
        {
            CrashLog.Write("Dashboard refresh", ex);
            RefreshError.Message = "暂时无法读取记录，请稍后再试。";
            RefreshError.IsOpen = true;
        }
    }

    private async void Root_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        UpdatePeriodButtons();
        await RefreshAsync();
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (PageTitle is null) return;
        if (args.IsSettingsSelected)
        {
            (Application.Current as App)?.ShowSettings();
            Navigation.SelectedItem = _selectedPage ?? OverviewNav;
            return;
        }
        if (args.SelectedItem is not NavigationViewItem item) return;
        _selectedPage = item;
        var tag = item.Tag as string;
        OverviewPage.Visibility = tag == "Overview" ? Visibility.Visible : Visibility.Collapsed;
        SessionsPage.Visibility = tag == "Sessions" ? Visibility.Visible : Visibility.Collapsed;
        PlansPage.Visibility = tag == "Plans" ? Visibility.Visible : Visibility.Collapsed;
        AnalysisPage.Visibility = tag == "Analysis" ? Visibility.Visible : Visibility.Collapsed;
        PerformancePage.Visibility = tag == "Performance" ? Visibility.Visible : Visibility.Collapsed;
        PageScroll.ChangeView(null, 0, null, true);
        PageTitle.Text = item.Content as string ?? "总览";
        PageSubtitle.Text = tag switch
        {
            "Sessions" => "回看每一次对话与模型用量",
            "Plans" => "管理订阅，了解持续投入",
            "Analysis" => "按模型与日期展开使用记录",
            "Performance" => "比较模型的响应速度与首字延迟",
            _ => "了解你的 AI 使用与花费",
        };
    }

    private async void Period_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        if (sender is not ToggleButton { Tag: string tag } || !Enum.TryParse<UsagePeriodSlot>(tag, out var slot)) return;
        if (_vm.Overview.Filter.Slot == slot) { UpdatePeriodButtons(); return; }
        _vm.Overview.Filter.Slot = slot;
        UpdatePeriodButtons();
        await RefreshAsync();
    }
    private void Period_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_loaded && sender is ToggleButton button && button.Tag as string == _vm.Overview.Filter.Slot.ToString())
            button.IsChecked = true;
    }
    private void UpdatePeriodButtons()
    {
        foreach (var button in new[] { TodayPeriod, WeekPeriod, MonthPeriod, AllPeriod })
        {
            var selected = button.Tag as string == _vm.Overview.Filter.Slot.ToString();
            button.IsChecked = selected;
            button.Background = (Brush)Application.Current.Resources[selected ? "TMAccentSoftBrush" : "TMQuietBrush"];
            button.Foreground = (Brush)Application.Current.Resources[selected ? "TMAccentBrush" : "TMMutedBrush"];
        }
    }
    private void DrawActivity()
    {
        ActivityPlot.Children.Clear();
        ActivityPlot.ColumnDefinitions.Clear();
        foreach (var day in _vm.Overview.DailyBars)
        {
            var column = ActivityPlot.ColumnDefinitions.Count;
            ActivityPlot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var bar = new Border
            {
                CornerRadius = new CornerRadius(3), Margin = new Thickness(2, 0, 2, 0),
                Height = Math.Max(2, day.BarPercent * 0.40), VerticalAlignment = VerticalAlignment.Bottom,
                Background = (Brush)Application.Current.Resources[day.Tokens > 0 ? "TMAccentBrush" : "TMLineBrush"],
            };
            ToolTipService.SetToolTip(bar, day.DayText + " · " + Format.Full(day.Tokens) + " tokens · " + Format.Money(day.Cost));
            Grid.SetColumn(bar, column);
            ActivityPlot.Children.Add(bar);
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var content = (sender as Button)?.Content as string;
        (Application.Current as App)?.ShowSettings(content is "管理订阅" or "添加订阅" ? 3 : 2);
    }
    private async void SessionsTool_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        if (sender is ComboBox { SelectedItem: ComboBoxItem item })
        {
            var tag = item.Tag as string;
            _vm.Sessions.Filter.Tool = string.IsNullOrEmpty(tag) ? null : tag;
            await RefreshAsync();
        }
    }
    private async void Sessions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView { SelectedItem: SessionRow row })
        {
            try { await _vm.Sessions.SelectAsync(row); }
            catch (Exception ex) { CrashLog.Write("Session details", ex); RefreshError.Message = "暂时无法读取会话明细。"; RefreshError.IsOpen = true; }
        }
    }
}
