using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Koalix.Core;

namespace Koalix.SplitTunneling;

/// <summary>
/// Compiles a <see cref="SplitPolicy"/> into sing-box (1.12+) route and DNS rules using process_path / process_path_regex.
///
/// Design:
///  - The TUN inbound (Wintun) captures all system traffic (auto_route); sing-box identifies the owning process of each
///    TCP/UDP flow (IPv4 and IPv6) and routes it to "proxy" or "direct". Direct flows leave via the physical interface
///    (route.auto_detect_interface), which also prevents routing loops for the engine's own sockets.
///  - Folder rules use a case-insensitive regex anchored on the folder so helper/child executables of a game match too.
///  - Engine helper processes (e.g. xray.exe when Xray is the upstream) are always forced DIRECT to avoid loops.
///  - DNS queries are hijacked inside TUN. NOTE: on Windows they are sent by the DNS Client service (svchost.exe),
///    not by the owning app, so DNS cannot be split per process; whenever TunnelDns is on, ALL hijacked DNS is
///    resolved through the tunnel's encrypted remote resolver (per-process DNS rules remain only as a backstop
///    for apps that resolve in-process). This also defeats ISP DNS hijacking/poisoning of blocked domains, which
///    otherwise breaks tunneled apps even though their traffic is routed correctly.
/// </summary>
public static class SplitRuleCompiler
{
    public const string ProxyTag = "proxy";
    public const string DirectTag = "direct";
    public const string RemoteDnsTag = "dns-remote";
    public const string LocalDnsTag = "dns-local";

    public sealed record Compiled(JsonArray RouteRules, string RouteFinal, JsonArray DnsRules, string DnsFinal, bool DnsIpv4Only = false);

    private static JsonObject RejectV6() => new() { ["ip_version"] = 6, ["action"] = "reject" };

    private static JsonObject OutsideCidrs(IReadOnlyList<string> cidrs) =>
        new() { ["ip_cidr"] = ToArray(cidrs), ["invert"] = true, ["outbound"] = DirectTag };

    /// <summary>Global-unicast IPv6 (2000::/3). ULA (the TUN's own fdfe:: address) and link-local stay untouched.</summary>
    public const string GlobalIpv6Cidr = "2000::/3";

    /// <summary>
    /// v0.4.32 (field bug: "foreign IP shows, but Google/YouTube never load"): sing-box only PRE-MATCHES a TCP flow
    /// (decides it before the gVisor stack completes the handshake) up to the first <c>sniff</c> rule. A reject
    /// placed after sniff therefore fires only AFTER the browser already saw "connected", so it gets a reset
    /// mid-TLS — Chrome/Firefox treat that as a real failure and never fall back from IPv6 to IPv4 (Happy Eyeballs
    /// only falls back on a failed CONNECT). Dual-stack sites (Google, YouTube…) died while IPv4-only sites
    /// (ipnumberia) worked. Rejects that must look like "IPv6 unreachable" are emitted BEFORE sniff, so the SYN
    /// itself is refused and the browser switches to IPv4 instantly. TCP only — UDP keeps the later rules
    /// (DNS hijack must still see v6 DNS packets first).
    /// </summary>
    private static JsonObject EarlyTcpReject(IEnumerable<string>? exceptExact = null, IEnumerable<string>? exceptRegex = null) =>
        EarlyReject(new JsonObject { ["network"] = "tcp", ["ip_cidr"] = new JsonArray(GlobalIpv6Cidr) }, exceptExact, exceptRegex);

    /// <summary>v0.4.33 — QUIC (UDP 443) refused before sniff: the sender gets "port unreachable" straight away and
    /// Chrome races to TCP+TLS instead of waiting on a QUIC handshake the tunnel silently drops.</summary>
    private static JsonObject EarlyQuicReject(IEnumerable<string>? exceptExact = null, IEnumerable<string>? exceptRegex = null) =>
        EarlyReject(new JsonObject { ["network"] = "udp", ["port"] = 443 }, exceptExact, exceptRegex);

    private static JsonObject EarlyReject(JsonObject match, IEnumerable<string>? exceptExact, IEnumerable<string>? exceptRegex)
    {
        var subs = new List<JsonObject> { match };
        var ex = exceptExact?.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var rx = exceptRegex?.Distinct().ToList();
        if (ex is { Count: > 0 }) subs.Add(new JsonObject { ["process_path"] = ToArray(ex), ["invert"] = true });
        if (rx is { Count: > 0 }) subs.Add(new JsonObject { ["process_path_regex"] = ToArray(rx), ["invert"] = true });
        if (subs.Count == 1) { match["action"] = "reject"; return match; }
        return new JsonObject
        {
            ["type"] = "logical",
            ["mode"] = "and",
            ["rules"] = new JsonArray(subs.Select(x => (JsonNode)x).ToArray()),
            ["action"] = "reject",
        };
    }

