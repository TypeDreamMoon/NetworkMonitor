using NetworkMonitor.Core.Monitoring;

namespace NetworkMonitor.Core.Configuration;

public static class AppPaths
{
    public const string AppName = "NetworkMonitor";

    static AppPaths()
    {
        string? overrideHome = Environment.GetEnvironmentVariable("NETWORKMONITOR_HOME");
        if (!string.IsNullOrWhiteSpace(overrideHome))
        {
            IsPortable = true;
            DataDirectory = Path.GetFullPath(overrideHome);
            Directory.CreateDirectory(DataDirectory);
            return;
        }

        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt")))
        {
            IsPortable = true;
            DataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(DataDirectory);
            return;
        }

        // The service runs as LocalSystem, whose %APPDATA% is the system profile. Machine-wide
        // storage keeps the ledger and settings shared between the service and the UI.
        string shared = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppName);

        if (TryPrepareDirectory(shared))
        {
            DataDirectory = shared;
        }
        else
        {
            DataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
            Directory.CreateDirectory(DataDirectory);
        }

        IsShared = string.Equals(DataDirectory, shared, StringComparison.OrdinalIgnoreCase);
        if (IsShared) MigrateLegacyData();
    }

    public static string DataDirectory { get; }

    public static bool IsPortable { get; }

    /// <summary>True when the data directory is the machine-wide %ProgramData% location.</summary>
    public static bool IsShared { get; }

    public static string DatabasePath => Path.Combine(DataDirectory, "networkmonitor.db");

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public static string LogPath => Path.Combine(DataDirectory, "networkmonitor.log");

    private static bool TryPrepareDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            string probe = Path.Combine(path, ".write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Copies an existing per-user ledger (older versions used %APPDATA%) into the shared directory.
    /// Runs once: only when the shared database does not exist yet.
    /// </summary>
    private static void MigrateLegacyData()
    {
        try
        {
            if (File.Exists(DatabasePath)) return;

            string? source = FindLegacyDirectory();
            if (source is null) return;

            CopyIfExists(Path.Combine(source, "networkmonitor.db"), DatabasePath);
            CopyIfExists(Path.Combine(source, "networkmonitor.db-wal"), DatabasePath + "-wal");
            CopyIfExists(Path.Combine(source, "networkmonitor.db-shm"), DatabasePath + "-shm");

            string legacySettings = Path.Combine(source, "settings.json");
            if (File.Exists(legacySettings) && !File.Exists(SettingsPath))
                File.Copy(legacySettings, SettingsPath);

            LogSink.Write($"已迁移旧数据: {source} -> {DataDirectory}");
        }
        catch (Exception ex)
        {
            LogSink.Write("迁移旧数据失败", ex);
        }
    }

    private static string? FindLegacyDirectory()
    {
        var candidates = new List<string>();

        string local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
        if (!string.Equals(local, DataDirectory, StringComparison.OrdinalIgnoreCase))
            candidates.Add(local);

        if (IsSystemAccount())
        {
            try
            {
                foreach (string user in Directory.EnumerateDirectories(@"C:\Users"))
                {
                    candidates.Add(Path.Combine(user, "AppData", "Roaming", AppName));
                }
            }
            catch
            {
                // ignore enumeration failures
            }
        }

        return candidates
            .Where(dir => File.Exists(Path.Combine(dir, "networkmonitor.db")))
            .OrderByDescending(dir => File.GetLastWriteTimeUtc(Path.Combine(dir, "networkmonitor.db")))
            .FirstOrDefault();
    }

    private static bool IsSystemAccount() =>
        string.Equals(Environment.UserName, "SYSTEM", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Environment.UserName, "NETWORK SERVICE", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Environment.UserName, "LOCAL SERVICE", StringComparison.OrdinalIgnoreCase);

    private static void CopyIfExists(string source, string target)
    {
        if (!File.Exists(source)) return;
        File.Copy(source, target, overwrite: true);
    }

    /// <summary>Diagnostics helper used by the log sink.</summary>
    internal static string DescribeStorageMode() =>
        IsPortable ? "portable" : IsShared ? "shared" : "user";
}
