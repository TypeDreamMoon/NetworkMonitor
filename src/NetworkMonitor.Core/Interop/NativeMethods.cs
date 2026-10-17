using System.Runtime.InteropServices;

namespace NetworkMonitor.Core.Interop;

internal static class NativeMethods
{
    /// <summary>Returns NETIO_STATUS (0 == NO_ERROR). Allocates an unmanaged MIB_IF_TABLE2.</summary>
    [DllImport("iphlpapi.dll", SetLastError = true)]
    internal static extern uint GetIfTable2(out IntPtr table);

    [DllImport("iphlpapi.dll")]
    internal static extern void FreeMibTable(IntPtr memory);
}
