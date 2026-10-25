using ToastMonitor.Collect;
using ToastMonitor.Core;

namespace ToastMonitor.ViewModels;

/// <summary>
/// Live quota/balance refresh (Mac quota clients' State equivalent).
/// Credential sourcing is deliberately file-based here: platform secure
/// storage (Keychain -> Credential Manager) is the platform stage, not
/// this stage. Tokens are never stored in view models and never logged.
/// Refresh is best-effort: any client failure becomes a row error, never
/// an exception out of RefreshAllAsync.
/// </summary>
public sealed class QuotaService
{
    private readonly ICredentialProvider _creds;
    private readonly Func<string, string?> _openRouterKeys; // returns newline-separated keys from settings, or null
    private readonly Func<string, string?> _settings; // settings reader for go workspace id

    public QuotaService(ICredentialProvider? creds = null, Func<string, string?>? openRouterKeys = null, Func<string, string?>? settings = null)
    {
        _creds = creds ?? new FileCredentialProvider(key => Task.FromResult(key switch
        {
            "claude" => CredentialFiles.ReadClaudeCredentials().Token,
            "codex" => CredentialFiles.ReadCodexToken(),
            "grok" => CredentialFiles.ReadGrokAuth().Token,
            _ => (string?)null,
        }));
        _openRouterKeys = openRouterKeys ?? (_ => null);
        _settings = settings ?? (_ => null);
    }

    public sealed record QuotaRefreshResult(List<QuotaRowViewModel> Rows, long FetchedAt);

