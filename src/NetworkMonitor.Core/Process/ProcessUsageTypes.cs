namespace NetworkMonitor.Core.Process;

public enum ProcessScope
{
    Today,
    Week,
    Month,
}

public sealed record ProcessUsageItem(
    string Process,
    string DisplayName,
    string Category,
    long Rx,
    long Tx)
{
    public long Total => Rx + Tx;
}

public sealed record ProcessCategoryUsage(string Category, long Rx, long Tx)
{
    public long Total => Rx + Tx;
}

public sealed record ProcessUsageReport(
    ProcessScope Scope,
    long Rx,
    long Tx,
    IReadOnlyList<ProcessUsageItem> Apps,
    IReadOnlyList<ProcessCategoryUsage> Categories);

public sealed record ProcessTrafficSnapshot(
    bool Active,
    string Status,
    long SessionRx,
    long SessionTx,
    IReadOnlyList<ProcessUsageItem> SessionApps,
    long DroppedEvents);

/// <summary>
/// What the UI needs from the system-wide per-process accounting. Implemented by the
/// in-process <see cref="ProcessTrafficService"/> and by the service-backed remote client.
/// Property names are deliberately distinct from <see cref="NetworkMonitor.Core.Proxy.IProxyTrafficSource"/>
/// because both are implemented by the same backend object.
/// </summary>
public interface IProcessTrafficSource : IDisposable
{
    /// <summary>True only when the user enabled it and the ETW session is actually delivering events.</summary>
    bool IsCapturing { get; }

    /// <summary>Human readable capture state (why it is not capturing, etc).</summary>
    string CaptureStatus { get; }

    event Action<ProcessTrafficSnapshot>? ProcessSnapshotUpdated;

    Task<ProcessUsageReport> BuildReportAsync(ProcessScope scope);

    Task SetEnabledAsync(bool enabled);
}
