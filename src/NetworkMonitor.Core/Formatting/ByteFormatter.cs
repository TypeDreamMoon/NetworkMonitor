using System.Globalization;

namespace NetworkMonitor.Core.Formatting;

/// <summary>
/// 1024-based units, short suffixes (KB / MB / GB) — the convention Chinese network tools use.
/// Set <see cref="UseDecimalUnits"/> to switch to 1000-based units for carrier billing.
/// </summary>
public static class ByteFormatter
{
    public static bool UseDecimalUnits { get; set; }

    private static double Div => UseDecimalUnits ? 1000d : 1024d;

    /// <summary>Byte count, e.g. "1.24 GB".</summary>
    public static string Size(long bytes) => Humanize(bytes, string.Empty);

    /// <summary>Throughput, e.g. "1.24 MB/s".</summary>
    public static string Rate(double bytesPerSecond) => Humanize(bytesPerSecond, "/s");

    private static string Humanize(double value, string suffix)
    {
        if (value < 0) value = 0;
        double div = Div;
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        int unit = 0;
        while (value >= div && unit < units.Length - 1)
        {
            value /= div;
            unit++;
        }

        string number = unit == 0
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString(value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00", CultureInfo.InvariantCulture);
        return number + " " + units[unit] + suffix;
    }
}
