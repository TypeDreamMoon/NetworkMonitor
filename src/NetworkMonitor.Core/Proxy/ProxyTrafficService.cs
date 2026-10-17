using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Monitoring;
using NetworkMonitor.Core.Storage;

namespace NetworkMonitor.Core.Proxy;

public enum ProxyScope
{
    Today,
    Week,
    Month,
}

public sealed record ProxyNodeUsage(string Node, long Rx, long Tx, double Multiplier)
{
    public long Total => Rx + Tx;

    public long BilledRx => Scale(Rx);

    public long BilledTx => Scale(Tx);

    public long BilledTotal => BilledRx + BilledTx;

    private long Scale(long value) => (long)Math.Round(value * Math.Max(0, Multiplier));
}

public sealed record ProxyAppUsage(
    string Process,
    string DisplayName,
    string Category,
    long Rx,
    long Tx,
    long BilledRx,
    long BilledTx,
    IReadOnlyList<ProxyNodeUsage> Nodes)
{
    public long Total => Rx + Tx;

    public long BilledTotal => BilledRx + BilledTx;
}

public sealed record ProxyUsageReport(
    ProxyScope Scope,
    long Rx,
    long Tx,
    long BilledRx,
    long BilledTx,
    IReadOnlyList<ProxyAppUsage> Apps,
    IReadOnlyList<ProxyNodeUsage> Nodes);

public sealed record ProxyTrafficSnapshot(
    bool Connected,
    string Status,
    string? Endpoint,
    long SessionRx,
    long SessionTx,
    IReadOnlyList<ProxyAppUsage> SessionApps,
    IReadOnlyList<ProxyNodeUsage> SessionNodes);

/// <summary>
/// Polls the Clash/Mihomo /connections endpoint and attributes byte deltas to
/// (process, proxy node). This covers traffic that actually passed through the proxy;
/// direct traffic never appears in the Clash API.
/// </summary>
public sealed class ProxyTrafficService : IDisposable
{
    private sealed class AppAccumulator
    {
        public string DisplayName = string.Empty;
        public string Category = "其他";
        public long Rx;
        public long Tx;
        public readonly Dictionary<string, (long Rx, long Tx)> Nodes = [];
    }

    private readonly AppSettings _settings;
    private readonly Action<Action> _post;
    private readonly ClashApiClient _client = new();
    private readonly ProxyUsageStore _store;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    private readonly Dictionary<string, (long Upload, long Download)> _connections = [];
    private readonly Dictionary<(string Process, string Node), (long Rx, long Tx)> _minute = [];
    private readonly Dictionary<string, AppAccumulator> _sessionApps = [];
    private readonly Dictionary<string, (long Rx, long Tx)> _sessionNodes = [];

    private Task? _loop;
    private long _bucket = -1;
    private long _sessionRx;
    private long _sessionTx;

    private ClashEndpoint? _detected;
    private ClashEndpoint? _active;
    private DateTime _lastDetect = DateTime.MinValue;
    private DateTime _lastIdlePost = DateTime.MinValue;
    private DateTime _lastProbeFailureLog = DateTime.MinValue;

    private bool _connected;
    private string _status = "正在检测 FlClash…";
    private string? _error;

    public ProxyTrafficService(AppSettings settings, Action<Action>? post = null)
    {
        _settings = settings;
        _post = post ?? (static action => action());
        _store = new ProxyUsageStore(AppPaths.DatabasePath);
    }

    public bool Connected
    {
        get { lock (_gate) return _connected; }
    }

    public string Status
    {
        get
        {
            lock (_gate)
                return _error is null ? _status : $"{_status}：{_error}";
        }
    }

    public string? Endpoint
    {
        get { lock (_gate) return _active?.BaseUri.Authority; }
    }

    public event Action<ProxyTrafficSnapshot>? SnapshotUpdated;

    public void Start()
    {
        _loop ??= Task.Run(() => LoopAsync(_cts.Token));
    }

    public double GetNodeMultiplier(string node)
    {
        _settings.NodeMultipliers ??= new Dictionary<string, double>(StringComparer.Ordinal);
        return _settings.NodeMultipliers.TryGetValue(node, out var value) ? value : 1.0;
    }

