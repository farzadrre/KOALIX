using Koalix.App.Services;
using Koalix.Infrastructure;

namespace Koalix.App.Controls;

/// <summary>
/// Small live UI preferences bridge (v0.4.14): settings that controls read directly, outside of any
/// DataContext. Currently holds the connect-button shape chosen on the Settings page
/// ("ui.connectShape": "rounded" = the rounded-square tile, "circle" = the classic orb).
/// <see cref="Changed"/> lets every live ConnectOrb re-shape itself the moment the user flips the option —
/// the same pattern ThemeService.Changed uses for the palette.
/// </summary>
public static class UiPrefs
{
    private const string Key = "ui.connectShape";
    private const string MotionKey = "ui.connectMotion";
    private static bool _loaded;
    private static bool _circle;
    private static string _shape = "rounded";
    private static string _motion = "water";

    /// <summary>v0.4.54 — «شکل دکمه اتصال هم چند مدل کن»: rounded (default), circle, hex, shield, diamond.</summary>
    public static string Shape
    {
        get
        {
            if (!_loaded) Load();
            return _shape;
        }
    }

    /// <summary>True → the big connect button renders as a circle (the pre-0.4.13 orb); kept for older callers.</summary>
    public static bool CircleConnect
    {
        get
        {
            if (!_loaded) Load();
            return _circle;
        }
    }

    /// <summary>v0.4.54 — «داخلش انیمیشن و متحرک بودنش هم چن مدل بکن بشه انتخاب کرد»: the liquid inside the core —
    /// water (the floating waves), breathe (the aura breathes), pulse (fast rings), orbit (two riders circling), none.</summary>
    public static string Motion
    {
        get
        {
            if (!_loaded) Load();
            return _motion;
        }
    }

    /// <summary>Raised after <see cref="Set"/> flips the shape or the motion.</summary>
    public static event Action? Changed;

    private static void Load()
    {
        _loaded = true;
        try
        {
            _shape = AppHost.Get<SettingsRepository>().Get(Key) is { Length: > 0 } s && s is "circle" or "hex" or "shield" or "diamond" ? s : "rounded";
            _circle = _shape == "circle";
            _motion = AppHost.Get<SettingsRepository>().Get(MotionKey) is { Length: > 0 } m && m is "breathe" or "pulse" or "orbit" or "none" or "heartbeat" or "tide" or "storm" or "galaxy" or "vortex" ? m : "water"; // v0.4.56 — +5 motions
        }
        catch { /* DI not ready yet (design time) — stay on the defaults. */ }
    }

    /// <summary>Called by SettingsViewModel when the user picks a shape/motion; persists nothing (the VM owns storage).</summary>
    public static void Set(bool circle)
    {
        Set(circle ? "circle" : "rounded", Motion);
    }

    public static void Set(string shape, string motion)
    {
        if (!_loaded) Load();
        var changed = false;
        if (_shape != shape) { _shape = shape; _circle = shape == "circle"; changed = true; }
        if (_motion != motion) { _motion = motion; changed = true; }
        if (changed) Changed?.Invoke();
    }
}
