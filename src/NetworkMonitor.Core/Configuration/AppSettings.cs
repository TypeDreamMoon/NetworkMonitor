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

    /// <summary>
    /// Enables the ETW (Microsoft-Windows-Kernel-Network) system-wide per-process accounting.
    /// This one does not need FlClash: it reads the kernel's own send/receive events.
    /// </summary>
    public bool ProcessTrafficEnabled { get; set; } = true;

    /// <summary>Collapses the left navigation rail to an icon-only strip.</summary>
    public bool NavCollapsed { get; set; }

    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>UI / sampling refresh interval in seconds (1, 2 or 5).</summary>
    public int RefreshIntervalSeconds { get; set; } = 1;

    /// <summary>Version the user chose to skip in the automatic update prompt.</summary>
    public string? SkippedUpdateVersion { get; set; }

    /// <summary>Per-node traffic multiplier used for billed bytes (actual bytes are always stored).</summary>
    public Dictionary<string, double> NodeMultipliers { get; set; } = new(StringComparer.Ordinal);

    public double? FloatingLeft { get; set; }
    public double? FloatingTop { get; set; }

    /// <summary>Shows the download rate in the floating window.</summary>
    public bool FloatingShowDown { get; set; } = true;

    /// <summary>Shows the upload rate in the floating window.</summary>
    public bool FloatingShowUp { get; set; } = true;

    /// <summary>Appends this session's downloaded/uploaded totals under the rates.</summary>
    public bool FloatingShowSession { get; set; }

    /// <summary>Text scale of the floating window (0.85 compact, 1.0 normal, 1.25 large).</summary>
    public double FloatingScale { get; set; } = 1.0;

    /// <summary>Background opacity of the floating window, 0.30 - 1.00.</summary>
    public double FloatingOpacity { get; set; } = 0.70;

    /// <summary>Uses the desktop acrylic material behind the floating window.</summary>
    public bool FloatingAcrylic { get; set; } = true;

    /// <summary>Hex colour of the download arrow/text.</summary>
    public string FloatingDownColor { get; set; } = "#3EDCC4";

    /// <summary>Hex colour of the upload arrow/text.</summary>
    public string FloatingUpColor { get; set; } = "#FFB454";

    /// <summary>Lets pointer input pass through the floating window to whatever is behind it.</summary>
    public bool FloatingClickThrough { get; set; }

    /// <summary>Keeps the floating window where it is: dragging is disabled.</summary>
    public bool FloatingLocked { get; set; }

    /// <summary>Snaps the floating window to the nearest screen edge when it is dropped.</summary>
    public bool FloatingSnapEdges { get; set; } = true;

    /// <summary>Main window position in physical pixels (top-left of the window).</summary>
    public double? MainLeft { get; set; }
    public double? MainTop { get; set; }

    /// <summary>Main window client size in DIP, so it survives DPI changes between monitors.</summary>
    public double? MainWidth { get; set; }
    public double? MainHeight { get; set; }

    /// <summary>
    /// When false, <see cref="Save"/> is a no-op. The service loads the UI-owned settings file
    /// read-only so the two processes never fight over the same JSON document.
    /// </summary>
    [JsonIgnore]
    public bool PersistOnSave { get; set; } = true;

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
        if (!PersistOnSave) return;

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