    public void SetNodeMultiplier(string node, double value)
    {
        _settings.NodeMultipliers ??= new Dictionary<string, double>(StringComparer.Ordinal);
        _settings.NodeMultipliers[node] = Math.Max(0, value);
        _settings.Save();
    }

    /// <summary>Forces the next tick to re-read the proxy config (used by the "重新检测" button).</summary>
    public void RefreshEndpoint()
    {
        _lastDetect = DateTime.MinValue;
        _detected = null;

        lock (_gate)
        {
            _connections.Clear();
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                SetStatus(connected: false, "采集失败", ex.Message);
                PublishSnapshot();
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(ct)) break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        FlushMinute();
    }

    private async Task TickAsync(CancellationToken ct)
    {
        ClashEndpoint? endpoint = await ResolveEndpointAsync(ct);

        if (endpoint is null)
        {
            SetStatus(false, "未检测到可用的代理控制器（FlClash 需开启 External controller）", null);

            if (DateTime.UtcNow - _lastIdlePost > TimeSpan.FromSeconds(5))
            {
                _lastIdlePost = DateTime.UtcNow;
                PublishSnapshot();
            }

            return;
        }

        if (_active is null || _active.BaseUri != endpoint.BaseUri || _active.Secret != endpoint.Secret)
        {
            LogSink.Write($"proxy: endpoint={endpoint.BaseUri} source={endpoint.Source}");
            lock (_gate)
            {
                _connections.Clear();
            }

            _active = endpoint;
        }

        _client.Configure(endpoint.Secret);

        ClashConnectionsResponse? response;
        try
        {
            response = await _client.GetConnectionsAsync(endpoint.BaseUri, ct);
        }
        catch (Exception ex)
        {
            // The controller may have gone away; probe again after the backoff window.
            _detected = null;
            _lastDetect = DateTime.UtcNow;

            if (DateTime.UtcNow - _lastProbeFailureLog > TimeSpan.FromSeconds(30))
            {
                _lastProbeFailureLog = DateTime.UtcNow;
                LogSink.Write($"proxy: 请求失败 {endpoint.BaseUri} {ex.Message}");
            }

            SetStatus(false, "连接代理控制器失败", ex.Message);
            PublishSnapshot();
            return;
        }

        if (response is null)
        {
            SetStatus(false, "FlClash 返回空数据", null);
            PublishSnapshot();
            return;
        }

        ProcessConnections(response);
        SetStatus(true, "已连接 FlClash", null);
        PublishSnapshot();
    }

    private async Task<ClashEndpoint?> ResolveEndpointAsync(CancellationToken ct)
    {
        var manual = ClashConfigLocator.FromSettings(_settings.ClashController, _settings.ClashSecret);
        if (manual is not null) return manual;

        if (!_settings.ClashAutoDetect) return null;

        if (_detected is not null) return _detected;
        if (DateTime.UtcNow - _lastDetect < TimeSpan.FromSeconds(10)) return null;

        _lastDetect = DateTime.UtcNow;

        foreach (var candidate in ClashConfigLocator.DetectAll())
        {
            if (await ProbeAsync(candidate, ct))
            {
                _detected = candidate;
                LogSink.Write($"proxy: 可用控制器 {candidate.BaseUri}（{candidate.Source}）");
                return candidate;
            }
        }

        _detected = null;
        return null;
    }

    private async Task<bool> ProbeAsync(ClashEndpoint endpoint, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(900));

