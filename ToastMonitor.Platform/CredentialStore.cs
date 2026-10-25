using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ToastMonitor.Collect;

namespace ToastMonitor.Platform;

/// <summary>
/// Windows credential store (Mac Keychain equivalent).
/// Secrets are encrypted with DPAPI (CurrentUser scope: only this Windows
/// user on this machine can decrypt) and kept in a single file
/// %APPDATA%/ToastMonitor/credentials. The file content is opaque bytes;
/// no secret ever appears in a log, a test, or source code.
/// Auth files owned by the tools remain the migration source only: this
/// store never deletes them and never writes a plaintext copy of a secret.
/// </summary>
public sealed class DpapiCredentialProvider : ICredentialProvider
{
    /// <summary>Default file, mirroring the Mac app's Application Support location.</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ToastMonitor", "credentials");

    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, string> _cache = new(StringComparer.Ordinal);

    public DpapiCredentialProvider(string? path = null)
    {
        _path = path ?? DefaultPath;
        Load();
    }

    /// <summary>True when the store file exists and holds at least one entry.</summary>
    public bool HasStoredCredentials
    {
        get { lock (_lock) return _cache.Count > 0; }
    }

    /// <summary>Configured keys only (presence, never values) — for Settings display.</summary>
    public IReadOnlyList<string> ConfiguredKeys
    {
        get { lock (_lock) return _cache.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(); }
    }

    public Task<string?> GetTokenAsync(string key, CancellationToken ct = default)
    {
        string? value;
        lock (_lock) _cache.TryGetValue(key, out value);
        return Task.FromResult(value);
    }

    public Task SetTokenAsync(string key, string? value, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (value is null) _cache.Remove(key);
            else _cache[key] = value;
            SaveLocked();
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// One-time migration from tool auth files (Mac "first run imports the
    /// existing login" equivalent). Only fills keys that are not already
    /// stored; never overwrites a DPAPI value, never deletes the source
    /// files, and never writes a plaintext copy. Returns the migrated keys.
    /// </summary>
    public async Task<IReadOnlyList<string>> MigrateFromAuthFilesAsync(CancellationToken ct = default)
    {
        var migrated = new List<string>();
        var candidates = new (string Key, Func<string?> Read)[]
        {
            ("claude", () => CredentialFiles.ReadClaudeCredentials().Token),
            ("codex", () => CredentialFiles.ReadCodexToken()),
            ("grok", () => CredentialFiles.ReadGrokAuth().Token),
        };
        foreach (var (key, read) in candidates)
        {
            string? existing;
            lock (_lock) _cache.TryGetValue(key, out existing);
            if (existing is not null) continue;
            string? value;
            try { value = read(); } catch { value = null; }
            if (string.IsNullOrEmpty(value)) continue;
            await SetTokenAsync(key, value, ct);
            migrated.Add(key);
        }
        return migrated;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var raw = File.ReadAllText(_path);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(raw);
            if (dict is null) return;
            var decoded = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, b64) in dict)
            {
                var plain = ProtectedData.Unprotect(Convert.FromBase64String(b64),
                    null, DataProtectionScope.CurrentUser);
                decoded[key] = Encoding.UTF8.GetString(plain);
            }
            _cache = decoded;
        }
        catch
        {
            // A corrupt or foreign store is treated as empty rather than
            // crashing startup; the user can re-enter credentials. The old
            // file is left untouched so nothing is destroyed on a bad read.
            _cache = new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void SaveLocked()
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in _cache)
        {
            var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(value),
                null, DataProtectionScope.CurrentUser);
            dict[key] = Convert.ToBase64String(cipher);
        }
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // Write-then-replace so a crash mid-save cannot leave a half file.
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(dict));
        File.Move(tmp, _path, overwrite: true);
    }
}
