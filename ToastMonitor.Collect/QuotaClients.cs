using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ToastMonitor.Core;

namespace ToastMonitor.Collect;

/// <summary>Credential providers. Stage 2 reads tokens from local auth files
/// (the Mac sources) or from an injected provider; the platform stage replaces
/// these with the real DPAPI/credential store. No secret is ever hard-coded.</summary>
public interface ICredentialProvider
{
    Task<string?> GetTokenAsync(string key, CancellationToken ct = default);
    Task SetTokenAsync(string key, string? value, CancellationToken ct = default);
}

public sealed class InMemoryCredentialProvider : ICredentialProvider
{
    private readonly Dictionary<string, string?> _tokens = new();
    public Task<string?> GetTokenAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(_tokens.TryGetValue(key, out var v) ? v : null);
    public Task SetTokenAsync(string key, string? value, CancellationToken ct = default)
    {
        _tokens[key] = value;
        return Task.CompletedTask;
    }
    public void Set(string key, string? value) => _tokens[key] = value;
}

/// <summary>File-backed provider: reads a token from a local auth file via a
/// caller-supplied extractor (file formats differ per tool). Tokens are never
/// written back to these files by this provider.</summary>
public sealed class FileCredentialProvider : ICredentialProvider
{
    private readonly Func<string, Task<string?>> _read;
    public FileCredentialProvider(Func<string, Task<string?>> read) => _read = read;
    public Task<string?> GetTokenAsync(string key, CancellationToken ct = default) => _read(key);
    public Task SetTokenAsync(string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
}

public static class CredentialFiles
{
    /// <summary>Mac ClaudeOAuthToken rule: length cap, no control chars, expiresAt ms-or-s normalised to seconds.</summary>
    public static string? SanitizeToken(string? token) =>
        string.IsNullOrWhiteSpace(token) || token!.Length > 16384 || token.Any(char.IsControl) ? null : token.Trim();

    public static (string? Token, long? ExpiresAt, string? SubscriptionType) ReadClaudeCredentials(string? path = null)
    {
        path ??= SourceRoots.ClaudeCredentialsFile;
        try
        {
            if (!File.Exists(path)) return (null, null, null);
            var text = File.ReadAllText(path);
            if (text.Length > 1_000_000) return (null, null, null);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var nested = JsonUtil.Get(root, "claudeAiOauth") ?? root;
            var token = SanitizeToken(JsonUtil.GetString(nested, "accessToken") ?? JsonUtil.GetString(nested, "access_token"));
            var expiresRaw = JsonUtil.GetLong(nested, "expiresAt", -1);
            if (expiresRaw < 0) expiresRaw = JsonUtil.GetLong(nested, "expires_at", -1);
            long? expires = expiresRaw < 0 ? null : (expiresRaw > 1_000_000_000_000L ? expiresRaw / 1000 : expiresRaw);
            var sub = JsonUtil.GetString(nested, "subscriptionType") ?? JsonUtil.GetString(nested, "subscription_type");
            if (sub is not null && sub.Length > 128) sub = null;
            return (token, expires, sub);
        }
        catch { return (null, null, null); }
    }

    public static string? ReadCodexToken(string? path = null)
    {
        path ??= SourceRoots.CodexAuthFile;
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            if (text.Length > 1_000_000) return null;
            using var doc = JsonDocument.Parse(text);
            var tokens = JsonUtil.Get(doc.RootElement, "tokens");
            var token = tokens is not null ? JsonUtil.GetString(tokens.Value, "access_token") : null;
            return SanitizeToken(token);
        }
        catch { return null; }
    }

    /// <summary>Grok auth.json: map keyed by scope; prefer the OIDC scope prefix, else legacy sign-in scope.</summary>
    public static (string? Token, DateTime? ExpiresAt) ReadGrokAuth(string? path = null)
    {
        path ??= SourceRoots.GrokAuthFile;
        try
        {
            if (!File.Exists(path)) return (null, null);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null);
            JsonElement? pick = null;
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Name.StartsWith("https://auth.x.ai::", StringComparison.Ordinal)) { pick = prop.Value; break; }
            }
            if (pick is null)
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name == "https://accounts.x.ai/sign-in" || prop.Name.Contains("/sign-in", StringComparison.Ordinal))
                    { pick = prop.Value; break; }
                }
            if (pick is null) return (null, null);
            var key = SanitizeToken(JsonUtil.GetString(pick.Value, "key"));
            var expiresRaw = JsonUtil.GetString(pick.Value, "expires_at");
            DateTime? expires = null;
            if (!string.IsNullOrEmpty(expiresRaw) && DateTime.TryParse(expiresRaw, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
                expires = dt;
            return (key, expires);
        }
        catch { return (null, null); }
    }
}

