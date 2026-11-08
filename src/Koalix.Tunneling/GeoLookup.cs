using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Koalix.Tunneling;

/// <summary>Country of an egress IP (v0.4.6): name + ISO-3166 alpha-2 code.</summary>
public sealed record GeoInfo(string Country, string Code);

/// <summary>
/// Resolves the country of a public IP through two keyless services (ipwho.is, then geojs.io as fallback).
/// When a tunnel health-proxy port is passed, the query itself goes through the tunnel, so geo APIs that are
/// blocked or throttled on the local ISP still answer. No API keys, no telemetry — one small JSON request.
/// </summary>
public sealed class GeoLookup
{
    public async Task<GeoInfo?> LookupAsync(string? ip, int? viaProxyPort, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(9));
        var handler = new SocketsHttpHandler { UseProxy = viaProxyPort is not null, PooledConnectionLifetime = TimeSpan.Zero };
        if (viaProxyPort is { } port) handler.Proxy = new WebProxy($"http://127.0.0.1:{port}");
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        var q = string.IsNullOrWhiteSpace(ip) ? "" : "/" + ip.Trim();
        try
        {
            using var res = await http.GetAsync(new Uri("https://ipwho.is" + q), cts.Token);
            if (res.IsSuccessStatusCode)
            {
                var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync(cts.Token)).RootElement;
                if (json.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
                    throw new HttpRequestException("ipwho.is: " + (json.TryGetProperty("message", out var m) ? m.ToString() : "failed"));
                if (json.TryGetProperty("country", out var country) && json.TryGetProperty("country_code", out var code) &&
                    country.ValueKind == JsonValueKind.String && code.ValueKind == JsonValueKind.String)
                    return new GeoInfo(country.GetString()!, code.GetString()!.ToUpperInvariant());
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception) { /* fall through to the second service */ }
        try
        {
            // Fallback: geojs — different network path, different answer format.
            using var res = await http.GetAsync(new Uri("https://get.geojs.io/v1/ip/country" + (q.Length == 0 ? ".json" : q + ".json")), cts.Token);
            if (res.IsSuccessStatusCode)
            {
                var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync(cts.Token)).RootElement;
                var country = json.TryGetProperty("country", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                var code = (json.TryGetProperty("country_code", out var c1) ? c1.GetString() : null)
                        ?? (json.TryGetProperty("code", out var c2) ? c2.GetString() : null);
                if (country is not null && code is not null) return new GeoInfo(country, code.ToUpperInvariant());
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) { }
        return null;
    }

    /// <summary>Regional-indicator flag emoji for an ISO-3166 alpha-2 code (🇩🇪). Empty for invalid codes.</summary>
    public static string FlagEmoji(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != 2) return "";
        var a = char.ToUpperInvariant(code[0]) - 'A';
        var b = char.ToUpperInvariant(code[1]) - 'A';
        if (a is < 0 or > 25 || b is < 0 or > 25) return "";
        return string.Concat(char.ConvertFromUtf32(0x1F1E6 + a), char.ConvertFromUtf32(0x1F1E6 + b));
    }
}
