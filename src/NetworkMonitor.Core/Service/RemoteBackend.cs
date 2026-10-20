using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Monitoring;
using NetworkMonitor.Core.Process;
using NetworkMonitor.Core.Proxy;
using NetworkMonitor.Core.Sampling;
using NetworkMonitor.Core.Storage;

namespace NetworkMonitor.Core.Service;

/// <summary>Live state returned by GET /api/live. One round trip per UI refresh tick.</summary>
public sealed record LiveState(
    SpeedSample Speed,
    IReadOnlyList<InterfaceSnapshot> Interfaces,
    IReadOnlyList<string> SelectedKeys,
    bool AutoSelect,
    bool ProxyConnected,
    string ProxyStatus,
    string? ProxyEndpoint,
    ProxyTrafficSnapshot ProxySnapshot,
    IReadOnlyDictionary<string, double> Multipliers,
    ProcessTrafficSnapshot? ProcessSnapshot = null);

public sealed record ServiceHealth(
    bool Ok,
    string Version,
    string DataDirectory,
    int ProcessId,
    DateTimeOffset StartedAt);

public sealed record SelectInterfaceRequest(string? Key);

public sealed record RefreshIntervalRequest(int Seconds);

public sealed record MultiplierRequest(string Node, double Value);

public sealed record ProxyConfigRequest(string? Controller, string? Secret);

public sealed record ProcessEnabledRequest(bool Enabled);

public sealed record ApiError(string Message);

internal static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };
}

/// <summary>
/// HTTP client for the loopback service. The UI talks to the service exclusively through this class;
/// it never opens the SQLite ledger itself.
/// </summary>
public sealed class RemoteBackend : IMonitoringSource, IProxyTrafficSource, IProcessTrafficSource
{
    private readonly AppSettings _settings;
    private readonly Action<Action> _post;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cts = new();

    private readonly object _gate = new();
    private readonly Dictionary<string, double> _multipliers = new(StringComparer.Ordinal);
    private IReadOnlyList<InterfaceSnapshot> _interfaces = [];
    private string[] _selectedKeys = [];
    private bool _autoSelect = true;
    private ProxyTrafficSnapshot _snapshot = new(false, "未连接", null, 0, 0, [], []);
    private ProcessTrafficSnapshot _processSnapshot = new(false, "未连接", 0, 0, [], 0);
    private Task? _loop;
    private volatile bool _online;
    private int _intervalSeconds = 1;

    public RemoteBackend(AppSettings settings, Action<Action>? post = null)
    {
        _settings = settings;
        _post = post ?? (static action => action());
        _intervalSeconds = Math.Clamp(settings.RefreshIntervalSeconds, 1, 10);

        _http = new HttpClient
        {
            BaseAddress = new Uri(ServiceConfig.BaseAddress),
            Timeout = TimeSpan.FromSeconds(3),
        };
        _http.DefaultRequestHeaders.Add(ServiceConfig.TokenHeader, ServiceConfig.EnsureToken());
    }

    public bool IsOnline => _online;

    public IReadOnlyList<InterfaceSnapshot> Interfaces => _interfaces;

    public IReadOnlyList<string> SelectedKeys => _selectedKeys;

    public bool AutoSelect => _autoSelect;

    public UsageReport? Latest { get; private set; }

    public bool Connected => _online && _snapshot.Connected;

    public string Status => _online ? _snapshot.Status : "后台服务未连接";

    public string? Endpoint => _snapshot.Endpoint;

    public bool IsCapturing => _online && _processSnapshot.Active;

    public string CaptureStatus => _online ? _processSnapshot.Status : "后台服务未连接";

    public event Action<SpeedSample>? SpeedTick;
    public event Action<UsageReport>? UsageRefreshed;
    public event Action? InterfacesChanged;
    public event Action<ProxyTrafficSnapshot>? SnapshotUpdated;
    public event Action<ProcessTrafficSnapshot>? ProcessSnapshotUpdated;

