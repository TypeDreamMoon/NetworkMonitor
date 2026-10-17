using System.Runtime.InteropServices;

namespace NetworkMonitor.Core.Interop;

/// <summary>
/// Native MIB_IF_ROW2 (Windows SDK, ipmib.h / netioapi.h).
/// The layout is asserted at runtime in <see cref="Sampling.NetworkSampler"/>; if Windows ever
/// changes it, we fail loudly instead of silently reporting garbage counters.
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MibIfRow2
{
    public ulong InterfaceLuid;
    public uint InterfaceIndex;
    public Guid InterfaceGuid;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)]
    public string Alias;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)]
    public string Description;

    public uint PhysicalAddressLength;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
    public byte[] PhysicalAddress;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
    public byte[] PermanentPhysicalAddress;

    public uint Mtu;
    public uint Type;
    public uint TunnelType;
    public uint MediaType;
    public uint PhysicalMediumType;
    public uint AccessType;
    public uint DirectionType;

    /// <summary>Bitfield: bit0 HardwareInterface, bit1 FilterInterface, bit2 ConnectorPresent, ...</summary>
    public byte InterfaceAndOperStatusFlags;

    public uint OperStatus;
    public uint AdminStatus;
    public uint MediaConnectState;
    public Guid NetworkGuid;
    public uint ConnectionType;

    public ulong TransmitLinkSpeed;
    public ulong ReceiveLinkSpeed;
    public ulong InOctets;
    public ulong InUcastPkts;
    public ulong InNUcastPkts;
    public ulong InDiscards;
    public ulong InErrors;
    public ulong InUnknownProtos;
    public ulong InUcastOctets;
    public ulong InMulticastOctets;
    public ulong InBroadcastOctets;
    public ulong OutOctets;
    public ulong OutUcastPkts;
    public ulong OutNUcastPkts;
    public ulong OutDiscards;
    public ulong OutErrors;
    public ulong OutUcastOctets;
    public ulong OutMulticastOctets;
    public ulong OutBroadcastOctets;
    public ulong OutQLen;
}

internal static class IfType
{
    public const uint EthernetCsmacd = 6;
    public const uint Ppp = 23;
    public const uint SoftwareLoopback = 24;
    public const uint Ieee80211 = 71;
    public const uint Tunnel = 131;
    public const uint Wwanpp = 243;
    public const uint Wwanpp2 = 244;

    public static bool IsWireless(uint type) => type == Ieee80211 || type == Wwanpp || type == Wwanpp2;

    public static string Describe(uint type) => type switch
    {
        EthernetCsmacd => "以太网",
        Ppp => "PPP",
        SoftwareLoopback => "回环",
        Ieee80211 => "Wi-Fi",
        Tunnel => "隧道",
        Wwanpp or Wwanpp2 => "蜂窝",
        _ => $"类型 {type}",
    };
}

internal static class IfOperStatus
{
    public const uint Up = 1;
}