public static class HttpHelpers
{
    public static HttpClient CreateClient(TimeSpan timeout)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        return new HttpClient(handler) { Timeout = timeout };
    }

    public static async Task<(HttpStatusCode? Status, string? Body, string? RetryAfter)> GetAsync(
        HttpClient client, string url, Action<HttpRequestMessage>? headers, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        headers?.Invoke(req);
        try
        {
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var retryAfter = resp.Headers.RetryAfter?.Delta?.TotalSeconds;
            string? body = null;
            if (resp.Content is not null)
            {
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length <= 10 * 1024 * 1024) body = System.Text.Encoding.UTF8.GetString(bytes);
            }
            return (resp.StatusCode, body, retryAfter?.ToString(CultureInfo.InvariantCulture));
        }
        catch (TaskCanceledException) { return (null, null, null); }
        catch (HttpRequestException) { return (null, null, null); }
    }

    public static double? BoundedPercent(JsonElement el, string name)
    {
        var v = JsonUtil.GetDouble(el, name, double.NaN);
        return double.IsFinite(v) && v >= 0 && v <= 100 ? v : null;
    }

    public static long? BoundedReset(JsonElement el, string name)
    {
        var v = JsonUtil.GetLong(el, name, -1);
        return v >= 0 && v <= 4_102_444_800L ? v : null; // up to year 2100
    }
}

public sealed record QuotaWindow(string Name, double? PercentUsed, long? ResetAtSeconds, double? RemainingPercent);
public sealed record QuotaResult(string Tool, List<QuotaWindow> Windows, string? Plan, string? Error, bool AuthExpired);

public static class QuotaParsing
{
    public static string TierName(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "Unknown";
        return raw.ToLowerInvariant() switch
        {
            "free" => "Free", "go" => "Go", "plus" => "Plus", "pro" => "Pro",
            "team" or "business" => "Business", "enterprise" => "Enterprise",
            "edu" or "education" => "Edu",
            _ => string.Join(" ", raw.Split(new[] { '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant())),
        };
    }

    public static string PlanFromPrice(double monthlyPrice, string? apiTier, string? customName)
    {
        if (!string.IsNullOrEmpty(customName)) return customName;
        if (!string.IsNullOrEmpty(apiTier)) return TierName(apiTier);
        return monthlyPrice < 14 ? "Go" : monthlyPrice < 100 ? "Plus" : "Pro";
    }
}

public sealed class ClaudeQuotaClient
{
    public const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private readonly ICredentialProvider _creds;
    private readonly HttpClient _http;
    public ClaudeQuotaClient(ICredentialProvider creds, HttpClient? http = null)
    {
        _creds = creds;
        _http = http ?? HttpHelpers.CreateClient(TimeSpan.FromSeconds(15));
    }

    public static QuotaResult Parse(string json)
    {
        var windows = new List<QuotaWindow>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                var label = prop.Name switch
                {
                    "five_hour" or "fiveHour" or "5h" => "5h",
                    "seven_day" or "sevenDay" or "7d" => "weekly",
                    "weekly_opus" or "weeklyOpus" => "weekly Opus",
                    _ => prop.Name,
                };
                var pct = HttpHelpers.BoundedPercent(prop.Value, "utilization");
                if (pct is null) pct = HttpHelpers.BoundedPercent(prop.Value, "used_percent");
                var reset = HttpHelpers.BoundedReset(prop.Value, "resets_at");
                if (reset is null) reset = HttpHelpers.BoundedReset(prop.Value, "reset_at");
                windows.Add(new QuotaWindow(label, pct, reset, pct is null ? null : 100 - pct.Value));
            }
        }
        catch { return new QuotaResult("claude", windows, null, "parse error", false); }
        return new QuotaResult("claude", windows, null, null, false);
    }

    public async Task<QuotaResult> RefreshAsync(CancellationToken ct = default)
    {
        var token = await _creds.GetTokenAsync("claude");
        if (token is null) return new QuotaResult("claude", new(), null, "no token", true);
        var (status, body, _) = await HttpHelpers.GetAsync(_http, UsageUrl, req =>
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            req.Headers.UserAgent.ParseAdd("ToastMonitor");
        }, ct);
        if (status is null) return new QuotaResult("claude", new(), null, "network error", false);
        if (status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Forbidden)
            return new QuotaResult("claude", new(), null, "login expired — reconfigure", true);
        if (body is null) return new QuotaResult("claude", new(), null, $"HTTP {(int)status.Value}", false);
        return Parse(body);
    }
}

