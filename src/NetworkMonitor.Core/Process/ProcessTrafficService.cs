using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Monitoring;
using NetworkMonitor.Core.Proxy;
using NetworkMonitor.Core.Storage;

namespace NetworkMonitor.Core.Process;

/// <summary>
/// System-wide per-process traffic accounting built on an ETW real-time session for
/// Microsoft-Windows-Kernel-Network. Unlike <see cref="ProxyTrafficService"/> this does not
/// need FlClash (or any proxy) to be running: it sees every TCP/UDP send/receive the kernel
/// performs and attributes the byte count to the owning PID. Loopback, LAN and link-local
/// traffic is dropped so the numbers stay comparable with the NIC ledger.
/// </summary>
public sealed class ProcessTrafficService : IProcessTrafficSource
{
    private readonly AppSettings _settings;
    private readonly Action<Action> _post;
    private readonly ProcessUsageStore _store;
    private readonly KernelNetworkSession _session;
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;
    private readonly object _gate = new();

    private readonly Dictionary<string, (long Rx, long Tx)> _minute = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Rx, long Tx)> _sessionApps = new(StringComparer.Ordinal);

    /// <summary>PID cache: process name plus start time, so a recycled PID is re-resolved.</summary>
    private readonly Dictionary<int, (string Name, long StartTicks)> _names = [];

    private Task? _loop;
    private long _bucket = -1;
    private long _sessionRx;
    private long _sessionTx;
    private bool _active;
    private string _status = "未启动";
    private DateTime _lastStartAttempt = DateTime.MinValue;

    public ProcessTrafficService(AppSettings settings, Action<Action>? post = null)
    {
        _settings = settings;
        _post = post ?? (static action => action());
        _store = new ProcessUsageStore(AppPaths.DatabasePath);
        _session = new KernelNetworkSession(OnPacket);
    }

    public bool IsCapturing
    {
        get { lock (_gate) return _active; }
    }

    public string CaptureStatus
    {
        get { lock (_gate) return _status; }
    }

    public event Action<ProcessTrafficSnapshot>? ProcessSnapshotUpdated;

    public void Start()
    {
        _loop ??= Task.Run(() => LoopAsync(_cts.Token));
    }

    public Task SetEnabledAsync(bool enabled)
    {
        _settings.ProcessTrafficEnabled = enabled;
        _settings.Save();

        if (enabled)
        {
            StartSession();
        }
        else
        {
            StopSession();
        }

        PublishSnapshot();
        return Task.CompletedTask;
    }

    internal void StartSession()
    {
        _session.Start();

        lock (_gate)
        {
            _active = _session.Running;
            _status = _session.Status;
        }

        if (_session.Running)
        {
            LogSink.Write("process: 按进程统计已启用");
        }
    }

    internal void StopSession()
    {
        if (!_session.Running)
        {
            lock (_gate)
            {
                _active = false;
                _status = "已停止";
            }

            return;
        }

        FlushMinute();
        _session.Stop();

        lock (_gate)
        {
            _active = false;
            _status = _session.Status;
        }

        LogSink.Write("process: 按进程统计已停止");
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                LogSink.Write("process: 采集循环异常", ex);
            }

            try
            {
                int seconds = Math.Clamp(_settings.RefreshIntervalSeconds, 1, 10);
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, seconds)), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        FlushMinute();
    }

    private void Tick()
    {
        bool enabled = _settings.ProcessTrafficEnabled;

        if (!enabled)
        {
            if (_session.Running) StopSession();
            return;
        }

        if (!_session.Running && DateTime.UtcNow - _lastStartAttempt > TimeSpan.FromSeconds(30))
        {
            _lastStartAttempt = DateTime.UtcNow;
            StartSession();
        }

        lock (_gate)
        {
            _active = _session.Running;
            _status = _session.Status;

            if (_active) ValidatePidCacheLocked();
        }

        PublishSnapshot();
    }

    /// <summary>
    /// Called on the ETW processing thread for every network event. Must stay cheap:
    /// no allocations beyond a dictionary probe, and no synchronous file IO.
    /// </summary>
    private void OnPacket(int eventId, int processId, long bytes, ReadOnlySpan<byte> remote,
        ushort remotePort, ushort localPort, bool isTx)
    {
        if (bytes <= 0) return;
        if (NetworkAddressFilter.IsNoise(remote)) return;

        lock (_gate)
        {
            long bucket = UsageStore.BucketOf(DateTimeOffset.Now);
            if (_bucket == -1)
            {
                _bucket = bucket;
            }
            else if (bucket != _bucket)
            {
                FlushMinuteLocked();
                _bucket = bucket;
            }

            string process = ResolveNameLocked(processId);

            if (isTx) _sessionTx += bytes;
            else _sessionRx += bytes;

            _sessionApps.TryGetValue(process, out var app);
            _sessionApps[process] = isTx ? (app.Rx, app.Tx + bytes) : (app.Rx + bytes, app.Tx);

            _minute.TryGetValue(process, out var minute);
            _minute[process] = isTx ? (minute.Rx, minute.Tx + bytes) : (minute.Rx + bytes, minute.Tx);
        }
    }

    private string ResolveNameLocked(int processId)
    {
        if (_names.TryGetValue(processId, out var cached)) return cached.Name;

        string name = Lookup(processId, out long startTicks);
        _names[processId] = (name, startTicks);
        return name;
    }

    /// <summary>Re-checks the cached PIDs so a recycled PID does not bill the wrong process.</summary>
    private void ValidatePidCacheLocked()
    {
        if (_names.Count == 0) return;

        bool dirty = false;

        foreach (var (pid, entry) in _names.ToList())
        {
            if (entry.StartTicks == 0)
            {
                // Could not be resolved earlier; try again while it may be alive.
                string retry = Lookup(pid, out long ticks);
                if (retry != entry.Name || ticks != entry.StartTicks)
                {
                    _names[pid] = (retry, ticks);
                    dirty = true;
                }

                continue;
            }

            long current = StartTicksOf(pid);
            if (current != 0 && current != entry.StartTicks)
            {
                string name = Lookup(pid, out long ticks);
                _names[pid] = (name, ticks);
                dirty = true;
            }
        }

        if (dirty) LogSink.Write("process: PID 复用已重新解析");
    }

    private static string Lookup(int processId, out long startTicks)
    {
        startTicks = 0;

        if (processId <= 0) return "System Idle";
        if (processId == 4) return "System";

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            startTicks = process.StartTime.Ticks;
            return process.ProcessName + ".exe";
        }
        catch
        {
            return $"PID {processId}";
        }
    }

    private static long StartTicksOf(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return process.StartTime.Ticks;
        }
        catch
        {
            return 0;
        }
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
            .Select(pair => (_bucket, pair.Key, pair.Value.Rx, pair.Value.Tx))
            .ToList();

        _minute.Clear();

        try
        {
            _store.AddIntervals(rows);
        }
        catch (Exception ex)
        {
            LogSink.Write("进程流量写入失败", ex);
        }
    }

    /// <summary>Snapshot of the in-memory session totals, used by the service API and the UI.</summary>
    public ProcessTrafficSnapshot CreateSnapshot()
    {
        lock (_gate)
        {
            var apps = _sessionApps
                .Select(pair =>
                {
                    var identity = AppCatalog.Resolve(pair.Key);
                    return new ProcessUsageItem(pair.Key, identity.DisplayName, identity.Category, pair.Value.Rx, pair.Value.Tx);
                })
                .OrderByDescending(app => app.Total)
                .ToList();

            return new ProcessTrafficSnapshot(
                _active,
                _status,
                _sessionRx,
                _sessionTx,
                apps,
                _session.UnparsedEvents);
        }
    }

    private void PublishSnapshot()
    {
        var snapshot = CreateSnapshot();

        _post(() =>
        {
            try
            {
                ProcessSnapshotUpdated?.Invoke(snapshot);
            }
            catch (Exception ex)
            {
                LogSink.Write("进程流量快照事件失败", ex);
            }
        });
    }

    /// <summary>Aggregates persisted minutes plus the in-flight minute for the given scope.</summary>
    public Task<ProcessUsageReport> BuildReportAsync(ProcessScope scope)
    {
        return Task.Run(() =>
        {
            var timeZone = TimeZoneInfo.Local;
            var now = DateTimeOffset.Now;
            var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
            var today = DateOnly.FromDateTime(localNow.DateTime);

            DateOnly start = scope switch
            {
                ProcessScope.Today => today,
                ProcessScope.Week => UsageService.StartOfWeek(today),
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
                        rows.Add((pair.Key, pair.Value.Rx, pair.Value.Tx));
                }
            }

            var totals = new Dictionary<string, (long Rx, long Tx)>(StringComparer.Ordinal);
            long totalRx = 0;
            long totalTx = 0;

            foreach (var (process, rx, tx) in rows)
            {
                totalRx += rx;
                totalTx += tx;

                totals.TryGetValue(process, out var current);
                totals[process] = (current.Rx + rx, current.Tx + tx);
            }

            var apps = totals
                .Select(pair =>
                {
                    var identity = AppCatalog.Resolve(pair.Key);
                    return new ProcessUsageItem(pair.Key, identity.DisplayName, identity.Category, pair.Value.Rx, pair.Value.Tx);
                })
                .OrderByDescending(app => app.Total)
                .ToList();

            var categories = apps
                .GroupBy(app => app.Category)
                .Select(group => new ProcessCategoryUsage(group.Key, group.Sum(app => app.Rx), group.Sum(app => app.Tx)))
                .OrderByDescending(category => category.Total)
                .ToList();

            return new ProcessUsageReport(scope, totalRx, totalTx, apps, categories);
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _cts.Cancel();
        FlushMinute();
        _session.Dispose();
        _store.Dispose();
        _cts.Dispose();
    }
}
