using NetworkMonitor.Core.Sampling;

namespace NetworkMonitor.Core.Storage;

/// <summary>
/// Buffers per-interface byte deltas for the current minute and flushes them to the ledger when the
/// minute rolls over (or on exit). Worst-case loss on a crash is one minute.
/// </summary>
public sealed class TrafficRecorder(UsageStore store)
{
    private readonly UsageStore _store = store;
    private readonly Dictionary<string, (long Rx, long Tx)> _accum = [];
    private long _bucket = -1;

    public event EventHandler? Flushed;

    public void Record(DateTimeOffset now, TrafficMeter meter)
    {
        long bucket = UsageStore.BucketOf(now);
        if (_bucket == -1) _bucket = bucket;
        if (bucket != _bucket)
        {
            Flush();
            _bucket = bucket;
        }

        var deltas = meter.LastDeltas;
        if (deltas.Count == 0) return;

        foreach (var nic in meter.SelectedInterfaces)
        {
            if (!deltas.TryGetValue(nic.Luid, out var delta)) continue;
            if (delta.In == 0 && delta.Out == 0) continue;

            var current = _accum.TryGetValue(nic.Key, out var acc) ? acc : (0L, 0L);
            _accum[nic.Key] = (current.Item1 + delta.In, current.Item2 + delta.Out);
        }
    }

    public void Flush()
    {
        if (_accum.Count == 0 || _bucket < 0) return;

        var rows = _accum.Select(kv => (_bucket, kv.Key, kv.Value.Rx, kv.Value.Tx)).ToList();
        _accum.Clear();
        _store.AddIntervals(rows);
        Flushed?.Invoke(this, EventArgs.Empty);
    }

    public void Discard()
    {
        _accum.Clear();
    }
}
