using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Koalix.Core;
using Koalix.Tunneling;

namespace Koalix.Aether;

/// <summary>v0.4.48 — port of Aether-GUI's full option set (github.com/MatinSenPai/Aether-GUI, profile.rs v0.8.0).
/// Every protocol/scan/noize/network option Aether's CLI understands, mapped to KOALIX settings ("aether.*").</summary>
public enum AetherProtocol { Auto, Masque, Wireguard, Gool, Mim }
public enum AetherScanMode { Turbo, Balanced, Thorough, Verified, Ironclad }
public enum AetherIpVersion { V4, V6, Both }
public enum AetherMasqueNoize { Firewall, Gfw, Off }
public enum AetherWgNoize { Balanced, Aggressive, Light, Off }
public enum AetherNetworkMode { Warp, PsiphonOnly, TorOnly, PsiphonChain, TorChain, PsiphonReverse, TorReverse }
public enum AetherZeroTrustAuth { Email, Service, Token }

/// <summary>The whole Aether connection profile — flags, env vars, readiness ports and timeouts, mirroring
/// Aether-GUI's ConnectionProfile (profiles.rs). Constructed from settings by <see cref="FromSettings"/>;
/// turned into CLI arguments by <see cref="AsArgs"/>.</summary>
public sealed record AetherProfile
{
    public AetherProtocol Protocol { get; init; } = AetherProtocol.Auto;
    public AetherScanMode ScanMode { get; init; } = AetherScanMode.Balanced;
    public AetherIpVersion IpVersion { get; init; } = AetherIpVersion.Both;
    public bool QuickReconnect { get; init; } = true;
    public bool MasqueHttp2 { get; init; }
    public AetherMasqueNoize MasqueNoize { get; init; } = AetherMasqueNoize.Firewall;
    public AetherWgNoize WgNoize { get; init; } = AetherWgNoize.Balanced;
    /// <summary>Local SOCKS5 listen address (Aether --bind). Default 127.0.0.1:1819 like Aether-GUI.</summary>
    public string BindAddress { get; init; } = "127.0.0.1:1819";
    /// <summary>Optional HTTP CONNECT listener address (Aether --http-proxy / --psiphon-http / --tor-http).</summary>
    public string HttpProxy { get; init; } = "";
    /// <summary>Optional upstream proxy Aether dials out through (socks5://host:port, http://host:port or bare host:port).</summary>
    public string Upstream { get; init; } = "";
    /// <summary>Exit-country filter, e.g. "!IR,RU" or "DE,SE" (Aether --exit-loc).</summary>
    public string ExitLoc { get; init; } = "";
    /// <summary>Tor/Psiphon carry mode (Aether --psiphon/--tor/--psiphon-only/…).</summary>
    public AetherNetworkMode NetworkMode { get; init; } = AetherNetworkMode.Warp;
    /// <summary>Ask Psiphon to leave from a country (Aether --psiphon-region), 2-letter code.</summary>
    public string PsiphonRegion { get; init; } = "";
    /// <summary>Resolvers used INSIDE the tunnel (Aether --dns), comma-separated like "1.1.1.1,1.0.0.1".</summary>
    public string Dns { get; init; } = "";
    /// <summary>Cloudflare Zero Trust organization name (Aether --team); empty = normal consumer WARP.</summary>
    public string ZeroTrustTeam { get; init; } = "";
    public AetherZeroTrustAuth ZeroTrustAuth { get; init; } = AetherZeroTrustAuth.Email;
    /// <summary>Sensitive Zero Trust credentials — supplied to the engine as ENV VARS, never command-line args
    /// (command lines are visible to other local processes). Never logged, never persisted as "last good".</summary>
    public string AccessEmail { get; init; } = "";
    public string AccessClientId { get; init; } = "";
    public string AccessClientSecret { get; init; } = "";
    public string AccessToken { get; init; } = "";
    public bool ZeroTrustGateway { get; init; }
    /// <summary>Point the OS-wide proxy at Aether's HTTP CONNECT listener while connected; restored on disconnect.</summary>
    public bool SystemProxy { get; init; }

