using System.IO;
using System.Text;
using NetworkMonitor.Core.Configuration;

namespace NetworkMonitor.WinUI.Services;

internal static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message, Exception? ex = null)
    {
        try
        {
            lock (Gate)
            {
                var file = new FileInfo(AppPaths.LogPath);
                if (file.Exists && file.Length > 512 * 1024) file.Delete();

                var line = new StringBuilder()
                    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                    .Append("  ")
                    .Append(message);
                if (ex is not null) line.Append(Environment.NewLine).Append(ex);

                File.AppendAllText(AppPaths.LogPath, line.AppendLine().ToString());
            }
        }
        catch
        {
            // logging must never take the app down
        }
    }
}