public sealed class CodexQuotaClient
{
    public const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    private readonly ICredentialProvider _creds;
    private readonly HttpClient _http;
    public CodexQuotaClient(ICredentialProvider creds, HttpClient? http = null)
    {
        _creds = creds;
        _http = http ?? HttpHelpers.CreateClient(TimeSpan.FromSeconds(15));
    }

    public static QuotaResult Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var planType = JsonUtil.GetString(root, "plan_type");
            var windows = new List<QuotaWindow>();
            var rl = JsonUtil.Get(root, "rate_limit");
            var primary = rl is not null ? JsonUtil.Get(rl.Value, "primary_window") : null;
            if (primary is not null)
            {
                var used = HttpHelpers.BoundedPercent(primary.Value, "used_percent");
                var reset = HttpHelpers.BoundedReset(primary.Value, "reset_at");
                windows.Add(new QuotaWindow("primary", used, reset, used is null ? null : 100 - used.Value));
            }
            return new QuotaResult("codex", windows, QuotaParsing.TierName(planType), null, false);
        }
        catch { return new QuotaResult("codex", new(), null, "parse error", false); }
    }

    public async Task<QuotaResult> RefreshAsync(CancellationToken ct = default)
    {
        var token = await _creds.GetTokenAsync("codex");
        if (token is null) return new QuotaResult("codex", new(), null, "no token", true);
        var (status, body, _) = await HttpHelpers.GetAsync(_http, UsageUrl, req =>
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.ParseAdd("application/json");
            req.Headers.UserAgent.ParseAdd("ToastMonitor");
        }, ct);
        if (status is null) return new QuotaResult("codex", new(), null, "network error", false);
        if (status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Forbidden)
            return new QuotaResult("codex", new(), null, "login expired — reconfigure", true);
        if ((int)status.Value >= 300 && (int)status.Value < 400)
            return new QuotaResult("codex", new(), null, "redirect rejected", false);
        if (body is null) return new QuotaResult("codex", new(), null, $"HTTP {(int)status.Value}", false);
        return Parse(body);
    }
}

public sealed class GrokQuotaClient
{
    public const string BillingUrl = "https://cli-chat-proxy.grok.com/v1/billing?format=credits";
    public const string SettingsUrl = "https://cli-chat-proxy.grok.com/v1/settings";
    private readonly ICredentialProvider _creds;
    private readonly HttpClient _http;
    public GrokQuotaClient(ICredentialProvider creds, HttpClient? http = null)
    {
        _creds = creds;
        _http = http ?? HttpHelpers.CreateClient(TimeSpan.FromSeconds(15));
    }

    public static (double? Balance, string? Plan) ParseBilling(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            double? balance = null;
            var prepaid = JsonUtil.Get(root, "prepaid_balance");
            if (prepaid is not null) balance = JsonUtil.GetDouble(prepaid.Value, "amount", double.NaN);
            if (balance is null || !double.IsFinite(balance.Value))
            {
                var credits = JsonUtil.Get(root, "credits");
                if (credits is not null) balance = JsonUtil.GetDouble(credits.Value, "balance", double.NaN);
            }
            string? plan = null;
            var products = JsonUtil.Get(root, "products");
            if (products is not null && products.Value.ValueKind == JsonValueKind.Array)
                foreach (var p in products.Value.EnumerateArray())
                    plan ??= JsonUtil.GetString(p, "name");
            return (double.IsFinite(balance ?? double.NaN) ? balance : null, plan);
        }
        catch { return (null, null); }
    }

    public async Task<(double? Balance, string? Plan, string? Error, bool AuthExpired)> RefreshAsync(CancellationToken ct = default)
    {
        var token = await _creds.GetTokenAsync("grok");
        if (token is null) return (null, null, "no token", true);
        void Headers(HttpRequestMessage req)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("x-xai-token-auth", "xai-grok-cli");
            req.Headers.Accept.ParseAdd("application/json");
            req.Headers.UserAgent.ParseAdd("ToastMonitor");
        }
        var (status, body, _) = await HttpHelpers.GetAsync(_http, BillingUrl, Headers, ct);
        if (status is null) return (null, null, "network error", false);
        if (status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Forbidden)
            return (null, null, "login expired — reconfigure", true);
        if (body is null) return (null, null, $"HTTP {(int)status.Value}", false);
        var (balance, plan) = ParseBilling(body);
        return (balance, plan, null, false);
    }
}