    // ── Tor/Psiphon side listeners (Aether opens these next to --bind in chain modes) — same ports as Aether-GUI. ──
    public const string TorBind = "127.0.0.1:1820";
    public const string PsiphonBind = "127.0.0.1:1821";
    /// <summary>Local HTTP proxy used for the system proxy when the user hasn't set one (Aether-GUI's SYSTEM_PROXY_BIND).</summary>
    public const string SystemProxyBind = "127.0.0.1:1822";

    public bool IsPsiphon => NetworkMode is AetherNetworkMode.PsiphonOnly or AetherNetworkMode.PsiphonChain or AetherNetworkMode.PsiphonReverse;
    public bool IsTor => NetworkMode is AetherNetworkMode.TorOnly or AetherNetworkMode.TorChain or AetherNetworkMode.TorReverse;
    /// <summary>No WARP tunnel underneath: protocol, scan mode and noize don't apply.</summary>
    public bool IsOnly => NetworkMode is AetherNetworkMode.PsiphonOnly or AetherNetworkMode.TorOnly;
    /// <summary>Reverse modes carry TCP only, so the core refuses WireGuard/gool and always runs MASQUE over HTTP/2.</summary>
    public bool IsReverse => NetworkMode is AetherNetworkMode.PsiphonReverse or AetherNetworkMode.TorReverse;

    /// <summary>Extra seconds to wait on top of the scan budget: Tor may try plainly (75s) then walk bridges (up to
    /// 360s each); Psiphon waits up to 180s to tunnel (Aether-GUI's extra_wait_secs).</summary>
    public uint ExtraWaitSecs => IsTor ? 480u : IsPsiphon ? 200u : 0u;

    /// <summary>The whole profile as Aether ≥1.1.1 CLI flags, so no interactive prompt ever appears. Mirrors
    /// Aether-GUI's as_args(): one of the quick-reconnect flags is ALWAYS passed (otherwise 1.1.1 asks its own
    /// interactive "reconnect with last gateway?" question nothing answers).</summary>
    public IReadOnlyList<string> AsArgs()
    {
        var args = new List<string>(24);
        var protocol = IsReverse && Protocol is AetherProtocol.Wireguard or AetherProtocol.Gool ? AetherProtocol.Masque : Protocol;
        args.AddRange(protocol switch
        {
            AetherProtocol.Masque => ["--masque"],
            AetherProtocol.Wireguard => ["--wg"],
            AetherProtocol.Gool => ["--gool"],
            AetherProtocol.Mim => ["--mim"],
            _ => [],
        });
        var net = NetworkMode switch
        {
            AetherNetworkMode.PsiphonOnly => "--psiphon-only",
            AetherNetworkMode.TorOnly => "--tor-only",
            AetherNetworkMode.PsiphonChain => "--psiphon",
            AetherNetworkMode.TorChain => "--tor",
            AetherNetworkMode.PsiphonReverse => "--psiphon-reverse",
            AetherNetworkMode.TorReverse => "--tor-reverse",
            _ => null,
        };
        if (net is { } n)
        {
            args.Add(n);
            if (IsPsiphon && PsiphonRegion.Trim().Length == 2 && PsiphonRegion.Trim().All(char.IsAsciiLetter))
            { args.Add("--psiphon-region"); args.Add(PsiphonRegion.Trim().ToUpperInvariant()); }
        }
        args.Add(ScanMode switch
        {
            AetherScanMode.Turbo => "--turbo",
            AetherScanMode.Thorough => "--thorough",
            AetherScanMode.Verified => "--verified",
            AetherScanMode.Ironclad => "--ironclad",
            _ => "--balanced",
        });
        args.Add(IpVersion switch { AetherIpVersion.V4 => "-4", AetherIpVersion.V6 => "-6", _ => "--dual" });
        args.Add(QuickReconnect ? "--quick-reconnect" : "--no-quick-reconnect");
        args.Add("--noize");
        args.Add(NoizeFlag(protocol));
        if (BindAddress != "127.0.0.1:1819" && System.Net.IPEndPoint.TryParse(BindAddress, out var bind))
        { args.Add("--bind"); args.Add(bind.ToString()); }
        if (Dns.Trim().Length > 0) { args.Add("--dns"); args.Add(Dns.Trim()); }
        if (ZeroTrustTeam.Trim().Length > 0)
        {
            args.Add("--team"); args.Add(ZeroTrustTeam.Trim());
            if (ZeroTrustGateway) args.Add("--gateway");
        }
        if (HttpFront() is { } front)
        { args.Add(HttpFrontFlag()); args.Add(front.ToString()); }
        if (Upstream.Trim().Length > 0) { args.Add("--upstream"); args.Add(Upstream.Trim()); }
        if (ExitLoc.Trim().Length > 0) { args.Add("--exit-loc"); args.Add(ExitLoc.Trim()); }
        return args;
    }

