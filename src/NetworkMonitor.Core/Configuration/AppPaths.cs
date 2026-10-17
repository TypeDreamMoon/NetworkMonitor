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
        }
        else if (File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt")))
        {
            IsPortable = true;
            DataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
        }
        else
        {
            DataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
        }

        Directory.CreateDirectory(DataDirectory);
    }

    public static string DataDirectory { get; }

    public static bool IsPortable { get; }

    public static string DatabasePath => Path.Combine(DataDirectory, "networkmonitor.db");

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public static string LogPath => Path.Combine(DataDirectory, "networkmonitor.log");
}