public sealed class OpenRouterClient
{
    private readonly ICredentialProvider _creds;
    private readonly HttpClient _http;
    public OpenRouterClient(ICredentialProvider creds, HttpClient? http = null)
    {
        _creds = creds;
        _http = http ?? HttpHelpers.CreateClient(TimeSpan.FromSeconds(15));
    }

    public static string NormalizeKey(string key) => key.Trim();

    /// <summary>Parse one /api/v1/key response (Mac fields: usage, usage_daily,
    /// usage_weekly, usage_monthly, limit, limit_remaining, is_free_tier).</summary>
    public static (double Usage, double UsageDaily, double UsageWeekly, double UsageMonthly,
        double? Limit, double? Remaining, bool FreeTier) ParseUsage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var data = JsonUtil.Get(root, "data") ?? root;
            double Finite(double v) => double.IsFinite(v) ? v : 0;
            var limit = JsonUtil.GetDouble(data, "limit", double.NaN);
            var remaining = JsonUtil.GetDouble(data, "limit_remaining", double.NaN);
            return (Finite(JsonUtil.GetDouble(data, "usage", 0)),
                Finite(JsonUtil.GetDouble(data, "usage_daily", 0)),
                Finite(JsonUtil.GetDouble(data, "usage_weekly", 0)),
                Finite(JsonUtil.GetDouble(data, "usage_monthly", 0)),
                double.IsFinite(limit) ? limit : null,
                double.IsFinite(remaining) ? remaining : null,
                JsonUtil.GetBool(data, "is_free_tier"));
        }
        catch { return (0, 0, 0, 0, null, null, false); }
    }

    public async Task<List<(string Key, double UsageDaily, string? Error)>> RefreshAllAsync(
        IEnumerable<string> keys, CancellationToken ct = default)
    {
        var results = new List<(string, double, string?)>();
        foreach (var raw in keys)
        {
            var key = NormalizeKey(raw);
            if (string.IsNullOrEmpty(key)) continue;
            var (status, body, _) = await HttpHelpers.GetAsync(_http, "https://openrouter.ai/api/v1/key", req =>
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                req.Headers.UserAgent.ParseAdd("ToastMonitor");
            }, ct);
            if (status is null || body is null) results.Add((key, 0, "network error"));
            else results.Add((key, ParseUsage(body).UsageDaily, null));
        }
        return results;
    }
}

public sealed class DeepSeekBillingClient
{
    private readonly ICredentialProvider _creds;
    private readonly HttpClient _http;
    public DeepSeekBillingClient(ICredentialProvider creds, HttpClient? http = null)
    {
        _creds = creds;
        _http = http ?? HttpHelpers.CreateClient(TimeSpan.FromSeconds(15));
    }

    public static (string Currency, double Balance, double? GrantedBalance, double? ToppedUpBalance) ParseBalance(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var info = JsonUtil.Get(root, "balance_infos") ?? root;
        string currency = "CNY";
        double balance = 0, granted = 0, topped = 0;
        if (info.ValueKind == JsonValueKind.Array && info.EnumerateArray().Any())
        {
            var first = info.EnumerateArray().First();
            currency = JsonUtil.GetString(first, "currency") ?? "CNY";
            balance = JsonUtil.GetDouble(first, "total_balance", 0);
            granted = JsonUtil.GetDouble(first, "granted_balance", 0);
            topped = JsonUtil.GetDouble(first, "topped_up_balance", 0);
        }
        else
        {
            balance = JsonUtil.GetDouble(info, "total_balance", 0);
            granted = JsonUtil.GetDouble(info, "granted_balance", 0);
            topped = JsonUtil.GetDouble(info, "topped_up_balance", 0);
        }
        return (currency, balance, granted, topped);
    }

