using Microsoft.UI.Dispatching;

namespace ToastMonitor.WinUI;

/// <summary>
/// The Windows App SDK module initializer loads the app-local runtime before
/// Main. Do not also bootstrap an installed framework runtime: mixing its
/// package graph with the self-contained DLLs crashes CoreMessagingXP.
/// Keep the standard WinUI startup sequence with diagnostic logging.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Log("Main entered");
        try
        {
            global::WinRT.ComWrappersSupport.InitializeComWrappers();
            Log("ComWrappers done, before Application.Start");
            global::Microsoft.UI.Xaml.Application.Start((p) =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
        }
        catch (Exception ex)
        {
            CrashLog.Write("Startup", ex);
            throw;
        }
        finally
        {
            Log("Main exiting");
        }
    }

    private static void Log(string line)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ToastMonitor");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "winui-bootstrap.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + line + Environment.NewLine);
        }
        catch
        {
            // Logging must never break startup.
        }
    }
}
