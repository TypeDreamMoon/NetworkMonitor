using NetworkMonitor.Core.Interop;

namespace NetworkMonitor.Core.Sampling;

/// <summary>One NIC row from GetIfTable2, flattened into managed fields.</summary>
public sealed record InterfaceSnapshot(
    ulong Luid,
    uint Index,
    Guid Guid,
    string Alias,
    string Description,
    uint Type,
    uint TunnelType,
    uint OperStatus,
    bool IsHardware,
    bool IsFilter,
    ulong LinkSpeedBps,
    ulong InOctets,
    ulong OutOctets)
{
    public bool IsUp => OperStatus == IfOperStatus.Up;

    public bool IsLoopback => Type == IfType.SoftwareLoopback;

    /// <summary>Stable storage key — the LUID does not change when the adapter is re-enabled (the index does).</summary>
    public string Key => $"luid:{Luid:x16}";

    public string Kind => IfType.Describe(Type);

    /// <summary>Friendly name: Alias is "WLAN"/"以太网" while Description is the driver string.</summary>
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Alias) && !Alias.StartsWith("本地连接*", StringComparison.Ordinal))
                return Alias;
            return string.IsNullOrWhiteSpace(Alias) ? Description : Alias;
        }
    }

    public string Detail =>
        $"{(IsHardware ? "物理" : "虚拟")} · {Kind} · {FormatLinkSpeed(LinkSpeedBps)} · {Alias}｜{Description}";

    private static string FormatLinkSpeed(ulong bps) => bps switch
    {
        0 => "速率未知",
        >= 1_000_000_000 when bps % 1_000_000_000 == 0 => $"{bps / 1_000_000_000} Gbps",
        >= 1_000_000 when bps % 1_000_000 == 0 => $"{bps / 1_000_000} Mbps",
        _ => $"{bps / 1_000_000.0:0.#} Mbps",
    };
}

/// <summary>Counters only count toward "traffic" when this returns true.</summary>
public static class InterfaceFilter
{
    public static bool IsCountable(InterfaceSnapshot nic) =>
        nic.IsUp && !nic.IsLoopback && !nic.IsFilter && !string.IsNullOrEmpty(nic.Description);

    /// <summary>
    /// Default selection: all up physical, non-tunnel interfaces (Wi-Fi + Ethernet are both summed).
    /// VPN/virtual adapters are skipped so their bytes are not counted twice.
    /// </summary>
    public static List<InterfaceSnapshot> SelectDefault(IEnumerable<InterfaceSnapshot> all)
    {
        var usable = all.Where(IsCountable).ToList();

        var physical = usable.Where(n => n.IsHardware && n.TunnelType == 0).ToList();
        if (physical.Count > 0) return physical;

        var nonTunnel = usable.Where(n => n.TunnelType == 0).ToList();
        if (nonTunnel.Count > 0) return nonTunnel;

        return usable;
    }
}
