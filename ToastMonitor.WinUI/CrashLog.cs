namespace ToastMonitor.WinUI;

/// <summary>
/// Crash evidence for the WinUI shell. Writes exception type, message and
/// stack to %APPDATA%/ToastMonitor/winui-crash.log so a startup crash can
/// be diagnosed from the log instead of guessed at. Never writes secrets:
/// only exception text is logged, never credentials or tokens. Logging
/// itself is best-effort and never throws.
/// </summary>
public static class CrashLog
{
    public static void Write(string stage, Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ToastMonitor");
            Directory.CreateDirectory(dir);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {stage}: HRESULT=0x{ex.HResult:X8}\n{ex}\n";
            File.AppendAllText(Path.Combine(dir, "winui-crash.log"), line);
        }
        catch
        {
            // Logging must never cause a second crash.
        }
    }

    public static void Write(string stage, string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ToastMonitor");
            Directory.CreateDirectory(dir);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {stage}: {message}\n";
            File.AppendAllText(Path.Combine(dir, "winui-crash.log"), line);
        }
        catch
        {
        }
    }
}
