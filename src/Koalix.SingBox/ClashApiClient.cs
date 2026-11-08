using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Koalix.Core;

namespace Koalix.SingBox;

public sealed record ClashConnectionMetadata(
    [property: JsonPropertyName("network")] string? Network,
    [property: JsonPropertyName("type")] string? InboundType,
    [property: JsonPropertyName("sourceIP")] string? SourceIP,
    [property: JsonPropertyName("sourcePort")] string? SourcePort,
    [property: JsonPropertyName("destinationIP")] string? DestinationIP,
    [property: JsonPropertyName("destinationPort")] string? DestinationPort,
    [property: JsonPropertyName("host")] string? Host,
    [property: JsonPropertyName("processPath")] string? ProcessPath,
    [property: JsonPropertyName("dnsMode")] string? DnsMode);

public sealed record ClashConnection(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("metadata")] ClashConnectionMetadata Metadata,
    [property: JsonPropertyName("upload")] long Upload,
    [property: JsonPropertyName("download")] long Download,
    [property: JsonPropertyName("start")] DateTimeOffset Start,
    [property: JsonPropertyName("chains")] string[] Chains,
    [property: JsonPropertyName("rule")] string? Rule,
    [property: JsonPropertyName("rulePayload")] string? RulePayload)
{
    /// <summary>The outbound actually selected by the engine for this flow (last element of the chain).</summary>
    public string Outbound => Chains.Length > 0 ? Chains[0] : "?";
}

public sealed record ClashConnectionsSnapshot(
    [property: JsonPropertyName("downloadTotal")] long DownloadTotal,
    [property: JsonPropertyName("uploadTotal")] long UploadTotal,
    [property: JsonPropertyName("connections")] ClashConnection[]? Connections);

/// <summary>Client for sing-box's Clash-compatible API (bound to 127.0.0.1 with a random secret per session).</summary>
public sealed class ClashApiClient(HttpClient http, Uri baseUrl, string secret)
{
    private HttpRequestMessage Req(string path)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUrl, path));
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return r;
    }

    public async Task<ClashConnectionsSnapshot?> GetConnectionsAsync(CancellationToken ct)
    {
        using var resp = await http.SendAsync(Req("/connections"), ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsyncSafe<ClashConnectionsSnapshot>(ct);
    }

    /// <summary>The /traffic endpoint streams one JSON object per second; this is event-driven (no polling).</summary>
    public async IAsyncEnumerable<TrafficSample> StreamTrafficAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var resp = await http.SendAsync(Req("/traffic"), HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(s);
        long totalUp = 0, totalDown = 0;
        while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0) continue;
            using var doc = JsonDocument.Parse(line);
            var up = doc.RootElement.GetProperty("up").GetInt64();
            var down = doc.RootElement.GetProperty("down").GetInt64();
            totalUp += up; totalDown += down;
            yield return new TrafficSample(up, down, totalUp, totalDown, DateTimeOffset.Now);
        }
    }

    public async Task CloseConnectionAsync(string id, CancellationToken ct)
    {
        var r = Req("/connections/" + Uri.EscapeDataString(id)); r.Method = HttpMethod.Delete;
        using var resp = await http.SendAsync(r, ct);
    }
}

internal static class HttpContentJson
{
    public static async Task<T?> ReadFromJsonAsyncSafe<T>(this HttpContent c, CancellationToken ct)
    {
        await using var s = await c.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(s, cancellationToken: ct);
    }
}
