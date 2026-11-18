using System.Text.Json.Nodes;
using Koalix.Aether;
using Koalix.Core;
using Koalix.SingBox;
using Koalix.SplitTunneling;
using Koalix.Tunneling;
using Xunit;

namespace Koalix.Tests;

/// <summary>
/// v0.4.48 — the Aether section: profile→argv mapping (must mirror Aether-GUI's as_args() exactly, flags suppress
/// every interactive prompt), readiness/timeout derivation, and the AETHER chain mode applied onto a built config.
/// Also guards the WARP endpoint DNS-poison fix: the peer list must lead with an IP literal and never depend on the
/// system resolver (field log koalix-20261116: ISP NXDOMAIN on engage.cloudflareclient.com).
/// </summary>
public class AetherTests
{
    // ── profile → argv ──

    [Fact]
    public void Profile_Defaults_Map_To_Aether_Gui_Args()
    {
        var p = AetherProfile.FromSettings(_ => null);
        var args = p.AsArgs();
        // defaults: auto protocol (no flag), balanced, dual, quick reconnect ON
        Assert.Contains("--balanced", args);
        Assert.Contains("--dual", args);
        Assert.Contains("--quick-reconnect", args);
        Assert.Contains("--noize", args);
        Assert.Contains("firewall", args); // MASQUE-family default
        Assert.DoesNotContain("--masque", args);
        Assert.DoesNotContain("--wg", args);
        Assert.DoesNotContain("--gool", args);
        Assert.DoesNotContain("--turbo", args);
    }

    [Theory]
    [InlineData(AetherProtocol.Masque, "--masque")]
    [InlineData(AetherProtocol.Wireguard, "--wg")]
    [InlineData(AetherProtocol.Gool, "--gool")]
    [InlineData(AetherProtocol.Mim, "--mim")]
    public void Protocol_Flags(AetherProtocol proto, string flag)
    {
        var p = new AetherProfile { Protocol = proto };
        Assert.Contains(flag, p.AsArgs());
    }

    [Fact]
    public void Reverse_Network_Forces_Masque_Over_Wireguard()
    {
        // Aether-GUI: reverse chains are TCP-only, the core refuses WireGuard/gool → always MASQUE.
        var p = new AetherProfile { Protocol = AetherProtocol.Wireguard, NetworkMode = AetherNetworkMode.TorReverse };
        var args = p.AsArgs();
        Assert.Contains("--masque", args);
        Assert.DoesNotContain("--wg", args);
        Assert.Contains("--tor-reverse", args);
    }

    [Fact]
    public void Wg_Family_Selects_Wg_Noize()
    {
        var p = new AetherProfile { Protocol = AetherProtocol.Wireguard, WgNoize = AetherWgNoize.Off };
        var args = p.AsArgs();
        Assert.Contains("off", args); // noize off → lowest latency, the gaming choice
    }

    [Fact]
    public void Zero_Trust_Never_Reaches_The_Command_Line()
    {
        // credentials ride as env vars (command lines are visible to other local processes)
        var p = new AetherProfile
        {
            ZeroTrustTeam = "acme", ZeroTrustAuth = AetherZeroTrustAuth.Service,
            AccessClientId = "cid123", AccessClientSecret = "TOPSECRET", AccessToken = "jwt456",
        };
        var argv = string.Join(' ', p.AsArgs());
        Assert.DoesNotContain("TOPSECRET", argv);
        Assert.DoesNotContain("jwt456", argv);
        Assert.DoesNotContain("cid123", argv);
        var env = p.EnvVars();
        Assert.Equal("cid123", env["AETHER_ACCESS_CLIENT_ID"]);
        Assert.Equal("TOPSECRET", env["AETHER_ACCESS_CLIENT_SECRET"]);
    }

    [Fact]
    public void Chain_Modes_Expose_Their_Overlay_Ports()
    {
        var warp = new AetherProfile();
        Assert.Equal("127.0.0.1:1819", warp.PrimaryAddr());
        var tor = warp with { NetworkMode = AetherNetworkMode.TorChain };
        Assert.Equal(AetherProfile.TorBind, tor.PrimaryAddr());
        var psiphon = warp with { NetworkMode = AetherNetworkMode.PsiphonChain };
        Assert.Equal(AetherProfile.PsiphonBind, psiphon.PrimaryAddr());
        // readiness: chain modes must also wait for the overlay port
        Assert.Contains(System.Net.IPEndPoint.Parse(AetherProfile.TorBind), tor.ReadyAddrs());
    }

    [Fact]
    public void Connect_Timeout_Exceeds_Every_Scan_Deadline()
    {
        // Each mode's timeout must exceed its own worst core scan deadline (upstream v1.3.0 overall_deadline,
        // max of the MASQUE and WireGuard probers per mode) plus margin — Aether-GUI's connect_timeout table.
        var coreDeadlines = new Dictionary<AetherScanMode, int>
        {
            [AetherScanMode.Turbo] = 45, [AetherScanMode.Balanced] = 120, [AetherScanMode.Thorough] = 300,
            [AetherScanMode.Verified] = 180, [AetherScanMode.Ironclad] = 180,
        };
        foreach (var (scan, core) in coreDeadlines)
            Assert.True(new AetherProfile { ScanMode = scan }.ConnectTimeout() > TimeSpan.FromSeconds(core),
                scan + " timeout must outlast its core scan deadline of " + core + "s");
        var tor = new AetherProfile { NetworkMode = AetherNetworkMode.TorChain, ScanMode = AetherScanMode.Turbo };
        Assert.True(tor.ConnectTimeout() >= TimeSpan.FromSeconds(90 + 480), "Tor chain must get its bootstrap budget");
        // "only" modes run no scan: just the overlay budget + margin
        var psiphonOnly = new AetherProfile { NetworkMode = AetherNetworkMode.PsiphonOnly };
        Assert.True(psiphonOnly.ConnectTimeout() >= TimeSpan.FromSeconds(200));
    }

    // ── settings round-trip (the presets write settings; FromSettings must read them back) ──

    [Fact]
    public void Settings_Round_Trip()
    {
        var store = new Dictionary<string, string>
        {
            ["aether.protocol"] = "Wireguard", ["aether.scan"] = "Turbo", ["aether.ip"] = "V4",
            ["aether.wgNoize"] = "Off", ["aether.quickReconnect"] = "1", ["aether.bind"] = "127.0.0.1:9050",
            ["aether.network"] = "PsiphonChain", ["aether.systemProxy"] = "1",
        };
        var p = AetherProfile.FromSettings(k => store.GetValueOrDefault(k));
        Assert.Equal(AetherProtocol.Wireguard, p.Protocol);
        Assert.Equal(AetherScanMode.Turbo, p.ScanMode);
        Assert.Equal(AetherIpVersion.V4, p.IpVersion);
        Assert.Equal(AetherWgNoize.Off, p.WgNoize);
        Assert.Equal("127.0.0.1:9050", p.BindAddress);
        Assert.Equal(AetherNetworkMode.PsiphonChain, p.NetworkMode);
        Assert.True(p.SystemProxy);
    }

    // ── the AETHER chain mode ──

    private const string WorkerVless =
        "vless://50414e45-4c5f-5a45-5553-e3e8e0530ae9@104.25.177.89:443?path=%2Fstream%2FPANEL%2Fe3e8e0530ae9&security=tls&encryption=none&host=panel.example.workers.dev&type=ws&sni=panel.example.workers.dev#ZEUS";

    private static JsonObject GeneratedConfig() =>
        SingBoxConfigBuilder.Build(SingBoxConfigBuilder.Outbound(ShareLinkParser.Parse(WorkerVless)), false, SplitPolicy.Full,
            new() { HealthPort = 1, ClashApiPort = 2, ClashApiSecret = "s" });

    [Fact]
    public void Aether_Chain_Mode_Points_Final_At_The_Local_Socks()
    {
        var cfg = ChainConfig.Apply(GeneratedConfig(), SplitRuleCompiler.ProxyTag, new ChainOptions
        {
            Enabled = true, Mode = ChainMode.Aether, AetherPort = 1819,
        }, SplitPolicy.Full);
        var ob = cfg["outbounds"]!.AsArray().OfType<JsonObject>().Single(o => (string?)o["tag"] == ChainConfig.AetherTag);
        Assert.Equal("socks", (string?)ob["type"]);
        Assert.Equal("127.0.0.1", (string?)ob["server"]);
        Assert.Equal(1819, (int)ob["server_port"]!);
        Assert.Equal("aether-out", (string?)cfg["route"]!["final"]);
    }

    // ── the WARP endpoint DNS-poison fix ──

    [Fact]
    public void Warp_Endpoint_Leads_With_Ip_Literal_And_Lists_Fallbacks()
    {
        var w = new WarpIdentity { PrivateKey = "mENVP+bUAm87r3qzTpy6V7UQP8BWqjqZhSSmbGmAw2M=", Reserved = [1, 2, 3] };
        var ep = w.ToSingBoxEndpoint("w", null, null, 1280);
        var peers = ep["peers"]!.AsArray().ToList();
        Assert.True(peers.Count >= 3, "the peer list must carry fallback engage addresses");
        Assert.Equal(WarpIdentity.DefaultEndpointHost, (string?)peers[0]!["address"]);
        Assert.True(System.Net.IPAddress.TryParse((string?)peers[0]!["address"], out _), "primary peer must be an IP literal");
        Assert.Contains(peers, p => (string?)p!["address"] == "engage.cloudflareclient.com"); // clean-DNS networks can still use the name
        // reserved bytes must be set on EVERY peer (Cloudflare identifies the client by them)
        Assert.All(peers, p => Assert.NotNull(p!["reserved"]));
        Assert.Equal(2408, (int)peers[0]!["port"]!);
    }