    public static Compiled Compile(SplitPolicy policy, IReadOnlyList<string> alwaysDirectExecutables, string proxyTag = ProxyTag, bool tunnelIpv6 = true, IReadOnlyList<string>? tunnelCidrs = null)
    {
        var routeRules = new JsonArray
        {
            new JsonObject { ["action"] = "sniff" },
            new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" },
        };
        // Rules that must be decided before the TCP handshake (see EarlyTcpReject) — prepended at the end.
        var early = new JsonArray();
        if (alwaysDirectExecutables.Count > 0)
            routeRules.Add(new JsonObject { ["process_path"] = ToArray(alwaysDirectExecutables), ["outbound"] = DirectTag });
        routeRules.Add(new JsonObject { ["ip_is_private"] = true, ["outbound"] = DirectTag });
        // IPv6 leak protection must come BEFORE any outbound rule, otherwise tunneled/direct rules win and it never applies.
        if (policy.BlockIpv6Leaks)
        {
            early.Add(EarlyTcpReject(alwaysDirectExecutables));
            routeRules.Add(RejectV6());
        }
        var v6Unsupported = !policy.BlockIpv6Leaks && !tunnelIpv6;
        var partial = tunnelCidrs is { Count: > 0 } ? tunnelCidrs : null;

        var blocked = policy.Rules.Where(r => r.Action == AppAction.Block).ToList();
        if (blocked.Count > 0)
        {
            AddMatch(early, blocked, new JsonObject { ["network"] = "tcp", ["action"] = "reject" });
            AddMatch(routeRules, blocked, new JsonObject { ["action"] = "reject" });
        }

        var dnsRules = new JsonArray();
        // v0.4.32 — domain rules win over the app split (sniffed SNI/Host; suffix match), engine and blocks stay first.
        if (policy.DirectDomains.Count > 0)
        {
            routeRules.Add(new JsonObject { ["domain_suffix"] = ToArray(policy.DirectDomains), ["outbound"] = DirectTag });
            dnsRules.Add(new JsonObject { ["domain_suffix"] = ToArray(policy.DirectDomains), ["server"] = LocalDnsTag });
        }
        if (policy.ProxyDomains.Count > 0)
        {
            if (v6Unsupported) routeRules.Add(new JsonObject { ["domain_suffix"] = ToArray(policy.ProxyDomains), ["ip_version"] = 6, ["action"] = "reject" });
            routeRules.Add(new JsonObject { ["domain_suffix"] = ToArray(policy.ProxyDomains), ["outbound"] = proxyTag });
            dnsRules.Add(new JsonObject { ["domain_suffix"] = ToArray(policy.ProxyDomains), ["server"] = RemoteDnsTag });
        }
        string final, dnsFinal;
        switch (policy.Mode)
        {
            case SplitMode.Include:
                {
                    var tunneled = policy.Rules.Where(r => r.Action == AppAction.Tunnel).ToList();
                    if (policy.BlockQuic) AddMatch(early, tunneled, EarlyQuicReject());
                    if (v6Unsupported)
                    {
                        AddMatch(early, tunneled, EarlyTcpReject());
                        AddMatch(routeRules, tunneled, RejectV6()); // UDP backstop (QUIC over v6)
                    }
                    // Partial AllowedIPs: only destinations inside the list go to the proxy (fields are AND-ed); the rest
                    // falls through to the DIRECT final. (An "invert" here would negate the process match too.)
                    AddMatch(routeRules, tunneled, partial is null
                        ? new JsonObject { ["outbound"] = proxyTag }
                        : new JsonObject { ["ip_cidr"] = ToArray(partial), ["outbound"] = proxyTag });
                    // sing-box 1.14: the legacy `strategy` DNS rule action option is already FATAL today
                    // ("initialize dns router: …") whenever the same config also has a query_type/ip_version DNS
                    // rule or rule-set — which every BPB-style panel config carries — and is removed outright in
                    // 1.16. IPv4-only DNS for IPv6-less upstreams is enforced globally instead: see DnsIpv4Only
                    // (dns.strategy), which Build()/MergeUserConfig() apply to the whole configuration.
                    AddMatch(dnsRules, tunneled, new JsonObject { ["server"] = RemoteDnsTag });
                    // CRITICAL (field bug, v0.3.6): the DNS final must NOT be the local resolver in Include mode.
                    // On Windows, applications do not send their own DNS queries — the DNS Client service (svchost.exe)
                    // does, so per-process DNS rules above can never match the tunneled app; every query fell through to
                    // the LOCAL final. On ISPs that hijack plain UDP DNS (e.g. Iran: blocked domains answered with the
                    // 10.10.34.35 sinkhole, TTL 1) tunneled apps then connected to the sinkhole — which the
                    // ip_is_private route rule even sent DIRECT — and failed ("Google loads, YouTube doesn't",
                    // browser looks unconnected) while the tunnel itself and its health probe were fine.
                    // Resolving ALL hijacked DNS through the tunnel's encrypted remote resolver is the only reliable
                    // split-DNS on Windows; direct apps still receive real IPs and keep connecting directly.
                    // Only an explicit user opt-out (TunnelDns = false) restores the local final.
                    final = DirectTag; dnsFinal = policy.TunnelDns ? RemoteDnsTag : LocalDnsTag;
                    break;
                }
            case SplitMode.Exclude:
                {
                    var direct = policy.Rules.Where(r => r.Action == AppAction.Direct).ToList();
                    AddMatch(routeRules, direct, new JsonObject { ["outbound"] = DirectTag });
                    AddMatch(dnsRules, direct, new JsonObject { ["server"] = LocalDnsTag });
                    if (policy.BlockQuic)
                        early.Add(EarlyQuicReject(
                            alwaysDirectExecutables.Concat(direct.Where(a => !a.IncludeFolder && !a.IsFolderRule).Select(a => a.ExecutablePath)),
                            direct.Where(a => a.IncludeFolder || a.IsFolderRule).Select(a => FolderPrefixRegex(a.FolderPrefix))));
                    if (v6Unsupported)
                    {
                        // everything EXCEPT the direct apps (and the engine itself) is proxied → refuse its v6 SYNs early
                        early.Add(EarlyTcpReject(
                            alwaysDirectExecutables.Concat(direct.Where(a => !a.IncludeFolder && !a.IsFolderRule).Select(a => a.ExecutablePath)),
                            direct.Where(a => a.IncludeFolder || a.IsFolderRule).Select(a => FolderPrefixRegex(a.FolderPrefix))));
                        routeRules.Add(RejectV6());
                    }
                    if (partial is not null) routeRules.Add(OutsideCidrs(partial));
                    final = proxyTag; dnsFinal = policy.TunnelDns ? RemoteDnsTag : LocalDnsTag;
                    break;
                }
            default:
                if (policy.BlockQuic) early.Add(EarlyQuicReject(alwaysDirectExecutables));
                if (v6Unsupported)
                {
                    early.Add(EarlyTcpReject(alwaysDirectExecutables));
                    routeRules.Add(RejectV6());
                }
                if (partial is not null) routeRules.Add(OutsideCidrs(partial));
                final = proxyTag; dnsFinal = policy.TunnelDns ? RemoteDnsTag : LocalDnsTag;
                break;
        }
        // When the upstream cannot carry IPv6, handing out AAAA answers only makes apps stall on unreachable
        // IPv6 destinations (or on synthetic NAT64 translations of poisoned answers) before falling back.
        // This now applies to Include mode too (v0.3.6): its DNS is resolved through the tunnel (see above),
        // so the tunnel's IPv6 capability is what decides — the process-level RejectV6 rule stays as backstop.
        var ipv4Only = policy.BlockIpv6Leaks || v6Unsupported;
        var earlyNodes = early.ToList();
        early.Clear();
        for (var i = earlyNodes.Count - 1; i >= 0; i--) routeRules.Insert(0, earlyNodes[i]);
        return new Compiled(routeRules, final, dnsRules, dnsFinal, ipv4Only);
    }

