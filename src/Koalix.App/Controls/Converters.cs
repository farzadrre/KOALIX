using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Koalix.App.Controls;

/// <summary>true → Collapsed, false → Visible (v0.4.15: the OpenVPN "get it" label vs the "ready" pill).</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public static InverseBoolToVisibilityConverter Instance { get; } = new();
    public object Convert(object value, Type t, object p, CultureInfo c) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>v0.4.23: "#FF7C5CFF" / "#7C5CFF" → Color — drives the ready-palette chips' colour tiles.</summary>
public sealed class HexToColor : IValueConverter
{
    public static HexToColor Instance { get; } = new();
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        try { return System.Windows.Media.ColorConverter.ConvertFromString(value as string ?? "#00000000"); }
        catch (FormatException) { return System.Windows.Media.Colors.Transparent; }
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>v0.4.38: 0 neutral · 1 good · 2 warm · 3 hot → the themed Subtle / Success / Warning / Danger brush
/// (the floating widget's colour-coded temperatures and ping).</summary>
public sealed class LevelBrushConverter : IValueConverter
{
    public static LevelBrushConverter Instance { get; } = new();
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        var key = value is int i ? i switch { 1 => "SuccessBrush", 2 => "WarningBrush", 3 => "DangerBrush", _ => "SubtleTextBrush" } : "SubtleTextBrush";
        return Application.Current?.TryFindResource(key) ?? System.Windows.Media.Brushes.Gray;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>v0.4.38: 0-100 → a width in px (parameter = full width) for the widget's tiny load bars.</summary>
public sealed class PercentToWidth : IValueConverter
{
    public static PercentToWidth Instance { get; } = new();
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        var full = double.TryParse(p as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 60;
        var v = value is double d ? d : 0;
        return Math.Clamp(v, 0, 100) / 100.0 * full;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