        try
        {
            _client.Configure(endpoint.Secret);
            var response = await _client.GetConnectionsAsync(endpoint.BaseUri, timeout.Token);
            return response is not null;
        }
        catch
        {
            return false;
        }
    }

    private void ProcessConnections(ClashConnectionsResponse response)
    {
        long bucket = UsageStore.BucketOf(DateTimeOffset.Now);

        lock (_gate)
        {
            if (_bucket == -1)
            {
                _bucket = bucket;
            }
            else if (bucket != _bucket)
            {
                FlushMinuteLocked();
                _bucket = bucket;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var connection in response.Connections)
            {
                if (string.IsNullOrEmpty(connection.Id)) continue;
                seen.Add(connection.Id);

                long upload = Math.Max(0, connection.Upload);
                long download = Math.Max(0, connection.Download);

                if (_connections.TryGetValue(connection.Id, out var previous))
                {
                    long deltaUp = upload >= previous.Upload ? upload - previous.Upload : 0;
                    long deltaDown = download >= previous.Download ? download - previous.Download : 0;

                    if (deltaUp > 0 || deltaDown > 0)
                        Accumulate(connection, deltaDown, deltaUp);
                }

                // First sight is a baseline only, so restarting the app never re-counts
                // bytes that a long-lived connection already transferred.
                _connections[connection.Id] = (upload, download);
            }

            if (_connections.Count > seen.Count)
            {
                foreach (string id in _connections.Keys.Where(id => !seen.Contains(id)).ToList())
                    _connections.Remove(id);
            }
        }
    }

    private void Accumulate(ClashConnection connection, long rx, long tx)
    {
        string process = ResolveProcess(connection);
        string node = ResolveNode(connection);

        _sessionRx += rx;
        _sessionTx += tx;

        if (!_sessionApps.TryGetValue(process, out var app))
        {
            var identity = AppCatalog.Resolve(process, connection.Metadata.ProcessPath);
            app = new AppAccumulator
            {
                DisplayName = identity.DisplayName,
                Category = identity.Category,
            };
            _sessionApps[process] = app;
        }

        app.Rx += rx;
        app.Tx += tx;
        app.Nodes.TryGetValue(node, out var nodeTotals);
        app.Nodes[node] = (nodeTotals.Rx + rx, nodeTotals.Tx + tx);

        _sessionNodes.TryGetValue(node, out var sessionNode);
        _sessionNodes[node] = (sessionNode.Rx + rx, sessionNode.Tx + tx);

        var key = (process, node);
        _minute.TryGetValue(key, out var minute);
        _minute[key] = (minute.Rx + rx, minute.Tx + tx);
    }

    private static string ResolveProcess(ClashConnection connection)
    {
        string? process = connection.Metadata.Process;
        if (string.IsNullOrWhiteSpace(process) && !string.IsNullOrWhiteSpace(connection.Metadata.ProcessPath))
        {
            try
            {
                process = Path.GetFileName(connection.Metadata.ProcessPath);
            }
            catch
            {
                // fall through
            }
        }

        return string.IsNullOrWhiteSpace(process) ? "未知进程" : process;
    }

    private static string ResolveNode(ClashConnection connection)
    {
        if (connection.Chains.Count > 0 && !string.IsNullOrWhiteSpace(connection.Chains[0]))
            return connection.Chains[0];

        return "DIRECT";
    }

    private void FlushMinute()
    {
        lock (_gate)
        {
            FlushMinuteLocked();
        }
    }

    private void FlushMinuteLocked()
    {
        if (_minute.Count == 0 || _bucket < 0) return;

        var rows = _minute
            .Select(pair => (_bucket, pair.Key.Process, pair.Key.Node, pair.Value.Rx, pair.Value.Tx))
            .ToList();

        _minute.Clear();

        try
        {
            _store.AddIntervals(rows);
        }
        catch (Exception ex)
        {
            LogSink.Write("代理流量写入失败", ex);
        }
    }

    private void SetStatus(bool connected, string status, string? error)
    {
        lock (_gate)
        {
            _connected = connected;
            _status = status;
            _error = error;
        }
    }

    private void PublishSnapshot()
    {
        ProxyTrafficSnapshot snapshot;

        lock (_gate)
        {
            snapshot = new ProxyTrafficSnapshot(
                _connected,
                _error is null ? _status : $"{_status}：{_error}",
                _active?.BaseUri.Authority,
                _sessionRx,
                _sessionTx,
                BuildSessionAppsLocked(),
                BuildSessionNodesLocked());
        }

        _post(() =>
        {
            try
            {
                SnapshotUpdated?.Invoke(snapshot);
            }
            catch (Exception ex)
            {
                LogSink.Write("代理流量快照事件失败", ex);
            }
        });
    }

    private List<ProxyAppUsage> BuildSessionAppsLocked()
    {
        return _sessionApps
            .Select(pair =>
            {
                var nodes = pair.Value.Nodes
                    .Select(node => new ProxyNodeUsage(
                        node.Key,
                        node.Value.Rx,
                        node.Value.Tx,
                        GetNodeMultiplier(node.Key)))
                    .OrderByDescending(node => node.Total)
                    .ToList();

                return new ProxyAppUsage(
                    pair.Key,
                    pair.Value.DisplayName,
                    pair.Value.Category,
                    pair.Value.Rx,
                    pair.Value.Tx,
                    nodes.Sum(node => node.BilledRx),
                    nodes.Sum(node => node.BilledTx),
                    nodes);
            })
            .OrderByDescending(app => app.Total)
            .ToList();
    }

    private List<ProxyNodeUsage> BuildSessionNodesLocked()
    {
        return _sessionNodes
            .Select(pair => new ProxyNodeUsage(pair.Key, pair.Value.Rx, pair.Value.Tx, GetNodeMultiplier(pair.Key)))
            .OrderByDescending(node => node.Total)
            .ToList();
    }

    /// <summary>Aggregates persisted minutes plus the in-flight minute for the given scope.</summary>
    public Task<ProxyUsageReport> BuildReportAsync(ProxyScope scope)
    {
        return Task.Run(() =>
        {
            var timeZone = TimeZoneInfo.Local;
            var now = DateTimeOffset.Now;
            var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
            var today = DateOnly.FromDateTime(localNow.DateTime);

            DateOnly start = scope switch
            {
                ProxyScope.Today => today,
                ProxyScope.Week => UsageService.StartOfWeek(today),
                _ => new DateOnly(today.Year, today.Month, 1),
            };

            long from = UsageService.ToBucket(start, timeZone);
            long to = UsageService.ToBucket(today.AddDays(1), timeZone);

            var rows = _store.QueryGrouped(from, to).ToList();

            lock (_gate)
            {
                if (_bucket >= from && _bucket < to)
                {
                    foreach (var pair in _minute)
                        rows.Add((pair.Key.Process, pair.Key.Node, pair.Value.Rx, pair.Value.Tx));
                }
            }

            var apps = new Dictionary<string, (long Rx, long Tx, Dictionary<string, (long Rx, long Tx)> Nodes)>(StringComparer.Ordinal);
            var nodes = new Dictionary<string, (long Rx, long Tx)>(StringComparer.Ordinal);
            long totalRx = 0;
            long totalTx = 0;

            foreach (var (process, node, rx, tx) in rows)
            {
                totalRx += rx;
                totalTx += tx;

                if (!apps.TryGetValue(process, out var app))
                {
                    app = (0, 0, new Dictionary<string, (long Rx, long Tx)>(StringComparer.Ordinal));
                }

                app.Rx += rx;
                app.Tx += tx;
                app.Nodes.TryGetValue(node, out var nodeTotals);
                app.Nodes[node] = (nodeTotals.Rx + rx, nodeTotals.Tx + tx);
                apps[process] = app;

                nodes.TryGetValue(node, out var nodeTotal);
                nodes[node] = (nodeTotal.Rx + rx, nodeTotal.Tx + tx);
            }

            var appList = apps
                .Select(pair =>
                {
                    var identity = AppCatalog.Resolve(pair.Key);
                    var appNodes = pair.Value.Nodes
                        .Select(node => new ProxyNodeUsage(
                            node.Key,
                            node.Value.Rx,
                            node.Value.Tx,
                            GetNodeMultiplier(node.Key)))
                        .OrderByDescending(node => node.Total)
                        .ToList();

                    return new ProxyAppUsage(
                        pair.Key,
                        identity.DisplayName,
                        identity.Category,
                        pair.Value.Rx,
                        pair.Value.Tx,
                        appNodes.Sum(node => node.BilledRx),
                        appNodes.Sum(node => node.BilledTx),
                        appNodes);
                })
                .OrderByDescending(app => app.Total)
                .ToList();

            var nodeList = nodes
                .Select(pair => new ProxyNodeUsage(pair.Key, pair.Value.Rx, pair.Value.Tx, GetNodeMultiplier(pair.Key)))
                .OrderByDescending(node => node.Total)
                .ToList();

            long billedRx = nodeList.Sum(node => node.BilledRx);
            long billedTx = nodeList.Sum(node => node.BilledTx);

            return new ProxyUsageReport(scope, totalRx, totalTx, billedRx, billedTx, appList, nodeList);
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        FlushMinute();
        _client.Dispose();
        _store.Dispose();
        _cts.Dispose();
    }
}