    /// <summary>The noize flag matching the active protocol family (MASQUE: firewall/gfw/off; WG/gool: balanced/aggressive/light/off).</summary>
    public string NoizeFlag(AetherProtocol protocol) =>
        protocol is AetherProtocol.Wireguard or AetherProtocol.Gool
            ? WgNoize switch { AetherWgNoize.Aggressive => "aggressive", AetherWgNoize.Light => "light", AetherWgNoize.Off => "off", _ => "balanced" }
            : MasqueNoize switch { AetherMasqueNoize.Gfw => "gfw", AetherMasqueNoize.Off => "off", _ => "firewall" };

    /// <summary>The HTTP CONNECT address to serve next to the primary SOCKS5 proxy: the user's own, or a default
    /// when the system proxy needs one.</summary>
    public System.Net.IPEndPoint? HttpFront() =>
        System.Net.IPEndPoint.TryParse(HttpProxy.Trim(), out var own) ? own
        : SystemProxy ? System.Net.IPEndPoint.Parse(SystemProxyBind) : null;

    /// <summary>Each Tor/Psiphon chain has its own HTTP flag; plain and reverse modes serve HTTP from the WARP proxy.</summary>
    public string HttpFrontFlag() => NetworkMode switch
    {
        AetherNetworkMode.PsiphonOnly or AetherNetworkMode.PsiphonChain => "--psiphon-http",
        AetherNetworkMode.TorOnly or AetherNetworkMode.TorChain => "--tor-http",
        _ => "--http-proxy",
    };

    /// <summary>Ports that must answer before KOALIX reports the tunnel as connected (Aether-GUI's ready_addrs).</summary>
    public IReadOnlyList<System.Net.IPEndPoint> ReadyAddrs()
    {
        var list = new List<System.Net.IPEndPoint> { ParseBind(BindAddress) };
        if (NetworkMode is AetherNetworkMode.TorChain or AetherNetworkMode.TorReverse) list.Add(System.Net.IPEndPoint.Parse(TorBind));
        if (NetworkMode is AetherNetworkMode.PsiphonChain or AetherNetworkMode.PsiphonReverse) list.Add(System.Net.IPEndPoint.Parse(PsiphonBind));
        return list;
    }

    /// <summary>The SOCKS5 address apps should be pointed at. In a chain that is the Tor/Psiphon proxy (--bind keeps
    /// the plain WARP exit); otherwise it is --bind (Aether-GUI's primary_addr).</summary>
    public string PrimaryAddr() => NetworkMode switch
    {
        AetherNetworkMode.TorChain => TorBind,
        AetherNetworkMode.PsiphonChain => PsiphonBind,
        _ => BindAddress,
    };

    /// <summary>Every local port Aether must have open before connected (and free before launching).</summary>
    public IReadOnlyList<System.Net.IPEndPoint> ListenAddrs()
    {
        var list = new List<System.Net.IPEndPoint>(ReadyAddrs());
        if (HttpFront() is { } f) list.Add(f);
        return list;
    }

    /// <summary>The GUI's connect timeout must exceed Aether's own per-mode scan deadline (upstream v1.3.0
    /// overall_deadline: MASQUE 45/120/300/180/180s, WireGuard 30/80/250/150/180s) — each value below is the larger
    /// of the two probers' deadlines plus margin (Aether-GUI's connect_timeout), plus Tor/Psiphon bootstrap time.
    /// "only" modes run no scan, so they get just the overlay budget.</summary>
    public TimeSpan ConnectTimeout() => (IsOnly ? TimeSpan.Zero : TimeSpan.FromSeconds(ScanMode switch
    {
        AetherScanMode.Turbo => 90,
        AetherScanMode.Thorough => 330,
        AetherScanMode.Verified => 210,
        AetherScanMode.Ironclad => 240,
        _ => 150,
    })) + TimeSpan.FromSeconds(ExtraWaitSecs) + TimeSpan.FromSeconds(30);

