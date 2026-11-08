using System.Text.Json.Nodes;

namespace Koalix.SingBox;

/// <summary>
/// Semantic checks that `sing-box check` does not perform (verified: sing-box 1.14 accepts route rules pointing to
/// non-existent outbounds). Every outbound/DNS-server reference must exist, and a TUN config must keep process routing
/// enforceable (no silent full-tunnel fallback).
/// </summary>
public static class SingBoxConfigLinter
{
    public static IReadOnlyList<string> Lint(JsonObject cfg)
    {
        var errors = new List<string>();
        var outTags = Tags(cfg["outbounds"]).Concat(Tags(cfg["endpoints"])).ToHashSet();
        var dnsTags = Tags(cfg["dns"]?["servers"]).ToHashSet();

        void Out(string? tag, string where) { if (tag is not null && !outTags.Contains(tag)) errors.Add($"Lint.UnknownOutbound:{tag} ({where})"); }
        void Dns(string? tag, string where) { if (tag is not null && !dnsTags.Contains(tag)) errors.Add($"Lint.UnknownDnsServer:{tag} ({where})"); }

        var ruleSetTags = (cfg["route"]?["rule_set"] as JsonArray ?? []).OfType<JsonObject>().Select(s => Str(s["tag"])).OfType<string>().ToHashSet();
        void RuleSet(JsonNode? node, string where)
        {
            if (node is null) return;
            var tags = node is JsonArray arr ? arr.OfType<JsonValue>().Select(v => Str(v)).OfType<string>() : [Str(node)!];
            foreach (var t in tags) if (!ruleSetTags.Contains(t)) errors.Add($"Lint.UnknownRuleSet:{t} ({where})");
        }

        if (cfg["route"] is JsonObject route)
        {
            Out(Str(route["final"]), "route.final");
            if (route["rules"] is JsonArray rules)
                for (int i = 0; i < rules.Count; i++)
                    if (rules[i] is JsonObject r) { Out(Str(r["outbound"]), $"route.rules[{i}]"); RuleSet(r["rule_set"], $"route.rules[{i}]"); }
            var resolver = route["default_domain_resolver"];
            Dns(resolver is JsonObject ro ? Str(ro["server"]) : Str(resolver), "route.default_domain_resolver");
        }
        if (cfg["dns"] is JsonObject dns)
        {
            Dns(Str(dns["final"]), "dns.final");
            if (dns["rules"] is JsonArray rules)
                for (int i = 0; i < rules.Count; i++)
                    if (rules[i] is JsonObject r) { Dns(Str(r["server"]), $"dns.rules[{i}]"); RuleSet(r["rule_set"], $"dns.rules[{i}]"); }
            if (dns["servers"] is JsonArray servers)
                foreach (var s in servers.OfType<JsonObject>())
                {
                    Out(Str(s["detour"]), $"dns.servers[{Str(s["tag"])}].detour");
                    // A DNS server whose own address is a domain needs another server to resolve it (new-format
                    // `domain_resolver` / legacy `address_resolver`) — dangling references fail at engine start.
                    Dns(Str(s["domain_resolver"]), $"dns.servers[{Str(s["tag"])}].domain_resolver");
                    Dns(Str(s["address_resolver"]), $"dns.servers[{Str(s["tag"])}].address_resolver");
                }
        }
        foreach (var key in new[] { "outbounds", "endpoints" })
            foreach (var o in (cfg[key] as JsonArray ?? []).OfType<JsonObject>())
            {
                Out(Str(o["detour"]), $"{key}[{Str(o["tag"])}].detour");
                var resolver = o["domain_resolver"];
                Dns(resolver is JsonObject ro ? Str(ro["server"]) : Str(resolver), $"{key}[{Str(o["tag"])}].domain_resolver");
            }

        var hasTun = (cfg["inbounds"] as JsonArray ?? []).OfType<JsonObject>().Any(i => Str(i["type"]) == "tun");
        var usesProcess = cfg["route"]?["rules"] is JsonArray rr && rr.OfType<JsonObject>().Any(r => r.ContainsKey("process_path") || r.ContainsKey("process_path_regex") || r.ContainsKey("process_name"));
        if (usesProcess && !hasTun) errors.Add("Lint.ProcessRulesWithoutTun");
        return errors;
    }

    private static IEnumerable<string> Tags(JsonNode? arr) => (arr as JsonArray ?? []).OfType<JsonObject>().Select(o => Str(o["tag"])).OfType<string>();
    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
