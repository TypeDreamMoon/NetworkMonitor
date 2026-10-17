using System.Diagnostics;
using System.Text;
using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Formatting;
using NetworkMonitor.Core.Sampling;
using NetworkMonitor.Core.Storage;

try
{
    Console.OutputEncoding = Encoding.UTF8;
}
catch
{
    // redirected output — fine
}

int seconds = 15;
bool listOnly = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--seconds" or "-s" when i + 1 < args.Length:
            int.TryParse(args[++i], out seconds);
            break;
        case "--list" or "-l":
            listOnly = true;
            break;
        case "--help" or "-h":
            Console.WriteLine("用法: NetworkMonitor.Console [--seconds N] [--list]");
            return;
    }
}

Console.WriteLine($"数据目录: {AppPaths.DataDirectory} (portable={AppPaths.IsPortable})");
Console.WriteLine($"数据库  : {AppPaths.DatabasePath}");
Console.WriteLine();

IReadOnlyList<InterfaceSnapshot> all;
try
{
    all = NetworkSampler.Snapshot();
}
catch (Exception ex)
{
    Console.WriteLine("GetIfTable2 失败: " + ex);
    return;
}

var selected = InterfaceFilter.SelectDefault(all);
var selectedSet = selected.Select(s => s.Luid).ToHashSet();

Console.WriteLine($"{"",2} {"名称",-22} {"类型",-8} {"状态",-4} {"物理",-4} {"收",-12} {"发",-12} 详情");
foreach (var nic in all.OrderByDescending(n => selectedSet.Contains(n.Luid)).ThenBy(n => n.DisplayName))
{
    bool isSelected = selectedSet.Contains(nic.Luid);
    Console.WriteLine(string.Format(
        "{0,2} {1,-22} {2,-8} {3,-4} {4,-4} {5,-12} {6,-12} {7}",
        isSelected ? "*" : "",
        Trim(nic.DisplayName, 22),
        nic.Kind,
        nic.IsUp ? "up" : "down",
        nic.IsHardware ? "是" : "否",
        ByteFormatter.Size((long)nic.InOctets),
        ByteFormatter.Size((long)nic.OutOctets),
        Trim(nic.Detail, 90)));
}

Console.WriteLine();
Console.WriteLine("* = 参与统计的网卡: " + string.Join(", ", selected.Select(s => s.DisplayName)));
Console.WriteLine();

if (listOnly) return;

using var store = new UsageStore(AppPaths.DatabasePath);
var service = new UsageService(store);
var meter = new TrafficMeter();
meter.Select(selected);
var recorder = new TrafficRecorder(store);

try
{
    long start = Stopwatch.GetTimestamp();
    long lastReport = 0;
    UsageReport? report = null;
    long prevRx = 0, prevTx = 0;

    while (true)
    {
        var sample = meter.Tick();
        recorder.Record(DateTimeOffset.Now, meter);

        long elapsed = Stopwatch.GetTimestamp() - start;
        if (elapsed / Stopwatch.Frequency >= seconds) break;

        if (elapsed - lastReport > Stopwatch.Frequency * 2 || report is null)
        {
            lastReport = elapsed;
            report = service.Build(DateTimeOffset.Now);
        }

        double secs = elapsed / (double)Stopwatch.Frequency;
        Console.Write($"\r[{secs,5:0.0}s] ↓ {ByteFormatter.Rate(sample.DownBps),-12} ↑ {ByteFormatter.Rate(sample.UpBps),-12}" +
                      $"(原始 ↓{ByteFormatter.Rate(sample.RawDownBps)} ↑{ByteFormatter.Rate(sample.RawUpBps)})   " +
                      $"本次 ↓{ByteFormatter.Size(sample.SessionDownBytes)} ↑{ByteFormatter.Size(sample.SessionUpBytes)}    ");

        prevRx = sample.SessionDownBytes;
        prevTx = sample.SessionUpBytes;

        Thread.Sleep(1000);
    }

    recorder.Flush();
    report = service.Build(DateTimeOffset.Now);

    Console.WriteLine();
    Console.WriteLine();
    Console.WriteLine("—— 流量台账 ——");
    Console.WriteLine($"今日: ↓ {ByteFormatter.Size(report.Today.Rx),-12} ↑ {ByteFormatter.Size(report.Today.Tx),-12} 合计 {ByteFormatter.Size(report.Today.Total)}");
    Console.WriteLine($"本周: ↓ {ByteFormatter.Size(report.Week.Rx),-12} ↑ {ByteFormatter.Size(report.Week.Tx),-12} 合计 {ByteFormatter.Size(report.Week.Total)}");
    Console.WriteLine($"本月: ↓ {ByteFormatter.Size(report.Month.Rx),-12} ↑ {ByteFormatter.Size(report.Month.Tx),-12} 合计 {ByteFormatter.Size(report.Month.Total)}");
    Console.WriteLine($"总计: ↓ {ByteFormatter.Size(report.Total.Rx),-12} ↑ {ByteFormatter.Size(report.Total.Tx),-12} 合计 {ByteFormatter.Size(report.Total.Total)}");
    Console.WriteLine($"首次记录: {report.FirstTrackedDate?.ToString("yyyy-MM-dd") ?? "（本次为首次运行）"}");
    Console.WriteLine();
    Console.WriteLine("最近 7 天: " + string.Join("  ", report.DailySeries.TakeLast(7)
        .Select(d => $"{d.Date:MM-dd}={ByteFormatter.Size(d.Total)}")));
}
finally
{
    recorder.Flush();
}

static string Trim(string value, int width) =>
    value.Length <= width ? value : value[..(width - 1)] + "…";