    public void Start()
    {
        if (_loop is not null) return;
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void SetRefreshInterval(int seconds)
    {
        _intervalSeconds = Math.Clamp(seconds, 1, 10);
        _settings.RefreshIntervalSeconds = _intervalSeconds;
        _settings.Save();
        _ = SendAsync(HttpMethod.Post, "api/refresh-interval", new RefreshIntervalRequest(_intervalSeconds));
    }

    public void SelectAutomatic()
    {
        _settings.SelectedKeys = null;
        _settings.Save();
        _ = SendAsync(HttpMethod.Post, "api/interfaces/auto", new { });
    }

    public void SelectInterface(string key)
    {
        _settings.SelectedKeys = key;
        _settings.Save();
        _ = SendAsync(HttpMethod.Post, "api/interfaces/select", new SelectInterfaceRequest(key));
    }

    public void RefreshUsage()
    {
        _ = RefreshUsageAsync();
    }

    public void RefreshEndpoint() =>
        _ = SendAsync(HttpMethod.Post, "api/proxy/refresh", new { });

    /// <summary>Pushes a controller change to the service and forces re-detection.</summary>
    public void ApplyProxyConfig(string? controller, string? secret)
    {
        _settings.ClashController = controller;
        _settings.ClashSecret = secret;
        _settings.ClashAutoDetect = string.IsNullOrWhiteSpace(controller);
        _settings.Save();
        _ = SendAsync(HttpMethod.Post, "api/proxy/config", new ProxyConfigRequest(controller, secret));
    }

    public void SetNodeMultiplier(string node, double value)
    {
        lock (_gate) _multipliers[node] = value;
        _settings.NodeMultipliers[node] = value;
        _settings.Save();
        _ = SendAsync(HttpMethod.Post, "api/proxy/multiplier", new MultiplierRequest(node, value));
    }

    public double GetNodeMultiplier(string node)
    {
        lock (_gate) return _multipliers.TryGetValue(node, out double value) ? value : 1;
    }

    public async Task SetNodeMultiplierAsync(string node, double value)
    {
        lock (_gate) _multipliers[node] = value;
        _settings.NodeMultipliers[node] = value;
        _settings.Save();
        await SendAsync(HttpMethod.Post, "api/proxy/multiplier", new MultiplierRequest(node, value)).ConfigureAwait(false);
    }

    public async Task<ProxyUsageReport> BuildReportAsync(ProxyScope scope)
    {
        using var response = await _http.GetAsync($"api/proxy/report?scope={scope}").ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ProxyUsageReport>(ApiJson.Options).ConfigureAwait(false)
               ?? throw new InvalidOperationException("empty proxy report");
    }

    public async Task<ProcessUsageReport> BuildReportAsync(ProcessScope scope)
    {
        using var response = await _http.GetAsync($"api/process/report?scope={scope}").ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ProcessUsageReport>(ApiJson.Options).ConfigureAwait(false)
               ?? throw new InvalidOperationException("empty process report");
    }

    public async Task SetEnabledAsync(bool enabled)
    {
        _settings.ProcessTrafficEnabled = enabled;
        _settings.Save();
        await SendAsync(HttpMethod.Post, "api/process/enabled", new ProcessEnabledRequest(enabled)).ConfigureAwait(false);
    }

    private async Task LoopAsync(CancellationToken token)
    {
        DateTimeOffset nextUsage = DateTimeOffset.MinValue;

        while (!token.IsCancellationRequested)
        {
            try
            {
                var live = await _http.GetFromJsonAsync<LiveState>("api/live", ApiJson.Options, token)
                    .ConfigureAwait(false);

                if (live is not null) ApplyLive(live);

                if (DateTimeOffset.Now >= nextUsage)
                {
                    nextUsage = DateTimeOffset.Now.AddSeconds(30);
                    await RefreshUsageAsync(token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                // The service regenerated service.token (e.g. the data dir was cleared).
                RefreshToken();
                if (_online)
                {
                    _online = false;
                    _post(() => InterfacesChanged?.Invoke());
                }
            }
            catch
            {
                if (_online)
                {
                    _online = false;
                    _post(() => InterfacesChanged?.Invoke());
                }

                _post(() => SpeedTick?.Invoke(SpeedSample.Empty));
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void ApplyLive(LiveState live)
    {
        bool interfacesChanged;

        lock (_gate)
        {
            interfacesChanged = !SameKeys(_interfaces, live.Interfaces);
            _interfaces = live.Interfaces;
            _selectedKeys = live.SelectedKeys.ToArray();
            _autoSelect = live.AutoSelect;
            _snapshot = live.ProxySnapshot;
            _processSnapshot = live.ProcessSnapshot ?? _processSnapshot;

            _multipliers.Clear();
            foreach (var pair in live.Multipliers) _multipliers[pair.Key] = pair.Value;
        }

        _online = true;

        _post(() =>
        {
            SpeedTick?.Invoke(live.Speed);
            if (interfacesChanged) InterfacesChanged?.Invoke();
            SnapshotUpdated?.Invoke(live.ProxySnapshot);
            ProcessSnapshotUpdated?.Invoke(live.ProcessSnapshot ?? _processSnapshot);
        });
    }

    private async Task RefreshUsageAsync(CancellationToken token = default)
    {
        try
        {
            var report = await _http.GetFromJsonAsync<UsageReport>("api/usage", ApiJson.Options, token)
                .ConfigureAwait(false);
            if (report is null) return;

            Latest = report;
            _post(() => UsageRefreshed?.Invoke(report));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch
        {
            // The live loop reports connectivity; usage failures are transient.
        }
    }

    private async Task SendAsync(HttpMethod method, string path, object body)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path)
            {
                Content = JsonContent.Create(body, options: ApiJson.Options),
            };
            using var response = await _http.SendAsync(request).ConfigureAwait(false);
        }
        catch
        {
            // Offline: the settings file keeps the user's choice for the next service start.
        }
    }

    private void RefreshToken()
    {
        string? token = ServiceConfig.TryReadToken();
        if (string.IsNullOrWhiteSpace(token)) return;

        _http.DefaultRequestHeaders.Remove(ServiceConfig.TokenHeader);
        _http.DefaultRequestHeaders.Add(ServiceConfig.TokenHeader, token);
    }

    private static bool SameKeys(IReadOnlyList<InterfaceSnapshot> left, IReadOnlyList<InterfaceSnapshot> right)
    {
        if (left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
        {
            if (left[i].Key != right[i].Key) return false;
        }

        return true;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
        _http.Dispose();
    }
}
