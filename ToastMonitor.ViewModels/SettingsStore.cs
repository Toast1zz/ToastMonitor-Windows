using ToastMonitor.Core;

namespace ToastMonitor.ViewModels;

/// <summary>
/// App settings persisted in the settings table (Mac settings wrapper equivalent).
/// Keys mirror the Mac setting names where they exist; Windows-only keys are prefixed win_.
/// </summary>
public sealed class SettingsStore
{
    public const string PollIntervalSecondsKey = "poll_interval_seconds";
    public const string ShowRemainingKey = "show_remaining"; // "1" = show remaining, "0" = show used (Mac M13 note: number and bar must agree; see QuotaRowViewModel)
    public const string PeriodModeKey = "period_mode"; // "recent" | "calendar"
    public const string WeekStartKey = "week_start"; // "monday" | "sunday"
    public const string TrayFontSizeKey = "win_tray_font_size";
    public const string TrayTextModeKey = "win_tray_text"; // "cost" | "tokens" | "none"
    public const string QuotaRefreshMinutesKey = "quota_refresh_minutes";

    private readonly ToastDatabase _db;

    public SettingsStore(ToastDatabase db) => _db = db;

    public int PollIntervalSeconds
    {
        get => int.TryParse(_db.GetSetting(PollIntervalSecondsKey), out var v) && v >= 1 && v <= 3600 ? v : 5;
        set => _db.SetSetting(PollIntervalSecondsKey, value.ToString());
    }

    public bool ShowRemaining
    {
        get => _db.GetSetting(ShowRemainingKey) != "0";
        set => _db.SetSetting(ShowRemainingKey, value ? "1" : "0");
    }

    public UsagePeriodConfiguration PeriodConfiguration
    {
        get
        {
            var mode = _db.GetSetting(PeriodModeKey) == "calendar" ? UsagePeriodMode.Calendar : UsagePeriodMode.Recent;
            var week = _db.GetSetting(WeekStartKey) == "sunday" ? UsageWeekStart.Sunday : UsageWeekStart.Monday;
            return new UsagePeriodConfiguration(mode, week);
        }
        set
        {
            _db.SetSetting(PeriodModeKey, value.Mode == UsagePeriodMode.Calendar ? "calendar" : "recent");
            _db.SetSetting(WeekStartKey, value.WeekStart == UsageWeekStart.Sunday ? "sunday" : "monday");
        }
    }

    public double TrayFontSize
    {
        get => double.TryParse(_db.GetSetting(TrayFontSizeKey), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 8 && v <= 24 ? v : 12;
        set => _db.SetSetting(TrayFontSizeKey, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public string TrayTextMode
    {
        get => _db.GetSetting(TrayTextModeKey) is "tokens" or "none" ? _db.GetSetting(TrayTextModeKey)! : "cost";
        set => _db.SetSetting(TrayTextModeKey, value);
    }

    public int QuotaRefreshMinutes
    {
        get => int.TryParse(_db.GetSetting(QuotaRefreshMinutesKey), out var v) && v >= 1 && v <= 1440 ? v : 5;
        set => _db.SetSetting(QuotaRefreshMinutesKey, value.ToString());
    }

    public bool GetHideQuotaRow(string key) => _db.GetSetting("hide_quota_row_" + key) == "1";
    public void SetHideQuotaRow(string key, bool value) => _db.SetSetting("hide_quota_row_" + key, value ? "1" : "0");

    public bool GetSourceEnabled(string toolRaw) => _db.GetSetting("src_enabled_" + toolRaw) != "0";
    public void SetSourceEnabled(string toolRaw, bool value) => _db.SetSetting("src_enabled_" + toolRaw, value ? "1" : "0");

    public bool SourceIsRemote(string toolRaw) => (_db.GetSetting("src_" + toolRaw) ?? "local") == "remote";

    public string? GetRaw(string key) => _db.GetSetting(key);
    public void SetRaw(string key, string? value) => _db.SetSetting(key, value);
}
