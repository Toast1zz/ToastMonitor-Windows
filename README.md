# ToastMonitor for Windows

An experimental Windows port of [ToastMonitor](https://github.com/Toast1zz/ToastMonitor), built with WinUI 3 and .NET 8. It collects AI coding usage into a local SQLite database and puts a usage panel in the Windows system tray.

> **Unstable build.** This port is a work in progress. Bugs, incomplete UI, and missing features are expected, including in the tray popover. Updates may be infrequent, and there is no commitment to regular maintenance or feature parity with the macOS app. Issues and pull requests are welcome.

## Download and run

Get the ZIP for your processor from [Releases](https://github.com/Toast1zz/ToastMonitor-Windows/releases). Releases are marked as pre-release.

| Package | Use on |
| --- | --- |
| `x64` | Intel or AMD 64-bit Windows PCs |
| `arm64` | Windows on ARM devices |

The ARM64 package will not run on an ordinary Intel or AMD PC. These are portable packages: extract the entire ZIP into a folder, then run `ToastMonitor.WinUI.exe`. Keep all files together. The packages include .NET and the Windows App SDK; no separate runtime installation or MSIX certificate import is required.

The app starts in the system tray, which may be inside the taskbar's hidden-icons menu. Click its icon for the usage popover, double-click for Dashboard, or right-click for Settings and Exit. Closing a window hides it to the tray. Use Exit to quit before replacing the app folder with a newer release.

## What is included

- Local collectors for Claude Code, Codex, OpenCode, Hermes, Oh My Pi, and DeepSeek Harness, plus OpenRouter usage support.
- A dashboard with Overview, Sessions, Plans, Usage analysis, and Performance pages, and period selectors for Today, Week, Month, and All time.
- A tray popover with today's usage, configured account quotas, and recent sessions.
- Account and subscription settings, credential storage using Windows DPAPI, and database maintenance tools.

Account integrations depend on provider endpoints and your credentials. Their availability and behavior can change. Cost figures are estimates, and individual integrations have not all been verified with live accounts on Windows.

## Data sources

Local tools must have produced logs on this Windows user profile before usage can appear. A new installation with no matching logs will show an empty state.

| Source | Default local path |
| --- | --- |
| Claude Code | `%USERPROFILE%\.claude\projects` |
| Codex | `%USERPROFILE%\.codex\sessions` and `state_5.sqlite` in the same `.codex` folder |
| OpenCode | `%USERPROFILE%\.local\share\opencode\opencode.db` |
| Hermes | `%USERPROFILE%\.hermes\state.db` |
| Oh My Pi | `%USERPROFILE%\.omp\agent\sessions` |
| DeepSeek Harness | `%USERPROFILE%\.dsh\sessions` and projection cache |

`OPENCODE_HOME`, `HERMES_HOME`, and `DSH_HOME` override their respective roots. Remote feeds and account services require configuration. Usage data is stored in `%APPDATA%\ToastMonitor\toastmonitor.db`; the dedicated credential store uses Windows DPAPI encryption for the current user. Tool-owned login files may also be read to obtain account credentials.

## Current limitations

- The Windows UI, especially the tray popover, is unfinished. Much of the interface is currently in Chinese.
- ARM64 startup and window interactions were tested natively in a Windows 11 ARM64 virtual machine. The x64 package passed the same checks through Windows 11 ARM64's x64 emulation; it has not been tested on physical Intel or AMD hardware.
- Windows 11 is the tested environment. The project declares Windows 10 version 1809 as its minimum target, but older Windows versions have not been verified.
- The Windows port does not provide the macOS app's signed automatic update installation. Download updates manually from this repository's Releases page.
- Passing startup and unit tests does not establish that every collector, provider login, or quota endpoint works with a real account.

## Build

Use Windows with the .NET 8 SDK and access to NuGet. Keep the checkout path short, for example `C:\src\ToastMonitor-Windows`: Windows App SDK build tools can fail when generated file paths exceed the Windows path limit. Run from the repository root in PowerShell:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build-winui.ps1 -Architecture x64
powershell -NoProfile -ExecutionPolicy Bypass -File .\build-winui.ps1 -Architecture arm64
```

Build the architecture you need. Output is written to `artifacts\win-x64` or `artifacts\win-arm64`. The script publishes the WinUI project and checks that its runtime, compiled XAML resources, and icon are present. Exit a running copy before rebuilding its output folder.

See [CONTRIBUTING.md](CONTRIBUTING.md) for tests and the pull request workflow, and [README-WinUI.md](README-WinUI.md) for startup troubleshooting.

## Contributing

PRs are welcome, especially fixes for startup, the popover, accessibility, data collection, and compatibility on real x64 hardware. Please include reproduction steps, your Windows version and CPU architecture, and the checks you ran. Maintainer responses and reviews may take time.

## License

[MIT](LICENSE). This port is based on [ToastMonitor for macOS](https://github.com/Toast1zz/ToastMonitor). Bundled runtimes and third-party dependencies retain their own licenses.
