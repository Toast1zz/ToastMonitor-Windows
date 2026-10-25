using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace ToastMonitor.WinUI;

/// <summary>WinUI has no built-in bool-to-visibility converter usable from XAML resources, so this one is local.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        (value is true) != (parameter as string == "Inverse") ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Visible;
}

/// <summary>
/// Replaces WPF's Binding StringFormat (not supported by WinUI Binding).
/// Parameter is either a .NET format ("N0", "C2", "F0") or a composite
/// format containing {0} (e.g. "本周花费 {0}").
/// </summary>
public sealed class FormatConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is null) return "";
        if (parameter as string == "USD") return ToastMonitor.Core.Format.Money(System.Convert.ToDouble(value));
        if (parameter as string == "DayKey") return ToastMonitor.Core.Format.DayKeyString(System.Convert.ToInt64(value));
        if (parameter is not string fmt || fmt.Length == 0) return value.ToString() ?? "";
        // XAML markup-extension escape: a parameter starting with {0} must be
        // written as {}{0}... in XAML; strip the escape before formatting.
        if (fmt.StartsWith("{}")) fmt = fmt[2..];
        try
        {
            return fmt.Contains("{0}")
                ? string.Format(System.Globalization.CultureInfo.CurrentCulture, fmt, value)
                : string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:" + fmt + "}", value);
        }
        catch
        {
            return value.ToString() ?? "";
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}

/// <summary>Collection bindings update when view models replace their snapshot lists.</summary>
public sealed class CollectionVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var hasItems = value is System.Collections.ICollection { Count: > 0 };
        return hasItems != (parameter as string == "Empty") ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}
