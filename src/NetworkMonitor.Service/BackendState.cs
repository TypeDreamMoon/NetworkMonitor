using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Monitoring;
using NetworkMonitor.Core.Process;
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
    private ProcessTrafficSnapshot _processSnapshot = new(false, "未启动", 0, 0, [], 0);
    private DateTime _settingsStamp;
    private bool _started;

    public BackendState()
    {
        _settings = AppSettings.Load();
        // The UI owns settings.json; the service only reads it and applies commands from the API.
        _settings.PersistOnSave = false;
        _settingsStamp = ReadSettingsStamp();

        Monitor = new MonitoringService(_settings);
        Monitor.SpeedTick += sample => _speed = sample;

        Proxy = new ProxyTrafficService(_settings);
        Proxy.SnapshotUpdated += snapshot =>
        {
            lock (_gate) _snapshot = snapshot;
        };

        ProcessTraffic = new ProcessTrafficService(_settings);
        ProcessTraffic.ProcessSnapshotUpdated += snapshot =>
        {
            lock (_gate) _processSnapshot = snapshot;
        };
    }

    public MonitoringService Monitor { get; }

    public ProxyTrafficService Proxy { get; }

    public ProcessTrafficService ProcessTraffic { get; }

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
        ProcessTraffic.Start();
        LogSink.Write($"后台统计服务已启动（{AppPaths.DataDirectory}）");
    }

    public LiveState Snapshot()
    {
        ProxyTrafficSnapshot snapshot;
        lock (_gate) snapshot = _snapshot;

        IReadOnlyDictionary<string, double> multipliers;
        lock (_gate) multipliers = new Dictionary<string, double>(_settings.NodeMultipliers);

        ReloadProcessSettingIfChanged();

        return new LiveState(
            _speed,
            Monitor.Interfaces,
            Monitor.SelectedKeys,
            Monitor.AutoSelect,
            snapshot.Connected,
            snapshot.Status,
            snapshot.Endpoint,
            snapshot,
            multipliers,
            ProcessTraffic.CreateSnapshot());
    }

    /// <summary>Applies the toggle coming from the UI (which is also the owner of settings.json).</summary>
    public void EnableProcessTraffic(bool enabled)
    {
        lock (_gate) _settings.ProcessTrafficEnabled = enabled;
        _settingsStamp = ReadSettingsStamp();
        _ = ProcessTraffic.SetEnabledAsync(enabled);
    }

    /// <summary>
    /// The UI persists the toggle in settings.json, and the service only holds a snapshot of it,
    /// so pick the change up without restarting the service.
    /// </summary>
    private void ReloadProcessSettingIfChanged()
    {
        DateTime stamp = ReadSettingsStamp();
        if (stamp == _settingsStamp || stamp == DateTime.MinValue) return;

        _settingsStamp = stamp;

        try
        {
            var latest = AppSettings.Load();
            bool enabled;

            lock (_gate)
            {
                enabled = latest.ProcessTrafficEnabled;
                if (_settings.ProcessTrafficEnabled == enabled) return;
                _settings.ProcessTrafficEnabled = enabled;
            }

            _ = ProcessTraffic.SetEnabledAsync(enabled);
            LogSink.Write($"process: 设置变更，按进程统计已{(enabled ? "启用" : "停用")}");
        }
        catch (Exception ex)
        {
            LogSink.Write("process: 重新读取设置失败", ex);
        }
    }

    private static DateTime ReadSettingsStamp()
    {
        try
        {
            var info = new FileInfo(AppPaths.SettingsPath);
            return info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
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
        ProcessTraffic.Dispose();
        Proxy.Dispose();
        Monitor.Dispose();
    }
}
