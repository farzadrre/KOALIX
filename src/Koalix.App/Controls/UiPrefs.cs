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
    private static bool _loaded;
    private static bool _circle;

    /// <summary>True → the big connect button renders as a circle (the pre-0.4.13 orb); false → rounded square (default).</summary>
    public static bool CircleConnect
    {
        get
        {
            if (!_loaded) Load();
            return _circle;
        }
    }

    /// <summary>Raised after <see cref="Set"/> flips the shape.</summary>
    public static event Action? Changed;

    private static void Load()
    {
        _loaded = true;
        try { _circle = AppHost.Get<SettingsRepository>().Get(Key) == "circle"; }
        catch { /* DI not ready yet (design time) — stay on the default rounded square. */ }
    }

    /// <summary>Called by SettingsViewModel when the user picks a shape; persists nothing (the VM owns storage).</summary>
    public static void Set(bool circle)
    {
        if (!_loaded) Load();
        if (_circle == circle) return;
        _circle = circle;
        Changed?.Invoke();
    }
}
