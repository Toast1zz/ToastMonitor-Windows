# WinUI startup and troubleshooting

See [README.md](README.md) for downloads and build commands.

## Deployment

The supported build is an unpackaged, self-contained WinUI application. Keep the entire publish folder together, including `ToastMonitor.WinUI.pri`, the .NET runtime, Windows App SDK DLLs, and `Assets\AppIcon.ico`. Copying only the EXE cannot work.

The build script stages output in a new directory, verifies required files, and then moves it into `artifacts\win-<architecture>`. A previous output folder is retained beside it with a `.previous-*` suffix.

## Startup fixes in this preview

The app uses the Windows App SDK's generated initialization for its app-local runtime. A conflicting manual `MddBootstrapInitialize2` call was removed. Application resources are registered in `OnLaunched`, after WinUI initializes the application resources, instead of accessing them in the application constructor.

Dashboard, Settings, and Popover normally hide when closed. The tray Exit command bypasses that behavior and closes all windows so the process can end.

## Logs

- `%APPDATA%\ToastMonitor\winui-bootstrap.log`: startup stages.
- `%APPDATA%\ToastMonitor\winui-crash.log`: exceptions, stack traces, and HRESULT values.

If the app appears not to open, check the taskbar's hidden tray icons first. If no tray icon appears, include the relevant log excerpt when reporting an issue, after removing personal paths or account details.

## Verification

The initial preview passed 137 unit tests: 85 Core, 20 Collect, 15 Platform, and 17 ViewModels. Startup and window checks passed for native ARM64 and emulated x64 on Windows 11 ARM64. These checks do not cover all real-world provider logins or data sources. Physical Intel/AMD PCs and older Windows versions still need testing.
