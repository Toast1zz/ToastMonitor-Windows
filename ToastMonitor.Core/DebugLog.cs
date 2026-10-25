namespace ToastMonitor.Core;

/// <summary>
/// Debug logging gate (Mac DebugLog): set TM_DEBUG=1 to trace per-file
/// scan decisions and maintenance operations. Read on every access so
/// tests can toggle it via Environment.SetEnvironmentVariable.
/// Callers must never pass secrets (tokens, cookies, credential contents);
/// only paths, counts, and decisions belong in a debug line.
/// </summary>
public static class DebugLog
{
    public static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("TM_DEBUG"), "1", StringComparison.Ordinal);

    public static void Trace(string message)
    {
        if (!Enabled) return;
        System.Diagnostics.Trace.WriteLine("[ToastMonitor] " + message);
    }
}