    public async Task<(string Currency, double Balance, string? Error, bool AuthExpired)> RefreshAsync(CancellationToken ct = default)
    {
        var token = await _creds.GetTokenAsync("deepseek");
        if (token is null) return ("CNY", 0, "no token", true);
        // Mac DeepSeekBilling: platform accounts use /api/v0/users/get_user_summary,
        // API-key accounts use https://api.deepseek.com/user/balance (balance_infos).
        var (status, body, _) = await HttpHelpers.GetAsync(_http, "https://api.deepseek.com/user/balance", req =>
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.ParseAdd("application/json");
            req.Headers.UserAgent.ParseAdd("ToastMonitor");
        }, ct);
        if (status is null) return ("CNY", 0, "network error", false);
        if (status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Forbidden)
            return ("CNY", 0, "login expired — reconfigure", true);
        if (body is null) return ("CNY", 0, $"HTTP {(int)status.Value}", false);
        try
        {
            var (currency, balance, _, _) = ParseBalance(body);
            return (currency, balance, null, false);
        }
        catch { return ("CNY", 0, "parse error", false); }
    }
}

public sealed record OpencodeGoWindow(double PercentUsed, long? ResetAtSeconds);

public static class OpenCodeGoParsing
{
    /// <summary>Parse the opencode.ai workspace/go page (SolidJS SSR hydration data slots).</summary>
    public static (OpencodeGoWindow? Rolling, OpencodeGoWindow? Weekly, OpencodeGoWindow? Monthly) Parse(string html)
    {
        OpencodeGoWindow? Window(string label)
        {
            var idx = html.IndexOf(label, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            var window = html.Substring(idx, Math.Min(2000, html.Length - idx));
            var pct = ParsePercent(window);
            var reset = ParseHumanDuration(window);
            return pct is null ? null : new OpencodeGoWindow(pct.Value, reset);
        }
        return (Window("rolling"), Window("weekly"), Window("monthly"));
    }

    public static double? ParsePercent(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, @"(\d+(?:\.\d+)?)\s*%");
        if (!m.Success) return null;
        var v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return v >= 0 && v <= 100 ? v : null;
    }

    /// <summary>Human reset text like "resets in 3h 12m" -&gt; unix seconds from now.</summary>
    public static long? ParseHumanDuration(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, @"resets?\s+in\s+((?:\d+\s*[dhm]\s*)+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        long seconds = 0;
        foreach (System.Text.RegularExpressions.Match part in System.Text.RegularExpressions.Regex.Matches(m.Groups[1].Value, @"(\d+)\s*([dhm])"))
        {
            var n = long.Parse(part.Groups[1].Value);
            seconds += part.Groups[2].Value switch { "d" => n * 86400, "h" => n * 3600, _ => n * 60 };
        }
        return seconds > 0 ? JsonUtil.NowSeconds() + seconds : null;
    }
}

public sealed class OpenCodeGoClient
{
    private readonly ICredentialProvider _creds;
    private readonly HttpClient _http;
    public OpenCodeGoClient(ICredentialProvider creds, HttpClient? http = null)
    {
        _creds = creds;
        _http = http ?? HttpHelpers.CreateClient(TimeSpan.FromSeconds(15));
    }

    public async Task<(OpencodeGoWindow? Rolling, OpencodeGoWindow? Weekly, OpencodeGoWindow? Monthly, string? Error)> RefreshAsync(
        string workspaceId, CancellationToken ct = default)
    {
        var cookie = await _creds.GetTokenAsync("opencodego");
        var url = "https://opencode.ai/workspace/" + Uri.EscapeDataString(workspaceId) + "/go";
        var (status, body, _) = await HttpHelpers.GetAsync(_http, url, req =>
        {
            if (!string.IsNullOrEmpty(cookie)) req.Headers.Add("Cookie", cookie);
            req.Headers.UserAgent.ParseAdd("ToastMonitor");
        }, ct);
        if (status is null || body is null) return (null, null, null, "network error");
        if (status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Forbidden)
            return (null, null, null, "login expired — reconfigure");
        var (r, w, m) = OpenCodeGoParsing.Parse(body);
        return (r, w, m, null);
    }
}

