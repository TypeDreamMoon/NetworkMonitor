using System.Runtime.InteropServices;

namespace NetworkMonitor.Core.Process;

/// <summary>
/// Minimal ETW interop for a private real-time session on a kernel provider.
/// Layouts were checked against the Windows SDK headers (evntrace.h / evntprov.h),
/// the byte sizes noted next to each struct are part of the ABI contract.
/// </summary>
internal static unsafe class EtwInterop
{
    // --- status codes ----------------------------------------------------------------
    internal const int ErrorSuccess = 0;
    internal const int ErrorAccessDenied = 5;
    internal const int ErrorNotEnoughMemory = 8;
    internal const int ErrorBadLength = 24;
    internal const int ErrorInvalidParameter = 87;
    internal const int ErrorInsufficientBuffer = 122;
    internal const int ErrorAlreadyExists = 183;
    internal const int ErrorNoSystemResources = 1450;
    internal const int ErrorWmiInstanceNotFound = 4201;

    // --- control codes ---------------------------------------------------------------
    internal const uint WnodeFlagTracedGuid = 0x00020000;
    internal const uint EventTraceRealTimeMode = 0x00000100;
    internal const uint EventTraceUseSystemTime = 0x00000002;

    internal const uint EventTraceControlQuery = 0;
    internal const uint EventTraceControlStop = 1;

    internal const uint EventControlCodeEnableProvider = 1;
    internal const uint EventControlCodeDisableProvider = 2;

    internal const byte TraceLevelInformation = 4;

    internal const uint ProcessTraceModeRealTime = 0x00000100;
    internal const uint ProcessTraceModeEventRecord = 0x10000000;

    internal static readonly ulong InvalidProcessTraceHandle = unchecked((ulong)(-1));

    // --- structures ------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    internal struct WnodeHeader
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;  // union { Version; Linkage }
        public long TimeStamp;           // union { CountLost; KernelHandle; TimeStamp }
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    /// <summary>EVENT_TRACE_PROPERTIES — 120 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct EventTraceProperties
    {
        public WnodeHeader Wnode;
        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;             // union { AgeLimit; FlushThreshold }
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    /// <summary>EVENT_TRACE_HEADER — 48 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct EventTraceHeader
    {
        public ushort Size;
        public ushort FieldTypeFlags;    // union { FieldTypeFlags; {HeaderType, MarkerFlags} }
        public uint Version;             // union { Version; {Type, Level, ushort Version} }
        public int ThreadId;
        public int ProcessId;
        public long TimeStamp;
        public Guid Guid;
        public uint KernelTime;          // union { {KernelTime, UserTime}; ProcessorTime; ... }
        public uint UserTime;
    }

    /// <summary>EVENT_TRACE — 88 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct EventTrace
    {
        public EventTraceHeader Header;
        public uint InstanceId;
        public uint ParentInstanceId;
        public Guid ParentGuid;
        public IntPtr MofData;
        public uint MofLength;
        public uint ClientContext;       // union { ClientContext; ETW_BUFFER_CONTEXT }
    }

    /// <summary>TRACE_LOGFILE_HEADER — 280 bytes (TimeZone alone is 0xAC).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct TraceLogfileHeader
    {
        public uint BufferSize;
        public uint Version;             // union { Version; 4 x UCHAR }
        public uint ProviderVersion;
        public uint NumberOfProcessors;
        public long EndTime;
        public uint TimerResolution;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint BuffersWritten;
        public Guid LogInstanceGuid;     // union { LogInstanceGuid; {StartBuffers, PointerSize, EventsLost, CpuSpeedInMHz} }
        public IntPtr LoggerName;
        public IntPtr LogFileName;
        public fixed byte TimeZone[0xAC];
        public long BootTime;
        public long PerfFreq;
        public long StartTime;
        public uint ReservedFlags;
        public uint BuffersLost;
    }

    /// <summary>EVENT_TRACE_LOGFILEW — 448 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct EventTraceLogfile
    {
        public IntPtr LogFileName;
        public IntPtr LoggerName;
        public long CurrentTime;
        public uint BuffersRead;
        public uint ProcessTraceMode;    // union { LogFileMode; ProcessTraceMode }
        public EventTrace CurrentEvent;  // offset 32
        public TraceLogfileHeader LogfileHeader; // offset 120
        public IntPtr BufferCallback;    // offset 400
        public int BufferSize;           // offset 408
        public int Filled;
        public int EventsLost;
        public IntPtr EventRecordCallback; // offset 424
        public int IsKernelTrace;
        public IntPtr Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EventDescriptor
    {
        public ushort Id;
        public byte Version;
        public byte Channel;
        public byte Level;
        public byte Opcode;
        public ushort Task;
        public ulong Keyword;
    }

    /// <summary>EVENT_HEADER — 80 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct EventHeader
    {
        public ushort Size;
        public ushort HeaderType;
        public ushort Flags;
        public ushort EventProperty;
        public int ThreadId;
        public int ProcessId;
        public long TimeStamp;
        public Guid ProviderId;
        public EventDescriptor EventDescriptor;
        public uint KernelTime;
        public uint UserTime;
        public Guid ActivityId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EtwBufferContext
    {
        public byte ProcessorNumber;
        public byte Alignment;
        public ushort LoggerId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct EventHeaderExtendedDataItem
    {
        public ushort ExtType;
        public ushort Reserved1;
        public uint DataSize;
        public IntPtr DataPtr;
        public ushort Reserved2;
        public ushort Reserved3;
    }

    /// <summary>EVENT_RECORD — 112 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct EventRecord
    {
        public EventHeader EventHeader;
        public EtwBufferContext BufferContext;
        public ushort ExtendedDataCount;
        public ushort UserDataLength;
        public EventHeaderExtendedDataItem* ExtendedData;
        public byte* UserData;
        public IntPtr UserContext;
    }

    internal static int SizeOfEventTraceLogfile => sizeof(EventTraceLogfile);
    internal static int SizeOfEventTraceProperties => sizeof(EventTraceProperties);
    internal static int SizeOfEventRecord => sizeof(EventRecord);

    // --- imports ---------------------------------------------------------------------
    [DllImport("advapi32.dll", ExactSpelling = true)]
    internal static extern int StartTraceW(out ulong traceHandle, [MarshalAs(UnmanagedType.LPWStr)] string instanceName,
        EventTraceProperties* properties);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    internal static extern int ControlTraceW(ulong traceHandle, [MarshalAs(UnmanagedType.LPWStr)] string? instanceName,
        EventTraceProperties* properties, uint controlCode);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    internal static extern int EnableTraceEx2(ulong traceHandle, Guid* providerId, uint controlCode, byte level,
        ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr enableParameters);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    internal static extern ulong OpenTraceW(EventTraceLogfile* logfile);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    internal static extern int ProcessTrace(ulong* handleArray, uint handleCount, long* startTime, long* endTime);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    internal static extern int CloseTrace(ulong traceHandle);
}
