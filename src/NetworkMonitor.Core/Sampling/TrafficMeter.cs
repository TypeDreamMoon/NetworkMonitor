using System.Diagnostics;

namespace NetworkMonitor.Core.Sampling;

public readonly record struct SpeedSample(
    long DownBps,
    long UpBps,
    long RawDownBps,
    long RawUpBps,
    long SessionDownBytes,
    long SessionUpBytes,
    double ElapsedSeconds)
{
    public static SpeedSample Empty => new(0, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// Turns monotonic interface counters into rates. Handles counter resets, adapter re-enumeration,
/// sleep/resume time gaps, and smooths the display value with an EMA.
/// </summary>
public sealed class TrafficMeter
{
    private sealed class CounterState
    {
        public ulong In;
        public ulong Out;
        public bool Primed;
    }

    private readonly object _gate = new();
    private readonly Dictionary<ulong, CounterState> _state = [];
    private readonly Dictionary<ulong, (long In, long Out)> _lastDeltas = [];
    private HashSet<ulong>? _selected;

    private long _lastTimestamp;
    private double _emaDown;
    private double _emaUp;
    private long _sessionRx;
    private long _sessionTx;
    private IReadOnlyList<InterfaceSnapshot> _lastSnapshot = [];

    /// <summary>Display smoothing time constant, seconds.</summary>
    public double SmoothingSeconds { get; set; } = 2.5;

    public IReadOnlyList<InterfaceSnapshot> LastSnapshot
    {
        get { lock (_gate) return _lastSnapshot; }
    }

    public IReadOnlyList<InterfaceSnapshot> SelectedInterfaces { get; private set; } = [];

    /// <summary>Per-interface byte deltas produced by the most recent <see cref="Tick"/>.</summary>
    public IReadOnlyDictionary<ulong, (long In, long Out)> LastDeltas
    {
        get { lock (_gate) return new Dictionary<ulong, (long In, long Out)>(_lastDeltas); }
    }

    public void Select(IEnumerable<InterfaceSnapshot> interfaces)
    {
        lock (_gate)
        {
            SelectedInterfaces = interfaces.ToList();
            _selected = SelectedInterfaces.Select(i => i.Luid).ToHashSet();
        }
    }

    public void SelectAllStable(IEnumerable<InterfaceSnapshot> all)
    {
        lock (_gate)
        {
            SelectedInterfaces = InterfaceFilter.SelectDefault(all);
            _selected = SelectedInterfaces.Select(i => i.Luid).ToHashSet();
        }
    }

    public void ResetSession()
    {
        lock (_gate)
        {
            _sessionRx = 0;
            _sessionTx = 0;
        }
    }

    /// <summary>Call once per second (or as often as needed); rate math uses real elapsed time.</summary>
    public SpeedSample Tick()
    {
        IReadOnlyList<InterfaceSnapshot> snapshot;
        try
        {
            snapshot = NetworkSampler.Snapshot();
        }
        catch
        {
            return SpeedSample.Empty;
        }

        lock (_gate)
        {
            _lastSnapshot = snapshot;

            if (_selected is null)
            {
                SelectedInterfaces = InterfaceFilter.SelectDefault(snapshot);
                _selected = SelectedInterfaces.Select(i => i.Luid).ToHashSet();
            }

            long now = Stopwatch.GetTimestamp();
            double elapsed = _lastTimestamp == 0
                ? 0
                : (now - _lastTimestamp) / (double)Stopwatch.Frequency;
            _lastTimestamp = now;

            long deltaIn = 0;
            long deltaOut = 0;
            _lastDeltas.Clear();

            foreach (var nic in snapshot)
            {
                if (!_selected.Contains(nic.Luid)) continue;

                if (!_state.TryGetValue(nic.Luid, out var state))
                {
                    state = new CounterState();
                    _state[nic.Luid] = state;
                }

                long nicIn = 0;
                long nicOut = 0;
                if (state.Primed)
                {
                    // Negative delta means the adapter was re-enabled or counters wrapped/reset.
                    // Drop that one interval instead of reporting a bogus spike.
                    if (nic.InOctets >= state.In) nicIn = (long)(nic.InOctets - state.In);
                    if (nic.OutOctets >= state.Out) nicOut = (long)(nic.OutOctets - state.Out);
                }

                if (nicIn != 0 || nicOut != 0) _lastDeltas[nic.Luid] = (nicIn, nicOut);
                deltaIn += nicIn;
                deltaOut += nicOut;

                state.In = nic.InOctets;
                state.Out = nic.OutOctets;
                state.Primed = true;
            }

            _sessionRx += deltaIn;
            _sessionTx += deltaOut;

            double rawDown = 0, rawUp = 0;
            if (elapsed > 0.02)
            {
                // Cap the divisor so a long sleep/resume gap does not mask the current rate entirely.
                double divisor = Math.Min(elapsed, 60.0);
                rawDown = deltaIn / divisor;
                rawUp = deltaOut / divisor;

                double alpha = 1 - Math.Exp(-elapsed / Math.Max(0.1, SmoothingSeconds));
                _emaDown += (rawDown - _emaDown) * alpha;
                _emaUp += (rawUp - _emaUp) * alpha;
            }

            return new SpeedSample(
                DownBps: (long)Math.Round(_emaDown),
                UpBps: (long)Math.Round(_emaUp),
                RawDownBps: (long)Math.Round(rawDown),
                RawUpBps: (long)Math.Round(rawUp),
                SessionDownBytes: _sessionRx,
                SessionUpBytes: _sessionTx,
                ElapsedSeconds: elapsed);
        }
    }
}