/// <summary>Hermes remote feed: shares HermesUsageBaseline with the local parser so a session
/// is counted once whichever source reports it. The feed itself (VPS URL/credentials) is
/// injected; stage 2 ports baseline-sharing + feed parsing, not live credentials.</summary>
public sealed class HermesRemoteClient
{
    private readonly ICredentialProvider _creds;
    private readonly HttpClient _http;
    public HermesRemoteClient(ICredentialProvider creds, HttpClient? http = null)
    {
        _creds = creds;
        _http = http ?? HttpHelpers.CreateClient(TimeSpan.FromSeconds(15));
    }

    public static List<(string Session, string? Model, string? Provider, string? BaseUrl, HermesUsageBaseline.Counters Counters, long LastSeen)> ParseFeed(string json)
    {
        var outList = new List<(string, string?, string?, string?, HermesUsageBaseline.Counters, long)>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray()
                : (JsonUtil.Get(root, "sessions")?.EnumerateArray() ?? Enumerable.Empty<JsonElement>());
            foreach (var item in items)
            {
                var sid = JsonUtil.GetString(item, "session_id") ?? JsonUtil.GetString(item, "id");
                if (string.IsNullOrEmpty(sid)) continue;
                var counters = new HermesUsageBaseline.Counters(
                    Math.Max(JsonUtil.GetLong(item, "input_tokens"), 0),
                    Math.Max(JsonUtil.GetLong(item, "output_tokens"), 0),
                    Math.Max(JsonUtil.GetLong(item, "reasoning_tokens"), 0),
                    Math.Max(JsonUtil.GetLong(item, "cache_read_tokens"), 0),
                    Math.Max(JsonUtil.GetLong(item, "cache_write_tokens"), 0));
                outList.Add((sid!, JsonUtil.GetString(item, "model"), JsonUtil.GetString(item, "billing_provider"),
                    JsonUtil.GetString(item, "billing_base_url"),
                    counters, JsonUtil.Sec(JsonUtil.GetLong(item, "last_seen"))));
            }
        }
        catch { /* malformed feed: no rows, like the Mac client */ }
        return outList;
    }

    /// <summary>Mac feed-URL rule: HTTPS anywhere, or HTTP only for localhost /
    /// private ranges / Tailscale CGNAT (100.64.0.0/10); max 2048 chars, no control chars.</summary>
    public static bool IsAllowedFeedUrl(string url)
    {
        if (string.IsNullOrEmpty(url) || url.Length > 2048 || url.Any(char.IsControl)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme == "https") return true;
        if (uri.Scheme != "http") return false;
        var host = uri.Host;
        if (host == "localhost") return true;
        if (System.Net.IPAddress.TryParse(host, out var ip))
        {
            var b = ip.GetAddressBytes();
            if (b.Length == 4)
            {
                if (b[0] == 10) return true;
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
                if (b[0] == 192 && b[1] == 168) return true;
                if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true; // CGNAT
                if (b[0] == 127) return true;
            }
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal;
        }
        return false;
    }

    public async Task<(List<(string Session, string? Model, string? Provider, string? BaseUrl, HermesUsageBaseline.Counters Counters, long LastSeen)> Rows, string? Error)> FetchFeedAsync(
        string url, CancellationToken ct = default)
    {
        if (!IsAllowedFeedUrl(url)) return (new(), "Feed URL invalid or unsafe (HTTPS or private range only)");
        var (status, body, _) = await HttpHelpers.GetAsync(_http, url, req =>
        {
            req.Headers.UserAgent.ParseAdd("ToastMonitor");
        }, ct);
        if (status is null || body is null) return (new(), "network error");
        if (status != HttpStatusCode.OK) return (new(), $"HTTP {(int)status.Value}");
        return (ParseFeed(body), null);
    }

    /// <summary>Apply one feed row against the shared baselines; returns the delta turn or null.</summary>
    public static TurnRecord? ApplyRow(IParserStateStore store, (string Session, string? Model, string? Provider, string? BaseUrl, HermesUsageBaseline.Counters Counters, long LastSeen) row)
    {
        var key = HermesUsageBaseline.Key(row.Session, row.Model, row.Provider, row.BaseUrl);
        var totals = store.SessionTotals();
        var localKey = HermesUsageBaseline.LocalTotalsKey(key);
        var local = totals.TryGetValue(localKey, out var lt)
            ? new long[] { lt.Input, lt.Output, lt.Reasoning, lt.CacheRead, lt.CacheWrite, 0 } : null;
        var prev = HermesUsageBaseline.Merge(row.Counters, new[] { local, HermesUsageBaseline.Parse(store.GetSetting(key)) });
        var step = HermesUsageBaseline.Advance(row.Counters, prev);
        var b = step.Base;
        store.SetSetting(key, HermesUsageBaseline.Encode(b));
        store.SetSessionTotals(localKey, "hermes", b.Input, b.Output, b.Reasoning, b.CacheRead, b.CacheWrite, 0, row.LastSeen);
        var d = step.Delta;
        if (d.Input + d.Output + d.Reasoning + d.CacheRead + d.CacheWrite <= 0) return null;
        return new TurnRecord(ToolKind.Hermes, row.Session, null, row.Model, row.LastSeen,
            d.Input, d.Output, d.Reasoning, d.CacheRead, d.CacheWrite, 0, provider: row.Provider,
            eventId: $"hermes-remote:{key}:{row.Counters.Input}:{row.Counters.Output}:{row.Counters.Reasoning}:{row.Counters.CacheRead}:{row.Counters.CacheWrite}",
            costQuality: "unknown");
    }
}

