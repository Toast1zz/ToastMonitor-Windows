using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ToastMonitor.Collect;

namespace ToastMonitor.Platform;

/// <summary>
/// DeepSeek manual login (Mac DeepSeekBrowserSession replacement).
/// The Mac path opens platform.deepseek.com in a WKWebView and reads the
/// `userToken` from localStorage (plus Apple Events for Chrome/Safari).
/// That path is NOT ported: no WebView2, no embedded browser on Windows.
/// Instead the user pastes either an API key or a platform session token
/// (the `userToken` value from their own browser's localStorage) into
/// Settings; it is stored via DPAPI and validated against the real
/// DeepSeek endpoints below. Nothing is ever logged.
/// </summary>
public static class DeepSeekCredentialText
{
    /// <summary>
    /// Mac Credential.make rules: 20–16384 chars, printable ASCII, no
    /// '"' and no '\'. A pasted JSON string or JSON object is unwrapped
    /// first (object keys tried: userToken, token, secret).
    /// Returns the extracted credential or null when invalid.
    /// </summary>
    public static string? Extract(string? pasted)
    {
        if (string.IsNullOrWhiteSpace(pasted)) return null;
        var text = pasted.Trim();
        if (text.StartsWith("\""))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind == JsonValueKind.String)
                    text = doc.RootElement.GetString() ?? "";
            }
            catch { return null; }
        }
        else if (text.StartsWith("{"))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                string? found = null;
                foreach (var name in new[] { "userToken", "token", "secret" })
                {
                    if (doc.RootElement.TryGetProperty(name, out var el) &&
                        el.ValueKind == JsonValueKind.String)
                    { found = el.GetString(); break; }
                }
                if (found is null) return null;
                text = found;
            }
            catch { return null; }
        }
        if (text.Length < 20 || text.Length > 16384) return null;
        foreach (var c in text)
            if (c < 0x20 || c > 0x7E || c == '"' || c == '\\') return null;
        return text;
    }
}

/// <summary>Platform (web-account) DeepSeek balance + spend, Mac DeepSeekBilling platform kind.</summary>
public sealed class DeepSeekPlatformClient
{
    public const string PlatformOrigin = "https://platform.deepseek.com";
    private const long MaxResponseBytes = 8L * 1024 * 1024;

    private readonly HttpClient _http;
    public DeepSeekPlatformClient(HttpClient? http = null) =>
        _http = http ?? HttpHelpers.CreateClient(TimeSpan.FromSeconds(20));

    public sealed record Wallet(string Currency, decimal Paid, decimal Granted)
    {
        public decimal Total => Paid + Granted;
    }

    public sealed record BalanceResult(IReadOnlyList<Wallet> Wallets, string? Error, bool AuthExpired);

    public async Task<BalanceResult> GetBalanceAsync(string token, CancellationToken ct = default)
    {
        var (status, body) = await GetAsync(PlatformOrigin + "/api/v0/users/get_user_summary", token, ct);
        if (status is null) return new BalanceResult(Array.Empty<Wallet>(), "network error", false);
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return new BalanceResult(Array.Empty<Wallet>(), "login expired — reconfigure", true);
        if (body is null) return new BalanceResult(Array.Empty<Wallet>(), "HTTP " + (int)status.Value, false);
        try { return new BalanceResult(ParseBalance(body), null, false); }
        catch { return new BalanceResult(Array.Empty<Wallet>(), "parse error", false); }
    }

    /// <summary>Spend in [start, end) unix seconds, tz offset seconds; window span must be &lt;= 32 days, |offset| &lt;= 18h (Mac guards).</summary>
    public async Task<(IReadOnlyList<(string Currency, decimal Amount)> Amounts, string? Error, bool AuthExpired)>
        GetSpendAsync(string token, long start, long end, int tzOffsetSeconds, CancellationToken ct = default)
    {
        if (end <= start || end - start > 32L * 86400 || Math.Abs(tzOffsetSeconds) > 18 * 3600)
            return (Array.Empty<(string, decimal)>(), "invalid window", false);
        var url = $"{PlatformOrigin}/api/v0/usage/by_api_key/cost?start={start}&end={end}&tz={tzOffsetSeconds}";
        var (status, body) = await GetAsync(url, token, ct);
        if (status is null) return (Array.Empty<(string, decimal)>(), "network error", false);
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return (Array.Empty<(string, decimal)>(), "login expired — reconfigure", true);
        if (body is null) return (Array.Empty<(string, decimal)>(), "HTTP " + (int)status.Value, false);
        try { return (ParseSpend(body, start, end), null, false); }
        catch { return (Array.Empty<(string, decimal)>(), "parse error", false); }
    }

