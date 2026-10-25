using Microsoft.Win32;

namespace ToastMonitor.Platform;

/// <summary>
/// Autostart switch (Mac "Open at Login" / SMAppService equivalent).
/// Writes HKCU\Software\Microsoft\Windows\CurrentVersion\Run, value name
/// "ToastMonitor", value = quoted path to the running exe. Fully reversible:
/// disabling removes exactly that value. Tests use a caller-supplied subkey
/// (or an in-memory registry) so the real Run key is never touched by tests.
/// </summary>
public interface IAutoStartService
{
    bool IsEnabled { get; }
    void SetEnabled(bool enabled);
}

/// <summary>Registry abstraction so tests can substitute an in-memory store.</summary>
public interface IRegistryStore
{
    string? GetValue(string subKey, string name);
    void SetValue(string subKey, string name, string value);
    void DeleteValue(string subKey, string name);
}

public sealed class WindowsRegistryStore : IRegistryStore
{
    public string? GetValue(string subKey, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: false);
        return key?.GetValue(name) as string;
    }

    public void SetValue(string subKey, string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKey)
            ?? throw new InvalidOperationException("Cannot open registry subkey: " + subKey);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void DeleteValue(string subKey, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
        if (key is not null && key.GetValue(name) is not null) key.DeleteValue(name);
    }
}

public sealed class InMemoryRegistryStore : IRegistryStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    public string? GetValue(string subKey, string name) =>
        _values.TryGetValue(subKey + "\n" + name, out var v) ? v : null;
    public void SetValue(string subKey, string name, string value) => _values[subKey + "\n" + name] = value;
    public void DeleteValue(string subKey, string name) => _values.Remove(subKey + "\n" + name);
}

public sealed class AutoStartService : IAutoStartService
{
    public const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "ToastMonitor";

    private readonly IRegistryStore _registry;
    private readonly string _subKey;
    private readonly string _exePath;

    public AutoStartService(IRegistryStore? registry = null, string? subKey = null, string? exePath = null)
    {
        _registry = registry ?? new WindowsRegistryStore();
        _subKey = subKey ?? RunSubKey;
        _exePath = exePath ?? Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "ToastMonitor.App.exe");
    }

    public bool IsEnabled
    {
        get
        {
            var current = _registry.GetValue(_subKey, ValueName);
            if (current is null) return false;
            // Enabled only when the stored command still points at this exe.
            return current.Contains(Path.GetFileName(_exePath), StringComparison.OrdinalIgnoreCase);
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled) _registry.SetValue(_subKey, ValueName, "\"" + _exePath + "\"");
        else _registry.DeleteValue(_subKey, ValueName);
    }
}