/// <summary>Command Code quota (Mac CommandCodeQuotaClient): billing API + plan catalog.</summary>
public sealed class CommandCodeQuotaClient
{
    public const string CreditsUrl = "https://api.commandcode.ai/internal/billing/credits";
    public const string SubscriptionsUrl = "https://api.commandcode.ai/internal/billing/subscriptions";
    private readonly ICredentialProvider _creds;
    private readonly HttpClient _http;
    public CommandCodeQuotaClient(ICredentialProvider creds, HttpClient? http = null)
    {
        _creds = creds;
        _http = http ?? HttpHelpers.CreateClient(TimeSpan.FromSeconds(15));
    }

    public static (double Remaining, double Total, string? PlanId, long? ResetAt) ParseCredits(string json, string subscriptionsJson)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var remaining = JsonUtil.GetDouble(root, "remaining", 0);
        var total = JsonUtil.GetDouble(root, "total", 0);
        string? planId = null;
        long? reset = null;
        try
        {
            using var sdoc = JsonDocument.Parse(subscriptionsJson);
            var sroot = sdoc.RootElement;
            var subs = sroot.ValueKind == JsonValueKind.Array ? sroot.EnumerateArray()
                : (JsonUtil.Get(sroot, "subscriptions")?.EnumerateArray() ?? Enumerable.Empty<JsonElement>());
            foreach (var s in subs)
            {
                planId ??= JsonUtil.GetString(s, "plan_id") ?? JsonUtil.GetString(s, "planId");
                var r = JsonUtil.GetLong(s, "reset_at", -1);
                if (r < 0) r = JsonUtil.GetLong(s, "current_period_end", -1);
                if (r > 0) reset = JsonUtil.Sec(r);
            }
        }
        catch { /* subscriptions optional */ }
        return (remaining, total, planId, reset);
    }

    public static (double Used, double Percent) ComputeUsed(double remaining, double total)
    {
        var used = Math.Max(total - remaining, 0);
        return (used, total > 0 ? used / total * 100 : 0);
    }

    public async Task<(double? Remaining, double? Total, string? PlanId, double? AllowanceUsd, string? Error, bool AuthExpired)> RefreshAsync(CancellationToken ct = default)
    {
        var cookie = await _creds.GetTokenAsync("commandcode");
        if (string.IsNullOrEmpty(cookie)) return (null, null, null, null, "no token", true);
        void Headers(HttpRequestMessage req)
        {
            req.Headers.Add("Cookie", cookie);
            req.Headers.Add("Origin", "https://commandcode.ai");
            req.Headers.Add("Referer", "https://commandcode.ai/");
            req.Headers.UserAgent.ParseAdd("ToastMonitor");
        }
        var (status, body, _) = await HttpHelpers.GetAsync(_http, CreditsUrl, Headers, ct);
        if (status is null || body is null) return (null, null, null, null, "network error", false);
        if (status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Forbidden)
            return (null, null, null, null, "login expired — reconfigure", true);
        var (_, subsBody, _) = await HttpHelpers.GetAsync(_http, SubscriptionsUrl, Headers, ct);
        var (remaining, total, planId, _) = ParseCredits(body, subsBody ?? "[]");
        var allowance = planId is null ? null : CommandCodePlanCatalog.Allowance(planId);
        return (remaining, total, planId, allowance,
            planId is not null && allowance is null ? $"unknown plan id: {planId}" : null, false);
    }
}
