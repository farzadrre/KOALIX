using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Koalix.App.Services;

public enum AppTheme { Dark, Light }
public enum Backdrop { Mica, Acrylic, None }

/// <summary>
/// Theme swapping, accent palette generation and Windows 11 window material (Mica/Acrylic via DWMWA_SYSTEMBACKDROP_TYPE,
/// rounded corners via DWMWA_WINDOW_CORNER_PREFERENCE). Falls back to a solid background on Windows 10.
/// </summary>
public sealed class ThemeService
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_SYSTEMBACKDROP_TYPE = 38;
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins m);
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int L, R, T, B; }

    public const string DefaultAccent = "#FF7C5CFF";
    public static event Action? Changed;
    /// <summary>v0.4.23 (user: «وقتی رو حالت دارک یا لایت میزنیم صفحه با موشن و انیمیشن خاصی رنگش عوض شه») —
    /// raised at the TOP of <see cref="Apply"/>, BEFORE any resource changes, so the shell can snapshot the old
    /// look and cross-fade into the new one. Fires for accent-only changes too; the shell checks whether the
    /// light/dark MODE is actually flipping before doing the expensive snapshot.</summary>
    /// <remarks>v0.4.32: the argument says whether light/dark is flipping (true) or only the accent colour (false);
    /// the event is not raised at all when nothing visible changes.</remarks>
    public static event Action<bool>? PreviewApply;
    /// <summary>v0.4.42 — raised after a REAL restyle (light/dark flip, accent or palette pick, style/glass change),
    /// never on the ARGB rainbow ticks. <see cref="Controls.ThemeRefresh"/> uses it to repaint the hand-drawn
    /// controls (map, charts, emblems, hover glow) that read colours only when they render.</summary>
    public static event Action? Restyled;
    public AppTheme Mode { get; private set; } = AppTheme.Dark;
    public Backdrop Backdrop { get; private set; } = Backdrop.Mica;
    public bool BackdropActive { get; private set; }
    public string Accent { get; private set; } = DefaultAccent;

    public void Apply(AppTheme mode, string? accentHex = null)
    {
        var modeFlip = mode != Mode;
        var accentChange = accentHex is not null && !string.Equals(accentHex, Accent, StringComparison.OrdinalIgnoreCase);
        if (modeFlip || accentChange) PreviewApply?.Invoke(modeFlip);
        Mode = mode;
        var dicts = Application.Current.Resources.MergedDictionaries;
        var old = dicts.FirstOrDefault(d => d.Source?.OriginalString.Contains("/Themes/Dark.xaml") == true || d.Source?.OriginalString.Contains("/Themes/Light.xaml") == true);
        var fresh = new ResourceDictionary { Source = new Uri($"pack://application:,,,/KOALIX;component/Themes/{mode}.xaml") };
        if (old is not null) dicts[dicts.IndexOf(old)] = fresh; else dicts.Insert(0, fresh);
        ApplyAccent(accentHex ?? Accent);
        WriteStyle();
        foreach (Window w in Application.Current.Windows) if (!w.AllowsTransparency) ApplyBackdrop(w, Backdrop);
        Changed?.Invoke();
        Restyled?.Invoke();
    }

    /* v0.4.38 - STYLE PRESETS + GLASS (user: ready-made themes with different fonts and corners - neon, minimal,
       retro gaming; plus a full glass mode with acrylic under every card and softer shadows, switchable in Settings).
       A style is corners + fonts + card surface; glass is an orthogonal switch (translucent cards, soft shadows,
       acrylic window). Both live in one merged dictionary re-written on every theme flip; the accent-tied bits (neon
       edge glow, retro outline) are written with the accent so the ARGB palette drives them too. */
    public const string StyleKey = "ui.style", GlassKey = "ui.glass";
    public static readonly string[] Styles = ["default", "neon", "minimal", "retro"];
    /// <summary>Static because <see cref="ApplyFont"/> is static (fonts follow both language and style).</summary>
    public static string StyleName { get; private set; } = "default";
    public bool Glass { get; private set; }
    private ResourceDictionary? _styleDict;
    private Color _accentNow = (Color)ColorConverter.ConvertFromString(DefaultAccent);

    public static string NormalizeStyle(string? s) => s is not null && Styles.Contains(s) ? s : "default";

    public void SetStyle(string style, bool glass)
    {
        StyleName = NormalizeStyle(style);
        Glass = glass;
        WriteStyle();
        WriteAccent(_accentNow);
        try { ApplyFont(Localization.Loc.I.Language); } catch { }
        foreach (Window w in Application.Current.Windows) if (!w.AllowsTransparency) ApplyBackdrop(w, Backdrop);
        Changed?.Invoke();
        Restyled?.Invoke();
    }

    /// <summary>Corner radii per style - card / control.</summary>
    public static (double Card, double Control) Radii(string style) => style switch
    {
        "neon" => (18, 12),
        "minimal" => (8, 6),
        "retro" => (3, 2),
        _ => (16, 10),
    };

    private void WriteStyle()
    {
        var r = new ResourceDictionary();
        var light = Mode == AppTheme.Light;
        var (card, ctl) = Radii(StyleName);
        r["CardRadius"] = new CornerRadius(card);
        r["ControlRadius"] = new CornerRadius(ctl);
        switch (StyleName)
        {
            case "minimal":
                r["CardBrush"] = Frozen(new SolidColorBrush(light ? Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)));
                r["CardBorderBrush"] = Frozen(new SolidColorBrush(light ? Color.FromArgb(0x14, 0, 0, 0) : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)));
                r["AuroraOpacity"] = 0.3;
                break;
            case "retro":
                r["CardBrush"] = Frozen(new SolidColorBrush(light ? Color.FromArgb(0xF5, 0xFB, 0xF8, 0xF0) : Color.FromArgb(0xE0, 0x0C, 0x0E, 0x17)));
                r["AuroraOpacity"] = 0.45;
                break;
            case "neon":
                r["CardBrush"] = Frozen(new LinearGradientBrush(light ? Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x59, 0x14, 0x10, 0x2A),
                    light ? Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x33, 0x08, 0x0A, 0x18), new Point(0, 0), new Point(1, 1)));
                break;
        }
        if (Glass)
        {
            // translucent frosted cards over an acrylic window, with a broad, soft shadow instead of hard edges
            r["CardBrush"] = Frozen(new LinearGradientBrush(light ? Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF),
                light ? Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF), new Point(0, 0), new Point(1, 1)));
            if (StyleName is not ("neon" or "retro"))
                r["CardBorderBrush"] = Frozen(new LinearGradientBrush(Color.FromArgb((byte)(light ? 0xCC : 0x55), 0xFF, 0xFF, 0xFF),
                    Color.FromArgb((byte)(light ? 0x33 : 0x0A), 0xFF, 0xFF, 0xFF), new Point(0, 0), new Point(0, 1)));
            r["ShellTintBrush"] = Frozen(new SolidColorBrush(light ? Color.FromArgb(0x59, 0xF4, 0xF6, 0xFB) : Color.FromArgb(0x6B, 0x0A, 0x0C, 0x13)));
            r["SidebarBrush"] = Frozen(new SolidColorBrush(light ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x33, 0x0E, 0x11, 0x20)));
            if (Controls.Fx.Enabled)
                r["CardEffect"] = Frozen(new DropShadowEffect { BlurRadius = 30, ShadowDepth = 6, Direction = 270, Opacity = light ? 0.16 : 0.32, Color = light ? Color.FromRgb(0x5A, 0x64, 0x80) : Colors.Black });
        }
        var dicts = Application.Current.Resources.MergedDictionaries;
        var at = _styleDict is null ? -1 : dicts.IndexOf(_styleDict);
        if (at >= 0) dicts[at] = r; else dicts.Add(r);
        _styleDict = r;
    }

    /// <summary>Derives the whole accent palette (gradient partner, soft fills, glow, aurora blobs) from one colour.</summary>
    private void ApplyAccent(string hex)
    {
        Color c;
        try { c = (Color)ColorConverter.ConvertFromString(hex); } catch (FormatException) { c = (Color)ColorConverter.ConvertFromString(DefaultAccent); hex = DefaultAccent; }
        Accent = hex;
        // v0.4.37: while the ARGB palette runs, a theme flip keeps the live rainbow hue instead of flashing the base colour.
        WriteAccent(_rgb is not null ? RgbColor(_hue) : c);
    }

    /// <summary>v0.4.37 — the derived accent resources now live in ONE merged dictionary that is swapped as a unit:
    /// a single resource-invalidation walk per change (the old 12 separate top-level writes did 12), which is what
    /// makes the animated ARGB palette cheap enough to tick several times a second.</summary>
    private ResourceDictionary? _accentDict;

    private void WriteAccent(Color c)
    {
        var r = new ResourceDictionary();
        var c2 = ShiftHue(c, 38, 1.08);
        var c3 = ShiftHue(c, -48, 1.0);
        var light = Mode == AppTheme.Light;
        r["AccentColor"] = c;
        r["Accent2Color"] = c2;
        r["AccentBrush"] = Frozen(new SolidColorBrush(c));
        r["Accent2Brush"] = Frozen(new SolidColorBrush(c2));
        r["AccentSoftBrush"] = Frozen(new SolidColorBrush(WithA(c, 0x2E)));
        r["AccentGradientBrush"] = Frozen(new LinearGradientBrush(c, c2, new Point(0, 0), new Point(1, 1)));
        r["AccentTextGradientBrush"] = Frozen(new LinearGradientBrush(Lighten(c, light ? 0 : 0.25), Lighten(c2, light ? 0 : 0.25), new Point(0, 0), new Point(1, 0)));
        r["NavSelectedBrush"] = Frozen(new LinearGradientBrush(WithA(c, 0x40), WithA(c2, 0x0C), new Point(0, 0.5), new Point(1, 0.5)));
        r["AuroraBrush1"] = Frozen(new RadialGradientBrush(WithA(c, (byte)(light ? 0x55 : 0x66)), WithA(c, 0)));
        r["AuroraBrush2"] = Frozen(new RadialGradientBrush(WithA(c2, (byte)(light ? 0x44 : 0x4D)), WithA(c2, 0)));
        r["AuroraBrush3"] = Frozen(new RadialGradientBrush(WithA(c3, (byte)(light ? 0x33 : 0x40)), WithA(c3, 0)));
        r["AccentGlowEffect"] = new DropShadowEffect { Color = c, BlurRadius = 22, ShadowDepth = 0, Opacity = 0.6 };
        _accentNow = c;
        // v0.4.38 - accent-tied style bits: the neon edge + glow and the retro outline follow the (ARGB) accent.
        if (StyleName == "neon")
        {
            r["CardBorderBrush"] = Frozen(new LinearGradientBrush(WithA(c, 0xD0), WithA(c2, 0x40), new Point(0, 0), new Point(1, 1)));
            if (Controls.Fx.Enabled) r["CardEffect"] = Frozen(new DropShadowEffect { Color = c, BlurRadius = 20, ShadowDepth = 0, Opacity = light ? 0.22 : 0.34 });
        }
        else if (StyleName == "retro")
            r["CardBorderBrush"] = Frozen(new SolidColorBrush(WithA(c, 0x8C)));
        var dicts = Application.Current.Resources.MergedDictionaries;
        var at = _accentDict is null ? -1 : dicts.IndexOf(_accentDict);
        if (at >= 0) dicts[at] = r; else dicts.Add(r); // appended last → wins over Dark/Light.xaml
        _accentDict = r;
    }

    /* ─────────── v0.4.37 — the ARGB palette (user: «تو قسمت پالت ها یک پالت argb هم بیاری»): the accent keeps
       gliding around the whole hue wheel like RGB gaming gear. Pauses while the window is hidden/minimized so a
       tray-parked KOALIX costs nothing. ─────────── */
    public const string RgbKey = "ui.argb";
    private DispatcherTimer? _rgb;
    private double _hue;
    private int _rgbTicks;
    public bool RgbOn => _rgb is not null;
    /// <summary>Raised on every rainbow step (≈8 Hz) — for controls that paint the accent by hand (the connect orb).</summary>
    public static event Action? RgbTick;

    public void SetRgb(bool on)
    {
        if (on == RgbOn) return;
        if (on)
        {
            try { _hue = HueOf((Color)ColorConverter.ConvertFromString(Accent)); } catch { _hue = 0; }
            _rgb = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
            _rgb.Tick += (_, _) => StepRgb();
            _rgb.Start();
            StepRgb();
        }
        else
        {
            _rgb!.Stop(); _rgb = null;
            ApplyAccent(Accent); // back to the chosen colour
            Changed?.Invoke();
            Restyled?.Invoke();
        }
    }

    private void StepRgb()
    {
        if (Application.Current?.MainWindow is not { } w || !w.IsVisible || w.WindowState == WindowState.Minimized) return;
        _hue = (_hue + 2.5 * ReactiveFactor()) % 360;
        WriteAccent(RgbColor(_hue));
        RgbTick?.Invoke();
        if (++_rgbTicks % 16 == 0) Changed?.Invoke(); // ~every 2 s: hand-painted controls (skeletons…) catch up
    }

    /* v0.4.38 - REACTIVE ARGB (user: the hue speed follows the download speed or the ping). The dashboard reports
       the live traffic / latency; the multiplier glides toward its target so the wheel accelerates smoothly.
       Stale reports (disconnected) fall back to a slow idle drift. */
    public const string RgbReactiveKey = "ui.argb.reactive";
    public static readonly string[] ReactiveModes = ["off", "speed", "ping"];
    /// <summary>"off" (constant speed) · "speed" (download speed) · "ping" (lower ping = faster).</summary>
    public static string RgbReactive { get; set; } = "off";
    private static double _reactTarget = 1, _react = 1;
    private static DateTime _reactAt;

    /// <summary>0 B/s ~0.4x · 100 KB/s ~2.8x · 1 MB/s ~4x · 10 MB/s+ ~5.2x.</summary>
    public static double SpeedFactor(long downBps) => Math.Min(6, 0.4 + Math.Log10(1 + Math.Max(0, downBps) / 1024.0) * 1.2);
    /// <summary>30 ms ~5x · 100 ms 2.5x · 250 ms 1x · 500 ms+ slow.</summary>
    public static double PingFactor(double? ms) => ms is { } m and > 0 ? Math.Clamp(250 / Math.Max(m, 20), 0.35, 5) : 0.6;

    public static void ReportTraffic(long downBps)
    {
        LiveActivity = Math.Clamp(Math.Log10(1 + Math.Max(0, downBps) / 1024.0) / 4.0, 0, 1);
        if (RgbReactive != "speed") return;
        _reactTarget = SpeedFactor(downBps); _reactAt = DateTime.UtcNow;
    }

    public static void ReportPing(double? ms)
    {
        if (RgbReactive != "ping") return;
        _reactTarget = PingFactor(ms); _reactAt = DateTime.UtcNow;
    }

    /// <summary>v0.4.38 - 0..1 network activity for the animated dashboard background (log-scaled download speed).</summary>
    public static double LiveActivity { get; private set; }

    private static double ReactiveFactor()
    {
        if (RgbReactive is not ("speed" or "ping")) return 1;
        var target = (DateTime.UtcNow - _reactAt).TotalSeconds > 4 ? 0.4 : _reactTarget;
        _react += (target - _react) * 0.12;
        return _react;
    }

    /// <summary>A vivid, readable colour for hue <paramref name="h"/> — a touch darker on the light theme.</summary>
    internal Color RgbColor(double h) => FromHsl(h, 0.88, Mode == AppTheme.Light ? 0.50 : 0.62);

    internal static Color FromHsl(double h, double s, double l)
    {
        h = (h % 360 + 360) % 360;
        var C = (1 - Math.Abs(2 * l - 1)) * s; var X = C * (1 - Math.Abs(h / 60 % 2 - 1)); var m = l - C / 2;
        var (r1, g1, b1) = h switch { < 60 => (C, X, 0d), < 120 => (X, C, 0d), < 180 => (0d, C, X), < 240 => (0d, X, C), < 300 => (X, 0d, C), _ => (C, 0d, X) };
        return Color.FromRgb((byte)Math.Round((r1 + m) * 255), (byte)Math.Round((g1 + m) * 255), (byte)Math.Round((b1 + m) * 255));
    }

    internal static double HueOf(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0, max = Math.Max(r, Math.Max(g, b)), d = max - Math.Min(r, Math.Min(g, b));
        if (d == 0) return 0;
        var h = max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return (h + 360) % 360;
    }

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
    private static Color WithA(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
    private static Color Lighten(Color c, double t) => Color.FromRgb((byte)(c.R + (255 - c.R) * t), (byte)(c.G + (255 - c.G) * t), (byte)(c.B + (255 - c.B) * t));

    internal static Color ShiftHue(Color c, double degrees, double lightness)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), h = 0, s, l = (max + min) / 2;
        var d = max - min;
        s = d == 0 ? 0 : d / (1 - Math.Abs(2 * l - 1));
        if (d != 0) h = max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        h = ((h + degrees) % 360 + 360) % 360; l = Math.Clamp(l * lightness, 0, 0.92);
        var C = (1 - Math.Abs(2 * l - 1)) * s; var X = C * (1 - Math.Abs(h / 60 % 2 - 1)); var m = l - C / 2;
        var (r1, g1, b1) = h switch { < 60 => (C, X, 0d), < 120 => (X, C, 0d), < 180 => (0d, C, X), < 240 => (0d, X, C), < 300 => (X, 0d, C), _ => (C, 0d, X) };
        return Color.FromRgb((byte)Math.Round((r1 + m) * 255), (byte)Math.Round((g1 + m) * 255), (byte)Math.Round((b1 + m) * 255));
    }

    /// <summary>Persian UI uses IRANSansX (Persian digits); English uses Segoe UI Variable (Latin digits).</summary>
    /// <remarks>v0.4.38: the style preset picks the display (title) font - Bahnschrift for neon, light Segoe for
    /// minimal, Cascadia Mono for retro; Persian text keeps IRANSansX / Digikala through the fallback chain.</remarks>
    public static void ApplyFont(string language)
    {
        var r = Application.Current.Resources;
        var pack = new Uri("pack://application:,,,/");
        const string fa = "./Assets/Fonts/#IRANSansXFaNum";
        const string dk = "./Assets/Fonts/#Digikala";
        var (uiEn, dispEn) = StyleName switch
        {
            "neon" => ("Segoe UI Variable Text, Segoe UI", "Bahnschrift, Segoe UI Variable Display, Segoe UI"),
            "minimal" => ("Segoe UI Variable Text, Segoe UI", "Segoe UI Variable Display Light, Segoe UI Light, Segoe UI"),
            "retro" => ("Bahnschrift, Segoe UI", "Cascadia Mono, Consolas, Courier New"),
            _ => ("Segoe UI Variable Text, Segoe UI, Tahoma", "Segoe UI Variable Display, Segoe UI"),
        };
        var dispFa = StyleName switch
        {
            "minimal" => $"{fa}, Segoe UI",
            "retro" => $"Cascadia Mono, {fa}, Consolas",
            _ => $"{dk}, {fa}, Segoe UI Variable Display, Segoe UI",
        };
        r["UiFont"] = language == "fa" ? new FontFamily(pack, $"{fa}, Segoe UI Variable Text, Segoe UI, Tahoma") : new FontFamily(uiEn);
        r["DisplayFont"] = language == "fa" ? new FontFamily(pack, dispFa) : new FontFamily(dispEn);
    }

    public void ApplyBackdrop(Window w, Backdrop backdrop)
    {
        Backdrop = backdrop;
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = Mode == AppTheme.Dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
        int round = 2; // DWMWCP_ROUND — native rounded window corners on Windows 11 (ignored on Windows 10)
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
        BackdropActive = false;
        var effective = Glass ? Backdrop.Acrylic : backdrop; // v0.4.38 glass mode = acrylic window under frosted cards
        if (Environment.OSVersion.Version.Build >= 22621 && effective != Backdrop.None)
        {
            int type = effective == Backdrop.Mica ? 2 : 3; // DWMSBT_MAINWINDOW / DWMSBT_TRANSIENTWINDOW (acrylic)
            var m = new Margins { L = -1, R = -1, T = -1, B = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref m);
            BackdropActive = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, 4) == 0;
        }
        // With an active backdrop the window background must be transparent so the DWM material shows through.
        w.Background = BackdropActive ? Brushes.Transparent : (Brush)Application.Current.Resources["WindowBrush"];
        if (HwndSource.FromHwnd(hwnd) is { } src) src.CompositionTarget.BackgroundColor = BackdropActive ? Colors.Transparent : (Color)Application.Current.Resources["WindowColor"];
    }
}
