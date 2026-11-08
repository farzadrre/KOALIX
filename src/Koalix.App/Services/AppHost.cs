using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Koalix.Core;
using Koalix.Dns;
using Koalix.Infrastructure;
using Koalix.SingBox;
using Koalix.SystemIntegration;
using Koalix.Tunneling;
using Koalix.WireGuard;
using Koalix.Xray;
using Serilog.Events;

namespace Koalix.App.Services;

/// <summary>Composition root (dependency injection).</summary>
public static class AppHost
{
    public static IServiceProvider Services { get; private set; } = null!;
    public static T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public static IServiceProvider Build()
    {
        var s = new ServiceCollection();
        var paths = new AppPaths();
        s.AddSingleton(paths);
        s.AddSingleton(_ => { var db = new Database(paths.Database); db.Migrate(paths.BackupsDir); return db; });
        s.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        s.AddSingleton<IProcessRunner, ProcessRunner>();
        s.AddSingleton<SettingsRepository>();
        s.AddSingleton<TunnelProfileRepository>();
        s.AddSingleton<AppRuleRepository>();
        s.AddSingleton<DnsProfileRepository>();
        s.AddSingleton<SubscriptionRepository>();
        s.AddSingleton<HistoryRepository>();
        s.AddSingleton<UsageRepository>();
        s.AddSingleton(new RingSink(5000));
        s.AddSingleton<Serilog.ILogger>(sp => KoalixLog.Create(paths, sp.GetRequiredService<RingSink>(), sp.GetRequiredService<SettingsRepository>().Get("log.verbose") == "1"));
        s.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(60) });
        s.AddSingleton(new EnginePaths(paths.Root));
        s.AddSingleton(sp => new EngineManager(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<EnginePaths>().EnginesDir));
        s.AddSingleton<WindowsDnsConfigurator>();
        s.AddSingleton<DnsQueryClient>();
        s.AddSingleton<IIcmpProber, IcmpProber>();
        s.AddSingleton<DnsTester>();
        s.AddSingleton<HealthChecker>();
        s.AddSingleton<GeoLookup>();
        s.AddSingleton<SystemProxyManager>();
        s.AddSingleton(new NetworkStateJournal(paths.Journal));
        s.AddSingleton<RepairCenter>();
        s.AddSingleton<FirewallKillSwitch>();
        s.AddSingleton<IKillSwitch>(sp => sp.GetRequiredService<FirewallKillSwitch>());
        s.AddSingleton<INetworkConflictDetector>(sp => new ConflictDetector(() => sp.GetRequiredService<SystemProxyManager>().Read()));
        s.AddSingleton<GamingOptimizer>();
        s.AddSingleton<SystemSelfCheck>();
        s.AddSingleton(sp =>
        {
            var ep = sp.GetRequiredService<EnginePaths>(); var settings = sp.GetRequiredService<SettingsRepository>();
            var runner = sp.GetRequiredService<IProcessRunner>();
            T Configure<T>(T e) where T : SingBoxEngine
            {
                e.RemoteDns = settings.Get("tunnel.remoteDns") ?? "1.1.1.1";
                e.DirectDns = settings.Get("tunnel.directDns") ?? "1.1.1.1";
                e.LogLevel = settings.Get("log.verbose") == "1" ? "debug" : "info";
                e.TunnelIpv6 = settings.Get("tunnel.ipv6") == "1";
                e.TunStack = settings.Get("tun.stack") is "mixed" or "system" ? settings.Get("tun.stack")! : "gvisor";
                // v0.4.30 REVERSAL of the v0.4.15 default: strict routing is ON unless explicitly disabled.
                // Field log 354 (0.4.29, «وصل میشه ولی گوگل و یوتیوب باز نمی‌شه»): with strict_route=False
                // sing-box installs no WFP rules, so Windows' "ordinary DNS requests" (svchost, Wi-Fi adapter's
                // ISP resolvers) BYPASS the TUN — the ISP answers blocked domains with the 10.10.34.x sinkhole
                // (10.10.34.35 / 2001:4188:2:600::10:10:34:35 in the log, 164 dials) while unblocked names
                // resolve clean — hence «ipnumberia.com works and shows a foreign IP, Google/YouTube never load,
                // Iranian sites always do»: the tunnel itself was healthy the whole time. strict_route=True adds
                // the firewall rules that force those lookups into the TUN, where hijack-dns answers them through
                // the poison-proof remote DoH. Users with genuine filter-driver clashes can still switch it off.
                e.StrictRoute = settings.Get("tun.strict") != "0";
                // v0.4.28 — the audited connection-reliability set: DNS-poisoning shield, TUN MTU, TLS fragment, mux.
                e.PoisonGuardEnabled = settings.Get("net.poisonGuard") != "0";
                e.TunMtu = int.TryParse(settings.Get("tun.mtu"), out var tunMtu) ? tunMtu : 9000;
                e.FragmentEnabled = settings.Get("net.fragment") != "0";
                e.MuxEnabled = settings.Get("net.mux") == "1";
                WireGuardEngine.PreferOfficial = settings.Get("wireguard.official") == "1";
                WireGuardEngine.UseWireSock = settings.Get("wireguard.wiresock") is { } ws ? ws == "1" : EnginePaths.WireSockExe is not null;
                WireGuardEngine.MtuOverride = WireGuardEngine.ParseMtu(settings.Get("wireguard.mtu"));
                return e;
            }
            var factories = new Dictionary<EngineKind, Func<ITunnelEngine>>
            {
                [EngineKind.SingBox] = () => Configure(new SingBoxEngine(ep)),
                [EngineKind.Xray] = () => Configure(new XrayEngine(ep)),
                [EngineKind.WireGuard] = () => Configure(new WireGuardEngine(ep, runner)),
                [EngineKind.OpenVpn] = () => new OpenVpnEngine(ep),
            };
            var cm = new ConnectionManager(factories, sp.GetRequiredService<INetworkConflictDetector>(), sp.GetRequiredService<IKillSwitch>(), sp.GetRequiredService<HealthChecker>());
            var log = sp.GetRequiredService<Serilog.ILogger>();
            cm.Log += (c, m) => KoalixLog.Write(log, c, LogEventLevel.Information, m);
            return cm;
        });
        s.AddSingleton<ThemeService>();
        s.AddSingleton<TrayService>();
        s.AddSingleton<LanShareService>();
        s.AddSingleton<GameAutoService>(); // v0.4.37 per-game auto-activation
        s.AddSingleton<WidgetService>(); // v0.4.38 floating mini widget
        s.AddSingleton<UpdateService>();
        s.AddSingleton<BackupService>();

        s.AddSingleton<ViewModels.MainViewModel>();
        s.AddSingleton<ViewModels.DashboardViewModel>();
        s.AddSingleton<ViewModels.DnsViewModel>();
        s.AddSingleton<ViewModels.ProfilesViewModel>();
        s.AddSingleton<ViewModels.SplitTunnelViewModel>();
        s.AddSingleton<ViewModels.InspectorViewModel>();
        s.AddSingleton<ViewModels.PingViewModel>();
        s.AddSingleton<ViewModels.RepairViewModel>();
        s.AddSingleton<ViewModels.GamingViewModel>();
        s.AddSingleton<ViewModels.ProxyViewModel>();
        s.AddSingleton<ViewModels.LogsViewModel>();
        s.AddSingleton<ViewModels.SettingsViewModel>();
        s.AddSingleton<ViewModels.AboutViewModel>(); // v0.4.38
        Services = s.BuildServiceProvider();
        // v0.4.30 — one-time migration: flip strict_route ON for every existing installation. The v0.4.15
        // default (OFF, out of WFP-clash caution) shipped for five versions and is what opened the DNS-leak
        // hole above; existing settings rows carry either "0" or nothing, and both must become ON exactly
        // once. tun.strict.migrated guards it: a user who turns strict routing back off afterwards is NEVER
        // re-flipped by an update — their later choice is final.
        if (Get<SettingsRepository>().Get("tun.strict.migrated") is null)
        {
            Get<SettingsRepository>().Set("tun.strict", "1");
            Get<SettingsRepository>().Set("tun.strict.migrated", "1");
        }
        // v0.4.31 — stamp the app version into every engine log: a field log now names its own build
        // (log 355 cost hours because nothing in it said "pre-0.4.30").
        Koalix.SingBox.SingBoxEngine.AppVersionTag = UpdateService.CurrentVersion;
        return Services;
    }

    public static void Log(LogCategory c, string msg, LogEventLevel level = LogEventLevel.Information) =>
        KoalixLog.Write(Get<Serilog.ILogger>(), c, level, msg);
}
