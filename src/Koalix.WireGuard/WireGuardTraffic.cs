using System.IO.Pipes;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Koalix.Core;
using Koalix.Tunneling;

namespace Koalix.WireGuard;

/// <summary>
/// Live traffic + latency for tunnels that carry packets OUTSIDE sing-box (v0.4.11). The v0.4.10 attempt read the
/// tunnel adapter's NDIS byte counters — frozen at zero in the field, because WireSock's per-app capture driver
/// (WinpkFilter) does not expose a counters-bearing NDIS adapter at all in filter mode, so the matcher found nothing.
/// This round is layered: every source is tried in order, the first that actually answers wins, and the log states
/// exactly WHICH one is live (or why none is):
///   1. WireGuard UAPI named pipe (official tunnel service): rx/tx straight from the kernel implementation —
///      no wg.exe needed, immune to adapter statistics gaps.
///   2. "wg show &lt;tunnel&gt; dump" (official service, when wg.exe is installed) — WireGuardEngine.GetStatsAsync.
///   3. Tunnel adapter counters (WireSock / Wintun) — relaxed match (any operational status, wider hints) and an
///      automatic switch to another candidate adapter when the chosen one's counters stay frozen.
/// Latency (v0.4.11): WireSock and the official service expose no local health port, so their latency card was a
/// permanent "—". A 5-second ICMP probe against the peer's public endpoint feeds the very same card and chart.
/// </summary>
public static class WireGuardTraffic
{
    public static async IAsyncEnumerable<TrafficSample> WatchAsync(WireGuardEngine? engine, string? conf,
        Action<LogCategory, string> log, [EnumeratorCancellation] CancellationToken ct)
    {
        var pipe = engine?.UapiPipeName;
        var announcedPipe = false;
        var useWg = engine is { UsingOfficialService: true };
        string? adapterId = null;
        var lastSource = "";
        long lastRx = -1, lastTx = -1, totalDown = 0, totalUp = 0;
        var frozen = 0;
        var tick = 0;
        var announcedNone = false;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (true)
        {
            try { if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) break; }
            catch (OperationCanceledException) { break; }
            catch { break; }
            tick++;

            (long Rx, long Tx)? snap = null;
            var source = "";

            // 1) UAPI pipe — authoritative for the official tunnel service.
            if (pipe is not null)
            {
                snap = await TryUapiAsync(pipe, ct);
                if (snap is null)
                {
                    log(LogCategory.WireGuard, "traffic: UAPI pipe went silent, falling back to wg.exe/adapter");
                    pipe = null;
                }
                else
                {
                    source = "uapi";
                    if (!announcedPipe) { announcedPipe = true; log(LogCategory.WireGuard, "traffic source: WireGuard UAPI pipe"); }
                }
            }

            // 2) wg.exe dump — heavier (process spawn), only every other tick.
            if (snap is null && useWg && tick % 2 == 0)
            {
                try
                {
                    var st = await engine!.GetStatsAsync(ct);
                    if (st is not null)
                    {
                        snap = (st.RxBytes, st.TxBytes);
                        source = "wg";
                        if (lastSource != "wg") log(LogCategory.WireGuard, "traffic source: wg.exe");
                    }
                }
                catch { useWg = false; }
            }

            // 3) Tunnel adapter counters — WireSock / Wintun; re-acquire and switch candidates when frozen.
            if (snap is null)
            {
                var wantFind = adapterId is null ? tick % 3 == 1 : frozen >= 15 && frozen % 15 == 0;
                if (wantFind)
                {
                    var nic = TunnelInterfaceStats.FindAdapter(conf, adapterId);
                    if (nic is not null && nic.Id != adapterId)
                    {
                        adapterId = nic.Id;
                        frozen = 0;
                        log(LogCategory.WireGuard, $"traffic source: tunnel adapter \"{nic.Name}\" ({nic.Description})");
                    }
                    else if (adapterId is null && !announcedNone && tick > 12)
                    {
                        announcedNone = true;
                        log(LogCategory.WireGuard, "traffic: no counters source found (no UAPI pipe, no matching adapter) — " +
                            "speed cards stay idle while the latency probe runs; WireSock's filter driver does not expose byte counters in per-app mode");
                    }
                }
                if (adapterId is not null && TunnelInterfaceStats.ReadCounters(adapterId) is { } c)
                {
                    snap = (c.Down, c.Up);
                    source = "adapter:" + adapterId;
                    // Counters alive but not moving → likely the wrong adapter; the periodic re-acquire switches.
                    frozen = lastRx >= 0 && c.Down == lastRx && c.Up == lastTx ? frozen + 1 : 0;
                }
                else frozen++;
            }

            if (snap is not { } s) continue;
            if (source != lastSource) { lastSource = source; lastRx = -1; } // source switch → new baseline, totals keep
            if (lastRx < 0) { lastRx = s.Rx; lastTx = s.Tx; continue; } // first sample of a source only sets the baseline
            var dDown = Math.Max(0, s.Rx - lastRx);
            var dUp = Math.Max(0, s.Tx - lastTx);
            lastRx = s.Rx; lastTx = s.Tx;
            totalDown += dDown; totalUp += dUp;
            yield return new TrafficSample(dUp, dDown, totalUp, totalDown, DateTimeOffset.Now);
        }
    }

    /// <summary>
    /// One UAPI "get=1" round-trip on the tunnel service's named pipe. Response is key=value lines per peer,
    /// terminated by "errno=…"; rx_bytes/tx_bytes are summed across peers. Any failure → null (caller falls back).
    /// </summary>
    private static async Task<(long Rx, long Tx)?> TryUapiAsync(string pipeName, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            await pipe.ConnectAsync(700, cts.Token);
            var req = Encoding.ASCII.GetBytes("get=1\n\n");
            await pipe.WriteAsync(req, cts.Token);
            var buf = new byte[8192];
            var sb = new StringBuilder();
            while (sb.Length < 65536)
            {
                var n = await pipe.ReadAsync(buf, cts.Token);
                if (n <= 0) break;
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                if (sb.ToString().Contains("errno=", StringComparison.Ordinal)) break;
            }
            long rx = 0, tx = 0; var seen = false;
            foreach (var line in sb.ToString().Split('\n'))
            {
                var l = line.Trim('\r');
                if (l.StartsWith("rx_bytes=", StringComparison.Ordinal) && long.TryParse(l.AsSpan(9), out var r)) { rx += r; seen = true; }
                else if (l.StartsWith("tx_bytes=", StringComparison.Ordinal) && long.TryParse(l.AsSpan(9), out var x)) tx += x;
            }
            return seen ? (rx, tx) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Latency for modes without a local health port (WireSock, official service — their card used to be a permanent
    /// "—"). Pings the peer's PUBLIC endpoint every 5 s: in per-app WireSock mode KOALIX itself is not routed through
    /// the tunnel, but the ICMP packet follows the same physical wire to the server, so the RTT is a faithful proxy.
    /// </summary>
    public static async Task RunLatencyAsync(string? conf, Action<double?> report, CancellationToken ct)
    {
        IPAddress? target = null;
        try
        {
            if (WireGuardConfig.Parse(conf ?? "", out _).Peers.FirstOrDefault()?.EndpointParts is { } ep)
            {
                var host = ep.Host.Trim('[', ']');
                if (!IPAddress.TryParse(host, out target))
                    try
                    {
                        var addrs = await Dns.GetHostAddressesAsync(host, ct);
                        target = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addrs.FirstOrDefault();
                    }
                    catch { }
            }
        }
        catch { }
        if (target is null) return;
        using var ping = new Ping();
        while (!ct.IsCancellationRequested)
        {
            double? ms = null;
            try
            {
                var r = await ping.SendPingAsync(target, 1800);
                if (r.Status == IPStatus.Success) ms = r.RoundtripTime;
            }
            catch { }
            report(ms);
            try { await Task.Delay(5000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }
}
