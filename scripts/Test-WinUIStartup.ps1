param([Parameter(Mandatory = $true)][string]$ExePath)
$ErrorActionPreference = 'Stop'
$ExePath = (Resolve-Path $ExePath).Path
if (Get-Process -Name 'ToastMonitor.WinUI' -ErrorAction SilentlyContinue) {
    throw 'Exit the running ToastMonitor instance from its tray menu before this test.'
}
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class ToastMonitorSmokeWindows {
    public class Window { public IntPtr Handle; public string Title; public string Class; public bool Visible; }
    private delegate bool Callback(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Callback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    public static Window[] ForProcess(int id) {
        var windows = new List<Window>();
        EnumWindows((handle, data) => {
            uint pid; GetWindowThreadProcessId(handle, out pid);
            if (pid == id) {
                var title = new StringBuilder(512); var type = new StringBuilder(256);
                GetWindowText(handle, title, 512); GetClassName(handle, type, 256);
                windows.Add(new Window { Handle=handle, Title=title.ToString(), Class=type.ToString(), Visible=IsWindowVisible(handle) });
            }
            return true;
        }, IntPtr.Zero);
        return windows.ToArray();
    }
}
'@
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$app = Start-Process -FilePath $ExePath -WorkingDirectory $env:TEMP -PassThru
function Wait-AppWindow([scriptblock]$Predicate) {
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        if ($app.HasExited) { throw "ToastMonitor exited during startup (code $($app.ExitCode)). Check winui-crash.log." }
        $window = [ToastMonitorSmokeWindows]::ForProcess($app.Id) | Where-Object $Predicate | Select-Object -First 1
        if ($window) { return $window }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Timed out waiting for a usable app window.'
}
function Send-TrayCommand([int]$Command) {
    if (-not [ToastMonitorSmokeWindows]::PostMessage($tray.Handle, 0x0111, [IntPtr]$Command, [IntPtr]::Zero)) {
        throw "Could not send tray command $Command."
    }
}
function Assert-Control([IntPtr]$Handle, [string]$Property, [string]$Value) {
    $root = [Windows.Automation.AutomationElement]::FromHandle($Handle)
    $propertyId = [Windows.Automation.AutomationElement]::NameProperty
    if ($Property -eq 'Id') { $propertyId = [Windows.Automation.AutomationElement]::AutomationIdProperty }
    $condition = [Windows.Automation.PropertyCondition]::new($propertyId, $Value)
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        if ($root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)) { return }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Window exists but its XAML control is missing: $Property=$Value"
}
function Find-Control([IntPtr]$Handle, [string]$Id) {
    $root = [Windows.Automation.AutomationElement]::FromHandle($Handle)
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $control = $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
    if (-not $control) { throw "Missing control: $Id" }
    return $control
}
function Assert-VisibleControl([IntPtr]$Handle, [string]$Id) {
    $control = Find-Control $Handle $Id
    $bounds = $control.Current.BoundingRectangle
    if ($control.Current.IsOffscreen -or $bounds.Width -le 0 -or $bounds.Height -le 0) { throw "Control is not usable: $Id" }
}
try {
    $tray = Wait-AppWindow { $_.Class -eq 'ToastMonitorTrayClass' }
    Write-Output 'PASS: startup created the tray message window.'
    Send-TrayCommand 102
    $dashboard = Wait-AppWindow { $_.Title -eq 'ToastMonitor Dashboard' -and $_.Visible }
    Assert-Control $dashboard.Handle 'Id' 'RefreshButton'
    Write-Output 'PASS: Dashboard opened and its compiled XAML controls loaded.'
    foreach ($id in 'SessionsNav','PlansNav','AnalysisNav','PerformanceNav','OverviewNav') {
        $nav = Find-Control $dashboard.Handle $id
        $selection = $nav.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)
        $selection.Select()
        Start-Sleep -Milliseconds 200
        $heading = Find-Control $dashboard.Handle 'PageTitle'
        if ($heading.Current.Name -ne $nav.Current.Name) { throw "Navigation did not open its page: $id" }
    }
    foreach ($id in 'AllPeriod','TodayPeriod','MonthPeriod','WeekPeriod') {
        $toggle = (Find-Control $dashboard.Handle $id).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
        $toggle.Toggle()
        Start-Sleep -Milliseconds 250
        if ($toggle.Current.ToggleState -ne [Windows.Automation.ToggleState]::On) { throw "Date range did not stay selected: $id" }
    }
    Write-Output 'PASS: all five navigation pages and all four date ranges respond.' 
    Send-TrayCommand 103
    $settings = Wait-AppWindow { $_.Title -like 'ToastMonitor *' -and $_.Title -ne 'ToastMonitor Dashboard' -and $_.Visible }
    Assert-Control $settings.Handle 'Id' 'PollIntervalBox'
    Write-Output 'PASS: Settings opened and its editable controls loaded.'
    [void][ToastMonitorSmokeWindows]::PostMessage($settings.Handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    Send-TrayCommand 101
    $popover = Wait-AppWindow { $_.Title -eq 'ToastMonitor' -and $_.Visible }
    Assert-Control $popover.Handle 'Name' 'Dashboard'
    Assert-VisibleControl $popover.Handle 'DashboardButton'
    Assert-VisibleControl $popover.Handle 'RefreshQuotaButton'
    [void][ToastMonitorSmokeWindows]::PostMessage($popover.Handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 300
    Send-TrayCommand 101
    $popover = Wait-AppWindow { $_.Title -eq 'ToastMonitor' -and $_.Visible }
    Assert-VisibleControl $popover.Handle 'DashboardButton'
    Write-Output 'PASS: the popover actions fit its window; closing it allows the tray to reopen it.' 
    [void][ToastMonitorSmokeWindows]::PostMessage($dashboard.Handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 500
    if ($app.HasExited) { throw 'Closing Dashboard terminated the tray app.' }
    $stillVisible = [ToastMonitorSmokeWindows]::ForProcess($app.Id) | Where-Object { $_.Handle -eq $dashboard.Handle -and $_.Visible }
    if ($stillVisible) { throw 'Closing Dashboard did not hide it.' }
    Send-TrayCommand 102
    $null = Wait-AppWindow { $_.Title -eq 'ToastMonitor Dashboard' -and $_.Visible }
    Write-Output 'PASS: closing hides Dashboard; the tray reopens it.'
    Send-TrayCommand 104
    if (-not $app.WaitForExit(10000)) { throw 'The tray Exit command did not terminate the app.' }
    if ($app.ExitCode -ne 0) { throw "Exit returned $($app.ExitCode)." }
    Write-Output 'PASS: the tray Exit command shut down cleanly.'
}
finally {
    if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force }
    $app.Dispose()
}
