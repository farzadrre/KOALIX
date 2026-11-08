using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace Koalix.App.Localization;

/// <summary>Live-switchable localization (fa / en). Bindings to the indexer update instantly; FlowDirection follows the language.</summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc I { get; } = new();
    private Dictionary<string, string> _strings = new();
    private Dictionary<string, string> _fallback = new();
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? LanguageChanged;

    public string Language { get; private set; } = "fa";
    public bool IsRtl => Language == "fa";
    public FlowDirection Flow => IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    private Loc() { _fallback = Load("en"); SetLanguage("fa"); }

    public static Dictionary<string, string> Load(string lang)
    {
        using var s = typeof(Loc).Assembly.GetManifestResourceStream($"Koalix.App.Localization.{lang}.json")
            ?? throw new InvalidOperationException("Missing language " + lang);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(s)!;
    }

    public void SetLanguage(string lang)
    {
        Language = lang is "fa" or "en" ? lang : "en";
        _strings = Language == "en" ? _fallback : Load(Language);
        var culture = new CultureInfo(Language == "fa" ? "fa-IR" : "en-US");
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Flow)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        LanguageChanged?.Invoke();
    }

    /// <summary>Key lookup. Supports "Key:arg" codes produced by the core (e.g. "Dns.Error.InvalidV4:1.2.3").</summary>
    public string this[string key]
    {
        get
        {
            var arg = (string?)null;
            var idx = key.IndexOf(':');
            if (idx > 0 && !_strings.ContainsKey(key)) { arg = key[(idx + 1)..]; key = key[..idx]; }
            var s = _strings.TryGetValue(key, out var v) ? v : _fallback.TryGetValue(key, out var f) ? f : key;
            return arg is null ? s : $"{s}: {arg}";
        }
    }

    public string Format(string key, params object[] args) => string.Format(CultureInfo.CurrentUICulture, this[key], args);
}

/// <summary>XAML: Text="{l:T Nav.Dashboard}"</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;
    public override object ProvideValue(IServiceProvider sp) =>
        new Binding($"[{Key}]") { Source = Loc.I, Mode = BindingMode.OneWay }.ProvideValue(sp);
}

/// <summary>Converts a localization key (from a view model) to text; re-evaluated via LanguageChanged → collection refresh.</summary>
public sealed class KeyConverter : IValueConverter
{
    public static KeyConverter Instance { get; } = new();
    public object Convert(object value, Type t, object p, CultureInfo c) => value is string k ? Loc.I[k] : "";
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Converts an enum value to its localized name: key = Parameter + "." + value.</summary>
public sealed class EnumLocConverter : IValueConverter
{
    public static EnumLocConverter Instance { get; } = new();
    public object Convert(object value, Type t, object p, CultureInfo c) => value is null ? "" : Loc.I[$"{p}.{value}"];
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>
/// MultiBinding converter: values[0] = key or enum value, values[1] = Loc.I.Language (forces refresh when language changes).
/// Optional parameter = key prefix.
/// </summary>
public sealed class LocMulti : IMultiValueConverter
{
    public static LocMulti Instance { get; } = new();
    public object Convert(object[] values, Type t, object? p, CultureInfo c) =>
        values.Length == 0 || values[0] is null || values[0] == DependencyProperty.UnsetValue ? "" : Loc.I[p is string prefix && prefix.Length > 0 ? $"{prefix}.{values[0]}" : values[0].ToString()!];
    public object[] ConvertBack(object value, Type[] t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class JoinConverter : IValueConverter
{
    public static JoinConverter Instance { get; } = new();
    public object Convert(object value, Type t, object p, CultureInfo c) => value is IEnumerable<string> s ? string.Join(", ", s) : "";
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Null/whitespace → Collapsed, otherwise Visible (v0.4.12: tag chips).</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public static NullToCollapsedConverter Instance { get; } = new();
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is string s ? (string.IsNullOrWhiteSpace(s) ? Visibility.Collapsed : Visibility.Visible)
        : value is null ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
