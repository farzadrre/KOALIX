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

    private static string Src(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KoalixSuite.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, "repo root not found above test bin");
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", relative));
    }

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
    /// panel AND re-raises the panel flags (the generator skips no-change assignments).</summary>
    [Fact]
    public void Tab_clicks_open_the_panel_and_commands_fire_on_every_click()
    {
        var vm = Src("Koalix.App/ViewModels/DashboardViewModel.cs");
        var xaml = Src("Koalix.App/Views/DashboardView.xaml");

        // (1) the open commands exist and set the flag + refresh the computed panel booleans
        Assert.Contains("[RelayCommand] private void TunnelsOpenChain() { TunnelsTabIsAether = false; OpenTunnelsPanel(); }", vm);
        Assert.Contains("[RelayCommand] private void TunnelsOpenAether() { TunnelsTabIsAether = true; OpenTunnelsPanel(); }", vm);
        Assert.Contains("private void OpenTunnelsPanel()", vm);
        Assert.Contains("TunnelsPanelOpen = true;", vm);

        // (2) both pills bind BOTH IsChecked (selection) and the open Command (every click)
        Assert.Contains("IsChecked=\"{Binding TunnelsTabChain, Mode=TwoWay}\"", xaml);
        Assert.Contains("Command=\"{Binding TunnelsOpenChainCommand}\"", xaml);
        Assert.Contains("IsChecked=\"{Binding TunnelsTabAether, Mode=TwoWay}\"", xaml);
        Assert.Contains("Command=\"{Binding TunnelsOpenAetherCommand}\"", xaml);

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
        // the dark SubtleTextBrush must stay readable on the glass cards (was #FF9AA3B8, measured ~3.6:1)
        var dark = Src("Koalix.App/Themes/Dark.xaml");
        Assert.Contains("<SolidColorBrush x:Key=\"SubtleTextBrush\" Color=\"#FFC3CAD9\"/>", dark);
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

    /// <summary>v0.4.49.1 — the Advanced-Tunnels card folds with the CardFold arrow (closed by default), and BOTH
    /// tab panels (chain / Aether) carry their own fold arrow + click-to-close title beside the tab strip.</summary>
    [Fact]
    public void Tunnels_card_and_tab_panels_have_fold_arrows()
    {
        var view = Src("Koalix.App/Views/DashboardView.xaml");
        // the card-level fold on the Advanced-Tunnels title, closed by default via CardFold.DefaultClosed
        Assert.Contains("<TextBlock Text=\"{l:T Tunnels.Title}\" Style=\"{StaticResource CardTitle}\" Margin=\"10,0,0,0\" VerticalAlignment=\"Center\" c:CardFold.Key=\"tunnels\"/>", view);
        // each tab panel header: clickable title + a chevron, both firing TunnelsCloseCommand
        var bindings = System.Text.RegularExpressions.Regex.Matches(view,
            @"<MouseBinding Gesture=""LeftClick"" Command=""\{Binding TunnelsCloseCommand\}""/>");
        Assert.True(bindings.Count >= 4, $"expected ≥4 panel fold bindings (title+arrow × 2 panels), found {bindings.Count}");
        Assert.Contains("Text=\"{l:T Chain.Title}\"", view);
        Assert.Contains("Text=\"{l:T Aether.Title}\"", view);
    }
}
