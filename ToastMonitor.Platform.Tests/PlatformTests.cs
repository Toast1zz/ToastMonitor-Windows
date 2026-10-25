using ToastMonitor.Collect;
using ToastMonitor.Platform;

namespace ToastMonitor.Platform.Tests;

public class CredentialStoreTests : IDisposable
{
    private readonly string _path;

    public CredentialStoreTests() =>
        _path = Path.Combine(Path.GetTempPath(), "tm-creds-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { File.Delete(_path); } catch { /* best effort */ }
        try { File.Delete(_path + ".tmp"); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Dpapi_RoundTrip_Persists_AndHidesPlaintext()
    {
        const string secret = "sk-test-secret-value-1234567890";
        var store = new DpapiCredentialProvider(_path);
        await store.SetTokenAsync("deepseek", secret);
        Assert.Equal(secret, await store.GetTokenAsync("deepseek"));
        Assert.Contains("deepseek", store.ConfiguredKeys);
        Assert.DoesNotContain(secret, File.ReadAllText(_path));
        // A fresh instance decrypts the same file (same user, same machine).
        var reopened = new DpapiCredentialProvider(_path);
        Assert.Equal(secret, await reopened.GetTokenAsync("deepseek"));
        await reopened.SetTokenAsync("deepseek", null);
        Assert.Null(await reopened.GetTokenAsync("deepseek"));
        Assert.Empty(reopened.ConfiguredKeys);
    }

    [Fact]
    public async Task Dpapi_CorruptFile_ReadsAsEmpty()
    {
        File.WriteAllText(_path, "this is not a credential store");
        var store = new DpapiCredentialProvider(_path);
        Assert.Null(await store.GetTokenAsync("deepseek"));
        Assert.False(store.HasStoredCredentials);
    }
}

public class AutoStartTests
{
    [Fact]
    public void AutoStart_EnableDisable_IsReversible()
    {
        var registry = new InMemoryRegistryStore();
        var service = new AutoStartService(registry, "Test\\Run", @"C:\Apps\ToastMonitor.App.exe");
        Assert.False(service.IsEnabled);
        service.SetEnabled(true);
        Assert.True(service.IsEnabled);
        service.SetEnabled(false);
        Assert.False(service.IsEnabled);
    }

    [Fact]
    public void AutoStart_ForeignValue_DoesNotCountAsEnabled()
    {
        var registry = new InMemoryRegistryStore();
        registry.SetValue("Test\\Run", "ToastMonitor", @"C:\Other\Something.exe");
        var service = new AutoStartService(registry, "Test\\Run", @"C:\Apps\ToastMonitor.App.exe");
        Assert.False(service.IsEnabled);
    }
}

public class NotificationTests
{
    [Fact]
    public void Alerts_FireOnce_ThenRecover()
    {
        var fired = new List<string>();
        var notifier = new TrayAlertNotifier((title, _, t) => fired.Add(t.ToString()));
        Assert.Equal(QuotaAlertPolicy.Transition.Low, notifier.Evaluate("claude", 15, null));
        Assert.Null(notifier.Evaluate("claude", 15, null)); // still low: no repeat
        Assert.Null(notifier.Evaluate("claude", 12, null));
        Assert.Equal(QuotaAlertPolicy.Transition.Recovered, notifier.Evaluate("claude", 50, null));
        Assert.Null(notifier.Evaluate("claude", 55, null));
        Assert.Equal(new[] { "Low", "Recovered" }, fired);
    }

    [Fact]
    public void Alerts_NewResetWindow_DoesNotRepeatWhileStillLow()
    {
        var notifier = new TrayAlertNotifier();
        Assert.Equal(QuotaAlertPolicy.Transition.Low, notifier.Evaluate("codex", 10, 1000));
        // A new reset window >300s later adopts the new window but does
        // NOT re-fire Low while continuously low (Mac BadgeState.update +
        // TransitionOf: the transition is computed from pre-update state,
        // and the still-low reading re-triggers the badge immediately).
        Assert.Null(notifier.Evaluate("codex", 10, 2000));
        Assert.Null(notifier.Evaluate("codex", 10, 2000));
        // Recovered fires on leaving low, but with a known reset the
        // badge stays triggered — so a new Low needs a fresh window
        // read ABOVE threshold first (which clears triggered), then low.
        Assert.Equal(QuotaAlertPolicy.Transition.Recovered, notifier.Evaluate("codex", 50, 2000));
        Assert.Null(notifier.Evaluate("codex", 10, 2000));
        // The fresh-window high reading fires Recovered again (wasLow was
        // still true) and only now clears triggered, so the next low fires.
        Assert.Equal(QuotaAlertPolicy.Transition.Recovered, notifier.Evaluate("codex", 50, 3000));
        Assert.Equal(QuotaAlertPolicy.Transition.Low, notifier.Evaluate("codex", 10, 3000));
    }
}

public class UpdateCheckerTests
{
    [Fact]
    public void ParseLatestVersion_BareAndJson()
    {
        Assert.Equal("1.5.0", UpdateChecker.ParseLatestVersion("1.5.0\n"));
        Assert.Equal("2.0.1", UpdateChecker.ParseLatestVersion("{\"version\": \"v2.0.1\"}"));
        Assert.Equal("3.1.0", UpdateChecker.ParseLatestVersion("{\"latest\": \"3.1.0\"}"));
        Assert.Null(UpdateChecker.ParseLatestVersion("{\"foo\": 1}"));
        Assert.Null(UpdateChecker.ParseLatestVersion(""));
    }

    [Fact]
    public void CompareSemver_OrdersCorrectly()
    {
        Assert.True(UpdateChecker.CompareSemver("1.2.3", "1.2.10") < 0);
        Assert.True(UpdateChecker.CompareSemver("2.0.0", "2.0.0-alpha") > 0);
        Assert.Equal(0, UpdateChecker.CompareSemver("1.0.0", "1.0.0"));
        Assert.True(UpdateChecker.CompareSemver("1.10.0", "1.9.9") > 0);
    }

    [Fact]
    public async Task CheckAsync_EmptyUrl_IsDisabled()
    {
        var checker = new UpdateChecker();
        Assert.Null(await checker.CheckAsync("", "1.0.0"));
        Assert.Null(await checker.CheckAsync(null, "1.0.0"));
    }

    [Fact]
    public async Task CheckAsync_NewerVersion_PromptOnly()
    {
        var checker = new UpdateChecker((_, _) => Task.FromResult("1.5.0"));
        var result = await checker.CheckAsync("https://example.invalid/version", "1.0.0");
        Assert.NotNull(result);
        Assert.True(result.UpdateAvailable);
        Assert.Equal("1.5.0", result.LatestVersion);
    }

    [Fact]
    public async Task CheckAsync_FetchFailure_NeverThrows()
    {
        var checker = new UpdateChecker((_, _) => throw new HttpRequestException("boom"));
        var result = await checker.CheckAsync("https://example.invalid/version", "1.0.0");
        Assert.NotNull(result);
        Assert.False(result.UpdateAvailable);
        Assert.NotNull(result.Note);
    }
}

public class DeepSeekManualTests
{
    [Fact]
    public void Extract_RawAndJsonForms()
    {
        var good = new string('a', 32);
        Assert.Equal(good, DeepSeekCredentialText.Extract(good));
        Assert.Equal(good, DeepSeekCredentialText.Extract("\"" + good + "\""));
        Assert.Equal(good, DeepSeekCredentialText.Extract("{\"userToken\": \"" + good + "\"}"));
        Assert.Null(DeepSeekCredentialText.Extract("short"));
        Assert.Null(DeepSeekCredentialText.Extract(new string('a', 19)));
        Assert.Null(DeepSeekCredentialText.Extract("has\"quote" + new string('a', 25)));
        Assert.Null(DeepSeekCredentialText.Extract("{\"other\": \"value\"}"));
        Assert.Null(DeepSeekCredentialText.Extract(null));
    }

    [Fact]
    public void ParseBalance_PlatformEnvelope_StringAndNumberAmounts()
    {
        const string json = """
            {"code":0,"data":{"biz_code":0,"biz_data":{
              "normal_wallets":[{"currency":"CNY","balance":"12.50"},{"currency":"USD","balance":3}],
              "bonus_wallets":[{"currency":"CNY","balance":"1.25"}]}}}
            """;
        var wallets = DeepSeekPlatformClient.ParseBalance(json);
        Assert.Equal(2, wallets.Count);
        Assert.Equal(13.75m, wallets.First(w => w.Currency == "CNY").Total);
        Assert.Equal(3m, wallets.First(w => w.Currency == "USD").Total);
        Assert.Equal(12.50m, wallets.First(w => w.Currency == "CNY").Paid);
        Assert.Equal(1.25m, wallets.First(w => w.Currency == "CNY").Granted);
    }

    [Fact]
    public void ParseBalance_Expired_Throws()
    {
        const string json = """{"code":40002,"data":{"biz_code":40002,"biz_data":{}}}""";
        Assert.ThrowsAny<Exception>(() => DeepSeekPlatformClient.ParseBalance(json));
    }

    [Fact]
    public void ParseSpend_FiltersToWindow_AndRejectsPaging()
    {
        const string json = """
            {"code":0,"data":{"biz_code":0,"biz_data":{"has_more":false,"data":[
              {"currency":"CNY","series":[{"buckets":[
                {"time":100,"cost":"1.5"},{"time":200,"cost":2.5},{"time":300,"cost":9.0}]}]}]}}}
            """;
        var amounts = DeepSeekPlatformClient.ParseSpend(json, 100, 300);
        Assert.Single(amounts);
        Assert.Equal(4.0m, amounts[0].Amount);
        const string paged = """
            {"code":0,"data":{"biz_code":0,"biz_data":{"has_more":true,"data":[]}}}
            """;
        Assert.ThrowsAny<Exception>(() => DeepSeekPlatformClient.ParseSpend(paged, 0, 100));
    }
}
