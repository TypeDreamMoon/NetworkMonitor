using System.Runtime.InteropServices;
using NetworkMonitor.Core.Monitoring;

namespace NetworkMonitor.Core.Process;

/// <summary>
/// A private real-time ETW session bound to Microsoft-Windows-Kernel-Network.
/// Every send/receive event carries the originating PID plus the byte count, which is
/// what makes system-wide per-process accounting possible without a TUN adapter.
/// </summary>
internal sealed unsafe class KernelNetworkSession : IDisposable
{
    /// <summary>Microsoft-Windows-Kernel-Network.</summary>
    internal static readonly Guid ProviderId = new("7dd42a49-5329-4832-8dfd-43d979153a88");

    private const string SessionName = "NetworkMonitor-ProcessTraffic";

    // Keyword 0x10 == KERNEL_NETWORK_KEYWORD_IPV4, 0x20 == IPV6, 0x40 == TCP/IP connect...
    // We instead enable everything (MatchAnyKeyword = 0) and filter by event id, because the
    // manifest keyword layout differs between Windows versions.
    private const ulong MatchAnyKeyword = 0;

    /// <summary>Invoked for every network event, on the ETW processing thread.</summary>
    internal delegate void PacketHandler(
        int eventId, int processId, long bytes, ReadOnlySpan<byte> remote, ushort remotePort, ushort localPort, bool isTx);

    private readonly PacketHandler _handler;
    private readonly object _gate = new();

    private IntPtr _properties;
    private IntPtr _logfile;
    private IntPtr _sessionNamePtr;
    private IntPtr _recordCallbackPtr;
    private EventRecordCallbackDelegate? _recordCallback;

    private Thread? _thread;
    private ulong _sessionHandle;
    private ulong _traceHandle;
    private volatile bool _running;
    private volatile bool _stopping;

    private string _status = "未启动";
    private long _events;
    private long _unparsed;
    private readonly bool[] _seenIds = new bool[256];
    private long _lastDropped;

