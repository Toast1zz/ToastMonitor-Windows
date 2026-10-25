# Contributing

Issues and pull requests are welcome. This is an experimental port with limited maintenance time; there is no guaranteed response or release schedule.

## Build and test

Build on Windows with the .NET 8 SDK. Use the root `build-winui.ps1` script with `-Architecture x64` or `-Architecture arm64`, as described in the README.

Run the four unit test projects from PowerShell:

```powershell
$projects = @(
    'ToastMonitor.Core.Tests',
    'ToastMonitor.Collect.Tests',
    'ToastMonitor.Platform.Tests',
    'ToastMonitor.ViewModels.Tests'
)
foreach ($project in $projects) {
    dotnet test ".\$project\$project.csproj" -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed: $project" }
}
```

For startup or window changes, exit the app from its tray menu and run this from an interactive Windows desktop session. Change the path to `win-arm64` for an ARM64 build:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-WinUIStartup.ps1 -ExePath .\artifacts\win-x64\ToastMonitor.WinUI.exe
```

This script starts its own process, opens the dashboard and settings, switches pages and periods, checks popover actions, and verifies hide, reopen, and exit behavior. It does not save account settings or clear the database. The app's normal collectors can run during the check, so use a separate Windows test profile if you need an isolated environment.

## Project layout

- `ToastMonitor.WinUI`: WinUI windows, resources, tray integration, and startup.
- `ToastMonitor.ViewModels`: application state, queries, settings, and quota presentation.
- `ToastMonitor.Core`: SQLite storage, aggregation, pricing, and usage calculations.
- `ToastMonitor.Collect`: local log parsers, collection loop, and account clients.
- `ToastMonitor.Platform`: Windows credentials, startup registration, and platform services.
- `*.Tests`: unit tests and synthetic parser fixtures.

## Pull requests

Keep each PR focused on one problem. Explain what triggered the issue, how behavior changes, and which tests passed. State whether you tested natively or through emulation, along with the Windows version and architecture. Add a regression test when it covers a meaningful failure.

Use synthetic data for fixtures. Do not include real account tokens, cookies, personal session logs, or local databases in issues or pull requests. For provider failures, share a redacted error and reproduction steps.

UI fixes and English localization are welcome. A change should build for both published architectures, even if you can only run one of them locally.
