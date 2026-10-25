using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using System.Runtime.InteropServices;
using ToastMonitor.ViewModels;
using Windows.Graphics;
using Windows.System;

namespace ToastMonitor.WinUI;

/// <summary>
/// Tray popover. Borderless look via ExtendsContentIntoTitleBar, fixed 360
/// wide, height measured from content (capped at 640, scrolling inside),
/// docked bottom-right of the work area, hidden on deactivate or Esc.
/// </summary>
public sealed partial class PopoverWindow : Window
{
    private readonly AppState _state;
    private readonly PopoverViewModel _vm;
    private readonly Action _openDashboard;
    private readonly Action _openSettings;

    public PopoverWindow(AppState state, PopoverViewModel vm, Action openDashboard, Action openSettings)
    {
        _state = state;
        _vm = vm;
        _openDashboard = openDashboard;
        _openSettings = openSettings;
        InitializeComponent();
        RootGrid.DataContext = vm;
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        var presenter = AppWindow.Presenter as OverlappedPresenter;
        if (presenter is not null)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = false;
        }
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        AppWindow.Closing += (_, args) =>
        {
            if (Application.Current is App { IsExiting: true }) return;
            args.Cancel = true;
            HidePopover();
        };
        Activated += (_, args) =>
        {
            if (Application.Current is App { IsExiting: true }) return;
            if (args.WindowActivationState == WindowActivationState.Deactivated) HidePopover();
        };
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    public bool IsVisible => AppWindow.IsVisible;

    public async void ShowPopover()
    {
        AppWindow.Show();
        LayoutPopover();
        Activate();
        try { await _vm.OpenRefreshAsync(); }
        catch (Exception ex) { CrashLog.Write("Popover refresh", ex); }
        if (IsVisible) LayoutPopover();
    }

    private void LayoutPopover()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        if (scale <= 0) scale = 1;
        var maxHeight = Math.Min(720, (area.Height - 24) / scale);
        // A desktop Window arranges its root to the current client size.
        // Measure the content and footer independently so the old window
        // height cannot feed back into the next requested height.
        PopoverFooter.Measure(new Windows.Foundation.Size(386, double.PositiveInfinity));
        PopoverBody.Measure(new Windows.Foundation.Size(346, double.PositiveInfinity));
        var footerHeight = PopoverFooter.DesiredSize.Height;
        var bodyLimit = Math.Max(120, maxHeight - 60 - footerHeight);
        PopoverScroll.MaxHeight = bodyLimit;
        var logicalHeight = Math.Clamp(60 + Math.Min(PopoverBody.DesiredSize.Height, bodyLimit) + footerHeight, 240, maxHeight);
        RootGrid.Height = logicalHeight;
        var width = (int)Math.Ceiling(388 * scale);
        var height = (int)Math.Ceiling(logicalHeight * scale);
        AppWindow.MoveAndResize(new RectInt32(area.X + area.Width - width - 12, area.Y + area.Height - height - 12, width, height));
    }

    private void HidePopover()
    {
        if (IsVisible) AppWindow.Hide();
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) HidePopover();
    }

    private void Dashboard_Click(object sender, RoutedEventArgs e) { HidePopover(); _openDashboard(); }
    private void Settings_Click(object sender, RoutedEventArgs e) { HidePopover(); _openSettings(); }
    private void AccountSettings_Click(object sender, RoutedEventArgs e)
    {
        HidePopover();
        (Application.Current as App)?.ShowSettings(2);
    }
    private async void RefreshQuota_Click(object sender, RoutedEventArgs e)
    {
        try { await _vm.RefreshAsync(refreshQuota: true); }
        catch (Exception ex) { CrashLog.Write("Popover quota refresh", ex); }
        if (IsVisible) LayoutPopover();
    }
    private void Close_Click(object sender, RoutedEventArgs e) => HidePopover();
}
