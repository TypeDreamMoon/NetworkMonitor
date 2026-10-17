namespace NetworkMonitor.Core.Storage;

public readonly record struct UsageAmount(long Rx, long Tx)
{
    public long Total => Rx + Tx;
    public static UsageAmount Zero => new(0, 0);
    public static UsageAmount operator +(UsageAmount a, UsageAmount b) => new(a.Rx + b.Rx, a.Tx + b.Tx);
}

public readonly record struct DailyUsage(DateOnly Date, long Rx, long Tx)
{
    public long Total => Rx + Tx;
}

public sealed class UsageReport
{
    public required UsageAmount Today { get; init; }
    public required UsageAmount Week { get; init; }
    public required UsageAmount Month { get; init; }
    public required UsageAmount Total { get; init; }
    public DateOnly? FirstTrackedDate { get; init; }
    public IReadOnlyList<DailyUsage> DailySeries { get; init; } = [];
}
