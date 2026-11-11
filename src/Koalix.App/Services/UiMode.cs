using CommunityToolkit.Mvvm.ComponentModel;
using Koalix.Infrastructure;

namespace Koalix.App.Services;

/// <summary>
/// v0.4.42 — the Beginner / Advanced UI switch (user: «یک قابلیت انتخاب ui بزاری و بیاریش رو نوار بالای صفحه که تو دید
/// باشه .. مثلا مبتدی و پیشرفته / تو مبتدی همه چیز ساده باشه … پیشرفته مث همین الان با همه قابلیت هاش باشه نمیخوام کسی
/// که مبتدیه گیج شه»).
/// <para>Beginner hides the expert pages (DNS, Inspector, Ping, Gaming, Proxy, Logs) from the sidebar and palette and
/// folds away the expert widgets on the remaining pages (sort/test tools, map, charts, per-app usage, engine pickers,
/// advanced settings). Nothing is disabled — every setting keeps working, it just isn't in the way.</para>
/// <para>Persisted as <c>ui.mode</c> ("simple" / "advanced"). A fresh install starts in Beginner; anyone upgrading
/// from an older version (already onboarded) keeps the full Advanced UI they're used to.</para>
/// XAML: <c>Visibility="{Binding IsAdvanced, Source={x:Static svc:UiMode.I}, Converter={StaticResource BoolToVis}}"</c>.
/// </summary>
public sealed partial class UiMode : ObservableObject
{
    public static UiMode I { get; } = new();

    /// <summary>The pages Beginner mode keeps out of the sidebar.</summary>
    public static readonly IReadOnlySet<string> AdvancedPages =
        new HashSet<string> { "Nav.Dns", "Nav.Inspector", "Nav.Ping", "Nav.Gaming", "Nav.Proxy", "Nav.Logs" };

    private bool _loaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAdvanced))]
    private bool _isSimple;

    public bool IsAdvanced => !IsSimple;

    /// <summary>Raised (UI thread) after the mode flips.</summary>
    public event Action? Changed;

    private UiMode() { }

    /// <summary>Reads the saved mode once at startup (before the shell binds to it).</summary>
    public void Load(SettingsRepository settings)
    {
        try
        {
            var v = settings.Get("ui.mode");
            IsSimple = v is null ? settings.Get("ui.onboarded") is null : v == "simple";
        }
        catch { IsSimple = false; }
        _loaded = true;
    }

    public static bool IsPageVisible(string navKey) => I.IsAdvanced || !AdvancedPages.Contains(navKey);

    partial void OnIsSimpleChanged(bool value)
    {
        if (!_loaded) return;
        try { AppHost.Get<SettingsRepository>().Set("ui.mode", value ? "simple" : "advanced"); } catch { }
        Changed?.Invoke();
    }
}
