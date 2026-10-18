namespace NetworkMonitor.Core.Proxy;

/// <summary>
/// What the UI needs from Clash/Mihomo traffic accounting. Implemented by the in-process
/// <see cref="ProxyTrafficService"/> and by the service-backed remote client.
/// </summary>
public interface IProxyTrafficSource : IDisposable
{
    bool Connected { get; }

    string Status { get; }

    string? Endpoint { get; }

    event Action<ProxyTrafficSnapshot>? SnapshotUpdated;

    double GetNodeMultiplier(string node);

    void SetNodeMultiplier(string node, double value);

    Task SetNodeMultiplierAsync(string node, double value);

    void RefreshEndpoint();

    Task<ProxyUsageReport> BuildReportAsync(ProxyScope scope);
}