    public async Task<QuotaRefreshResult> RefreshAllAsync(bool showRemaining, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var rows = new List<QuotaRowViewModel>();
        try
        {
            var claude = await new ClaudeQuotaClient(_creds).RefreshAsync(ct);
            rows.Add(FromResult("claude", "Claude", claude, showRemaining, now));
        }
        catch (Exception ex) { rows.Add(ErrorRow("claude", "Claude", ex.Message)); }
        try
        {
            var codex = await new CodexQuotaClient(_creds).RefreshAsync(ct);
            rows.Add(FromResult("codex", "Codex", codex, showRemaining, now));
        }
        catch (Exception ex) { rows.Add(ErrorRow("codex", "Codex", ex.Message)); }
        try
        {
            var grok = await new GrokQuotaClient(_creds).RefreshAsync(ct);
            var row = new QuotaRowViewModel { Key = "grok", Name = "Grok", Group = "balance", Connected = grok.Error is null };
            row.StatusText = grok.Balance is null ? (grok.Error ?? "Not configured") : $"Balance {Format.Money(grok.Balance.Value)}";
            row.Error = grok.Error;
            rows.Add(row);
        }
        catch (Exception ex) { rows.Add(ErrorRow("grok", "Grok", ex.Message)); }
        try
        {
            var keysRaw = _openRouterKeys("openrouter_keys");
            var keys = string.IsNullOrWhiteSpace(keysRaw) ? Array.Empty<string>()
                : keysRaw.Split(new[] { '\n', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (keys.Length > 0)
            {
                var results = await new OpenRouterClient(_creds).RefreshAllAsync(keys, ct);
                var total = results.Sum(r => r.UsageDaily);
                var row = new QuotaRowViewModel { Key = "openrouter", Name = "OpenRouter", Group = "balance", Connected = results.All(r => r.Error is null) };
                row.StatusText = $"Today {Format.Money(total)}";
                row.Error = string.Join("; ", results.Where(r => r.Error is not null).Select(r => r.Error).Distinct());
                if (string.IsNullOrEmpty(row.Error)) row.Error = null;
                rows.Add(row);
            }
            else
            {
                rows.Add(new QuotaRowViewModel { Key = "openrouter", Name = "OpenRouter", Group = "balance", Connected = false, StatusText = "Not configured" });
            }
        }
        catch (Exception ex) { rows.Add(ErrorRow("openrouter", "OpenRouter", ex.Message)); }
        try
        {
            var ds = await new DeepSeekBillingClient(_creds).RefreshAsync(ct);
            var row = new QuotaRowViewModel { Key = "deepseek", Name = "DeepSeek", Group = "balance", Connected = ds.Error is null };
            row.StatusText = ds.Error is null ? $"Balance {ds.Currency} {ds.Balance:F2}" : ds.Error;
            row.Error = ds.Error;
            rows.Add(row);
        }
        catch (Exception ex) { rows.Add(ErrorRow("deepseek", "DeepSeek", ex.Message)); }
        try
        {
            var cc = await new CommandCodeQuotaClient(_creds).RefreshAsync(ct);
            var row = new QuotaRowViewModel { Key = "cc", Name = "Command Code", Group = "quota", Connected = cc.Error is null };
            if (cc.Error is null && cc.Remaining is not null && cc.Total is not null && cc.Total.Value > 0)
            {
                var used = CommandCodeQuotaClient.ComputeUsed(cc.Remaining.Value, cc.Total.Value);
                // CommandCode RefreshAsync carries no reset timestamp (its ParseCredits reset is not surfaced), so no reset text.
                var window = new QuotaWindowRow("Credits", used.Percent, null);
                row.Windows.Add(window);
                row.StatusText = QuotaRowViewModel.Headline(window, showRemaining, "credits");
                row.Critical = window.RemainingPercent < 20;
            }
            else { row.StatusText = cc.Error ?? "Not configured"; row.Error = cc.Error; }
            rows.Add(row);
        }
        catch (Exception ex) { rows.Add(ErrorRow("cc", "Command Code", ex.Message)); }
        try
        {
            var workspaceId = _settings("go_workspace_id");
            if (string.IsNullOrWhiteSpace(workspaceId))
            {
                rows.Add(new QuotaRowViewModel { Key = "go", Name = "OpenCode Go", Group = "quota", Connected = false, StatusText = "Not configured" });
                return new QuotaRefreshResult(rows, now);
            }
            var go = await new OpenCodeGoClient(_creds).RefreshAsync(workspaceId, ct);
            var row = new QuotaRowViewModel { Key = "go", Name = "OpenCode Go", Group = "quota", Connected = go.Error is null };
            if (go.Error is null)
            {
                AddWindow(row, "Rolling", go.Rolling, now);
                AddWindow(row, "Weekly", go.Weekly, now);
                AddWindow(row, "Monthly", go.Monthly, now);
                if (row.Windows.Count > 0)
                {
                    row.StatusText = QuotaRowViewModel.Headline(row.Windows[0], showRemaining, row.Windows[0].Label);
                    row.Critical = row.Windows[0].RemainingPercent < 20;
                }
                else row.StatusText = "No windows";
            }
            else { row.StatusText = go.Error; row.Error = go.Error; }
            rows.Add(row);
        }
        catch (Exception ex) { rows.Add(ErrorRow("go", "OpenCode Go", ex.Message)); }
        return new QuotaRefreshResult(rows, now);
    }

    private static void AddWindow(QuotaRowViewModel row, string label, OpencodeGoWindow? w, long now)
    {
        if (w is null) return;
        row.Windows.Add(new QuotaWindowRow(label, w.PercentUsed, QuotaRowViewModel.ResetText(w.ResetAtSeconds, now)));
    }

    private static QuotaRowViewModel FromResult(string key, string name, QuotaResult result, bool showRemaining, long now)
    {
        var row = new QuotaRowViewModel { Key = key, Name = name, Group = "quota", Connected = result.Error is null };
        if (result.Error is not null) { row.StatusText = result.Error; row.Error = result.Error; return row; }
        foreach (var w in result.Windows)
        {
            if (w.PercentUsed is null) continue;
            row.Windows.Add(new QuotaWindowRow(w.Name, w.PercentUsed.Value, QuotaRowViewModel.ResetText(w.ResetAtSeconds, now)));
        }
        if (row.Windows.Count > 0)
        {
            var primary = row.Windows[0];
            row.StatusText = QuotaRowViewModel.Headline(primary, showRemaining, primary.Label);
            row.Critical = primary.RemainingPercent < 20;
        }
        else row.StatusText = "No data";
        return row;
    }

    private static QuotaRowViewModel ErrorRow(string key, string name, string message) =>
        new() { Key = key, Name = name, Group = "quota", Connected = false, StatusText = message, Error = message };
}
