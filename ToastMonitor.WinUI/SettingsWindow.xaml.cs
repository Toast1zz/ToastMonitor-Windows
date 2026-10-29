using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using ToastMonitor.Core;
using ToastMonitor.ViewModels;
using Windows.Graphics;

namespace ToastMonitor.WinUI;

/// <summary>
/// Settings window (WPF SettingsWindow equivalent). Code-behind only maps
/// combo selections and numeric text to view-model values and wires
/// save/cancel; persistence lives in SettingsViewModel. Numeric fields use
/// code-behind parsing because WinUI Binding does not convert TextBox text
/// to int/double the way WPF does.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;
    private Subscription? _selectedSub;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);
    private const uint SwpNoMoveOrSize = 0x0001 | 0x0002;
    private int _subscriptionFeedbackVersion;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public SettingsWindow(AppState state)
    {
        _vm = new SettingsViewModel(state);
        InitializeComponent();
        RootGrid.DataContext = _vm;
        AppWindow.Resize(new SizeInt32(760, 620));
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        AppWindow.Closing += (_, args) =>
        {
            if (Application.Current is App { IsExiting: true }) return;
            args.Cancel = true;
            AppWindow.Hide();
        };
        SyncControls();
    }

    public void ShowWindow()
    {
        var presenter = AppWindow.Presenter as OverlappedPresenter;
        if (presenter is not null && presenter.State == OverlappedPresenterState.Minimized)
            presenter.Restore();
        AppWindow.Show();
        SubscriptionFeedback.IsOpen = false;
        _subscriptionFeedbackVersion++;
        // NavigationView may restore focus to the dashboard after this call.
        // Raise settings after that click event has completed.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!AppWindow.IsVisible) return;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMoveOrSize);
            SetWindowPos(hwnd, HwndNotTopmost, 0, 0, 0, 0, SwpNoMoveOrSize);
            SetForegroundWindow(hwnd);
            Activate();
        });
    }

    public void SelectTab(int tab) => SettingsTabs.SelectedIndex = tab;

    private void SyncControls()
    {
        PollIntervalBox.Text = _vm.PollIntervalSeconds.ToString();
        TrayFontSizeBox.Text = _vm.TrayFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        QuotaRefreshBox.Text = _vm.QuotaRefreshMinutes.ToString();
        SelectByTag(PeriodModeBox, _vm.PeriodMode == UsagePeriodMode.Calendar ? "Calendar" : "Recent");
        SelectByTag(WeekStartBox, _vm.WeekStart == UsageWeekStart.Sunday ? "Sunday" : "Monday");
        SelectByTag(TrayTextBox, _vm.TrayTextMode);
    }

    private static void SelectByTag(ComboBox box, string tag)
    {
        foreach (var item in box.Items)
            if (item is ComboBoxItem ci && (ci.Tag as string) == tag) { box.SelectedItem = item; break; }
    }

    private void Save_Click(object sender, RoutedEventArgs e) { _vm.Save(); AppWindow.Hide(); }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _vm.Load();
        SyncControls();
        AppWindow.Hide();
    }

    private void PollInterval_Changed(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box && int.TryParse(box.Text, out var v)) _vm.PollIntervalSeconds = v;
    }

    private void TrayFontSize_Changed(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box && double.TryParse(box.Text, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v)) _vm.TrayFontSize = v;
    }

    private void QuotaRefresh_Changed(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box && int.TryParse(box.Text, out var v)) _vm.QuotaRefreshMinutes = v;
    }

    private void PeriodMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox box && box.SelectedItem is ComboBoxItem item)
            _vm.PeriodMode = (item.Tag as string) == "Calendar" ? UsagePeriodMode.Calendar : UsagePeriodMode.Recent;
    }

    private void WeekStart_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox box && box.SelectedItem is ComboBoxItem item)
            _vm.WeekStart = (item.Tag as string) == "Sunday" ? UsageWeekStart.Sunday : UsageWeekStart.Monday;
    }

    private void TrayText_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox box && box.SelectedItem is ComboBoxItem item)
            _vm.TrayTextMode = item.Tag as string ?? "cost";
    }

    private async void UpdateCheck_Click(object sender, RoutedEventArgs e)
    {
        _vm.Save();
        await _vm.CheckForUpdatesAsync();
    }

    private async void DeepSeekApiSave_Click(object sender, RoutedEventArgs e) => await _vm.SaveDeepSeekApiAsync();
    private async void DeepSeekPlatformSave_Click(object sender, RoutedEventArgs e) => await _vm.SaveDeepSeekPlatformAsync();
    private async void DeepSeekValidate_Click(object sender, RoutedEventArgs e) => await _vm.ValidateDeepSeekAsync();
    private void Backup_Click(object sender, RoutedEventArgs e) => _vm.CreateBackup();

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".db");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var file = await picker.PickSingleFileAsync();
        if (file is not null) _vm.RestoreBackup(file.Path);
    }

    private async void ClearData_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "ToastMonitor",
            Content = "确定清空全部用量与历史数据吗？清空前会自动创建一个备份，设置与凭据不受影响。此操作不可撤销（可用备份恢复）。",
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            XamlRoot = RootGrid.XamlRoot,
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary) _vm.ClearAllData();
    }

    private async void AddSub_Click(object sender, RoutedEventArgs e)
    {
        _vm.NewSubName = SubscriptionNameBox.Text;
        _vm.NewSubPrice = SubscriptionPriceBox.Text;
        var added = _vm.AddSubscriptionFromFields();
        SubscriptionFeedback.Severity = added ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        SubscriptionFeedback.Message = added ? "订阅已添加。" : "添加失败，请检查名称和月费（USD）。";
        SubscriptionFeedback.IsOpen = true;
        var feedbackVersion = ++_subscriptionFeedbackVersion;
        if (!added) return;
        await Task.Delay(TimeSpan.FromSeconds(4));
        if (feedbackVersion == _subscriptionFeedbackVersion)
            SubscriptionFeedback.IsOpen = false;
    }

    private void Subscription_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView list) _selectedSub = list.SelectedItem as Subscription;
    }

    private void DeleteSub_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSub is not null) _vm.DeleteSubscriptionById(_selectedSub.Id);
    }
}
