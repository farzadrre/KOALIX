using System.Text.Json;
using System.Threading;
using System.Windows;
using Koalix.App.Localization;
using Koalix.App.Services;
using Koalix.Core;
using Koalix.Infrastructure;
using Koalix.SystemIntegration;
using Koalix.Tunneling;
using Serilog.Events;

namespace Koalix.App;

public partial class App : Application
{
    private Mutex? _single;
    public static new App Current => (App)Application.Current;

    /// <summary>v0.4.22 — releases the single-instance mutex BEFORE a replacement instance starts (elevated
    /// restart). Without this, the new instance can race the dying one for the mutex, see "already running",
    /// signal the exiting process and quit — the restart button would silently do nothing.</summary>
    public void ReleaseSingleInstance()
    {
        try { _single?.ReleaseMutex(); } catch { }
        try { _single?.Dispose(); } catch { }
        _single = null;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        _single = new Mutex(true, @"Local\KOALIX-App", out var first);
        if (!first)
        {
            StartupLog.Trace("another instance is running — asking it to show itself");
            SingleInstance.SignalExisting();
            Shutdown(); return;
        }
        base.OnStartup(e);

        DispatcherUnhandledException += (_, ex) =>
        {
            ex.Handled = true;
            StartupLog.Trace("UI exception: " + ex.Exception);
            try { AppHost.Log(LogCategory.Application, "UI exception: " + ex.Exception, LogEventLevel.Error); } catch { }
            if (!StartupLog.WindowShown) { StartupLog.Fatal("UI", ex.Exception); Environment.Exit(2); }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            StartupLog.Trace("Fatal: " + ex.ExceptionObject);
            try { AppHost.Log(LogCategory.Application, "Fatal: " + ex.ExceptionObject, LogEventLevel.Fatal); } catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, ex) => { try { AppHost.Log(LogCategory.Application, "Task: " + ex.Exception, LogEventLevel.Error); } catch { } ex.SetObserved(); };

        MainWindow window;
        bool crashed;
        try
        {
            StartupLog.Trace("building services");
            AppHost.Build();
            var settings = AppHost.Get<SettingsRepository>();
            var safe = e.Args.Contains("--safe");
            StartupLog.Trace("loading language/theme" + (safe ? " (safe mode)" : ""));
            try { Loc.I.SetLanguage(settings.Get("ui.language") ?? "fa"); } catch (Exception ex) { StartupLog.Trace("language: " + ex.Message); }
            try { ThemeService.ApplyFont(Loc.I.Language); } catch (Exception ex) { StartupLog.Trace("font: " + ex.Message); }
            Loc.I.LanguageChanged += () => { try { ThemeService.ApplyFont(Loc.I.Language); } catch { } };
            Controls.Fx.Enabled = !safe && settings.Get("ui.effects") != "0";
            Controls.LiveBackdrop.Allowed = settings.Get(Controls.LiveBackdrop.SettingKey) != "0";
            Controls.LiveBackdrop.PushKind(settings.Get("ui.bgKind") ?? "particles"); // v0.4.52 — the picked animated background
            ApplyFactoryDefaults(settings); // v0.4.52 — first-run defaults from the reference backup (settings only, sanitized)
            SoundService.Load(settings); // v0.4.23: the connect/disconnect/error tones honour "ui.sounds"
            Controls.ThemeRefresh.Install(); // v0.4.42: hand-drawn controls repaint on every theme/palette change
            Controls.Motion.Install();       // v0.4.43: right-click menus and tooltips open with a soft scale + fade
            var theme = AppHost.Get<ThemeService>();
            try { theme.Apply(Enum.TryParse<AppTheme>(settings.Get("ui.theme"), out var t) ? t : AppTheme.Dark, safe ? null : settings.Get("ui.accent")); }
            catch (Exception ex) { StartupLog.Trace("theme: " + ex); theme.Apply(AppTheme.Dark, ThemeService.DefaultAccent); }
            // v0.4.38 — style preset (corners + fonts + card surface), glass mode and reactive ARGB.
            try
            {
                if (!safe) theme.SetStyle(settings.Get(ThemeService.StyleKey) ?? "default", settings.Get(ThemeService.GlassKey) == "1");
                ThemeService.RgbReactive = settings.Get(ThemeService.RgbReactiveKey) is { } rr && ThemeService.ReactiveModes.Contains(rr) ? rr : "off";
            }
            catch (Exception ex) { StartupLog.Trace("style: " + ex); }
            // v0.4.37 — the ARGB palette resumes on launch (never in safe mode); it idles until the window is shown.
            try { if (!safe && settings.Get(ThemeService.RgbKey) == "1") theme.SetRgb(true); } catch (Exception ex) { StartupLog.Trace("argb: " + ex); }

            var paths = AppHost.Get<AppPaths>();
            crashed = File.Exists(paths.CrashMarker);
            File.WriteAllText(paths.CrashMarker, Environment.ProcessId.ToString());
            AppHost.Log(LogCategory.Application, $"KOALIX {UpdateService.CurrentVersion} started (crashed previously: {crashed}, admin: {Koalix.SystemIntegration.ProcessRunner.IsElevated})");

            StartupLog.Trace("creating main window");
            window = CreateWindow(safe);
            MainWindow = window;
            if (!e.Args.Contains("--minimized"))
            {
                window.Show();
                window.Activate();
                ShowWhatsNewOnce(settings); // v0.4.52 — «وقتی اولین بار اپدیت میکنیم اپو بیاد رو تصویر و ببینیم چیه»
            }
            StartupLog.WindowShown = true;
            StartupLog.Trace("main window shown");
        }
        catch (Exception ex)
        {
            StartupLog.Fatal("startup", ex);
            Environment.Exit(1);
            return;
        }

        try
        {
            var tray = AppHost.Get<TrayService>();
            tray.Initialize();
            tray.ShowRequested += () => ShowMain(window);
            tray.ExitRequested += () => _ = ExitAsync();
            tray.ToggleConnectionRequested += () => AppHost.Get<ViewModels.DashboardViewModel>().ConnectCommand.Execute(null);
            // v0.4.37 — game profiles with "auto" on start watching for their game right away (not only once the Gaming page opens).
            try { AppHost.Get<GameAutoService>().Start(); } catch (Exception ex) { StartupLog.Trace("gameauto: " + ex); }
            // v0.4.38 — the floating mini widget (opt-in; Settings / tray panel / its own × turn it on and off).
            try
            {
                var widget = AppHost.Get<WidgetService>();
                widget.OpenAppRequested += () => ShowMain(window);
                tray.WidgetToggleRequested += widget.Toggle;
                widget.Start();
            }
            catch (Exception ex) { StartupLog.Trace("widget: " + ex); }
            // v0.4.12: the tray "Servers" submenu connects a profile directly from the tray.
            tray.ConnectProfileRequested += id =>
            {
                var p = AppHost.Get<TunnelProfileRepository>().All().FirstOrDefault(x => x.Id == id);
                if (p is null) return;
                ShowMain(window);
                _ = AppHost.Get<ViewModels.DashboardViewModel>().ConnectToAsync(p);
            };
        }
        catch (Exception ex) { StartupLog.Trace("tray: " + ex); }
        // v0.4.36 - the optional LAN proxy relay (consoles / phones on the same network or this PC's hotspot).
        try { _ = AppHost.Get<LanShareService>().ApplyAsync(); } catch (Exception ex) { StartupLog.Trace("lan share: " + ex.Message); }
        SingleInstance.Listen(() => Dispatcher.BeginInvoke(() => ShowMain(window)));

        try { await RecoveryService.RunAsync(crashed); } catch (Exception ex) { StartupLog.Trace("recovery: " + ex); }
        _ = CheckUpdatesInBackgroundAsync();
        // Saved subscriptions older than 12 h refresh themselves on app open (setting "subs.auto", default on — v0.4.11).
        try { _ = AppHost.Get<ViewModels.ProfilesViewModel>().AutoRefreshAsync(); } catch (Exception ex) { StartupLog.Trace("sub auto-refresh: " + ex.Message); }
    }

