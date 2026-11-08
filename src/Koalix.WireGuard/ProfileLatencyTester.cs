using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Koalix.Core;
using Koalix.Tunneling;
using Koalix.SingBox;

namespace Koalix.WireGuard;

public enum LatencyMethod { RealDelay, TcpPing }
public sealed record LatencyResult(long ProfileId, double? Ms, LatencyMethod Method, string? Error);

/// <summary>
/// "Real delay" test like v2rayN: one temporary sing-box process (no TUN, no admin, no system changes) exposes one loopback
/// mixed inbound per profile, each routed to that profile's outbound; an HTTP 204 probe is sent through every inbound
/// concurrently. Profiles sing-box cannot run natively (Xray-only transports, raw JSON) fall back to a TCP connect ping.
/// </summary>
public sealed class ProfileLatencyTester(EnginePaths paths)
{
    private const int Batch = 32;
    public static readonly Uri Probe = new("http://cp.cloudflare.com/generate_204");

    public async Task<IReadOnlyList<LatencyResult>> TestAsync(IReadOnlyList<TunnelProfile> profiles, IProgress<LatencyResult>? progress, CancellationToken ct)
    {
        var results = new List<LatencyResult>();
        var real = new List<(TunnelProfile P, JsonObject Node, bool Endpoint)>();
        var tcp = new List<TunnelProfile>();
        foreach (var p in profiles)
        {
            if (File.Exists(paths.SingBoxExe) && TryOutbound(p, out var node, out var isEndpoint)) real.Add((p, node!, isEndpoint));
            else tcp.Add(p);
        }

        foreach (var chunk in real.Chunk(Batch))
            foreach (var r in await RealDelayAsync(chunk, progress, ct)) results.Add(r);

        var tcpTasks = tcp.Select(async p =>
        {
            var r = await TcpPingAsync(p, ct);
            progress?.Report(r);
            return r;
        });
        results.AddRange(await Task.WhenAll(tcpTasks));
        return results;
    }

    private static bool TryOutbound(TunnelProfile p, out JsonObject? node, out bool endpoint)
    {
        node = null; endpoint = false;
        try
        {
            switch (p.Source)
            {
                case ProfileSource.ShareLink:
                    var n = ShareLinkParser.Parse(p.Content);
                    if (n.Validate().Count > 0 || !SingBoxConfigBuilder.IsNativelySupported(n, out _)) return false;
                    node = SingBoxConfigBuilder.Outbound(n, "t");
                    return true;
                case ProfileSource.WireGuardConf:
                    var wg = WireGuardConfig.Parse(p.Content, out var errs);
                    if (errs.Count > 0) return false;
                    node = wg.ToSingBoxEndpoint("t", "dns-local");
                    endpoint = true;
                    return true;
                default: return false;
            }
        }
        catch { return false; }
    }