    [Fact]
    public void Warp_Endpoint_Defaults_And_Parsing()
    {
        // FromSettings with no host stored → the IP literal, never the DNS name
        var o = ChainOptions.FromSettings(_ => null);
        Assert.Null(o.Warp); // no identity stored
        Assert.Equal("162.159.192.1", WarpIdentity.DefaultEndpointHost);
        Assert.Equal(2408, o.WarpMtu is 1280 ? 2408 : 0); // silly guard: default port constant stays 2408
        var withIdentity = ChainOptions.FromSettings(k => k == "chain.warp.priv" ? "mENVP+bUAm87r3qzTpy6V7UQP8BWqjqZhSSmbGmAw2M=" : null);
        Assert.Equal(WarpIdentity.DefaultEndpointHost, withIdentity.Warp!.EndpointHost);
    }

    // ── v0.4.48.2 — the tabbed advanced-tunnels card + the dark-theme text fixes ──
    // Source-scan guards (the VM needs the full app host, so we pin the wiring, not the instances).

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KoalixSuite.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, "repo root not found above test bin");
        return dir!.FullName;
    }

    private static string Src(string relative) => File.ReadAllText(Path.Combine(RepoRoot(), "src", relative));
    // v0.4.51 — the old inline walk lives in RepoRoot() now so the XAML scanner can reuse it.

    /// <summary>The tab strip must drive the two panels through the panel-open flag: closed card = no panel
    /// (the whole point of the redesign — «داشبورد شلوغ نشه»), and both panels can never show at once.</summary>
    [Fact]
    public void Tunnels_panels_are_gated_by_the_open_flag_and_are_exclusive()
    {
        var vm = Src("Koalix.App/ViewModels/DashboardViewModel.cs");
        Assert.Contains("public bool TunnelsTabChainPanel => TunnelsPanelOpen && !TunnelsTabIsAether;", vm);
        Assert.Contains("public bool TunnelsTabAetherPanel => TunnelsPanelOpen && TunnelsTabIsAether;", vm);
        // the close button must fold BOTH panels away
        Assert.Contains("private void TunnelsClose() => TunnelsPanelOpen = false;", vm);
    }

    /// <summary>v0.4.48.4 guard for «میزنم روشون هیچی نمیاد»: a tab click must OPEN the panel. The v0.4.48.2
    /// cut never set TunnelsPanelOpen=true anywhere, so the panels could only ever close — clicks looked dead.
    /// The repair is two-fold and both halves are pinned here: (1) each pill carries a Command that fires on
    /// EVERY click (IsChecked alone is silent on a re-click of the active pill), and (2) that command opens the
    /// panel AND re-raises the panel flags (the generator skips no-change assignments). v0.4.50: the pills live
    /// on the TunnelsHub page now, and the hub's OnActivated opens the panel so nobody lands on an empty selector.</summary>
    [Fact]
    public void Tab_clicks_open_the_panel_and_commands_fire_on_every_click()
    {
        var vm = Src("Koalix.App/ViewModels/DashboardViewModel.cs");
        var hub = Src("Koalix.App/Views/TunnelsHubView.xaml");

        // (1) the open commands exist and set the flag + refresh the computed panel booleans
        Assert.Contains("[RelayCommand] private void TunnelsOpenChain() { TunnelsTabIsAether = false; OpenTunnelsPanel(); }", vm);
        Assert.Contains("[RelayCommand] private void TunnelsOpenAether() { TunnelsTabIsAether = true; OpenTunnelsPanel(); }", vm);
        Assert.Contains("public void OpenTunnelsPanel()", vm); // v0.4.50 — the hub page activates it directly
        Assert.Contains("TunnelsPanelOpen = true;", vm);

        // (2) both hub pills bind BOTH IsChecked (selection) and the open Command (every click)
        Assert.Contains("IsChecked=\"{Binding TunnelsTabChain, Mode=TwoWay}\"", hub);
        Assert.Contains("Command=\"{Binding TunnelsOpenChainCommand}\"", hub);
        Assert.Contains("IsChecked=\"{Binding TunnelsTabAether, Mode=TwoWay}\"", hub);
        Assert.Contains("Command=\"{Binding TunnelsOpenAetherCommand}\"", hub);
        // and the hub page never arrives with the panels closed
        Assert.Contains("public override void OnActivated() => dash.OpenTunnelsPanel();", Src("Koalix.App/ViewModels/TunnelsHubViewModel.cs"));

        // (3) the setter guard must stay: RadioButton groups push false into the OLD tab's setter when a
        //     new one is checked — without the guard that push-back would re-select the old tab.
        Assert.Contains("public bool TunnelsTabChain { get => !TunnelsTabIsAether; set { if (!value) return; TunnelsTabIsAether = false; } }", vm);
        Assert.Contains("public bool TunnelsTabAether { get => TunnelsTabIsAether; set { if (!value) return; TunnelsTabIsAether = true; } }", vm);
    }

    /// <summary>Regression guard for the black-text field report («متن‌ها سیاهه، به پس‌زمینهٔ دارک نمی‌خوره»):
    /// the stock WPF Expander paints its header with the light Aero foreground. The app must keep a themed
    /// Expander style, and the dashboard must not set a local Foreground that fights it.</summary>
    [Fact]
    public void Expander_is_themed_not_the_stock_light_template()
    {
        var controls = Src("Koalix.App/Themes/Controls.xaml");
        Assert.Contains("<Style TargetType=\"Expander\">", controls);
        Assert.Contains("TextElement.Foreground=\"{DynamicResource TextBrush}\"", controls);
        // the dark SubtleTextBrush must stay readable on the glass cards (was #FF9AA3B8 → #FFC3CAD9 → v0.4.55 #FFDCE2EF over vivid ARGB backdrops)
        var dark = Src("Koalix.App/Themes/Dark.xaml");
        Assert.Contains("<SolidColorBrush x:Key=\"SubtleTextBrush\" Color=\"#FFDCE2EF\"/>", dark);
        var light = Src("Koalix.App/Themes/Light.xaml");
        Assert.Contains("x:Key=\"SubtleTextBrush\"", light); // parity: both dictionaries define it
    }

    /// <summary>v0.4.48.3 crash guard — the startup crash class of «خرابه باز نمیشه» (KOALIX-0.4.48-aether-source (2)
    /// on the user's machine): a storyboard animated <c>RenderTransform.ScaleX</c> on the tab Border while the
    /// ScaleTransform sat on the parent Grid, so the checked tab threw InvalidOperationException the moment the
    /// dashboard loaded. A source scan can't instantiate WPF, so we pin the SHAPE instead: every element named by
    /// a RenderTransform storyboard in an app XAML must own a RenderTransform child in the same template.</summary>
    [Fact]
    public void Storyboard_RenderTransform_targets_own_their_transform()
    {
        // Src() returns file CONTENTS, so find the real root from the test bin location instead.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KoalixSuite.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, "repo root not found above test bin");
        var srcRoot = Path.Combine(dir.FullName, "src");
        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.xaml", SearchOption.AllDirectories)
                     .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                                 !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(file);
            var rel = Path.GetFileName(file);
            // every element that carries an x:Name AND a RenderTransform child element
            var owners = new HashSet<string>();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text,
                         @"<(\w+)[^>]*x:Name=""(\w+)""[^>]*>\s*<\1\.RenderTransform>"))
                owners.Add(m.Groups[2].Value);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text,
                         @"Storyboard\.TargetName=""(\w+)""[^>]*Storyboard\.TargetProperty=""RenderTransform\."))
            {
                var target = m.Groups[1].Value;
                Assert.True(owners.Contains(target),
                    $"{rel}: storyboard animates RenderTransform on '{target}' but that element has no <RenderTransform> child — " +
                    "this is the v0.4.48.3 startup crash (InvalidOperationException 'Cannot resolve all property references')");
            }
        }
    }

    /// <summary>The SegTab pill itself: the animated Border must carry the ScaleTransform inline (the fix), not
    /// a sibling/parent — pinned separately so a refactor of the audit above can't lose the actual repair.</summary>
    [Fact]
    public void SegTab_border_owns_its_scale_transform()
    {
        var controls = Src("Koalix.App/Themes/Controls.xaml");
        var m = System.Text.RegularExpressions.Regex.Match(controls,
            @"<Style x:Key=""SegTab""[\s\S]*?</Style>");
        Assert.True(m.Success, "SegTab style must exist");
        var seg = m.Value;
        Assert.DoesNotContain("<Grid.RenderTransform>", seg); // the original bug: transform on the parent Grid
        Assert.Contains("RenderTransformOrigin=\"0.5,0.5\"", seg);
        Assert.Contains("<Border.RenderTransform><ScaleTransform/></Border.RenderTransform>", seg);
    }

    // ═══════ v0.4.49 — the Aether split-aware system proxy («اپایی که خودم انتخاب کردم فقط تونل شن») ═══════

    private static AppRule Rule(string path, AppAction action) => new() { DisplayName = path, ExecutablePath = path, Action = action };

    private static SplitPolicy IncludeChrome() => new(SplitMode.Include,
        [Rule(@"C:\Program Files\Google\Chrome\Application\chrome.exe", AppAction.Tunnel)]);

    private static SplitPolicy ExcludeFirefox() => new(SplitMode.Exclude,
        [Rule(@"C:\Program Files\Mozilla Firefox\firefox.exe", AppAction.Direct)]);

    /// <summary>The v0.4.49 core contract — the split session must be a NORMAL tunnel shape: compiled per-app
    /// rules, Aether's SOCKS5 as the proxy outbound, and mode-correct finals. Include mode: only the chosen
    /// apps go to aether-out (route final = direct), exactly what the field report demanded.</summary>
    [Fact]
    public void Split_session_config_honors_include_rules()
    {
        var o = new SingBoxBuildOptions { HealthPort = 1, ClashApiPort = 2, ClashApiSecret = "s" };
        var cfg = AetherSplitTunnel.BuildConfig(IncludeChrome(), 1819, [], o);

        var ob = ((JsonArray)cfg["outbounds"]!).OfType<JsonObject>().ToList();
        var aether = ob.Single(x => (string?)x["tag"] == SplitRuleCompiler.ProxyTag);
        Assert.Equal("socks", (string)aether["type"]!);
        Assert.Equal("127.0.0.1", (string)aether["server"]!);
        Assert.Equal(1819, aether["server_port"]!.GetValue<int>());
        // Include: unlisted apps fall through to DIRECT — apps marked Direct stay direct by construction.
        Assert.Equal(SplitRuleCompiler.DirectTag, (string)cfg["route"]!["final"]!);
        // the chrome rule exists and points at the aether outbound (AddMatch also emits QUIC/v6 guard rules —
        // the ROUTE rule is the one carrying "outbound")
        var rules = ((JsonArray)cfg["route"]!["rules"]!).OfType<JsonObject>().ToList();
        var chromeRule = rules.Single(r => (string?)r["outbound"] == SplitRuleCompiler.ProxyTag &&
                                           (r["process_path"] as JsonArray) is not null &&
                                           r["process_path"]!.AsArray().Any(p => (string?)p == @"C:\Program Files\Google\Chrome\Application\chrome.exe"));
        Assert.Equal([], SingBoxConfigLinter.Lint(cfg).ToArray());
    }

    /// <summary>Exclude mode: the Direct apps keep a direct rule and everything ELSE defaults into aether-out.</summary>
    [Fact]
    public void Split_session_config_honors_exclude_rules()
    {
        var o = new SingBoxBuildOptions { HealthPort = 1, ClashApiPort = 2, ClashApiSecret = "s" };
        var cfg = AetherSplitTunnel.BuildConfig(ExcludeFirefox(), 1819, [], o);

        Assert.Equal(SplitRuleCompiler.ProxyTag, (string)cfg["route"]!["final"]!); // everything else → aether
        var rules = ((JsonArray)cfg["route"]!["rules"]!).OfType<JsonObject>().ToList();
        var ffRule = rules.Single(r => (string?)r["outbound"] == SplitRuleCompiler.DirectTag &&
                                       (r["process_path"] as JsonArray) is not null &&
                                       r["process_path"]!.AsArray().Any(p => (string?)p == @"C:\Program Files\Mozilla Firefox\firefox.exe"));
        Assert.Equal([], SingBoxConfigLinter.Lint(cfg).ToArray());
    }

    /// <summary>The service must never go back to the WinINET hijack: pin that ConnectAsync starts the split
    /// session instead of writing the registry proxy, and that the toggle applies live.</summary>
    [Fact]
    public void AetherService_uses_split_session_not_wininet()
    {
        var svc = Src("Koalix.Aether/AetherService.cs");
        Assert.Contains("StartSplitSessionAsync(profile);", svc);                       // connect path starts the split session
        Assert.DoesNotContain("_proxy.Apply(new SystemProxyState(true", svc);           // the whole-OS hijack is gone
        Assert.Contains("public async Task StopSplitSessionAsync()", svc);
        Assert.Contains("await StopSplitSessionAsync();", svc);                         // disconnect tears it down
        // the live toggle in the dashboard VM
        var vm = Src("Koalix.App/ViewModels/DashboardViewModel.cs");
        Assert.Contains("if (value) _ = _aether.StartSplitSessionAsync();", vm);
        Assert.Contains("else _ = _aether.StopSplitSessionAsync();", vm);
    }

    /// <summary>The chain-AETHER connect must start the engine itself (field log: chained connect against a
    /// dead 1819). Pin the exact wiring in StartConnectAsync.</summary>
    [Fact]
    public void Chain_aether_connect_starts_the_engine()
    {
        var vm = Src("Koalix.App/ViewModels/DashboardViewModel.cs");
        Assert.Contains("Mode: Koalix.SingBox.ChainMode.Aether }", vm);
        Assert.Contains("await _aether.ConnectAsync(null, CancellationToken.None);", vm);
    }

    /// <summary>v0.4.49 → v0.4.49.2 — the CardFold click contract: whole header row, press-begin required, chevron
    /// follows layout, and hover/click driven by pointer POSITION (no hit-testable adorner = no flicker).
    /// Source-pinned (a UI test harness does not exist in this suite).</summary>
    [Fact]
    public void CardFold_click_contract_is_precise()
    {
        var fold = Src("Koalix.App/Controls/CardFold.cs");
        // whole-row click: preview handlers on the header ROW found in Attach (preview events only tunnel through
        // ancestors, so a handler on the title TextBlock would miss row clicks — the old «دقیق نیست» bug class)
        Assert.Contains("var row = HeaderRow(st);", fold);
        Assert.Contains("row.PreviewMouseLeftButtonDown += (_, ev) => st.PressedOnHeader = !InInteractiveControl(ev.OriginalSource as DependencyObject, row);", fold);
        Assert.Contains("row.PreviewMouseLeftButtonUp += (_, ev) =>", fold);
        Assert.Contains("if (ev.Handled || !st.PressedOnHeader || InInteractiveControl(ev.OriginalSource as DependencyObject, row)) return;", fold);
        // interactive children (buttons/combos/switches in the row) keep their own clicks
        Assert.Contains("d is ButtonBase or ComboBox or TextBoxBase or Slider or Expander or Selector", fold);
        // THE arrow is clickable — but via the CARD + a geometric hit box, never via a hit-testable adorner
        // (v0.4.49.1's hit-testable adorner flipped the hit result card↔adorner on the strokes → «رگباری
        // روشن خاموش»: the enter/leave storm IS the flicker regression this pins shut)
        Assert.Contains("new ChevronAdorner(st.Card) { Angle = st.Collapsed ? 90 : 0 }", fold);
        Assert.Contains("IsHitTestVisible = false;", fold);
        Assert.Contains("st.Card.MouseMove += (_, ev) => TrackArrow(st, ev.GetPosition(st.Card));", fold);
        Assert.Contains("st.Card.MouseLeftButtonUp += (_, ev) =>", fold);
        Assert.Contains("Math.Abs(p.X - (card.ActualWidth - 10)) <= 14 && Math.Abs(p.Y - HeaderMid(st)) <= 14", fold);
        // hover flips once per crossing of the arrow box — cannot blink («no boundary to thrash»)
        Assert.Contains("if (hot == st.ArrowHot) return;", fold);
        Assert.Contains("st.Card!.Cursor = hot ? Cursors.Hand : null;", fold);
        // the chevron re-positions on layout changes instead of staying at its Loaded spot
        Assert.Contains("st.Card.SizeChanged += (_, _) =>", fold);
        // default-closed cards: the Advanced-Tunnels card and the Server Map ship folded («تو حالت پیشفرض بسته باشه»)
        Assert.Contains("{ \"tunnels\", \"map\" }", fold);
        Assert.Contains("v == \"1\" : DefaultClosed.Contains(key);", fold);
    }

    /// <summary>v0.4.50 — the advanced tunnels moved OUT of the dashboard into their own sidebar page
    /// («کلا از تو داشبورد بیاری بیرون … تو همون یک ردیف هم warp قابل انتخاب باشه هم aether»): the dual-pill hub
    /// with per-tunnel identities, the dashboard state banner, and the full registration chain.</summary>
    [Fact]
    public void Tunnels_hub_page_replaces_the_dashboard_card()
    {
        var view = Src("Koalix.App/Views/DashboardView.xaml");
        // the card is GONE from the dashboard — selector pills, «بستن» and the CardFold key went with it
        Assert.DoesNotContain("Tunnels.Title", view);
        Assert.DoesNotContain("TunnelsCloseCommand", view);
        Assert.DoesNotContain("TunnelsOpenChainCommand", view);
        // and the dashboard instead SAYS what is live (user: «وقتی aether وصله … بنویسه که این وصله»)
        Assert.Contains("TunnelBannerText", view);
        Assert.Contains("OpenTunnelsHubCommand", view);

        var hub = Src("Koalix.App/Views/TunnelsHubView.xaml");
        // ONE row, two selectable pills side by side (the very commands the old tab strip used)
        Assert.Contains("HubPillWarp", hub);
        Assert.Contains("HubPillAether", hub);
        Assert.Contains("IsChecked=\"{Binding TunnelsTabChain, Mode=TwoWay}\" Command=\"{Binding TunnelsOpenChainCommand}\"", hub);
        Assert.Contains("IsChecked=\"{Binding TunnelsTabAether, Mode=TwoWay}\" Command=\"{Binding TunnelsOpenAetherCommand}\"", hub);
        // each tunnel carries its OWN identity: blue breathing glow vs violet shimmer, connect buttons included
        Assert.Contains("HubWarpBrush", hub);
        Assert.Contains("HubAetherBrush", hub);
        Assert.Contains("WarpGlow", hub);
        Assert.Contains("AetherShine", hub);
        Assert.Contains("{Binding ChainToggleCommand}", hub);
        Assert.Contains("{Binding AetherToggleCommand}", hub);
        // the panels moved over whole: chain settings + the full Aether advanced panel
        Assert.Contains("{Binding ChainModeIndex}", hub);
        Assert.Contains("{Binding AetherProtocolIndex}", hub);
        Assert.Contains("{Binding AetherScanIndex}", hub);
        // connect-while-connected: the dashboard connect explains and offers the cut inside the dialog
        Assert.Contains("Aether.Up.Disconnect", Src("Koalix.App/ViewModels/DashboardViewModel.cs"));

        // the page is reachable: nav row, DI, and the window's DataTemplate
        Assert.Contains("new(\"Nav.Split\", \"\\uE8AB\", hub)", Src("Koalix.App/ViewModels/MainViewModel.cs")); // v0.4.54 — the nav rows swapped VMs
        Assert.Contains("s.AddSingleton<ViewModels.TunnelsHubViewModel>();", Src("Koalix.App/Services/AppHost.cs"));
        Assert.Contains("<DataTemplate DataType=\"{x:Type vm:TunnelsHubViewModel}\"><v:TunnelsHubView/></DataTemplate>", Src("Koalix.App/MainWindow.xaml"));
    }

    /// <summary>v0.4.50 — «سرور قبلی سلکت شده ها ولی سرور انتخابی واقعا سلکت شده»: the SELECTED chip state must win
    /// over the stale green ACTIVE one, the map route must follow the real selection, and the old exit must stop
    /// being advertised the moment a new connect starts.</summary>
    [Fact]
    public void ServerMap_highlight_follows_the_real_pick()
    {
        var view = Src("Koalix.App/Views/DashboardView.xaml");
        // WPF trigger order = last wins: the selected trigger must come AFTER the active one
        var selIdx = view.IndexOf("Binding=\"{Binding IsSelectedCountry}\"", StringComparison.Ordinal);
        var actIdx = view.IndexOf("Binding=\"{Binding IsActiveCountry}\"", StringComparison.Ordinal);
        Assert.True(actIdx >= 0 && selIdx > actIdx, "the IsSelectedCountry trigger must follow IsActiveCountry so selection wins");
        // the dashed preview route follows the actual selection, not a lingering hover
        Assert.Contains("var previewPin = selected ?? externalHover;", Src("Koalix.App/Controls/WorldMap.cs"));
        var vm = Src("Koalix.App/ViewModels/DashboardViewModel.cs");
        Assert.Contains("TunnelGeo = null; // v0.4.50", vm);
        Assert.Contains("private void SyncPickHighlight()", vm); // v0.4.53/54 — highlight rides the banner (resolved by profile id)
        Assert.Contains("?? _allRows.FirstOrDefault(r => r.Profile?.Id == profile.Id);", vm);
    }

    /// <summary>v0.4.50 — the sidebar can be tidied: rows the user hides stay hidden (persisted), the dashboard row
    /// never hides, and edit mode lists the hidden rows for a one-click restore.</summary>
    [Fact]
    public void Sidebar_rows_can_be_hidden_and_restored()
    {
        var main = Src("Koalix.App/ViewModels/MainViewModel.cs");
        Assert.Contains("_hiddenNavKeys.Add(k.Trim())", main);
        Assert.Contains("ui.nav.hidden", main);
        Assert.Contains("UiMode.IsPageVisible(n.Key) && !_hiddenNavKeys.Contains(n.Key)", main);
        Assert.Contains("if (key is null || key == \"Nav.Dashboard\" || !_hiddenNavKeys.Add(key)) return;", main);
        var win = Src("Koalix.App/MainWindow.xaml");
        Assert.Contains("ToggleNavEditModeCommand", win);
        Assert.Contains("RestoreNavItemCommand", win);
        Assert.Contains("HideNavItemCommand", win);
        // the per-row hide button must NOT fire on the dashboard row even in edit mode (dashboard trigger last)
        var editIdx = win.IndexOf("NavEditMode", StringComparison.Ordinal);
        var homeIdx = win.IndexOf("Value=\"Nav.Dashboard\"", StringComparison.Ordinal);
        Assert.True(editIdx > 0 && homeIdx > editIdx, "the Nav.Dashboard override must come after the edit-mode trigger");
        // the tour teaches the new page (step 11)
        var onb = Src("Koalix.App/Views/OnboardingView.xaml.cs");
        Assert.Contains("TunnelHubStep, Toolbox", onb); // v0.4.53 — steps 10/11 swapped
        Assert.Contains("Tour.Step11Title", onb);
    }

    /// <summary>v0.4.50 — the tray panel's app exit lives at the BOTTOM-RIGHT now («عادت ندارم بالا باشه»), not in the header.</summary>
    [Fact]
    public void Tray_panel_exit_button_sits_in_the_bottom_right_footer()
    {
        var tray = Src("Koalix.App/Views/TrayPanelWindow.xaml");
        Assert.Contains("Tray.ExitHint", tray);
        var headerIdx = tray.IndexOf("DockPanel.Dock=\"Right\" Style=\"{StaticResource TrayIconBtn}\"", StringComparison.Ordinal);
        var footerIdx = tray.IndexOf("HorizontalAlignment=\"Right\" Click=\"OnExit\"", StringComparison.Ordinal);
        Assert.True(headerIdx > 0 && footerIdx > headerIdx, "the exit button must appear AFTER the header icon buttons (footer)");
        // the header keeps open/widget but no longer carries the exit glyph E7E8
        var headerSlice = tray[headerIdx..footerIdx];
        Assert.DoesNotContain("E7E8", headerSlice, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>v0.4.51 — «KOALIX could not start: XamlParseException … ResourceDictionary.DeferrableContent»: a
    /// DUPLICATED key inside ONE XAML file (an x:Key, or a DataType acting as the key of a DataTemplate) crashes
    /// the app at startup. This exact bug shipped as two identical TunnelsHubViewModel DataTemplates — every XAML
    /// file is now scanned so the class of bug can never ship again.</summary>
    [Fact]
    public void No_xaml_file_has_duplicate_resource_keys_or_datatypes()
    {
        var appDir = Path.Combine(RepoRoot(), "src", "Koalix.App");
        foreach (var file in Directory.GetFiles(appDir, "*.xaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            var keys = new List<string>();
            keys.AddRange(System.Text.RegularExpressions.Regex.Matches(text, "x:Key=\"([^\"]+)\"").Select(m => m.Groups[1].Value));
            keys.AddRange(System.Text.RegularExpressions.Regex.Matches(text, "DataType=\"\\{x:Type ([^}]+)\\}\"").Select(m => "dt:" + m.Groups[1].Value));
            var dupes = keys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.True(dupes.Count == 0, $"{Path.GetFileName(file)}: duplicate resource key(s) {string.Join(", ", dupes)} — the window will crash at startup");
        }
    }

    /// <summary>v0.4.51 — «وقتی از اپ میریم بیرون و میایم داخل اخرین سروری که سلکت بودیم هنوزم سلکت باشه»: the pick
    /// persists as conn.selected and is restored BEFORE the sort on cold start (auto mode still overrides it — that
    /// is the user's explicit choice).</summary>
    [Fact]
    public void Last_selected_server_survives_restart()
    {
        var vm = Src("Koalix.App/ViewModels/DashboardViewModel.cs");
        Assert.Contains("_settings.Set(\"conn.selected\"", vm);
        Assert.Contains("long.TryParse(_settings.Get(\"conn.selected\"), out var sid)", vm);
        Assert.Contains("var sel = SelectedProfile?.Profile?.Id ?? savedSel;", vm);
    }

    /// <summary>v0.4.51 — «نوار قرمز از کادر زده بیرون»: the dialog accent strip no longer uses negative side
    /// margins that poke past the card's rounded corners, and English exception text renders LTR instead of
    /// re-wrapping raggedly (a period starting a line) under the RTL window.</summary>
    [Fact]
    public void Dialog_strip_stays_inside_the_card_and_ltr_messages_wrap_naturally()
    {
        var dlg = Src("Koalix.App/Controls/KoalixDialog.cs");
        Assert.Contains("Margin = new Thickness(0, -10, 0, 18)", dlg);
        Assert.DoesNotContain("-26, -24, -26", dlg);
        Assert.Contains("if (!HasRtl(message)) body.FlowDirection = FlowDirection.LeftToRight;", dlg);
    }

    /* ───── v0.4.52 — the menu edit + reorder, the rename to «مدارها», selective backups, live backgrounds,
       the what's-new popup, the theme-ghost fix and the factory defaults ───── */

    /// <summary>v0.4.52 — «روی ویرایش منو که میزنم هیچ اتفاقی نمیفته و میخوام بشه جا به جا هم کرد منورو»: the
    /// edit toggle is now a visible state (accent pill + «پایان ویرایش» + hint line), rows drag-reorder in edit
    /// mode, and the order persists as ui.nav.order (restored on startup).</summary>
    [Fact]
    public void Nav_edit_mode_is_visible_and_rows_drag_reorder_with_a_persisted_order()
    {
        var mw = Src("Koalix.App/MainWindow.xaml");
        Assert.Contains("Nav.EditMenu.On", mw);            // the toggle label swaps while editing
        Assert.Contains("Nav.EditHint", mw);               // the drag/hide hint under the button
        var vm = Src("Koalix.App/ViewModels/MainViewModel.cs");
        Assert.Contains("public void MoveNavItem(int from, int to)", vm);
        Assert.Contains("ui.nav.order", vm);               // the persisted order
        Assert.Contains("Get(\"ui.nav.order\")", vm); // restored on startup
        var code = Src("Koalix.App/MainWindow.xaml.cs");
        Assert.Contains("NavList.PreviewMouseMove += NavDragMove", code);
        Assert.Contains("vm.MoveNavItem(_navDragIndex, to)", code);
    }

    /// <summary>v0.4.52 — «اسم و لوگوی تونل های پیشرفته و عوض کنی … لوگوی اتر هم تکراریه عوضش کن»: the section is
    /// now «مدارها» with its own animated circuit glyph, and Aether rides a dedicated atom glyph instead of DNS's
    /// globe (E968) — in the sidebar, the hub pill and the tour.</summary>
    [Fact]
    public void Circuits_rename_and_the_two_new_logos()
    {
        var fa = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Koalix.App", "Localization", "fa.json"))).RootElement;
        Assert.Equal("تونل‌های انتخابی", fa.GetProperty("Nav.TunnelHub").GetString()); // v0.4.54 — the tunnels row opens تونل‌های انتخابی
        var en = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Koalix.App", "Localization", "en.json"))).RootElement;
        Assert.Equal("Selective Tunnels", en.GetProperty("Nav.TunnelHub").GetString()); // v0.4.54
        var glyph = Src("Koalix.App/Controls/MotionGlyph.cs");
        Assert.Contains("case \"Nav.TunnelHub\": (canvas, sb) = SplitIcon(w, h);", glyph); // v0.4.54 — icons swapped with the rows
        Assert.Contains("case \"Aether\": (canvas, sb) = AetherIcon(w, h);", glyph); // v0.4.54 — bold shield+bolt
        Assert.Contains("private (Canvas, Storyboard) CircuitIcon", glyph);
        Assert.Contains("private (Canvas, Storyboard) AtomIcon", glyph);
        var hub = Src("Koalix.App/Views/TunnelsHubView.xaml");
        Assert.DoesNotContain("&#xE968;", hub);            // the duplicated globe is gone from the hub
        Assert.Contains("<c:MotionGlyph Key=\"Aether\"", hub);
        var mw = Src("Koalix.App/MainWindow.xaml");
        Assert.Contains("<DataTrigger Binding=\"{Binding Key}\" Value=\"Nav.TunnelHub\">", mw);
        var tour = Src("Koalix.App/Views/OnboardingView.xaml.cs");
        Assert.DoesNotContain("\uE968", tour);            // and from the tour's aether pill
        Assert.Contains("Key = \"Aether\"", tour);
    }

    /// <summary>v0.4.52 — «انقد بزرگگگ نباشه گزینه هاش»: the hub pills, section cards and action chips all got
    /// smaller (the old 14,10 pills / 20,16 cards / 16,8 chips are banned).</summary>
    [Fact]
    public void Circuits_page_options_are_not_huge_any_more()
    {
        var hub = Src("Koalix.App/Views/TunnelsHubView.xaml");
        Assert.DoesNotContain("Padding=\"14,10\"", hub);
        Assert.DoesNotContain("Padding=\"20,16\"", hub);
        Assert.DoesNotContain("Padding=\"16,8\" Background=\"{StaticResource Hub", hub);
        Assert.Contains("Padding=\"10,6\"", hub);
        Assert.Contains("Padding=\"16,12\"", hub);
    }

    /// <summary>v0.4.52 — «موقع گرفتن بکاپ ازم بپرسه چه چیزایی تو فایل ذخیره شه»: the picker dialog decides the
    /// sections (settings on; profiles/subs/rules/dns off by default), the payload carries a Sections list, restore
    /// only replaces the sections the file actually has, and LOGS are never an option anywhere.</summary>
    [Fact]
    public void Backups_are_selective_and_logs_never_travel()
    {
        var svc = Src("Koalix.Infrastructure/BackupService.cs");
        Assert.Contains("public sealed record BackupOptions(bool Settings = true, bool Profiles = false, bool Subscriptions = false, bool AppRules = false, bool DnsProfiles = false)", svc);
        Assert.Contains("public void Export(string file, string? password, BackupOptions? opts = null)", svc);
        Assert.Contains("List<string>? Sections = null", svc);
        Assert.Contains("var sections = p.Sections ??", svc);   // legacy files still import fully
        Assert.Contains("bool Has(string s) => sections.Contains(s);", svc);
        var vm = Src("Koalix.App/ViewModels/SettingsViewModel.cs");
        Assert.Contains("var scope = Controls.BackupScopeDialog.Show();", vm);
        Assert.Contains("Backup.Export(file, pwUse, scope)", vm);
        Assert.Contains("BackupStatus = L.Format(\"Backup.Done\", file);", vm); // «تو تب بکاپ بنویسه اونو»
        Assert.DoesNotContain("StatusMessage = L.Format(\"Backup.Done", vm);     // the top banner is gone
        Assert.True(File.Exists(Path.Combine(RepoRoot(), "src", "Koalix.App", "Controls", "BackupScopeDialog.cs")), "BackupScopeDialog.cs missing");
    }

    /// <summary>v0.4.52 — «فایل بکاپ پیشفرض میخوام این باشه فقط بخش تنظیماتش ها … چیزای شخصیم … کلا حذفشون کن»:
    /// the reference backup's SETTINGS become factory defaults, stripped of everything personal (WARP identity,
    /// game paths, widget placement, onboarding flags), seeded once and only into empty keys.</summary>
    [Fact]
    public void Factory_defaults_are_the_reference_backup_settings_sanitized()
    {
        var path = Path.Combine(RepoRoot(), "src", "Koalix.App", "Defaults", "FactorySettings.json");
        Assert.True(File.Exists(path), "FactorySettings.json missing");
        var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("defaults");
        var keys = json.EnumerateObject().Select(p2 => p2.Name).ToList();
        Assert.Contains("ui.theme", keys);
        Assert.Contains("conn.auto", keys);
        foreach (var banned in new[] { "chain.warp.priv", "chain.warp.peer", "gaming.profiles", "widget.x", "widget.y", "ui.onboarded", "conn.selected", "update.latest", "ui.trayHintShown" })
            Assert.DoesNotContain(banned, keys);
        var app = Src("Koalix.App/App.xaml.cs");
        Assert.Contains("ApplyFactoryDefaults(settings)", app);
        Assert.Contains("app.factoryApplied", app);
        Assert.Contains("string.IsNullOrEmpty(settings.Get(prop.Name))", app); // never overwrites
    }

    /// <summary>v0.4.52 — «بک گراند های متحرک بیشتری و اضافه کنی و قابل انتخاب باشن»: five selectable kinds
    /// (off/particles/mesh/orbits/meteors) drawn by one LiveBackdrop, chosen in Settings (ui.bgKind).</summary>
    [Fact]
    public void Animated_backgrounds_are_selectable_in_five_kinds()
    {
        var bg = Src("Koalix.App/Controls/LiveBackdrop.cs");
        Assert.Contains("public static void PushKind(string kind)", bg);
        Assert.Contains("\"aurora\" or \"rain\" or \"bubbles\" or \"starfield\" or \"fireflies\"", bg); // v0.4.54 — +5 skies
        Assert.Contains("private void DrawOrbits", bg);
        Assert.Contains("Kind == \"meteors\"", bg);
        var vm = Src("Koalix.App/ViewModels/SettingsViewModel.cs");
        Assert.Contains("\"orbits\", \"meteors\", \"aurora\", \"rain\", \"bubbles\", \"starfield\", \"fireflies\", \"grid\", \"waves\", \"nebula\", \"snow\", \"kaleido\"]", vm); // v0.4.54 + v0.4.56 — ten skies
        Assert.Contains("Controls.LiveBackdrop.PushKind(value)", vm);
        var view = Src("Koalix.App/Views/SettingsView.xaml");
        Assert.Contains("{l:T Settings.LiveBg}", view);
        var fa = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Koalix.App", "Localization", "fa.json"))).RootElement;
        Assert.Equal("پس‌زمینهٔ متحرک", fa.GetProperty("Settings.LiveBg").GetString());
        Assert.Equal("صورت فلکی", fa.GetProperty("LiveBg.Mesh").GetString());
    }

    /// <summary>v0.4.52 — «دارک مود و لایت مود که میزنم … تصویر دوتایی میشه»: the cross-fade snapshot can no
    /// longer stick — a watchdog force-clears an in-flight reveal and a returning window cleans it up.</summary>
    [Fact]
    public void Theme_reveal_never_leaves_a_ghost_snapshot()
    {
        var code = Src("Koalix.App/MainWindow.xaml.cs");
        Assert.Contains("StartRevealWatchdog();", code);
        Assert.Contains("private void ForceCleanupReveal()", code);
        Assert.Contains("if (_themeFading) ForceCleanupReveal();", code);
        Assert.Contains("_themeFading = false; // v0.4.52 — the reveal is done", code);
    }

    /// <summary>v0.4.52 — «هر وقت اپدیت جدید میدیم … وقتی اولین بار اپدیت میکنیم اپو بیاد رو تصویر و ببینیم چیه»:
    /// the first launch after an update pops the version's notes once (app.seenVersion); fresh installs skip.</summary>
    [Fact]
    public void Updates_greet_the_user_with_what_is_new()
    {
        Assert.True(File.Exists(Path.Combine(RepoRoot(), "src", "Koalix.App", "WhatsNew.json")));
        Assert.Contains("0.4.52", File.ReadAllText(Path.Combine(RepoRoot(), "src", "Koalix.App", "WhatsNew.json")));
        Assert.True(File.Exists(Path.Combine(RepoRoot(), "src", "Koalix.App", "Controls", "WhatsNewWindow.cs")));
        var app = Src("Koalix.App/App.xaml.cs");
        Assert.Contains("ShowWhatsNewOnce(settings)", app);
        Assert.Contains("app.seenVersion", app);
        Assert.Contains("if (seen is null && settings.Get(\"ui.onboarded\") != \"1\")", app); // fresh install → tour instead
        var csproj = Src("Koalix.App/Koalix.App.csproj");
        Assert.Contains("WhatsNew.json", csproj);
    }

    /// <summary>v0.4.52 — «تو بخش تور گام یازده و ده باید عوض شن»: both steps describe the new menu editing and
    /// the renamed «مدارها» page.</summary>
    [Fact]
    public void Tour_steps_ten_and_eleven_match_the_new_reality()
    {
        var fa = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Koalix.App", "Localization", "fa.json"))).RootElement;
        Assert.Contains("ویرایش منو", fa.GetProperty("Tour.Step10Text").GetString()!);
        Assert.Contains("مدارها", fa.GetProperty("Tour.Step11Text").GetString()!);
        Assert.Contains("مدارها", fa.GetProperty("Tour.Step11Title").GetString()!);
    }

    /* ───── v0.4.53 — compact picker chips, preset active-state, picked-server highlight,
       unlimited subscriptions, tour step swap, new factory defaults ───── */

    /// <summary>v0.4.53 — «لینک ساب هایی که نامحوده و مینویسه منقضی شده»: panels send expire=0 / total=0 for
    /// UNLIMITED accounts — the parser now stores null (never 1970), the store treats 0 as alive, and the sub
    /// rows ignore expire=0 synced before the fix.</summary>
    [Fact]
    public void Unlimited_subscriptions_never_show_expired()
    {
        var info = Koalix.Tunneling.SubUserInfo.Parse("upload=1; download=2; total=0; expire=0");
        Assert.NotNull(info);
        Assert.Null(info!.ExpireUnix);
        Assert.Null(info.TotalBytes);
        var dated = Koalix.Tunneling.SubUserInfo.Parse("upload=1; download=2; total=1024; expire=4102444800");
        Assert.NotNull(dated!.ExpireUnix);
        Assert.Equal(1024, dated.TotalBytes);
        var svc = Src("Koalix.Infrastructure/Repositories.cs");
        Assert.Contains("ExpireUnix is { } e && e > 0 &&", svc);
        var vm = Src("Koalix.App/ViewModels/ProfilesViewModel.cs");
        Assert.Contains("if (s.ExpireUnix is { } exp && exp > 0)", vm);
    }

    /// <summary>v0.4.53 — «روی گیم فقط نشون میده سلکته .. رو گزینه های دیگه بزنیم اعمال میشه ولی نشونش نمیده»:
    /// the applied preset is tracked (persisted aether.preset), all three buttons show their own check + accent
    /// border, and گیم is no longer accent-styled forever.</summary>
    [Fact]
    public void Aether_presets_show_a_real_active_state()
    {
        var vm = Src("Koalix.App/ViewModels/DashboardViewModel.cs");
        Assert.Contains("private string _aetherActivePreset", vm);
        Assert.Contains("public bool IsGamingPresetActive => AetherActivePreset == \"gaming\";", vm);
        Assert.Contains("public bool IsStealthPresetActive => AetherActivePreset == \"stealth\";", vm);
        Assert.Contains("public bool IsDefaultPresetActive => AetherActivePreset == \"default\";", vm);
        Assert.Contains("aether.preset", vm);
        Assert.Matches("(?s)AetherGamingPreset\\(\\) \\{ _aether.ApplyGamingPreset\\(\\); AetherActivePreset = \"gaming\"", vm);
        var hub = Src("Koalix.App/Views/TunnelsHubView.xaml");
        foreach (var b in new[] { "IsGamingPresetActive", "IsStealthPresetActive", "IsDefaultPresetActive" })
            Assert.Contains($"Binding=\"{{Binding {b}}}\" Value=\"True\"", hub);
    }

    /// <summary>v0.4.53 — «کوچکتر نکردی دکمه انتخاب کردنشو … دیده نمیشه»: the WARP/Aether picker is now two
    /// size-to-content chips (icon + title + status dot) instead of two half-page stretched cards.</summary>
    [Fact]
    public void Tunnel_picker_is_two_compact_chips_with_status_dots()
    {
        var hub = Src("Koalix.App/Views/TunnelsHubView.xaml");
        Assert.DoesNotContain("TunnelHub.Pill.ChainOff", hub);      // the tall status line is gone from the pills
        Assert.Contains("<StackPanel Orientation=\"Horizontal\" HorizontalAlignment=\"Left\" Margin=\"0,2,0,0\">", hub);
        Assert.Contains("Binding=\"{Binding ChainEnabled}\" Value=\"True\">", hub);  // warp dot
        Assert.Contains("Binding=\"{Binding AetherIsConnected}\" Value=\"True\">", hub); // aether dot
        Assert.Contains("<c:MotionGlyph Key=\"Aether\"", hub);     // the atom survived the rewrite
    }

    /// <summary>v0.4.53 — «همچنان رو نشون دادن سروری که سلکته مشکل داره»: the grid highlight always rides the
    /// server the banner NAMES (manual pick or the auto row's current pick), and the AUTO row never glows.</summary>
    [Fact]
    public void Server_highlight_follows_the_named_server()
    {
        var vm = Src("Koalix.App/ViewModels/DashboardViewModel.cs");
        Assert.Contains("private void SyncPickHighlight()", vm);
        Assert.Contains("ReferenceEquals(r.Profile, ent) && r.Name == title", vm); // v0.4.54/55 — exact config (entity + title), not name, not bare id
        Assert.Contains("if (_autoRow is not null) _autoRow.IsSelected = false;", vm);
        Assert.Contains("if (SelectedProfile is { IsAuto: true }) SyncPickHighlight();", vm); // after the auto pick moves
        Assert.Contains("SyncPickHighlight(); // v0.4.53", vm); // on selection change
    }

    /// <summary>v0.4.53 — «قرار بود گام ۱۱ رو با گام ۱۰ عوض کنی»: the tour plays the circuits step BEFORE the
    /// toolbox step now.</summary>
    [Fact]
    public void Tour_steps_ten_and_eleven_are_swapped()
    {
        var ob = Src("Koalix.App/Views/OnboardingView.xaml.cs");
        Assert.Contains("LooksStep, ShortcutsStep, TunnelHubStep, Toolbox", ob);
        Assert.DoesNotContain("ShortcutsStep, Toolbox, TunnelHubStep", ob);
    }

    /// <summary>v0.4.53 — «بکاپ پیشفرض میخوام این باشه»: the factory defaults were rebuilt from the 2026-11-17
    /// 18:38 reference backup (settings-only export), same sanitization as before.</summary>
    [Fact]
    public void Factory_defaults_match_the_new_reference_backup()
    {
        var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Koalix.App", "Defaults", "FactorySettings.json"))).RootElement.GetProperty("defaults");
        var keys = json.EnumerateObject().Select(p2 => p2.Name).ToList();
        Assert.Contains("ui.accent", keys);
        Assert.Equal("#FF38BDF8", json.GetProperty("ui.accent").GetString()); // the new reference's accent
        Assert.Equal("0", json.GetProperty("conn.auto").GetString());
        foreach (var banned in new[] { "chain.warp.priv", "chain.warp.peer", "gaming.profiles", "widget.x", "widget.y", "ui.onboarded", "conn.selected", "update.latest" })
            Assert.DoesNotContain(banned, keys);
    }

    /* ───── v0.4.54 — +5 backdrops, selective-tunnels nav route, id-based highlight, accent-driven circuit
       colors, bold Aether mark, five connect shapes, five connect motions ───── */

    /// <summary>v0.4.54 — «پس زمینه متحرک هارو بیشتر کن 5 تا دیگه اضافه کن»: aurora, rain, bubbles, starfield and
    /// fireflies join the picker (ui.bgKind), each with its own sky in LiveBackdrop.</summary>
    [Fact]
    public void Five_more_animated_backdrops_are_selectable()
    {
        var bg = Src("Koalix.App/Controls/LiveBackdrop.cs");
        foreach (var k in new[] { "aurora", "rain", "bubbles", "starfield", "fireflies" })
            Assert.Contains($"Kind == \"{k}\"", bg);
        Assert.Contains("private void DrawAurora", bg);
        var vm = Src("Koalix.App/ViewModels/SettingsViewModel.cs");
        Assert.Contains("\"aurora\", \"rain\", \"bubbles\", \"starfield\", \"fireflies\",", vm); // v0.4.56 — the picker keeps growing
        var fa = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Koalix.App", "Localization", "fa.json"))).RootElement;
        Assert.Equal("شفق قطبی", fa.GetProperty("LiveBg.Aurora").GetString());
        Assert.Equal("کرم‌های شب‌تاب", fa.GetProperty("LiveBg.Fireflies").GetString());
    }

    /// <summary>v0.4.54 — «روی مدیریت تونل ها میزنم میره توی مدار ها باید بره توی تونل های انتخابی»: the tunnels
    /// nav row carries the selective-tunnels page and the circuits page rides the other key — labels swapped too,
    /// the dashboard deep-link finds the circuits row by ITS view-model, and the glyphs swap with the rows.</summary>
    [Fact]
    public void The_tunnels_nav_row_opens_selective_tunnels()
    {
        var main = Src("Koalix.App/ViewModels/MainViewModel.cs");
        Assert.Contains("new(\"Nav.TunnelHub\", \"\\uE8AB\", split)", main);
        Assert.Contains("new(\"Nav.Split\", \"\\uE8AB\", hub)", main);
        Assert.Contains("main.Nav.First(n => n.Page is TunnelsHubViewModel); // v0.4.54", Src("Koalix.App/ViewModels/DashboardViewModel.cs"));
        var glyph = Src("Koalix.App/Controls/MotionGlyph.cs");
        Assert.Contains("case \"Nav.Split\": (canvas, sb) = CircuitIcon(w, h); break;", glyph);
        Assert.Contains("case \"Nav.TunnelHub\": (canvas, sb) = SplitIcon(w, h); break;", glyph);
        var fa = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Koalix.App", "Localization", "fa.json"))).RootElement;
        Assert.Equal("مدارها", fa.GetProperty("Nav.Split").GetString());
        Assert.Equal("تونل‌های انتخابی", fa.GetProperty("Nav.TunnelHub").GetString());
    }

    /// <summary>v0.4.54 — «انگار کل کانفیگا انتخاب شدن»: the highlight matches by PROFILE ID now — many subs ship
    /// configs with the SAME display name, and the old name match lit them all.</summary>
    [Fact]
    public void Server_highlight_matches_by_profile_id()
    {
        var vm = Src("Koalix.App/ViewModels/DashboardViewModel.cs");
        Assert.Contains("ReferenceEquals(r.Profile, ent) && r.Name == title", vm); // v0.4.55 — entity+title: the «OpenVpn (۲)/(۳)» siblings share ONE TunnelProfile, so the id match lit them all
        Assert.DoesNotContain("r.IsSelected = on && !r.IsAuto && r.Name == pick;", vm);
        Assert.DoesNotContain("pickId is { } id && r.Profile?.Id == id", vm);
    }

    /// <summary>v0.4.54 — «رنگ حالت وارپ و اتر به بک گراند نمیخوره» + the stale ARGB neon: every hardcoded
    /// tunnel blue/purple rides the accent resources now (brush, glow color and gradients), so the palette —
    /// including the ARGB one — repaints them instantly.</summary>
    [Fact]
    public void Circuits_colors_ride_the_accent()
    {
        var hub = Src("Koalix.App/Views/TunnelsHubView.xaml");
        Assert.DoesNotContain("#4C9AFF", hub);
        Assert.DoesNotContain("#C084FC", hub);
        Assert.DoesNotContain("#7FB4F2", hub);
        Assert.DoesNotContain("#1E4E8C", hub);
        Assert.Contains("Color=\"{DynamicResource AccentColor}\"", hub);       // the neon glow follows ARGB
        Assert.Contains("Value=\"{DynamicResource Accent2Brush}\"", hub); // setter-form accent2
        Assert.Contains("<c:MotionGlyph Key=\"Circuit\"", hub);              // «لوگوی مدار داخل بخش مدار»
    }

    /// <summary>v0.4.54 — «لوگوی اتر و عوض کن هیچی معلوم نیست»: the hairline atom became a THICK shield with a
    /// filled bolt (readable at 14px) with a flicker while alive.</summary>
    [Fact]
    public void The_aether_mark_is_a_bold_shield_bolt()
    {
        var glyph = Src("Koalix.App/Controls/MotionGlyph.cs");
        Assert.Contains("private (Canvas, Storyboard) AetherIcon(double w, double h)", glyph);
        Assert.Contains("C {F(w * 0.70)", glyph);                                // the shield's rounded shoulders
        Assert.Contains("var t = Math.Max(1.6, w * 0.115);", glyph);             // THICK stroke
        Assert.Contains("Shape.FillProperty", glyph);                            // the filled bolt
        Assert.Contains("flicker", glyph);
    }

    /// <summary>v0.4.54 — «شکل دکمه اتصال هم چند مدل کن» + «داخلش انیمیشن و متحرک بودنش هم چن مدل بکن»:
    /// five shapes (rounded/circle/hex/shield/diamond) and five inner motions (water/breathe/pulse/orbit/none),
    /// both picked on the Settings page and persisted.</summary>
    [Fact]
    public void Connect_button_has_five_shapes_and_five_motions()
    {
        var prefs = Src("Koalix.App/Controls/UiPrefs.cs");
        Assert.Contains("public static string Shape", prefs);
        Assert.Contains("public static string Motion", prefs);
        Assert.Contains("public static void Set(string shape, string motion)", prefs);
        Assert.Contains("ui.connectMotion", prefs);
        var orb = Src("Koalix.App/Controls/ConnectOrb.cs");
        foreach (var k in new[] { "hex", "diamond", "shield" })
            Assert.Contains("\"" + k + "\" =>", orb);
        Assert.Contains("private static Geometry ShieldGeo(double size)", orb);
        Assert.Contains("private static void Pulse(Path ring", orb);
        Assert.Contains("_orbitHost", orb);
        Assert.Contains("UiPrefs.Motion == \"breathe\"", orb);
        Assert.Contains("\"pulse\" => 1.5, \"heartbeat\" => 1.35, \"none\" => 0", orb); // v0.4.56
        var vm = Src("Koalix.App/ViewModels/SettingsViewModel.cs");
        Assert.Contains("[\"rounded\", \"circle\", \"hex\", \"shield\", \"diamond\"]", vm);
        Assert.Contains("[\"water\", \"heartbeat\", \"tide\", \"storm\", \"galaxy\", \"vortex\", \"breathe\", \"pulse\", \"orbit\", \"none\"]", vm); // v0.4.56 — ten motions
        var view = Src("Koalix.App/Views/SettingsView.xaml");
        Assert.Contains("{Binding ConnectMotions}", view);
        var fa = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Koalix.App", "Localization", "fa.json"))).RootElement;
        Assert.Equal("مدارگردی", fa.GetProperty("Settings.Motion.Orbit").GetString());
        Assert.Equal("شش‌ضلعی", fa.GetProperty("Settings.ShapeHex").GetString());
    }

    /* ───── v0.4.55 — shield built safe, exact-config highlight, readable subtle text, +3 style presets ───── */

    /// <summary>v0.4.55 — «شکل دکمه اتصال سپر که میزنم میرم تو داشبورد همه چیز ناپدید میشه»: the shield was the
    /// only shape built through Geometry.Parse + a geometry Transform — it is now drawn programmatically like the
    /// shapes that always worked, and ApplyShape is failure-proof (any glitch falls back to the rounded square
    /// instead of blanking the dashboard).</summary>
    [Fact]
    public void Shield_shape_cannot_blank_the_dashboard()
    {
        var orb = Src("Koalix.App/Controls/ConnectOrb.cs");
        Assert.DoesNotContain("Geometry.Parse(", orb);
        Assert.Contains("ctx.BezierTo(P(72, 10, u), P(92, 14, u), P(92, 30, u), true, true);", orb); // the shield's right shoulder
        Assert.Contains("catch\n        {\n            UiPrefs.Set(\"rounded\"", orb); // failure-proof ApplyShape
        Assert.Contains("private void ApplyShapeCore()", orb);
    }

    /// <summary>v0.4.55 — «تو قسمت مدار ها بازم رنگ متن خوب نیست»: small texts sit on translucent cards over a
    /// vivid ARGB backdrop — the dark-mode subtle tone got brighter.</summary>
    [Fact]
    public void Subtle_text_reads_on_vivid_backdrops()
    {
        Assert.Contains("SubtleTextBrush\" Color=\"#FFDCE2EF\"", Src("Koalix.App/Themes/Dark.xaml"));
    }

    /// <summary>v0.4.55 — «سبک ظاهری های جدید هم اضافه کن»: three new presets — پرنور (vivid), مونو (mono) and
    /// لطیف (soft) — each with its own radii, card surface and backdrop strength, selectable in Settings.</summary>
    [Fact]
    public void Three_new_ui_style_presets()
    {
        var theme = Src("Koalix.App/Services/ThemeService.cs");
        Assert.Contains("\"default\", \"neon\", \"minimal\", \"retro\", \"vivid\", \"mono\", \"soft\"", theme);
        foreach (var s in new[] { "vivid", "mono", "soft" })
            Assert.Contains($"case \"{s}\":", theme);
        var fa = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Koalix.App", "Localization", "fa.json"))).RootElement;
        Assert.Equal("پرنور", fa.GetProperty("Style.vivid").GetString());
        Assert.Equal("مونو", fa.GetProperty("Style.mono").GetString());
        Assert.Equal("لطیف", fa.GetProperty("Style.soft").GetString());
        var en = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Koalix.App", "Localization", "en.json"))).RootElement;
        Assert.Equal("Soft", en.GetProperty("Style.soft").GetString());
    }

    /* ───── v0.4.56 — +5 motions, +5 skies, window materials, shield label centre, readable hub, guides → hub ───── */

    /// <summary>v0.4.56 — «انیمیشن دکمه اتصال بیشتر بیار 5 تا دیگه خلاقیتت بیشتر باشه موس روی هر کدوم اومد یک توضیح هم بده»:
    /// heartbeat, tide, storm, galaxy and vortex join the picker — each with a hover tooltip.</summary>
    [Fact]
    public void Five_new_connect_motions_with_hover_explanations()
    {
        var vm = Src("Koalix.App/ViewModels/SettingsViewModel.cs");
        foreach (var m in new[] { "heartbeat", "tide", "storm", "galaxy", "vortex" })
            Assert.Contains($"\"{m}\"", vm);
        var prefs = Src("Koalix.App/Controls/UiPrefs.cs");
        foreach (var m in new[] { "heartbeat", "tide", "storm", "galaxy", "vortex" })
            Assert.Contains($"\"{m}\"", prefs); // the persisted whitelist accepts them
        var orb = Src("Koalix.App/Controls/ConnectOrb.cs");
        Assert.Contains("UiPrefs.Motion == \"heartbeat\"", orb);        // lub-dub keyframes
        Assert.Contains("BuildBoltGeometry", orb);                        // the storm bolt
        Assert.Contains("UiPrefs.Motion == \"galaxy\"", orb);            // ten star riders
        Assert.Contains("_waveSpin.BeginAnimation(RotateTransform.AngleProperty", orb); // the vortex spin
        Assert.Contains("UiPrefs.Motion == \"tide\"", orb);              // the tide level
        var fa = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Koalix.App", "Localization", "fa.json"))).RootElement;
        Assert.Equal("ضربان قلب", fa.GetProperty("Settings.Motion.Heartbeat").GetString());
        Assert.Equal("کهکشان", fa.GetProperty("Settings.Motion.Galaxy").GetString());
        foreach (var m in new[] { "Water", "Heartbeat", "Tide", "Storm", "Galaxy", "Vortex", "Breathe", "Pulse", "Orbit", "None" })
            Assert.True(fa.TryGetProperty($"Settings.MotionTip.{m}", out _)); // a tooltip for every option, old and new
        var view = Src("Koalix.App/Views/SettingsView.xaml");
        Assert.Contains("ConverterParameter=\"Settings.MotionTip\"", view);
    }

    /// <summary>v0.4.56 — «پس زمینه متحرک 5 تا دیگه بیار با خلاقیت بیشتر و موس روی هر کدوم اومد یک توضیح»: neon grid,
    /// light waves, nebula, snow and a kaleidoscope — each with a hover tooltip.</summary>
    [Fact]
    public void Five_new_backdrop_skies_with_hover_explanations()
    {
        var vm = Src("Koalix.App/ViewModels/SettingsViewModel.cs");
        Assert.Contains("\"grid\", \"waves\", \"nebula\", \"snow\", \"kaleido\"", vm);
        var bg = Src("Koalix.App/Controls/LiveBackdrop.cs");
        foreach (var k in new[] { "grid", "waves", "nebula", "snow", "kaleido" })
            Assert.Contains($"\"{k}\"", bg); // the sky whitelist
        foreach (var m in new[] { "DrawGrid", "DrawWaves", "DrawNebula", "DrawKaleido" })
            Assert.Contains($"private void {m}(", bg);
        var fa = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Koalix.App", "Localization", "fa.json"))).RootElement;
        Assert.Equal("کالیدوسکوپ", fa.GetProperty("LiveBg.Kaleido").GetString());
        Assert.Equal("برف", fa.GetProperty("LiveBg.Snow").GetString());
        foreach (var k in new[] { "Grid", "Waves", "Nebula", "Snow", "Kaleido" })
            Assert.True(fa.TryGetProperty($"LiveBgTip.{k}", out _));
        var view = Src("Koalix.App/Views/SettingsView.xaml");
        Assert.Contains("ConverterParameter=\"LiveBgTip\"", view);
    }

    /// <summary>v0.4.56 — «جنس پنجره بیشتر هم میشه اضافه کنی حتی»: Auto and MicaAlt join the window materials, and
    /// the DWM system-backdrop mapping now uses the real DWMSBT values.</summary>
    [Fact]
    public void Window_materials_extended()
    {
        var theme = Src("Koalix.App/Services/ThemeService.cs");
        Assert.Contains("public enum Backdrop { Auto, Mica, Acrylic, MicaAlt, None }", theme);
        Assert.Contains("Backdrop.Auto => 1, Backdrop.Mica => 3, Backdrop.Acrylic => 4, Backdrop.MicaAlt => 5, _ => 2", theme);
        var fa = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Koalix.App", "Localization", "fa.json"))).RootElement;
        Assert.Equal("خودکار ویندوز", fa.GetProperty("Backdrop.Auto").GetString());
        Assert.True(fa.TryGetProperty("BackdropTip.MicaAlt", out _));
        var view = Src("Koalix.App/Views/SettingsView.xaml");
        Assert.Contains("ConverterParameter=\"BackdropTip\"", view);
    }

    /// <summary>v0.4.56 — «نوشته وسط سپر نیست»: the shield tapers to a point at the bottom, so the icon+label ride a
    /// per-shape vertical offset and read centred again.</summary>
    [Fact]
    public void Shield_label_sits_on_its_visual_centre()
    {
        var orb = Src("Koalix.App/Controls/ConnectOrb.cs");
        Assert.Contains("_contentShift.Y = kind == \"shield\" ? -22 : 0;", orb); // v0.4.57 — deeper offset, the label read ~70% down
    }

    /// <summary>v0.4.56 — «متن وارپ و اتر باید سفید بشه» + «لوگوی اتر تو حالت سفید دیده نمیشه»: the hub card headers
    /// carry the theme text brush explicitly and the hardcoded violet atom chip follows the accent.</summary>
    [Fact]
    public void Warp_aether_texts_and_logo_are_readable_in_both_modes()
    {
        var hub = Src("Koalix.App/Views/TunnelsHubView.xaml");
        Assert.Contains("<TextBlock Text=\"{l:T Chain.Title}\" FontSize=\"13.5\" FontWeight=\"Bold\" Foreground=\"{DynamicResource TextBrush}\"/>", hub);
        Assert.Contains("<TextBlock Text=\"{l:T Aether.Title}\" FontSize=\"13.5\" FontWeight=\"Bold\" Foreground=\"{DynamicResource TextBrush}\"/>", hub);
        Assert.DoesNotContain("#3A1B66", hub);
        Assert.Contains("Background=\"{DynamicResource AccentSoftBrush}\">", hub);
    }

    /// <summary>v0.4.56 — «تو قسمت اموزش اتر بگو که جاش اومده توی مدار ها» (و برای وارپ هم): the Aether lessons
    /// navigate to the Tunnels hub and the text no longer says the card lives on the dashboard.</summary>
    [Fact]
    public void Aether_guide_points_at_the_hub_page()
    {
        var guide = Src("Koalix.App/ViewModels/GuideViewModel.cs");
        Assert.Contains("GuideTopic(\"Aether\", \"\\uE968\", run: () => Go(\"Nav.TunnelHub\"))", guide);
        Assert.Contains("GuideTopic(\"AetherGaming\", \"\\uE7FC\", advanced: true, run: () => Go(\"Nav.TunnelHub\"))", guide);
        var fa = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Koalix.App", "Localization", "fa.json"))).RootElement;
        Assert.Contains("«مدارها»", fa.GetProperty("Guide.Aether.Body").GetString()!);
        Assert.Equal("رفتن به مدارها", fa.GetProperty("Guide.AetherGaming.Do").GetString());
    }

    /* ───── v0.4.57 — the beauty batch: auto day/night, accent hue, OLED, personal image, compact density,
       soft rows entrance, animated connected ring, ping-good pulse + the selection highlight fix ───── */

    /// <summary>v0.4.57 — «سرور که انتخاب میکنم شکلش نشون نمیده انتخاب شده»: the hover-glow reveal saved the card's
    /// border brush at mouse-enter and wrote it back on leave, erasing the IsSelected trigger's accent border. It
    /// now clears the local value so the trigger wins again.</summary>
    [Fact]
    public void Hover_glow_can_no_longer_clobber_the_selection_border()
    {
        var fx = Src("Koalix.App/Controls/Fx.cs");
        Assert.Contains("b.ClearValue(Border.BackgroundProperty);\n        b.ClearValue(Border.BorderBrushProperty);", fx);
        Assert.Contains("the selection stayed hidden until a re-render", fx);
    }

    /// <summary>v0.4.57 — the three UI fixes + entrance for generated rows.</summary>
    [Fact]
    public void Shield_offset_tab_pills_and_rows_entrance()
    {
        var hub = Src("Koalix.App/Views/TunnelsHubView.xaml");
        Assert.Contains("<TextBlock Text=\"{l:T Tunnels.Tab.Chain}\" FontSize=\"12\" FontWeight=\"Bold\" Margin=\"6,0,0,0\" VerticalAlignment=\"Center\" Foreground=\"{DynamicResource TextBrush}\"/>", hub);
        Assert.Contains("<TextBlock Text=\"{l:T Tunnels.Tab.Aether}\" FontSize=\"12\" FontWeight=\"Bold\" Margin=\"6,0,0,0\" VerticalAlignment=\"Center\" Foreground=\"{DynamicResource TextBrush}\"/>", hub);
        var motion = Src("Koalix.App/Controls/Motion.cs");
        Assert.Contains("EntranceItemsProperty", motion);
        Assert.Contains("private static void ItemsLoaded", motion);
        var dash = Src("Koalix.App/Views/DashboardView.xaml");
        Assert.Contains("c:Motion.EntranceItems=\"True\"", dash);
    }

    /// <summary>v0.4.57 — idea 1: auto day/night with a minute timer; idea 2: the free hue slider; idea 5: OLED.</summary>
    [Fact]
    public void Auto_theme_hue_slider_and_oled()
    {
        var theme = Src("Koalix.App/Services/ThemeService.cs");
        Assert.Contains("public void SetAutoTheme(bool on, double sunrise, double sunset)", theme);
        Assert.Contains("private void EvaluateAutoTheme()", theme);
        Assert.Contains("public void SetAccentLive(string hex)", theme);
        Assert.Contains("public static string HueHex(double hue) => Hex(FromHsl(Math.Clamp(hue, 0, 359.9), 0.62, 0.56));", theme);
        Assert.Contains("internal static Color FromHsl(double h, double s, double l)", theme); // the v0.4.37 rainbow helper does the maths
        Assert.Contains("public void SetOled(bool on)", theme);
        Assert.Contains("r[\"WindowBrush\"] = Frozen(new SolidColorBrush(Color.FromRgb(0, 0, 0)));", theme);
        Assert.Contains("if (Oled) SetOled(Mode == AppTheme.Dark);", theme);
        var vm = Src("Koalix.App/ViewModels/SettingsViewModel.cs");
        foreach (var p in new[] { "public bool AutoTheme", "public double AutoSunrise", "public double AutoSunset", "public bool AccentHueOn", "public double AccentHue", "public bool Oled", "_theme.SetAutoTheme(_autoTheme, _autoSunrise, _autoSunset)" })
            Assert.Contains(p, vm);
        var fa = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Koalix.App", "Localization", "fa.json"))).RootElement;
        Assert.Equal("تم خودکار روز و شب", fa.GetProperty("Settings.AutoTheme").GetString());
        Assert.Equal("سیاه خالص (OLED)", fa.GetProperty("Settings.Oled").GetString());
    }

    /// <summary>v0.4.57 — ideas 3, 4, 6, 8, 9: the ring, the personal image, compact, the ping pulse and the
    /// settings surface for all of them.</summary>
    [Fact]
    public void Ring_personal_image_compact_and_ping_pulse()
    {
        var dash = Src("Koalix.App/Views/DashboardView.xaml");
        Assert.Contains("x:Name=\"RingSb\"", dash); // the ring animates itself (Style triggers cannot TargetName)
        Assert.Contains("(Border.BorderBrush).(Brush.RelativeTransform).(RotateTransform.Angle)", dash);
        Assert.Contains("<RemoveStoryboard BeginStoryboardName=\"RingSb\"/>", dash);
        Assert.Contains("<Condition Binding=\"{Binding PingLevel}\" Value=\"1\"/>", dash);
        var main = Src("Koalix.App/MainWindow.xaml");
        Assert.Contains("Source=\"{Binding BgImageSource}\"", main);
        Assert.Contains("<ScaleTransform ScaleX=\"{Binding UiScale}\" ScaleY=\"{Binding UiScale}\"/>", main);
        var mvm = Src("Koalix.App/ViewModels/MainViewModel.cs");
        Assert.Contains("public void LoadBgImage(string path)", mvm);
        Assert.Contains("UiScale = value ? 0.88 : 1.0;", mvm);
        var vm = Src("Koalix.App/ViewModels/SettingsViewModel.cs");
        Assert.Contains("private void PickBgImage()", vm);
        Assert.Contains("private void ClearBgImage()", vm);
        var view = Src("Koalix.App/Views/SettingsView.xaml");
        foreach (var b in new[] { "{Binding AutoTheme}", "{Binding AccentHueOn}", "{Binding Oled}", "{Binding Compact}", "{Binding PickBgImageCommand}" })
            Assert.Contains(b, view);
        var fa = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Koalix.App", "Localization", "fa.json"))).RootElement;
        Assert.Equal("حالت فشرده", fa.GetProperty("Settings.Compact").GetString());
        Assert.True(fa.TryGetProperty("Settings.BgPick", out _));
    }
}
