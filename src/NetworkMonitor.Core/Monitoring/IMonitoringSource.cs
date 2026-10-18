using NetworkMonitor.Core.Sampling;
using NetworkMonitor.Core.Storage;

namespace NetworkMonitor.Core.Monitoring;

/// <summary>
/// What the UI needs from the sampling side. Implemented by the in-process
/// <see cref="MonitoringService"/> and by the service-backed remote client.
/// </summary>
public interface IMonitoringSource : IDisposable
{
    bool IsOnline { get; }

    IReadOnlyList<InterfaceSnapshot> Interfaces { get; }

    IReadOnlyList<string> SelectedKeys { get; }

    bool AutoSelect { get; }

    UsageReport? Latest { get; }

    event Action<SpeedSample>? SpeedTick;
    event Action<UsageReport>? UsageRefreshed;
    event Action? InterfacesChanged;

    void SetRefreshInterval(int seconds);

    void SelectAutomatic();

    void SelectInterface(string key);

    void RefreshUsage();
}
