using ToastMonitor.Collect;
using ToastMonitor.Core;

namespace ToastMonitor.ViewModels;

/// <summary>
/// Settings window view model (Mac SettingsWindow/SettingsPanes/SourcesSettingsPane equivalent).
/// Credential state is only "configured / not configured" — never a secret value (Mac AccountsSettingsSection rule).
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly AppState _state;
    private int _pollIntervalSeconds;
    private bool _showRemaining;
    private UsagePeriodMode _periodMode;
    private UsageWeekStart _weekStart;
    private double _trayFontSize;
    private string _trayTextMode = "cost";
    private int _quotaRefreshMinutes;
    private List<SourceRow> _sources = new();
    private List<Subscription> _subscriptions = new();
    private bool _autoStartEnabled;
    private string _updateCheckUrl = "";
    private string _deepSeekApiInput = "";
    private string _deepSeekPlatformInput = "";
    private string _deepSeekStatus = "";
    private string _updateStatus = "";
    private string _backupStatus = "";

    public SettingsViewModel(AppState state)
    {
        _state = state;
        Load();
    }

    public sealed class SourceRow : ViewModelBase
    {
        private bool _enabled;
        public SourceRow(string tool, string displayName, bool enabled, bool isRemote, string path, bool credentialConfigured)
        {
            Tool = tool; DisplayName = displayName; _enabled = enabled; IsRemote = isRemote; Path = path; CredentialConfigured = credentialConfigured;
        }
        public string Tool { get; }
        public string DisplayName { get; }
        public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
        public bool IsRemote { get; }
        public string Path { get; }
        public bool CredentialConfigured { get; }
    }

    public int PollIntervalSeconds { get => _pollIntervalSeconds; set => Set(ref _pollIntervalSeconds, value); }
    public bool ShowRemaining { get => _showRemaining; set => Set(ref _showRemaining, value); }
    public UsagePeriodMode PeriodMode { get => _periodMode; set => Set(ref _periodMode, value); }
    public UsageWeekStart WeekStart { get => _weekStart; set => Set(ref _weekStart, value); }
    public double TrayFontSize { get => _trayFontSize; set => Set(ref _trayFontSize, value); }
    public string TrayTextMode { get => _trayTextMode; set => Set(ref _trayTextMode, value); }
    public int QuotaRefreshMinutes { get => _quotaRefreshMinutes; set => Set(ref _quotaRefreshMinutes, value); }
    public List<SourceRow> Sources { get => _sources; private set => Set(ref _sources, value); }
    public List<Subscription> Subscriptions { get => _subscriptions; private set => Set(ref _subscriptions, value); }
    /// <summary>Stage-4 platform settings. AutoStart is a registry switch (not a DB setting); the update URL is empty = check disabled (no official Windows release source is invented).</summary>
    public bool AutoStartEnabled { get => _autoStartEnabled; set => Set(ref _autoStartEnabled, value); }
    public string UpdateCheckUrl { get => _updateCheckUrl; set => Set(ref _updateCheckUrl, value); }
    public string DeepSeekApiInput { get => _deepSeekApiInput; set => Set(ref _deepSeekApiInput, value); }
    public string DeepSeekPlatformInput { get => _deepSeekPlatformInput; set => Set(ref _deepSeekPlatformInput, value); }
    public string DeepSeekStatus { get => _deepSeekStatus; private set => Set(ref _deepSeekStatus, value); }
    public string UpdateStatus { get => _updateStatus; private set => Set(ref _updateStatus, value); }
    public string BackupStatus { get => _backupStatus; private set => Set(ref _backupStatus, value); }
    /// <summary>Presence only — never a secret value.</summary>
    public bool DeepSeekApiConfigured => _state.DpapiStore?.ConfiguredKeys.Contains("deepseek") == true;
    public bool DeepSeekPlatformConfigured => _state.DpapiStore?.ConfiguredKeys.Contains("deepseek_platform") == true;
    public int BackupCount => DataMaintenance.AvailableBackups().Count;
    public string CredentialStorePath => ToastMonitor.Platform.DpapiCredentialProvider.DefaultPath;

    public void Load()
    {
        var s = _state.Settings;
        PollIntervalSeconds = s.PollIntervalSeconds;
        ShowRemaining = s.ShowRemaining;
        var cfg = s.PeriodConfiguration;
        PeriodMode = cfg.Mode;
        WeekStart = cfg.WeekStart;
        TrayFontSize = s.TrayFontSize;
        TrayTextMode = s.TrayTextMode;
        QuotaRefreshMinutes = s.QuotaRefreshMinutes;
        AutoStartEnabled = _state.AutoStart.IsEnabled;
        UpdateCheckUrl = s.GetRaw("win_update_check_url") ?? "";
        Subscriptions = _state.Database.GetSubscriptions();
        Sources = new List<SourceRow>
        {
            Row(ToolKind.Claude, SourceRoots.ClaudeProjects, CredentialConfigured("claude") || CredentialFiles.ReadClaudeCredentials().Token is not null),
            Row(ToolKind.Codex, SourceRoots.CodexSessions, CredentialConfigured("codex") || CredentialFiles.ReadCodexToken() is not null),
            Row(ToolKind.Opencode, SourceRoots.OpenCodeDb, File.Exists(SourceRoots.OpenCodeDb)),
            Row(ToolKind.Hermes, SourceRoots.HermesHome, File.Exists(SourceRoots.HermesDb)),
            Row(ToolKind.Omp, SourceRoots.OmpSessions, Directory.Exists(SourceRoots.OmpSessions)),
            Row(ToolKind.Dsh, SourceRoots.DshHome, Directory.Exists(SourceRoots.DshHome)),
            new SourceRow("openrouter", "OpenRouter", s.GetSourceEnabled("openrouter"), s.SourceIsRemote("openrouter"), "API key in settings", !string.IsNullOrWhiteSpace(s.GetRaw("openrouter_keys"))),
        };
    }

    private bool CredentialConfigured(string key) =>
        _state.DpapiStore?.ConfiguredKeys.Contains(key) == true;

    private SourceRow Row(ToolKind kind, string path, bool credentialConfigured)
    {
        var raw = kind.RawValue();
        return new SourceRow(raw, kind.DisplayName(), _state.Settings.GetSourceEnabled(raw),
            _state.Settings.SourceIsRemote(raw), path, credentialConfigured);
    }

    private string _newSubName = "";
    private string _newSubPrice = "";
    public string NewSubName { get => _newSubName; set => Set(ref _newSubName, value); }
    public string NewSubPrice { get => _newSubPrice; set => Set(ref _newSubPrice, value); }

    public bool AddSubscriptionFromFields()
    {
        if (string.IsNullOrWhiteSpace(NewSubName)) return false;
        if (!double.TryParse(NewSubPrice, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var price)) return false;
        var ok = _state.Database.UpsertSubscription(new Subscription(0, NewSubName.Trim(), "", 0, 0, "monthly", price, "USD"));
        if (ok) { Subscriptions = _state.Database.GetSubscriptions(); NewSubName = ""; NewSubPrice = ""; }
        return ok;
    }

    public bool DeleteSubscriptionById(long id)
    {
        var ok = _state.Database.DeleteSubscription(id);
        if (ok) Subscriptions = _state.Database.GetSubscriptions();
        return ok;
    }

    public void Save()
    {
        var s = _state.Settings;
        s.PollIntervalSeconds = PollIntervalSeconds;
        s.ShowRemaining = ShowRemaining;
        s.PeriodConfiguration = new UsagePeriodConfiguration(PeriodMode, WeekStart);
        s.TrayFontSize = TrayFontSize;
        s.TrayTextMode = TrayTextMode;
        s.QuotaRefreshMinutes = QuotaRefreshMinutes;
        s.SetRaw("win_update_check_url", string.IsNullOrWhiteSpace(UpdateCheckUrl) ? null : UpdateCheckUrl.Trim());
        try { _state.AutoStart.SetEnabled(AutoStartEnabled); } catch { /* registry write failed; setting is re-read on Load */ }
        foreach (var src in Sources) s.SetSourceEnabled(src.Tool, src.Enabled);
        _state.Engine.Poller.BackgroundInterval = TimeSpan.FromSeconds(PollIntervalSeconds);
    }

    // MARK: - Stage-4 platform actions (called from SettingsWindow code-behind; never expose secret values)

    public async Task SaveDeepSeekApiAsync()
    {
        var error = await _state.DeepSeekManual.SaveAsync("api", DeepSeekApiInput);
        DeepSeekApiInput = "";
        DeepSeekStatus = error ?? "DeepSeek API Key 已保存（DPAPI 加密）。";
        Raise(nameof(DeepSeekApiConfigured));
    }

    public async Task SaveDeepSeekPlatformAsync()
    {
        var error = await _state.DeepSeekManual.SaveAsync("platform", DeepSeekPlatformInput);
        DeepSeekPlatformInput = "";
        DeepSeekStatus = error ?? "DeepSeek 会话凭据已保存（DPAPI 加密）。";
        Raise(nameof(DeepSeekPlatformConfigured));
    }

    public async Task ValidateDeepSeekAsync()
    {
        var (ok, summary, error) = await _state.DeepSeekManual.ValidateAsync(
            DeepSeekPlatformConfigured ? "platform" : "api");
        DeepSeekStatus = ok ? "校验通过：" + summary : "校验失败：" + (error ?? "未知错误");
    }

    public async Task CheckForUpdatesAsync()
    {
        var url = _state.Settings.GetRaw("win_update_check_url");
        var result = await _state.UpdateChecker.CheckAsync(url, AppState.CurrentVersion);
        UpdateStatus = result is null
            ? "更新检查未开启：没有配置检查地址（没有官方 Windows 发版源，不编造地址）。当前版本 " + AppState.CurrentVersion + "。"
            : result.LatestVersion is null
                ? "更新检查失败：" + (result.Note ?? "")
                : result.UpdateAvailable
                    ? $"发现新版本 {result.LatestVersion}（当前 {AppState.CurrentVersion}）。Windows 版默认不自动下载、不静默安装，请手动获取。"
                    : $"已是最新版本（{AppState.CurrentVersion}）。";
    }

    public void CreateBackup()
    {
        var path = DataMaintenance.CreateBackup("manual", _state.Database);
        BackupStatus = path is null ? "备份失败：请检查磁盘与备份目录权限。" : "备份已创建：" + path;
        Raise(nameof(BackupCount));
    }

    public void RestoreBackup(string path)
    {
        var snapshot = DataMaintenance.RestoreWithReceipt(path, _state.Database);
        BackupStatus = snapshot is null
            ? "恢复失败：仅接受备份目录内的正规非空 SQLite 备份；恢复前快照未创建时不替换现有数据。"
            : "恢复成功。恢复前快照：" + snapshot + "。请重启应用以刷新全部视图。";
    }

    public void ClearAllData()
    {
        try
        {
            var backup = DataMaintenance.ClearAllData(_state.Database);
            BackupStatus = "已清空用量与历史数据（设置与凭据保留）。清空前备份：" + backup;
        }
        catch (DataMaintenance.MaintenanceException ex) { BackupStatus = "清空失败：" + ex.Message; }
        Raise(nameof(BackupCount));
    }

    public void SetSourceEnabled(string tool, bool enabled)
    {
        var row = Sources.FirstOrDefault(r => r.Tool == tool);
        if (row is not null) row.Enabled = enabled;
    }
}
