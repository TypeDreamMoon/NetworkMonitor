using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Sampling;
using NetworkMonitor.Core.Storage;

namespace NetworkMonitor.Core.Monitoring;

/// <summary>
/// UI-agnostic monitoring host: samples the NIC counters every second, writes the minute ledger,
/// and raises events through the supplied post delegate (so callers can marshal to their UI thread).
/// </summary>
public sealed class MonitoringService : IDisposable
{
    private readonly Action<Action> _post;
    private readonly Timer _timer;
    private readonly object _gate = new();

    private IReadOnlyList<InterfaceSnapshot> _interfaces = [];
    private volatile string[] _keys = [];
    private volatile bool _autoSelect = true;
    private int _ticking;
    private int _refreshing;
    private volatile bool _disposed;
    private DateTime _lastUsageRefresh = DateTime.MinValue;
    private DateTime _lastInterfaceScan = DateTime.MinValue;

    public MonitoringService(AppSettings settings, Action<Action>? post = null)
    {
        Settings = settings;
        _post = post ?? (static action => action());

        Store = new UsageStore(AppPaths.DatabasePath);
        Meter = new TrafficMeter();
        Recorder = new TrafficRecorder(Store);
        Usage = new UsageService(Store);

        Recorder.Flushed += (_, _) => RefreshUsage();
        _timer = new Timer(static state => ((MonitoringService)state!).Tick(), this, Timeout.Infinite, Timeout.Infinite);
    }

    public AppSettings Settings { get; }
    public UsageStore Store { get; }
    public TrafficMeter Meter { get; }
    public TrafficRecorder Recorder { get; }
    public UsageService Usage { get; }

    public IReadOnlyList<InterfaceSnapshot> Interfaces => _interfaces;
    public IReadOnlyList<string> SelectedKeys => _keys;
    public bool AutoSelect => _autoSelect;
    public UsageReport? Latest { get; private set; }

    public event Action<SpeedSample>? SpeedTick;
    public event Action<UsageReport>? UsageRefreshed;
    public event Action? InterfacesChanged;

    public void SetRefreshInterval(int seconds)
    {
        int clamped = Math.Clamp(seconds, 1, 10);
        _timer.Change(TimeSpan.FromSeconds(clamped), TimeSpan.FromSeconds(clamped));
    }

    public void Initialize()
    {
        try
        {
            _interfaces = NetworkSampler.Snapshot();
        }
        catch (Exception ex)
        {
            LogSink.Write("初次读取网卡失败", ex);
        }

        if (Settings.SelectedKeyList.Count > 0)
        {
            var wanted = Settings.SelectedKeyList.ToHashSet();
            var picked = _interfaces.Where(i => wanted.Contains(i.Key)).ToList();
            if (picked.Count > 0)
            {
                _autoSelect = false;
                Meter.Select(picked);
            }
        }

        if (_autoSelect)
            Meter.Select(InterfaceFilter.SelectDefault(_interfaces));

        SyncKeys();
        _lastInterfaceScan = DateTime.UtcNow;
        RefreshUsage();
        int interval = Math.Clamp(Settings.RefreshIntervalSeconds, 1, 10);
        _timer.Change(TimeSpan.FromSeconds(interval), TimeSpan.FromSeconds(interval));
    }

    private void Tick()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;

        try
        {
            var sample = Meter.Tick();
            Recorder.Record(DateTimeOffset.Now, Meter);
            Post(() => SpeedTick?.Invoke(sample));

            var now = DateTime.UtcNow;
            if ((now - _lastInterfaceScan).TotalSeconds >= 15)
            {
                _lastInterfaceScan = now;
                ScanInterfaces();
            }

            if ((now - _lastUsageRefresh).TotalSeconds >= 30)
                RefreshUsage();
        }
        catch (Exception ex)
        {
            LogSink.Write("采样失败", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    private void ScanInterfaces()
    {
        var snapshot = Meter.LastSnapshot;
        if (snapshot.Count == 0) return;

        var usable = snapshot.Where(InterfaceFilter.IsCountable).ToList();

        if (_autoSelect)
        {
            var wanted = InterfaceFilter.SelectDefault(snapshot);
            if (!SameKeys(wanted, Meter.SelectedInterfaces))
            {
                Meter.Select(wanted);
                SyncKeys();
                LogSink.Write("自动切换到: " + string.Join(", ", wanted.Select(w => w.DisplayName)));
                Post(() => InterfacesChanged?.Invoke());
                RefreshUsage();
            }
        }

        if (!SameKeys(usable, _interfaces.Where(InterfaceFilter.IsCountable).ToList()))
        {
            _interfaces = snapshot;
            Post(() => InterfacesChanged?.Invoke());
        }
    }

    private static bool SameKeys(IReadOnlyList<InterfaceSnapshot> a, IReadOnlyList<InterfaceSnapshot> b)
    {
        if (a.Count != b.Count) return false;
        var set = a.Select(x => x.Key).ToHashSet();
        return b.All(x => set.Contains(x.Key));
    }

    public void SelectAutomatic()
    {
        var source = Meter.LastSnapshot.Count > 0 ? Meter.LastSnapshot : _interfaces;
        _autoSelect = true;
        Meter.Select(InterfaceFilter.SelectDefault(source));
        Settings.SelectedKeys = null;
        Settings.Save();
        SyncKeys();
        Post(() => InterfacesChanged?.Invoke());
        RefreshUsage();
    }

    public void SelectInterface(string key)
    {
        var source = Meter.LastSnapshot.Count > 0 ? Meter.LastSnapshot : _interfaces;
        var nic = source.FirstOrDefault(n => n.Key == key);
        if (nic is null) return;

        _autoSelect = false;
        Meter.Select(new[] { nic });
        Settings.SelectedKeys = key;
        Settings.Save();
        SyncKeys();
        Post(() => InterfacesChanged?.Invoke());
        RefreshUsage();
    }

    private void SyncKeys()
    {
        _keys = Meter.SelectedInterfaces.Select(i => i.Key).ToArray();
        Usage.InterfaceKeys = _keys;
    }

    public void RefreshUsage()
    {
        if (_disposed || Interlocked.Exchange(ref _refreshing, 1) == 1) return;

        var keys = _keys;
        _ = Task.Run(() =>
        {
            try
            {
                var report = Usage.Build(DateTimeOffset.Now, null, 30, keys);
                _lastUsageRefresh = DateTime.UtcNow;
                Post(() =>
                {
                    Latest = report;
                    UsageRefreshed?.Invoke(report);
                });
            }
            catch (Exception ex)
            {
                LogSink.Write("统计查询失败", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }
        });
    }

    private void Post(Action action)
    {
        if (_disposed) return;
        _post(() =>
        {
            if (!_disposed) action();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        _timer.Dispose();

        try
        {
            Recorder.Flush();
        }
        catch (Exception ex)
        {
            LogSink.Write("退出时写入台账失败", ex);
        }

        Store.Dispose();
    }
}

/// <summary>Plug-in point so Core does not depend on a particular logging implementation.</summary>
public static class LogSink
{
    public static Action<string, Exception?>? Handler { get; set; }

    public static void Write(string message, Exception? ex = null) => Handler?.Invoke(message, ex);
}
