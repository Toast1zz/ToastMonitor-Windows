using System.Runtime.InteropServices;

namespace ToastMonitor.WinUI;

/// <summary>
/// Tray icon for the WinUI app. WinUI has no tray control and a WinForms
/// NotifyIcon cannot be referenced from a WinUI project (the WinFX targets
/// reject UseWindowsForms with MC6000), so this is raw Win32:
/// Shell_NotifyIcon with a hidden message-only window, a Win32 popup menu
/// for right-click, and NIF_INFO balloons for quota alerts. Icon is the
/// app's own Assets/AppIcon.ico (black disc + white lowercase t); the
/// system Performance Monitor icon is only an emergency fallback if that
/// file is missing.
/// Menu ids: 101 Popover, 102 Dashboard, 103 Settings, 104 Exit.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    public const int MenuPopover = 101;
    public const int MenuDashboard = 102;
    public const int MenuSettings = 103;
    public const int MenuExit = 104;

    private const int WM_APP = 0x8000;
    private const int WM_COMMAND = 0x0111;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int NIM_ADD = 0;
    private const int NIM_MODIFY = 1;
    private const int NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_INFO = 0x00000010;
    private const uint NIIF_INFO = 0x00000001;
    private const uint NIIF_WARNING = 0x00000002;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    public event Action? LeftClicked;
    public event Action? DoubleClicked;
    public event Action<int>? MenuClicked;

    private readonly IntPtr _hwnd;
    private readonly IntPtr _hIcon;
    private readonly WndProcDelegate _wndProc;
    private bool _added;
    private string _tip = "ToastMonitor";

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public TrayIcon()
    {
        _wndProc = WndProc;
        var cls = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "ToastMonitorTrayClass",
        };
        RegisterClass(ref cls);
        _hwnd = CreateWindowEx(0, "ToastMonitorTrayClass", "ToastMonitorTray", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        var appIconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(appIconPath))
            ExtractIconEx(appIconPath, 0, out _hIcon, out _, 1);
        if (_hIcon == IntPtr.Zero)
            ExtractIconEx(Path.Combine(Environment.SystemDirectory, "perfmon.exe"), 0, out _hIcon, out _, 1);
        var data = BaseData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = WM_APP + 1;
        data.hIcon = _hIcon;
        data.szTip = _tip;
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
    }

    public void SetTip(string tip)
    {
        _tip = tip.Length > 127 ? tip[..127] : tip;
        if (!_added) return;
        var data = BaseData();
        data.uFlags = NIF_TIP;
        data.szTip = _tip;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    public void ShowBalloon(string title, string body, bool warning)
    {
        if (!_added) return;
        var data = BaseData();
        data.uFlags = NIF_INFO;
        data.szInfo = body;
        data.szInfoTitle = title;
        data.dwInfoFlags = warning ? NIIF_WARNING : NIIF_INFO;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private NOTIFYICONDATA BaseData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
    };

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_APP + 1)
        {
            var evt = lParam.ToInt32();
            if (evt == WM_LBUTTONUP) LeftClicked?.Invoke();
            else if (evt == WM_LBUTTONDBLCLK) DoubleClicked?.Invoke();
            else if (evt == WM_RBUTTONUP) ShowMenu();
        }
        else if (msg == WM_COMMAND)
        {
            MenuClicked?.Invoke(wParam.ToInt32() & 0xFFFF);
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, 0, MenuPopover, "打开 Popover");
        AppendMenu(menu, 0, MenuDashboard, "打开 Dashboard");
        AppendMenu(menu, 0, MenuSettings, "设置");
        AppendMenu(menu, 0, MenuExit, "退出");
        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd);
        var cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);
        if (cmd != 0) MenuClicked?.Invoke(cmd);
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = BaseData();
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);
    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, int uIDNewItem, string lpNewItem);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);
}