    private async Task<(HttpStatusCode? Status, string? Body)> GetAsync(string url, string token, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.ParseAdd("application/json");
            req.Headers.Add("x-client-platform", "web");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > MaxResponseBytes) return (resp.StatusCode, null);
            return (resp.StatusCode, System.Text.Encoding.UTF8.GetString(bytes));
        }
        catch { return (null, null); }
    }

    /// <summary>Envelope: {"code":0,"data":{"biz_code":0,"biz_data":{normal_wallets,bonus_wallets}}}. Amounts may be strings or numbers.</summary>
    public static IReadOnlyList<Wallet> ParseBalance(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        CheckCode(root.GetProperty("code").GetInt32());
        var data = root.GetProperty("data");
        CheckCode(data.GetProperty("biz_code").GetInt32());
        var summary = data.GetProperty("biz_data");
        var paid = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var granted = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var w in summary.GetProperty("normal_wallets").EnumerateArray())
            Add(paid, Currency(w), Amount(w.GetProperty("balance")));
        foreach (var w in summary.GetProperty("bonus_wallets").EnumerateArray())
            Add(granted, Currency(w), Amount(w.GetProperty("balance")));
        return paid.Keys.Union(granted.Keys, StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal)
            .Select(c => new Wallet(c, paid.GetValueOrDefault(c), granted.GetValueOrDefault(c))).ToList();
    }

    public static IReadOnlyList<(string Currency, decimal Amount)> ParseSpend(string json, long start, long end)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        CheckCode(root.GetProperty("code").GetInt32());
        var data = root.GetProperty("data");
        CheckCode(data.GetProperty("biz_code").GetInt32());
        var payload = data.GetProperty("biz_data");
        if (payload.TryGetProperty("has_more", out var hm) && hm.ValueKind == JsonValueKind.True)
            throw new InvalidDataException("paged spend not supported");
        if (payload.TryGetProperty("next_cursor", out var cur) &&
            cur.ValueKind == JsonValueKind.String && cur.GetString()?.Length > 0)
            throw new InvalidDataException("paged spend not supported");
        var amounts = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var block in payload.GetProperty("data").EnumerateArray())
        {
            var code = Currency(block);
            amounts.TryAdd(code, 0);
            foreach (var series in block.GetProperty("series").EnumerateArray())
                foreach (var bucket in series.GetProperty("buckets").EnumerateArray())
                {
                    var t = bucket.GetProperty("time").GetInt64();
                    if (t >= start && t < end) amounts[code] = checked(amounts[code] + Amount(bucket.GetProperty("cost")));
                }
        }
        return amounts.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private static void CheckCode(int code)
    {
        if (code == 40002 || code == 40003) throw new UnauthorizedAccessException("expired");
        if (code != 0) throw new InvalidDataException("business code " + code);
    }

    private static string Currency(JsonElement el)
    {
        var raw = el.GetProperty("currency").GetString() ?? "";
        if (raw.Length != 3 || raw.Any(c => c < 'A' || c > 'Z')) throw new InvalidDataException("bad currency");
        return raw;
    }

    private static void Add(Dictionary<string, decimal> map, string code, decimal v)
    {
        map[code] = map.TryGetValue(code, out var cur) ? checked(cur + v) : v;
    }

    private static decimal Amount(JsonElement el)
    {
        decimal v;
        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString() ?? "";
            if (s.Length > 64 || !decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                throw new InvalidDataException("bad amount");
        }
        else if (el.ValueKind == JsonValueKind.Number) v = el.GetDecimal();
        else throw new InvalidDataException("bad amount");
        if (v < -1_000_000_000_000m || v > 1_000_000_000_000m) throw new InvalidDataException("amount out of range");
        return v;
    }
}

/// <summary>Settings-facing DeepSeek manual login: validate + store via DPAPI, report balance/spend text.</summary>
public sealed class DeepSeekManualService
{
    private readonly ICredentialProvider _creds;
    private readonly DeepSeekPlatformClient _platform;
    private readonly Func<string, DeepSeekBillingClient> _apiClientFactory;

    public DeepSeekManualService(ICredentialProvider creds, DeepSeekPlatformClient? platform = null,
        Func<string, DeepSeekBillingClient>? apiClientFactory = null, HttpClient? http = null)
    {
        _creds = creds;
        _platform = platform ?? new DeepSeekPlatformClient(http);
        _apiClientFactory = apiClientFactory ?? (key => new DeepSeekBillingClient(new SingleKeyProvider(key), http));
    }

    private sealed class SingleKeyProvider : ICredentialProvider
    {
        private readonly string _key;
        public SingleKeyProvider(string key) => _key = key;
        public Task<string?> GetTokenAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<string?>(key == "deepseek" ? _key : null);
        public Task SetTokenAsync(string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Validate pasted text and store it under the DPAPI key for the kind. Returns an error string or null.</summary>
    public async Task<string?> SaveAsync(string kind, string pasted, CancellationToken ct = default)
    {
        var extracted = DeepSeekCredentialText.Extract(pasted);
        if (extracted is null) return "Credential looks invalid (expected 20–16384 printable ASCII characters, optionally pasted as JSON).";
        await _creds.SetTokenAsync(kind == "platform" ? "deepseek_platform" : "deepseek", extracted, ct);
        return null;
    }

    public Task ClearAsync(string kind, CancellationToken ct = default) =>
        _creds.SetTokenAsync(kind == "platform" ? "deepseek_platform" : "deepseek", null, ct);

    /// <summary>Validate the stored credential against the live endpoint; returns (ok, summary, error).</summary>
    public async Task<(bool Ok, string Summary, string? Error)> ValidateAsync(string kind, CancellationToken ct = default)
    {
        if (kind == "platform")
        {
            var token = await _creds.GetTokenAsync("deepseek_platform", ct);
            if (token is null) return (false, "", "Not configured");
            var result = await _platform.GetBalanceAsync(token, ct);
            if (result.Error is not null) return (false, "", result.Error);
            var text = result.Wallets.Count == 0 ? "No billed usage"
                : string.Join(" / ", result.Wallets.Select(w => $"{w.Currency} {w.Total:F2}"));
            return (true, "Balance " + text, null);
        }
        var apiKey = await _creds.GetTokenAsync("deepseek", ct);
        if (apiKey is null) return (false, "", "Not configured");
        var (currency, balance, error, _) = await _apiClientFactory(apiKey).RefreshAsync(ct);
        return error is null ? (true, $"Balance {currency} {balance:F2}", null) : (false, "", error);
    }
}
