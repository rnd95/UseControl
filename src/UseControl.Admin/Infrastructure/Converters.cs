using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using UseControl.Admin.ViewModels.Infrastructure;
using UseControl.Windows.Audit;

namespace UseControl.Admin.Infrastructure;

/// <summary>true → Visible, false → Collapsed; with ConverterParameter=Invert the other way round.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value is true;
        if (parameter is "Invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Non-empty text → Visible.</summary>
public sealed class TextToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>A count of 0 → Visible (for "nothing here yet" texts); with ConverterParameter=Invert, visible when not 0.</summary>
public sealed class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is 0) != (parameter is "Invert") ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>
/// Text colour of a yes/no state: true is green; false is grey, or red with the parameter "Danger"
/// (games allowed or not, protection on or off).
/// </summary>
public sealed class OkBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is true ? "Ok" : parameter is "Danger" ? "Danger" : "TextMuted";
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Picks a theme brush for a state: "Text" for the foreground, anything else for the background.</summary>
public sealed class StateBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var fore = parameter is "Text";
        var key = value switch
        {
            AuditStatus.Ok or MessageKind.Success => fore ? "Ok" : "OkLight",
            AuditStatus.Warning or true => fore ? "Warning" : "WarningLight",
            AuditStatus.Fail or MessageKind.Error => fore ? "Danger" : "DangerLight",
            MessageKind.Info => fore ? "Text" : "InfoLight",
            _ => fore ? "Text" : "CardBackground",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Icon of a security-check result (Segoe MDL2 Assets): check mark, exclamation mark, cross.</summary>
public sealed class StatusGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        AuditStatus.Ok => "",
        AuditStatus.Warning => "",
        _ => "",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Hours as shown in the games list: 0.5 → "0,5" in Ukrainian, "0.5" in English.</summary>
public sealed class HoursConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d ? d.ToString("0.##", TranslationSource.Instance.Culture) : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