    /// <summary>Builds the main window; if the full visual layer fails (effects, backdrop), retries once without them.</summary>
    private static MainWindow CreateWindow(bool safe)
    {
        try
        {
            return new MainWindow { DataContext = AppHost.Get<ViewModels.MainViewModel>() };
        }
        catch (Exception ex) when (!safe)
        {
            StartupLog.Trace("main window failed, retrying in safe mode: " + ex);
            Controls.Fx.Enabled = false;
            Koalix.App.MainWindow.SafeMode = true;
            return new MainWindow { DataContext = AppHost.Get<ViewModels.MainViewModel>() };
        }
    }

    /// <summary>v0.4.52 — «فایل بکاپ پیشفرض میخوام این باشه فقط بخش تنظیماتش ها»: the sanitized SETTINGS of the
    /// user's reference backup ship as factory defaults (Defaults/FactorySettings.json) and seed a FRESH install
    /// exactly once — only into keys that have no value yet, never overwriting anything already set. Personal
    /// data (WARP identity, game paths, widget placement, onboarding flags) was stripped at build time.</summary>
    private static void ApplyFactoryDefaults(SettingsRepository settings)
    {
        try
        {
            if (settings.Get("app.factoryApplied") == "1") return;
            using var s = typeof(App).Assembly.GetManifestResourceStream("Koalix.App.Defaults.FactorySettings.json");
            if (s is not null)
            {
                using var doc = JsonDocument.Parse(s);
                foreach (var prop in doc.RootElement.GetProperty("defaults").EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.String) continue;
                    if (string.IsNullOrEmpty(settings.Get(prop.Name))) settings.Set(prop.Name, prop.Value.GetString()!);
                }
            }
            settings.Set("app.factoryApplied", "1");
            StartupLog.Trace("factory defaults applied");
        }
        catch (Exception ex) { StartupLog.Trace("factory defaults: " + ex.Message); }
    }

    /// <summary>v0.4.52 — the first launch after an UPDATE pops the newest version's notes (WhatsNewWindow),
    /// exactly once per version (app.seenVersion). A fresh install skips it — the starter tour covers that.</summary>
    private static void ShowWhatsNewOnce(SettingsRepository settings)
    {
        try
        {
            var v = typeof(App).Assembly.GetName().Version!;
            var ver = $"{v.Major}.{v.Minor}.{v.Build}";
            var seen = settings.Get("app.seenVersion");
            if (seen == ver) return;
            if (seen is null && settings.Get("ui.onboarded") != "1") { settings.Set("app.seenVersion", ver); return; }
            var notes = Controls.WhatsNewWindow.LoadNotes(ver, Loc.I.Language);
            settings.Set("app.seenVersion", ver);
            if (notes is not { Count: > 0 }) return;
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => { try { new Controls.WhatsNewWindow(ver, notes).ShowDialog(); } catch { } }));
        }
        catch (Exception ex) { try { StartupLog.Trace("whats-new: " + ex.Message); } catch { } }
    }

    private static void ShowMain(Window window)
    {
        window.Show();
        try { ShowWhatsNewOnce(AppHost.Get<SettingsRepository>()); } catch { } // v0.4.52 — also when opened from the tray
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private static async Task CheckUpdatesInBackgroundAsync()
    {
        // v0.4.42 — announced on EVERY launch until the user updates (toast → also in the notification center, + title-bar
        // badge). The remembered version shows right away (works offline); the online check then confirms or refreshes it.
        // Now covers the portable build too (latest GitHub release), not only the installed one.
        var u = AppHost.Get<UpdateService>();
        if (!u.AutoCheck || string.IsNullOrWhiteSpace(u.Feed)) return;
        var main = AppHost.Get<ViewModels.MainViewModel>();
        string? announced = null;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4));
            announced = u.KnownLatest;
            if (announced is not null) main.AnnounceUpdate(announced);
            await Task.Delay(TimeSpan.FromSeconds(4));
            var v = await u.CheckLatestAsync(AppHost.Get<System.Net.Http.HttpClient>());
            if (v is null) { if (announced is not null) main.ClearUpdate(); }
            else if (v != announced) main.AnnounceUpdate(v);
        }
        catch (Exception ex) { AppHost.Log(LogCategory.Application, "auto update check: " + ex.Message); }
    }

    public async Task ExitAsync()
    {
        try
        {
            await AppHost.Get<ConnectionManager>().DisposeAsync(); // ordered teardown: engine → kill switch → DNS
            await RevertBoostsOnExitAsync();
            File.Delete(AppHost.Get<AppPaths>().CrashMarker);
            AppHost.Get<TrayService>().Dispose();
            (AppHost.Get<Serilog.ILogger>() as IDisposable)?.Dispose();
        }
        finally { Shutdown(); }
    }

    /// <summary>
    /// Gaming boosts revert when KOALIX closes (v0.4.11, setting "gaming.revertOnExit", default on) — previously they
    /// only came back after a crash via RecoveryService, so a clean exit left HighPerformance power plans etc. applied.
    /// </summary>
    private static async Task RevertBoostsOnExitAsync()
    {
        try
        {
            if (AppHost.Get<SettingsRepository>().Get("gaming.revertOnExit") == "0") return;
            var journal = AppHost.Get<NetworkStateJournal>();
            var pending = journal.Pending().Where(e => e.Kind == JournalKind.GamingTweak).ToList();
            if (pending.Count == 0) return;
            await AppHost.Get<GamingOptimizer>().RevertAsync(CancellationToken.None);
            foreach (var e in pending) journal.Complete(e.Id);
            AppHost.Log(LogCategory.Application, "gaming boosts reverted on exit");
        }
        catch (Exception ex) { StartupLog.Trace("exit boost revert: " + ex.Message); }
    }
}