    /// <summary>Adds up to two rules (exact paths, folder regexes) that share the same action.</summary>
    private static void AddMatch(JsonArray target, IReadOnlyList<AppRule> apps, JsonObject action)
    {
        if (apps.Count == 0) return;
        var exact = apps.Where(a => !a.IncludeFolder && !a.IsFolderRule).Select(a => a.ExecutablePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var folders = apps.Where(a => a.IncludeFolder || a.IsFolderRule).Select(a => FolderPrefixRegex(a.FolderPrefix)).Distinct().ToList();
        if (exact.Count > 0) { var r = (JsonObject)action.DeepClone(); r.Insert(0, "process_path", ToArray(exact)); target.Add(r); }
        if (folders.Count > 0) { var r = (JsonObject)action.DeepClone(); r.Insert(0, "process_path_regex", ToArray(folders)); target.Add(r); }
    }

    /// <summary>
    /// v0.4.37 (log koalix-diagnostics-20261108-133946 — «تحریم رو دور میزنه ولی فیلترینگ نه: جمنای میاد، یوتیوب نه»):
    /// whenever Windows' DNS leaks past the TUN (strict_route off, an on-link router resolver, a filter driver…), the
    /// ISP answers FILTERED names with its 10.10.34.x sinkhole while merely SANCTIONED names (Gemini) resolve clean —
    /// so sanctions were bypassed and filtering was not, through a perfectly healthy tunnel. These rules send a tunneled
    /// app's sinkhole-bound flows to the proxy instead of rejecting them; the Xray upstream sniffs the TLS SNI / HTTP
    /// Host and replaces the bogus IP with the real domain (socks-in <c>sniffing.destOverride</c>), so the far end
    /// resolves it cleanly. Only for upstreams that sniff (the Xray chain) — a native outbound would dial the sinkhole.
    /// Returned rules go BEFORE the sinkhole reject (pre-sniff); everything else still hits the reject.
    /// </summary>
    public static JsonArray SinkholeRescue(SplitPolicy policy, IReadOnlyList<string> alwaysDirectExecutables, IReadOnlyList<string> sinkholeCidrs, string proxyTag = ProxyTag)
    {
        var rules = new JsonArray();
        if (sinkholeCidrs.Count == 0) return rules;
        var match = new JsonObject { ["network"] = "tcp", ["ip_cidr"] = ToArray(sinkholeCidrs) };
        if (policy.Mode == SplitMode.Include)
        {
            var tunneled = policy.Rules.Where(r => r.Action == AppAction.Tunnel).ToList();
            var withOutbound = (JsonObject)match.DeepClone();
            withOutbound["outbound"] = proxyTag;
            AddMatch(rules, tunneled, withOutbound);
            return rules;
        }
        var direct = policy.Mode == SplitMode.Exclude ? policy.Rules.Where(r => r.Action == AppAction.Direct).ToList() : [];
        var exact = alwaysDirectExecutables.Concat(direct.Where(a => !a.IncludeFolder && !a.IsFolderRule).Select(a => a.ExecutablePath))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var folders = direct.Where(a => a.IncludeFolder || a.IsFolderRule).Select(a => FolderPrefixRegex(a.FolderPrefix)).Distinct().ToList();
        var blocked = policy.Rules.Where(r => r.Action == AppAction.Block).ToList();
        exact.AddRange(blocked.Where(a => !a.IncludeFolder && !a.IsFolderRule).Select(a => a.ExecutablePath));
        folders.AddRange(blocked.Where(a => a.IncludeFolder || a.IsFolderRule).Select(a => FolderPrefixRegex(a.FolderPrefix)));
        var subs = new List<JsonObject> { match };
        if (exact.Count > 0) subs.Add(new JsonObject { ["process_path"] = ToArray(exact.Distinct(StringComparer.OrdinalIgnoreCase)), ["invert"] = true });
        if (folders.Count > 0) subs.Add(new JsonObject { ["process_path_regex"] = ToArray(folders.Distinct()), ["invert"] = true });
        if (subs.Count == 1) { match["outbound"] = proxyTag; rules.Add(match); return rules; }
        rules.Add(new JsonObject
        {
            ["type"] = "logical",
            ["mode"] = "and",
            ["rules"] = new JsonArray(subs.Select(x => (JsonNode)x).ToArray()),
            ["outbound"] = proxyTag,
        });
        return rules;
    }

    /// <summary>Index right after the compiled <c>sniff</c> rule — where host-specific rules such as health-in go
    /// (the pre-sniff block holds only the early rejects, whose count varies by policy).</summary>
    public static int AfterSniff(JsonArray rules)
    {
        for (var i = 0; i < rules.Count; i++)
            if (rules[i] is JsonObject o && o.Count == 1 && (string?)o["action"] == "sniff") return i + 1;
        return 0;
    }

    public static string FolderRegex(string exePath)
    {
        var dir = Path.GetDirectoryName(exePath.Replace('/', '\\').Replace('\\', Path.DirectorySeparatorChar))!.Replace(Path.DirectorySeparatorChar, '\\');
        return "(?i)^" + Regex.Escape(dir.TrimEnd('\\') + "\\");
    }

    public static string FolderPrefixRegex(string prefix) => "(?i)^" + Regex.Escape(prefix.Replace('/', '\\').TrimEnd('\\') + "\\");

    private static JsonArray ToArray(IEnumerable<string> items) => new(items.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray());
}
