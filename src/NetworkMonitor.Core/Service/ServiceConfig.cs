using System.Security.Cryptography;
using System.Text;
using NetworkMonitor.Core.Configuration;

namespace NetworkMonitor.Core.Service;

/// <summary>
/// Loopback endpoint + shared token for the background service. Both the service and the UI read
/// the same token file, so no UI-side configuration is needed after installation.
/// </summary>
public static class ServiceConfig
{
    public const string ServiceName = "NetworkMonitorService";
    public const string DisplayName = "NetworkMonitor 后台统计服务";
    public const int DefaultPort = 7788;
    public const string TokenHeader = "X-NetworkMonitor-Token";
    public const string TokenFileName = "service.token";

    public static int Port
    {
        get
        {
            string? raw = Environment.GetEnvironmentVariable("NETWORKMONITOR_SERVICE_PORT");
            return int.TryParse(raw, out int port) && port is > 0 and < 65536 ? port : DefaultPort;
        }
    }

    public static string BaseAddress => $"http://127.0.0.1:{Port}/";

    public static string TokenPath => Path.Combine(AppPaths.DataDirectory, TokenFileName);

    /// <summary>Creates the token on first use; safe to call from both processes concurrently.</summary>
    public static string EnsureToken()
    {
        string? existing = TryReadToken();
        if (!string.IsNullOrWhiteSpace(existing)) return existing;

        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TokenPath)!);
            using var stream = new FileStream(TokenPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(token);
            return token;
        }
        catch (IOException)
        {
            // Another process won the race; use its token.
            return TryReadToken() ?? token;
        }
        catch
        {
            // Unwritable data directory: fall back to an in-memory token (service and UI will not match).
            return token;
        }
    }

    public static string? TryReadToken()
    {
        try
        {
            if (!File.Exists(TokenPath)) return null;
            string text = File.ReadAllText(TokenPath).Trim().Trim('﻿').Trim();
            return text.Length == 0 ? null : text;
        }
        catch
        {
            return null;
        }
    }

    public static bool FixedTimeEquals(string? provided, string expected)
    {
        if (string.IsNullOrEmpty(provided)) return false;
        byte[] a = Encoding.UTF8.GetBytes(provided);
        byte[] b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
