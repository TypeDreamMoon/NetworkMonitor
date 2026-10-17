using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetworkMonitor.Core.Proxy;

public sealed class ClashConnectionsResponse
{
    [JsonPropertyName("downloadTotal")]
    public long DownloadTotal { get; set; }

    [JsonPropertyName("uploadTotal")]
    public long UploadTotal { get; set; }

    [JsonPropertyName("connections")]
    public List<ClashConnection> Connections { get; set; } = [];
}

public sealed class ClashConnection
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("upload")]
    public long Upload { get; set; }

    [JsonPropertyName("download")]
    public long Download { get; set; }

    [JsonPropertyName("start")]
    public string? Start { get; set; }

    [JsonPropertyName("chains")]
    public List<string> Chains { get; set; } = [];

    [JsonPropertyName("rule")]
    public string? Rule { get; set; }

    [JsonPropertyName("rulePayload")]
    public string? RulePayload { get; set; }

    [JsonPropertyName("metadata")]
    public ClashMetadata Metadata { get; set; } = new();
}

public sealed class ClashMetadata
{
    [JsonPropertyName("network")]
    public string? Network { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("host")]
    public string? Host { get; set; }

    [JsonPropertyName("destinationIP")]
    public string? DestinationIp { get; set; }

    [JsonPropertyName("destinationPort")]
    public string? DestinationPort { get; set; }

    [JsonPropertyName("process")]
    public string? Process { get; set; }

    [JsonPropertyName("processPath")]
    public string? ProcessPath { get; set; }
}

/// <summary>Thin client for the Clash/Mihomo external controller.</summary>
public sealed class ClashApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(3),
    };

    private string _secret = string.Empty;

    public void Configure(string? secret) => _secret = secret ?? string.Empty;

    public async Task<ClashConnectionsResponse?> GetConnectionsAsync(Uri baseUri, CancellationToken ct)
    {
        var uri = new Uri(baseUri, "/connections");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (_secret.Length > 0)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _secret);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<ClashConnectionsResponse>(stream, JsonOptions, ct);
    }

    public void Dispose() => _http.Dispose();
}
