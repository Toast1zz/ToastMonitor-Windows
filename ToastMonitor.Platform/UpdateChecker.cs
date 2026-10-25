using System.Text.Json;

namespace ToastMonitor.Platform;

/// <summary>
/// Update check (Mac UpdateChecker equivalent, deliberately reduced).
/// Fetches a version from the settings-configured check URL and compares
/// semver. Default behaviour: prompt only — never auto-download, never
/// silently install, never modify user files. When the check URL is empty
/// the check is disabled entirely.
/// Mac deviation, stated plainly: the Mac checker verifies an
/// Ed25519-signed manifest (32-byte public key, artifact SHA-256, 256 KB
/// metadata / 512 MB artifact limits) and its UpdateManager stages,
/// replaces, and code-sign/team-ID-verifies the app. .NET 8 has no built-in
/// Ed25519 verification and there is no Windows release source, so the
/// Windows checker does NOT claim Mac-equivalent signature verification
/// and does not install anything. No release URL is invented here.
/// </summary>
public sealed class UpdateChecker
{
    private readonly Func<string?, CancellationToken, Task<string>> _fetch;

    /// <param name="fetch">Injectable fetch (url, ct) -> body; default uses HttpClient.</param>
    public UpdateChecker(Func<string?, CancellationToken, Task<string>>? fetch = null)
    {
        _fetch = fetch ?? DefaultFetchAsync;
    }

    public sealed record CheckResult(bool UpdateAvailable, string? LatestVersion, string? Note);

    /// <summary>
    /// Returns null when disabled (empty URL). Otherwise the latest version
    /// parsed from the check URL body, compared against <paramref name="currentVersion"/>.
    /// Any fetch/parse failure yields a result with a Note, never an exception.
    /// </summary>
    public async Task<CheckResult?> CheckAsync(string? checkUrl, string currentVersion, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(checkUrl)) return null;
        string body;
        try { body = await _fetch(checkUrl, ct); }
        catch (Exception ex) { return new CheckResult(false, null, "Update check failed: " + ex.Message); }
        var latest = ParseLatestVersion(body);
        if (latest is null) return new CheckResult(false, null, "Update check returned an unrecognized version payload.");
        var cmp = CompareSemver(latest, currentVersion);
        return new CheckResult(cmp > 0, latest, cmp > 0 ? "A newer version is available." : "You are up to date.");
    }

    /// <summary>
    /// Accepts a bare version string, {"version": "x"} or {"latest": "x"}.
    /// Returns the trimmed version or null. Never throws on malformed input.
    /// </summary>
    public static string? ParseLatestVersion(string body)
    {
        var trimmed = body.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 4096) return null;
        if (trimmed.StartsWith("{"))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                foreach (var name in new[] { "version", "latest", "tag_name" })
                {
                    if (doc.RootElement.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
                    {
                        var v = el.GetString()?.Trim().TrimStart('v');
                        if (!string.IsNullOrEmpty(v)) return v;
                    }
                }
            }
            catch { return null; }
            return null;
        }
        // Bare version: single line, semver-ish characters only.
        var first = trimmed.Split('\n')[0].Trim().TrimStart('v');
        return first.Length > 0 && first.Length <= 64 &&
               first.All(c => char.IsDigit(c) || c is '.' or '-' or '+' or '_' || char.IsLetter(c))
            ? first : null;
    }

    /// <summary>Semver compare ignoring build metadata; pre-release sorts below its release.</summary>
    public static int CompareSemver(string a, string b)
    {
        var (an, ap) = Split(a);
        var (bn, bp) = Split(b);
        var len = Math.Max(an.Length, bn.Length);
        for (var i = 0; i < len; i++)
        {
            var x = i < an.Length ? an[i] : 0;
            var y = i < bn.Length ? bn[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        if (ap == bp) return 0;
        if (ap is null) return 1; // release > pre-release
        if (bp is null) return -1;
        return string.Compare(ap, bp, StringComparison.Ordinal);
    }

    private static (int[] Numeric, string? Pre) Split(string v)
    {
        var noBuild = v.Split('+')[0];
        var dash = noBuild.IndexOf('-');
        var pre = dash >= 0 ? noBuild[(dash + 1)..] : null;
        var nums = (dash >= 0 ? noBuild[..dash] : noBuild)
            .Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        return (nums, pre);
    }

    private static async Task<string> DefaultFetchAsync(string? url, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain");
        return await http.GetStringAsync(url, ct);
    }
}