    internal KernelNetworkSession(PacketHandler handler)
    {
        _handler = handler;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void EventRecordCallbackDelegate(IntPtr eventRecord);

    internal bool Running => _running;

    internal string Status
    {
        get { lock (_gate) return _status; }
    }

    internal long EventsProcessed => Interlocked.Read(ref _events);

    internal long UnparsedEvents => Interlocked.Read(ref _unparsed);

    internal void Start()
    {
        lock (_gate)
        {
            if (_running) return;

            _stopping = false;
            _status = "正在启动 ETW 会话…";

            if (EtwInterop.SizeOfEventTraceLogfile != 448 || EtwInterop.SizeOfEventRecord != 112 ||
                EtwInterop.SizeOfEventTraceProperties != 120)
            {
                _status = "ETW 结构布局自检失败";
                LogSink.Write(
                    $"process: ETW 结构布局自检失败 logfile={EtwInterop.SizeOfEventTraceLogfile} " +
                    $"record={EtwInterop.SizeOfEventRecord} props={EtwInterop.SizeOfEventTraceProperties}");
                return;
            }

            try
            {
                int hr = CreateSession();
                if (hr != EtwInterop.ErrorSuccess)
                {
                    _status = DescribeStartFailure(hr);
                    LogSink.Write($"process: 启动 ETW 会话失败 {SessionName} 0x{hr:X8} {_status}");
                    Cleanup();
                    return;
                }

                hr = OpenConsumer();
                if (hr != EtwInterop.ErrorSuccess)
                {
                    _status = $"打开 ETW 实时会话失败（0x{hr:X8}）";
                    LogSink.Write($"process: OpenTrace 失败 0x{hr:X8}");
                    Cleanup();
                    return;
                }

                _running = true;
                _status = "正在采集";

                _thread = new Thread(ProcessLoop)
                {
                    IsBackground = true,
                    Name = "NetworkMonitor.ETW.Network",
                };
                _thread.Start();

                LogSink.Write($"process: ETW 会话已启动 {SessionName}");
            }
            catch (Exception ex)
            {
                _status = $"启动采集失败：{ex.Message}";
                LogSink.Write("process: 启动 ETW 采集异常", ex);
                Cleanup();
            }
        }
    }

    private int CreateSession()
    {
        // Stop a leftover session from a previous (possibly crashed) run.
        StopNamedSession();

        int propsSize = sizeof(EtwInterop.EventTraceProperties);
        int nameBytes = (SessionName.Length + 1) * sizeof(char);
        int total = propsSize + nameBytes;

        _properties = Marshal.AllocHGlobal(total + 16);
        new Span<byte>((void*)_properties, total + 16).Clear();

        _sessionNamePtr = Marshal.StringToHGlobalUni(SessionName);
        Buffer.MemoryCopy((void*)_sessionNamePtr, (void*)(_properties + propsSize), nameBytes, nameBytes);

        var props = (EtwInterop.EventTraceProperties*)_properties;
        props->Wnode.BufferSize = (uint)total;
        props->Wnode.Flags = EtwInterop.WnodeFlagTracedGuid;
        props->Wnode.ClientContext = 1;             // QPC timestamps
        props->BufferSize = 64;                     // KB per buffer
        props->MinimumBuffers = 32;
        props->MaximumBuffers = 128;
        props->FlushTimer = 1;
        props->LogFileMode = EtwInterop.EventTraceRealTimeMode;
        props->LoggerNameOffset = (uint)propsSize;

        int hr = EtwInterop.StartTraceW(out ulong handle, SessionName, props);
        if (hr != EtwInterop.ErrorSuccess) return hr;

        _sessionHandle = handle;

        fixed (Guid* provider = &ProviderId)
        {
            hr = EtwInterop.EnableTraceEx2(
                _sessionHandle,
                provider,
                EtwInterop.EventControlCodeEnableProvider,
                EtwInterop.TraceLevelInformation,
                MatchAnyKeyword,
                0,
                0,
                IntPtr.Zero);
        }

        if (hr != EtwInterop.ErrorSuccess)
        {
            ControlTraceStop(_properties);
            _sessionHandle = 0;
            return hr;
        }

        return EtwInterop.ErrorSuccess;
    }

    private int OpenConsumer()
    {
        _logfile = Marshal.AllocHGlobal(sizeof(EtwInterop.EventTraceLogfile) + 16);
        new Span<byte>((void*)_logfile, sizeof(EtwInterop.EventTraceLogfile) + 16).Clear();

        _recordCallback = OnEventRecord;
        _recordCallbackPtr = Marshal.GetFunctionPointerForDelegate(_recordCallback);

        var logfile = (EtwInterop.EventTraceLogfile*)_logfile;
        logfile->LogFileName = IntPtr.Zero;
        logfile->LoggerName = _sessionNamePtr;
        logfile->ProcessTraceMode = EtwInterop.ProcessTraceModeRealTime | EtwInterop.ProcessTraceModeEventRecord;
        logfile->EventRecordCallback = _recordCallbackPtr;

        ulong handle = EtwInterop.OpenTraceW(logfile);
        if (handle == EtwInterop.InvalidProcessTraceHandle)
        {
            int error = Marshal.GetLastWin32Error();
            return error == EtwInterop.ErrorSuccess ? EtwInterop.ErrorInvalidParameter : error;
        }

        _traceHandle = handle;
        return EtwInterop.ErrorSuccess;
    }

    private void ProcessLoop()
    {
        try
        {
            ulong handle = _traceHandle;
            int hr = EtwInterop.ProcessTrace(&handle, 1, null, null);

            if (!_stopping && hr != EtwInterop.ErrorSuccess)
            {
                lock (_gate)
                {
                    _status = $"采集已中断（0x{hr:X8}）";
                }

                LogSink.Write($"process: ProcessTrace 返回 0x{hr:X8}");
            }
        }
        catch (Exception ex)
        {
            LogSink.Write("process: ProcessTrace 异常", ex);
        }
        finally
        {
            _running = false;
        }
    }

    private void OnEventRecord(IntPtr eventRecord)
    {
        try
        {
            var record = (EtwInterop.EventRecord*)eventRecord;
            var header = &record->EventHeader;
            int id = header->EventDescriptor.Id;

            if (id is < 10 or > 17) return;

            ushort length = record->UserDataLength;
            byte* data = record->UserData;
            if (data is null) return;

            // Field order is fixed by the Microsoft-Windows-Kernel-Network manifest:
            //   PID, size, daddr, saddr, dport, sport   (network byte order for the addresses).
            // The payload length therefore identifies the address family with no TDH lookup:
            //   20 = IPv4 (4+4+4+4+2+2), 44 = IPv6 (4+4+16+16+2+2).
            bool isTx = (id & 1) == 0;
            int addressLength;
            int remoteOffset;
            int remotePort;
            int localPort;

            if (length == 20)
            {
                addressLength = 4;
                remoteOffset = isTx ? 8 : 12;
                remotePort = 16;
                localPort = 18;
            }
            else if (length == 44)
            {
                addressLength = 16;
                remoteOffset = isTx ? 8 : 24;
                remotePort = 40;
                localPort = 42;
            }
            else
            {
                Interlocked.Increment(ref _unparsed);
                LogUnknownEvent(id, length, data);
                return;
            }

            int processId = *(int*)data;
            long bytes = *(uint*)(data + 4);

            var remote = new ReadOnlySpan<byte>(data + remoteOffset, addressLength);
            ushort dport = *(ushort*)(data + remotePort);
            ushort sport = *(ushort*)(data + localPort);

            Interlocked.Increment(ref _events);

            if (bytes <= 0) return;

            _handler(id, processId, bytes, remote, dport, sport, isTx);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _unparsed);
            LogSink.Write("process: 解析 ETW 事件失败", ex);
        }
    }

