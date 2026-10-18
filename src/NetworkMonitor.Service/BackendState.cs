using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Monitoring;
using NetworkMonitor.Core.Proxy;
using NetworkMonitor.Core.Sampling;
using NetworkMonitor.Core.Service;
using NetworkMonitor.Core.Storage;

namespace NetworkMonitor.Service;

/// <summary>
/// Owns the sampling + ledger + Clash polling stack. Runs inside the Windows service (LocalSystem)
/// or as a console process for development; the UI only talks to it over loopback HTTP.
/// </summary>
public sealed class BackendState : IDisposable
{
    private readonly AppSettings _settings;
    private readonly object _gate = new();
    private SpeedSample _speed;
    private ProxyTrafficSnapshot _snapshot = new(false, "正在连接…", null, 0, 0, [], []);
    private bool _started;

    public BackendState()
    {
        _settings = AppSettings.Load();
        // The UI owns settings.json; the service only reads it and applies commands from the API.
        _settings.PersistOnSave = false;

        Monitor = new MonitoringService(_settings);
        Monitor.SpeedTick += sample => _speed = sample;

        Proxy = new ProxyTrafficService(_settings);
        Proxy.SnapshotUpdated += snapshot =>
        {
            lock (_gate) _snapshot = snapshot;
        };
    }

    public MonitoringService Monitor { get; }

    public ProxyTrafficService Proxy { get; }

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
        }

        Monitor.Initialize();
        Proxy.Start();
        LogSink.Write($"后台统计服务已启动（{AppPaths.DataDirectory}）");
    }

    public LiveState Snapshot()
    {
        ProxyTrafficSnapshot snapshot;
        lock (_gate) snapshot = _snapshot;

        IReadOnlyDictionary<string, double> multipliers;
        lock (_gate) multipliers = new Dictionary<string, double>(_settings.NodeMultipliers);

        return new LiveState(
            _speed,
            Monitor.Interfaces,
            Monitor.SelectedKeys,
            Monitor.AutoSelect,
            snapshot.Connected,
            snapshot.Status,
            snapshot.Endpoint,
            snapshot,
            multipliers);
    }

    public UsageReport BuildUsage() => Monitor.Usage.Build(DateTimeOffset.Now, null, 30);

    public void SelectInterface(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) Monitor.SelectAutomatic();
        else Monitor.SelectInterface(key);
    }

    public void ApplyProxyConfig(string? controller, string? secret)
    {
        string? normalized = string.IsNullOrWhiteSpace(controller) ? null : controller.Trim();

        lock (_gate)
        {
            _settings.ClashController = normalized;
            _settings.ClashSecret = string.IsNullOrWhiteSpace(secret) ? null : secret;
            _settings.ClashAutoDetect = normalized is null;
        }

        Proxy.RefreshEndpoint();
    }

    public void SetMultiplier(string node, double value)
    {
        if (string.IsNullOrWhiteSpace(node)) return;
        lock (_gate) Proxy.SetNodeMultiplier(node, Math.Clamp(value, 0, 100));
    }

    public void Dispose()
    {
        Proxy.Dispose();
        Monitor.Dispose();
    }
}