    private async Task<List<LatencyResult>> RealDelayAsync((TunnelProfile P, JsonObject Node, bool Endpoint)[] items, IProgress<LatencyResult>? progress, CancellationToken ct)
    {
        var inbounds = new JsonArray(); var outbounds = new JsonArray(); var endpoints = new JsonArray(); var rules = new JsonArray();
        var ports = new int[items.Length];
        for (var i = 0; i < items.Length; i++)
        {
            ports[i] = EnginePaths.FreeTcpPort();
            var node = (JsonObject)items[i].Node.DeepClone();
            node["tag"] = $"p{i}";
            (items[i].Endpoint ? endpoints : outbounds).Add(node);
            inbounds.Add(new JsonObject { ["type"] = "mixed", ["tag"] = $"in{i}", ["listen"] = "127.0.0.1", ["listen_port"] = ports[i] });
            rules.Add(new JsonObject { ["inbound"] = $"in{i}", ["outbound"] = $"p{i}" });
        }
        outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = "direct" });
        var cfg = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "error" },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray { new JsonObject { ["type"] = "udp", ["tag"] = "dns-local", ["server"] = "1.1.1.1" } },
                ["strategy"] = "ipv4_only",
            },
            ["certificate"] = new JsonObject { ["store"] = "mozilla" },
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
            ["route"] = new JsonObject { ["rules"] = rules, ["final"] = "direct", ["auto_detect_interface"] = true, ["default_domain_resolver"] = "dns-local" },
        };
        if (endpoints.Count > 0) cfg["endpoints"] = endpoints;

        var file = Path.Combine(paths.RuntimeDir, $"sb-ping-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(file, SingBoxConfigBuilder.ToJson(cfg), ct);
        var proc = new EngineProcess(LogCategory.SingBox);
        var list = new List<LatencyResult>();
        try
        {
            proc.Start(paths.SingBoxExe, ["run", "-c", file, "-D", paths.RuntimeDir, "--disable-color"], paths.RuntimeDir);
            await WaitListeningAsync(ports[0], ct);
            var tasks = items.Select(async (it, i) =>
            {
                var r = await ProbeAsync(it.P.Id, ports[i], ct);
                progress?.Report(r);
                return r;
            });
            list.AddRange(await Task.WhenAll(tasks));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var it in items) { var r = new LatencyResult(it.P.Id, null, LatencyMethod.RealDelay, ex.Message); progress?.Report(r); list.Add(r); }
        }
        finally
        {
            await proc.StopAsync(TimeSpan.FromSeconds(2));
            await proc.DisposeAsync();
            try { File.Delete(file); } catch { }
        }
        return list;
    }

    private static async Task WaitListeningAsync(int port, CancellationToken ct)
    {
        for (var i = 0; i < 40; i++)
        {
            try { using var c = new TcpClient(); await c.ConnectAsync(IPAddress.Loopback, port, ct); return; }
            catch (SocketException) { await Task.Delay(100, ct); }
        }
        throw new TimeoutException("sing-box did not start");
    }

    /// <summary>Two sequential requests on fresh connections; the best one is reported (first includes TLS/handshake warm-up).</summary>
    private static async Task<LatencyResult> ProbeAsync(long id, int port, CancellationToken ct)
    {
        double? best = null; string? err = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var h = new SocketsHttpHandler { Proxy = new WebProxy($"http://127.0.0.1:{port}"), UseProxy = true, PooledConnectionLifetime = TimeSpan.Zero };
            using var http = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(attempt == 0 ? 8 : 5) };
            var sw = Stopwatch.StartNew();
            try
            {
                using var r = await http.GetAsync(Probe, HttpCompletionOption.ResponseHeadersRead, ct);
                // 502/5xx is generated by the local sing-box proxy when the upstream cannot be reached.
                if ((int)r.StatusCode >= 500) { err = $"HTTP {(int)r.StatusCode}"; if (attempt == 0) break; continue; }
                var ms = sw.Elapsed.TotalMilliseconds;
                best = best is null ? ms : Math.Min(best.Value, ms);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                err = ex is TaskCanceledException ? "timeout" : ex.InnerException?.Message ?? ex.Message;
                if (attempt == 0) break; // dead on first try: don't wait again
            }
        }
        return new LatencyResult(id, best, LatencyMethod.RealDelay, best is null ? err : null);
    }

    /// <summary>TCP connect time to the server address (does not prove the proxy works — shown as "TCP").</summary>
    public static async Task<LatencyResult> TcpPingAsync(TunnelProfile p, CancellationToken ct)
    {
        var target = ServerOf(p);
        if (target is null) return new(p.Id, null, LatencyMethod.TcpPing, "no server");
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(target.Value.Host, AddressFamily.InterNetwork, ct);
            if (addrs.Length == 0) return new(p.Id, null, LatencyMethod.TcpPing, "dns");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            using var c = new TcpClient(AddressFamily.InterNetwork);
            var sw = Stopwatch.StartNew();
            await c.ConnectAsync(addrs[0], target.Value.Port, cts.Token);
            return new(p.Id, sw.Elapsed.TotalMilliseconds, LatencyMethod.TcpPing, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(p.Id, null, LatencyMethod.TcpPing, "timeout"); }
        catch (SocketException ex) { return new(p.Id, null, LatencyMethod.TcpPing, ex.SocketErrorCode.ToString()); }
    }

    // ================= Gaming test (v0.4.0) =================
    // Same temporary-sing-box batch as the real-delay test, but per profile it also takes 5 delay samples
    // (median + jitter + failures) and makes REAL UDP probes through the tunnel via SOCKS5 UDP-associate
    // STUN requests — the thing that actually decides whether a config can game. Combined with the static
    // analysis (GamingSuitability) this separates "browses fine" from "games fine".

    public async Task<IReadOnlyList<GamingResult>> GamingTestAsync(IReadOnlyList<TunnelProfile> profiles, IProgress<GamingResult>? progress, CancellationToken ct)
    {
        var results = new List<GamingResult>();
        var real = new List<(TunnelProfile P, JsonObject Node, bool Endpoint)>();
        var rest = new List<TunnelProfile>();
        foreach (var p in profiles)
        {
            if (File.Exists(paths.SingBoxExe) && TryOutbound(p, out var node, out var ep)) real.Add((p, node!, ep));
            else rest.Add(p);
        }
        foreach (var chunk in real.Chunk(Batch))
            results.AddRange(await GamingBatchAsync(chunk, progress, ct));
        foreach (var p in rest)
        {
            var statik = GamingSuitability.Analyze(p);
            var r = new GamingResult(p.Id, statik, null, null, null, null,
                statik.Verdict == GamingVerdict.Good ? GamingVerdict.Ok : statik.Verdict,
                [.. statik.ReasonCodes, "Gaming.Reason.TcpOnly"]);
            progress?.Report(r);
            results.Add(r);
        }
        return results;
    }

    private async Task<List<GamingResult>> GamingBatchAsync((TunnelProfile P, JsonObject Node, bool Endpoint)[] items, IProgress<GamingResult>? progress, CancellationToken ct)
    {
        var inbounds = new JsonArray(); var outbounds = new JsonArray(); var endpoints = new JsonArray(); var rules = new JsonArray();
        var ports = new int[items.Length];
        for (var i = 0; i < items.Length; i++)
        {
            ports[i] = EnginePaths.FreeTcpPort();
            var node = (JsonObject)items[i].Node.DeepClone();
            node["tag"] = $"p{i}";
            (items[i].Endpoint ? endpoints : outbounds).Add(node);
            inbounds.Add(new JsonObject { ["type"] = "mixed", ["tag"] = $"in{i}", ["listen"] = "127.0.0.1", ["listen_port"] = ports[i] });
            rules.Add(new JsonObject { ["inbound"] = $"in{i}", ["outbound"] = $"p{i}" });
        }
        outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = "direct" });
        var cfg = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "error" },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray { new JsonObject { ["type"] = "udp", ["tag"] = "dns-local", ["server"] = "1.1.1.1" } },
                ["strategy"] = "ipv4_only",
            },
            ["certificate"] = new JsonObject { ["store"] = "mozilla" },
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
            ["route"] = new JsonObject { ["rules"] = rules, ["final"] = "direct", ["auto_detect_interface"] = true, ["default_domain_resolver"] = "dns-local" },
        };
        if (endpoints.Count > 0) cfg["endpoints"] = endpoints;

        var file = Path.Combine(paths.RuntimeDir, $"sb-game-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(file, SingBoxConfigBuilder.ToJson(cfg), ct);
        var proc = new EngineProcess(LogCategory.SingBox);
        var list = new List<GamingResult>();
        try
        {
            proc.Start(paths.SingBoxExe, ["run", "-c", file, "-D", paths.RuntimeDir, "--disable-color"], paths.RuntimeDir);
            await WaitListeningAsync(ports[0], ct);
            var tasks = items.Select(async (it, i) =>
            {
                var samples = new List<double>(); var fails = 0;
                for (var k = 0; k < 5; k++)
                {
                    var s = await ProbeOnceAsync(ports[i], TimeSpan.FromSeconds(5), ct);
                    if (s is { } v) samples.Add(v); else fails++;
                }
                double? median = samples.Count > 0 ? Median(samples) : null;
                double? jitter = samples.Count > 1 ? samples.Zip(samples.Skip(1)).Average(p => Math.Abs(p.Second - p.First)) : null;
                bool? udpOk = null; double? udpMs = null;
                for (var k = 0; k < 3 && udpOk != true; k++)
                {
                    var u = await SocksUdpStunAsync(ports[i], ct);
                    if (u is { } uv) { udpOk = true; udpMs = udpMs is null ? uv : Math.Min(udpMs.Value, uv); }
                    else udpOk = false;
                }
                var statik = GamingSuitability.Analyze(it.P);
                var (verdict, reasons) = CombineGaming(statik, median, jitter, fails, udpOk);
                var r = new GamingResult(it.P.Id, statik, median, jitter, udpOk, udpMs, verdict, reasons);
                progress?.Report(r);
                return r;
            });
            list.AddRange(await Task.WhenAll(tasks));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var it in items)
            {
                var statik = GamingSuitability.Analyze(it.P);
                var r = new GamingResult(it.P.Id, statik, null, null, null, null, GamingVerdict.Bad, [.. statik.ReasonCodes, "Gaming.Reason.Unreachable"]);
                progress?.Report(r); list.Add(r);
            }
        }
        finally
        {
            await proc.StopAsync(TimeSpan.FromSeconds(2));
            await proc.DisposeAsync();
            try { File.Delete(file); } catch { }
        }
        return list;
    }

    private static (GamingVerdict, List<string>) CombineGaming(GamingAssessment a, double? median, double? jitter, int fails, bool? udpOk)
    {
        var reasons = new List<string>(a.ReasonCodes);
        var v = a.Verdict;
        if (median is null) { reasons.Add("Gaming.Reason.Unreachable"); return (GamingVerdict.Bad, reasons); }
        if (udpOk == false)
        {
            reasons.Add("Gaming.Reason.UdpFailed");
            v = a.Udp == UdpSupport.Native ? (v == GamingVerdict.Good ? GamingVerdict.Ok : v) : GamingVerdict.Bad;
        }
        if (jitter is > 50 || median is > 300) { reasons.Add("Gaming.Reason.Unstable"); v = Downgrade(v); }
        if (fails >= 2) { reasons.Add("Gaming.Reason.Lossy"); v = Downgrade(v); }
        return (v, reasons);
    }

    private static GamingVerdict Downgrade(GamingVerdict v) => v switch
    { GamingVerdict.Good => GamingVerdict.Ok, GamingVerdict.Ok => GamingVerdict.Bad, _ => v };

    private static double Median(List<double> xs)
    {
        var s = xs.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }

    private static async Task<double?> ProbeOnceAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        using var h = new SocketsHttpHandler { Proxy = new WebProxy($"http://127.0.0.1:{port}"), UseProxy = true, PooledConnectionLifetime = TimeSpan.Zero };
        using var http = new HttpClient(h) { Timeout = timeout };
        var sw = Stopwatch.StartNew();
        try
        {
            using var r = await http.GetAsync(Probe, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)r.StatusCode >= 500) return null;
            return sw.Elapsed.TotalMilliseconds;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            return null;
        }
    }

    /// <summary>
    /// Real UDP probe through a SOCKS5 proxy port: UDP-associate, then a STUN binding request to
    /// stun.l.google.com. Returns the round-trip ms, or null when UDP does not come back at all —
    /// the exact failure mode of Cloudflare-Worker configs (TCP works, UDP is dropped).
    /// </summary>
    private static async Task<double?> SocksUdpStunAsync(int proxyPort, CancellationToken ct)
    {
        try
        {
            var stunIp = (await Dns.GetHostAddressesAsync("stun.l.google.com", ct)).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (stunIp is null) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
            var stream = tcp.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 0 }, timeout.Token);
            var two = new byte[2];
            await stream.ReadExactlyAsync(two, timeout.Token);
            if (two[0] != 5 || two[1] != 0) return null;
            await stream.WriteAsync(new byte[] { 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 }, timeout.Token);
            var head = new byte[4];
            await stream.ReadExactlyAsync(head, timeout.Token);
            if (head[0] != 5 || head[1] != 0) return null;
            IPEndPoint relay;
            if (head[3] == 1)
            {
                var b = new byte[6]; await stream.ReadExactlyAsync(b, timeout.Token);
                var ip = new IPAddress(b.AsSpan(0, 4));
                if (ip.Equals(IPAddress.Any)) ip = IPAddress.Loopback;
                relay = new IPEndPoint(ip, (b[4] << 8) | b[5]);
            }
            else if (head[3] == 3)
            {
                var lb = new byte[1]; await stream.ReadExactlyAsync(lb, timeout.Token);
                var b = new byte[lb[0] + 2]; await stream.ReadExactlyAsync(b, timeout.Token);
                var host = System.Text.Encoding.ASCII.GetString(b, 0, lb[0]);
                var ip = (await Dns.GetHostAddressesAsync(host, timeout.Token)).FirstOrDefault() ?? IPAddress.Loopback;
                relay = new IPEndPoint(ip, (b[lb[0]] << 8) | b[lb[0] + 1]);
            }
            else return null;

            using var udp = new UdpClient();
            udp.Connect(relay);
            var txn = new byte[12];
            Random.Shared.NextBytes(txn);
            var stun = new byte[20];
            stun[1] = 1; // binding request 0x0001
            stun[4] = 0x21; stun[5] = 0x12; stun[6] = 0xA4; stun[7] = 0x42; // magic cookie
            txn.CopyTo(stun, 8);
            var dest = stunIp.GetAddressBytes();
            var packet = new byte[10 + stun.Length];
            packet[3] = 1;
            dest.CopyTo(packet, 4);
            packet[8] = (byte)(19302 >> 8); packet[9] = (byte)(19302 & 0xFF);
            stun.CopyTo(packet, 10);
            using var recvTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            recvTimeout.CancelAfter(TimeSpan.FromMilliseconds(2500));
            var sw = Stopwatch.StartNew();
            await udp.SendAsync(packet, ct);
            var resp = await udp.ReceiveAsync(recvTimeout.Token);
            sw.Stop();
            var d = resp.Buffer;
            if (d.Length < 30 || d[3] != 1) return null;
            var payload = d.AsSpan(10);
            if (payload.Length < 20) return null;
            if (payload[4] != 0x21 || payload[5] != 0x12 || payload[6] != 0xA4 || payload[7] != 0x42) return null;
            if (!payload.Slice(8, 12).SequenceEqual(txn)) return null;
            return sw.Elapsed.TotalMilliseconds;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (SocketException) { return null; }
        catch (IOException) { return null; }
    }

    private static (string Host, int Port)? ServerOf(TunnelProfile p)
    {
        try
        {
            switch (p.Source)
            {
                case ProfileSource.ShareLink:
                    var n = ShareLinkParser.Parse(p.Content);
                    return (n.Server, n.Port);
                case ProfileSource.WireGuardConf:
                    return WireGuardConfig.Parse(p.Content, out _).Peers.FirstOrDefault()?.EndpointParts;
                case ProfileSource.OpenVpnConf:
                    // v0.4.19 (user report: «تست برای اوپن وی پی ان خرابه و همرو خراب نشون میده»): .ovpn profiles
                    // used to fall through to "no server" and every OpenVPN row showed ×. The honest test for
                    // OpenVPN is a TCP connect to the first remote (the same fallback WireGuard gets when its
                    // endpoint is unreachable — sing-box cannot proxy an OpenVPN outbound).
                    {
                        var ovpn = OpenVpnConfig.Parse(p.Content, out _);
                        var first = ovpn.Remotes.FirstOrDefault(r => r.Proto == "tcp")
                                    ?? ovpn.Remotes.FirstOrDefault();
                        return first is null ? null : (first.Host, first.Port);
                    }
                case ProfileSource.XrayJson:
                    var x = JsonNode.Parse(p.Content);
                    var v = x?["outbounds"]?.AsArray().OfType<JsonObject>().Select(o => o["settings"]?["vnext"]?[0] ?? o["settings"]?["servers"]?[0]).FirstOrDefault(s => s is not null);
                    return v is null ? null : ((string)v["address"]!, (int)v["port"]!);
                case ProfileSource.SingBoxJson:
                    {
                        if (JsonNode.Parse(p.Content) is not JsonObject sb || sb["outbounds"] is not JsonArray arr) return null;
                        var byTag = arr.OfType<JsonObject>().GroupBy(o => (string?)o["tag"] ?? "").ToDictionary(g => g.Key, g => g.First());
                        // Follow the config's own routing intent (route.final → selector/urltest members).
                        JsonObject? Resolve(string? tag, int depth)
                        {
                            if (tag is null || depth > 3 || !byTag.TryGetValue(tag, out var o)) return null;
                            if (o["server"] is not null && o["server_port"] is not null) return o;
                            if (o["outbounds"] is JsonArray members)
                                foreach (var m in members)
                                    if (m is JsonValue v && v.TryGetValue<string>(out var t) && Resolve(t, depth + 1) is { } r) return r;
                            return null;
                        }
                        string? finalTag = null;
                        if (sb["route"]?["final"] is JsonValue f && f.TryGetValue<string>(out var ft)) finalTag = ft;
                        var nodes = new List<JsonObject>();
                        if (Resolve(finalTag, 0) is { } target) nodes.Add(target);
                        nodes.AddRange(arr.OfType<JsonObject>());
                        // Prefer a server that is a raw IP: no (possibly poisoned) DNS lookup between us and the ping.
                        var best = nodes
                            .Where(o => o["server"] is not null && o["server_port"] is not null)
                            .OrderByDescending(o => System.Net.IPAddress.TryParse((string?)o["server"], out _))
                            .FirstOrDefault();
                        return best is null ? null : ((string)best["server"]!, (int)best["server_port"]!);
                    }
            }
        }
        catch { }
        return null;
    }
}