    private void LogUnknownEvent(int id, int length, byte* data)
    {
        if (_seenIds[id & 0xFF]) return;
        _seenIds[id & 0xFF] = true;

        var preview = new ReadOnlySpan<byte>(data, Math.Min(length, 24));
        LogSink.Write($"process: 未知事件负载 id={id} length={length} data={Convert.ToHexString(preview)}");
    }

    internal long TakeDroppedDelta()
    {
        long total = UnparsedEvents;
        long previous = Interlocked.Exchange(ref _lastDropped, total);
        return total - previous;
    }

    internal void Stop()
    {
        lock (_gate)
        {
            if (!_running && _traceHandle == 0 && _sessionHandle == 0) return;

            _stopping = true;

            try
            {
                ControlTraceStop(_properties);
            }
            catch (Exception ex)
            {
                LogSink.Write("process: 停止 ETW 会话失败", ex);
            }
        }

        _thread?.Join(TimeSpan.FromSeconds(3));
        _thread = null;

        Cleanup();
        _running = false;

        lock (_gate)
        {
            _status = "已停止";
        }
    }

    private void ControlTraceStop(IntPtr properties)
    {
        if (properties == IntPtr.Zero) return;
        EtwInterop.ControlTraceW(0, SessionName, (EtwInterop.EventTraceProperties*)properties, EtwInterop.EventTraceControlStop);
    }

    private void StopNamedSession()
    {
        int propsSize = sizeof(EtwInterop.EventTraceProperties);
        int total = propsSize + (SessionName.Length + 1) * sizeof(char) + 16;
        IntPtr buffer = Marshal.AllocHGlobal(total);
        try
        {
            new Span<byte>((void*)buffer, total).Clear();
            var props = (EtwInterop.EventTraceProperties*)buffer;
            props->Wnode.BufferSize = (uint)total;
            props->LoggerNameOffset = (uint)propsSize;
            Marshal.Copy(SessionName.ToCharArray(), 0, buffer + propsSize, SessionName.Length);

            EtwInterop.ControlTraceW(0, SessionName, props, EtwInterop.EventTraceControlStop);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void Cleanup()
    {
        if (_traceHandle != 0)
        {
            EtwInterop.CloseTrace(_traceHandle);
            _traceHandle = 0;
        }

        _recordCallback = null;

        if (_logfile != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_logfile);
            _logfile = IntPtr.Zero;
        }

        if (_properties != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_properties);
            _properties = IntPtr.Zero;
        }

        if (_sessionNamePtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_sessionNamePtr);
            _sessionNamePtr = IntPtr.Zero;
        }

        _sessionHandle = 0;
    }

    private static string DescribeStartFailure(int hr) => hr switch
    {
        EtwInterop.ErrorAccessDenied => "启动 ETW 会话需要管理员权限",
        EtwInterop.ErrorAlreadyExists => "已存在同名 ETW 会话，无法启动",
        EtwInterop.ErrorNoSystemResources => "系统 ETW 资源不足，无法启动会话",
        EtwInterop.ErrorNotEnoughMemory => "内存不足，无法启动 ETW 会话",
        EtwInterop.ErrorInvalidParameter => "ETW 启动参数无效",
        EtwInterop.ErrorBadLength => "ETW 会话属性长度无效",
        _ => $"启动 ETW 会话失败（0x{hr:X8}）",
    };

    public void Dispose() => Stop();
}
