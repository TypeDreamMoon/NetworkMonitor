using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetworkMonitor.Core.Configuration;

public sealed class AppSettings
{
    /// <summary>Comma separated "luid:...." keys. Empty/null = automatic selection.</summary>
    public string? SelectedKeys { get; set; }

    public bool ShowFloatingWindow { get; set; } = true;
    public bool ShowTaskbarWindow { get; set; }
    public bool LaunchAtStartup { get; set; }
    public bool FloatingTopmost { get; set; } = true;
    public bool StartMinimized { get; set; } = true;
    public bool DecimalUnits { get; set; }
    public bool SmoothSpeed { get; set; } = true;

    /// <summary>Clash-compatible external controller, e.g. "127.0.0.1:9090". Empty = read from the proxy config.</summary>
    public string? ClashController { get; set; }

    public string? ClashSecret { get; set; }

    public bool ClashAutoDetect { get; set; } = true;

    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>Version the user chose to skip in the automatic update prompt.</summary>
    public string? SkippedUpdateVersion { get; set; }

    /// <summary>Per-node traffic multiplier used for billed bytes (actual bytes are always stored).</summary>
    public Dictionary<string, double> NodeMultipliers { get; set; } = new(StringComparer.Ordinal);

    public double? FloatingLeft { get; set; }
    public double? FloatingTop { get; set; }

    [JsonIgnore]
    public IReadOnlyList<string> SelectedKeyList =>
        string.IsNullOrWhiteSpace(SelectedKeys)
            ? []
            : SelectedKeys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AppSettings Load(string? path = null)
    {
        path ??= AppPaths.SettingsPath;
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(string? path = null)
    {
        path ??= AppPaths.SettingsPath;
        try
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // Settings are best-effort; never crash the monitor over them.
        }
    }
}