    /// <summary>Zero Trust credentials as env vars (command-line equivalents would leak to other local processes).
    /// Mirrors Aether-GUI's zero_trust_env / pty.rs service pair.</summary>
    public IReadOnlyDictionary<string, string> EnvVars()
    {
        var env = new Dictionary<string, string>();
        // Env var, not a flag: any value suppresses Aether 1.2.0's interactive "MASQUE transport" prompt;
        // only a truthy one selects HTTP/2. Reverse modes carry TCP only → always HTTP/2.
        env["AETHER_MASQUE_HTTP2"] = MasqueHttp2 || IsReverse ? "1" : "0";
        if (ZeroTrustTeam.Trim().Length == 0) return env;
        switch (ZeroTrustAuth)
        {
            case AetherZeroTrustAuth.Email when AccessEmail.Trim().Length > 0:
                env["AETHER_ACCESS_EMAIL"] = AccessEmail.Trim(); break;
            case AetherZeroTrustAuth.Service when AccessClientId.Trim().Length > 0 && AccessClientSecret.Trim().Length > 0:
                env["AETHER_ACCESS_CLIENT_ID"] = AccessClientId.Trim();
                env["AETHER_ACCESS_CLIENT_SECRET"] = AccessClientSecret.Trim(); break;
            case AetherZeroTrustAuth.Token when AccessToken.Trim().Length > 0:
                env["AETHER_ACCESS_TOKEN"] = AccessToken.Trim(); break;
        }
        return env;
    }

    public static System.Net.IPEndPoint ParseBind(string addr) =>
        System.Net.IPEndPoint.TryParse(addr, out var ep) ? ep : System.Net.IPEndPoint.Parse("127.0.0.1:1819");

    // ── settings (aether.*) ──
    public static AetherProfile FromSettings(Func<string, string?> get) => new()
    {
        Protocol = ParseEnum(get("aether.protocol"), AetherProtocol.Auto),
        ScanMode = ParseEnum(get("aether.scan"), AetherScanMode.Balanced),
        IpVersion = ParseEnum(get("aether.ip"), AetherIpVersion.Both),
        QuickReconnect = get("aether.quickReconnect") != "0",
        MasqueHttp2 = get("aether.masqueHttp2") == "1",
        MasqueNoize = ParseEnum(get("aether.masqueNoize"), AetherMasqueNoize.Firewall),
        WgNoize = ParseEnum(get("aether.wgNoize"), AetherWgNoize.Balanced),
        BindAddress = get("aether.bind") is { Length: > 0 } b && System.Net.IPEndPoint.TryParse(b, out _) ? b : "127.0.0.1:1819",
        HttpProxy = get("aether.httpProxy") is { Length: > 0 } hp && System.Net.IPEndPoint.TryParse(hp, out _) ? hp : "",
        Upstream = get("aether.upstream") ?? "",
        ExitLoc = get("aether.exitLoc") ?? "",
        NetworkMode = ParseEnum(get("aether.network"), AetherNetworkMode.Warp),
        PsiphonRegion = get("aether.psiphonRegion") ?? "",
        Dns = get("aether.dns") ?? "",
        ZeroTrustTeam = get("aether.zt.team") ?? "",
        ZeroTrustAuth = ParseEnum(get("aether.zt.auth"), AetherZeroTrustAuth.Email),
        AccessEmail = get("aether.zt.email") ?? "",
        AccessClientId = get("aether.zt.clientId") ?? "",
        AccessClientSecret = get("aether.zt.secret") ?? "",
        AccessToken = get("aether.zt.token") ?? "",
        ZeroTrustGateway = get("aether.zt.gateway") == "1",
        SystemProxy = get("aether.systemProxy") == "1",
    };

    private static T ParseEnum<T>(string? s, T def) where T : struct, Enum =>
        s is { Length: > 0 } && Enum.TryParse<T>(s, ignoreCase: true, out var v) ? v : def;
}
