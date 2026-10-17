using System.ComponentModel;
using System.Runtime.InteropServices;
using NetworkMonitor.Core.Interop;

namespace NetworkMonitor.Core.Sampling;

/// <summary>Reads the 64-bit interface counters via GetIfTable2 (MIB_IF_ROW2).</summary>
public static class NetworkSampler
{
    private static readonly int RowSize = ValidateLayout();

    private static int ValidateLayout()
    {
        int size = Marshal.SizeOf<MibIfRow2>();
        if (size != 1352)
        {
            throw new InvalidOperationException(
                $"MIB_IF_ROW2 layout mismatch: managed {size} bytes, expected 1352. " +
                "Refusing to report counters that would be read from the wrong offsets.");
        }

        return size;
    }

    public static IReadOnlyList<InterfaceSnapshot> Snapshot()
    {
        uint status = NativeMethods.GetIfTable2(out IntPtr table);
        if (status != 0)
            throw new Win32Exception((int)status, $"GetIfTable2 failed (NETIO_STATUS {status}).");

        try
        {
            int count = Marshal.ReadInt32(table);
            // MIB_IF_TABLE2: ULONG NumEntries; MIB_IF_ROW2 Table[]; -> rows start at offset 8 (8-byte alignment).
            IntPtr rows = IntPtr.Add(table, 8);

            var result = new List<InterfaceSnapshot>(count);
            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibIfRow2>(IntPtr.Add(rows, i * RowSize));
                if (row.Alias is null) continue;

                result.Add(new InterfaceSnapshot(
                    Luid: row.InterfaceLuid,
                    Index: row.InterfaceIndex,
                    Guid: row.InterfaceGuid,
                    Alias: row.Alias,
                    Description: row.Description ?? string.Empty,
                    Type: row.Type,
                    TunnelType: row.TunnelType,
                    OperStatus: row.OperStatus,
                    IsHardware: (row.InterfaceAndOperStatusFlags & 0b0000_0001) != 0,
                    IsFilter: (row.InterfaceAndOperStatusFlags & 0b0000_0010) != 0,
                    LinkSpeedBps: Math.Max(row.TransmitLinkSpeed, row.ReceiveLinkSpeed),
                    InOctets: row.InOctets,
                    OutOctets: row.OutOctets));
            }

            return result;
        }
        finally
        {
            NativeMethods.FreeMibTable(table);
        }
    }
}
