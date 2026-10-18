using System.Globalization;
using System.Text.Json;

namespace NetworkMonitor.Core.Proxy;

public sealed record ClashEndpoint(Uri BaseUri, string Secret, string Source);

/// <summary>
/// Finds the Clash-compatible external controller from the config file written by the proxy client.
/// FlClash writes %APPDATA%\com.follow\clash\config.yaml; Clash Verge / CFW use other paths.
/// </summary>
public static class ClashConfigLocator
{
    public static ClashEndpoint? Detect()
    {
        var all = DetectAll();
        return all.Count > 0 ? all[0] : null;
    }

    /// <summary>All configured controllers, FlClash first, de-duplicated by address + secret.</summary>
    public static IReadOnlyList<ClashEndpoint> DetectAll()
    {
        var result = new List<ClashEndpoint>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in CandidatePaths())
        {
            try
            {
                if (!File.Exists(candidate.Path)) continue;
                var endpoint = candidate.IsFlClashPrefs
                    ? ParseFlClashPreferences(candidate.Path)
                    : Parse(candidate.Path);
                if (endpoint is null) continue;

                string key = endpoint.BaseUri.Authority + "|" + endpoint.Secret;
                if (seen.Add(key)) result.Add(endpoint);
            }
            catch
            {
                // unreadable config — try the next candidate
            }
        }

        // Clients often pass the controller to their core without rewriting a config file.
        // Probe the well-known ports as a last resort; dead ports are filtered by the caller.
        foreach (string port in new[] { "9090", "9097", "9679" })
        {
            var fallback = BuildEndpoint($"127.0.0.1:{port}", string.Empty, $"默认 {port}");
            if (fallback is not null && seen.Add(fallback.BaseUri.Authority + "|" + fallback.Secret))
                result.Add(fallback);
        }

        return result;
    }

    public static ClashEndpoint? FromSettings(string? controller, string? secret)
    {
        if (string.IsNullOrWhiteSpace(controller)) return null;

        string raw = controller.Trim();
        if (!raw.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            raw = "http://" + raw;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return null;
        return new ClashEndpoint(uri, secret?.Trim() ?? string.Empty, "手动");
    }

    private static IEnumerable<(string Path, bool IsFlClashPrefs)> CandidatePaths()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        yield return (Path.Combine(appData, "com.follow", "clash", "config.yaml"), false); // FlClash core config
        yield return (Path.Combine(appData, "com.follow", "clash", "shared_preferences.json"), true); // FlClash UI settings
        yield return (Path.Combine(appData, "clash", "config.yaml"), false);
        yield return (Path.Combine(profile, ".config", "clash", "config.yaml"), false);
        yield return (Path.Combine(appData, "io.github.clash-verge-rev.clash-verge-rev", "config.yaml"), false);
        yield return (Path.Combine(appData, "io.github.clash-verge-rev.clash-verge-rev", "clash-verge.yaml"), false);

        // The background service runs as LocalSystem, so its own %APPDATA% is the system profile.
        // Also look at real users' profiles; every candidate is still verified against the live API.
        if (!IsSystemAccount()) yield break;

        foreach (string user in EnumerateUserProfiles())
        {
            string userAppData = Path.Combine(user, "AppData", "Roaming");
            yield return (Path.Combine(userAppData, "com.follow", "clash", "config.yaml"), false);
            yield return (Path.Combine(userAppData, "com.follow", "clash", "shared_preferences.json"), true);
            yield return (Path.Combine(userAppData, "clash", "config.yaml"), false);
            yield return (Path.Combine(user, ".config", "clash", "config.yaml"), false);
            yield return (Path.Combine(userAppData, "io.github.clash-verge-rev.clash-verge-rev", "config.yaml"), false);
            yield return (Path.Combine(userAppData, "io.github.clash-verge-rev.clash-verge-rev", "clash-verge.yaml"), false);
        }
    }

    private static bool IsSystemAccount() =>
        string.Equals(Environment.UserName, "SYSTEM", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Environment.UserName, "NETWORK SERVICE", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Environment.UserName, "LOCAL SERVICE", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> EnumerateUserProfiles()
    {
        string[] users;
        try
        {
            users = Directory.GetDirectories(Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "Users"));
        }
        catch
        {
            yield break;
        }

        foreach (string user in users)
        {
            string name = Path.GetFileName(user);
            if (name.Equals("Public", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Default User", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("All Users", StringComparison.OrdinalIgnoreCase))
                continue;

            yield return user;
        }
    }

    private static ClashEndpoint? ParseFlClashPreferences(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("flutter.config", out var raw))
            return null;

        JsonDocument? parsed = null;
        try
        {
            if (raw.ValueKind == JsonValueKind.String)
                parsed = JsonDocument.Parse(raw.GetString() ?? "{}");
            else if (raw.ValueKind == JsonValueKind.Object)
                parsed = JsonDocument.Parse(raw.GetRawText());
            else
                return null;

            if (!parsed.RootElement.TryGetProperty("patchClashConfig", out var patch))
                return null;

            string? controller = patch.TryGetProperty("external-controller", out var controllerElement)
                ? controllerElement.GetString()
                : null;
            string secret = patch.TryGetProperty("secret", out var secretElement)
                ? secretElement.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(controller)) return null;
            return BuildEndpoint(controller, secret, path);
        }
        finally
        {
            parsed?.Dispose();
        }
    }

    private static ClashEndpoint? BuildEndpoint(string controller, string secret, string source)
    {
        controller = controller.Trim();

        if (controller.StartsWith(':'))
            controller = "127.0.0.1" + controller;
        else if (controller.StartsWith("0.0.0.0:", StringComparison.Ordinal))
            controller = "127.0.0.1:" + controller["0.0.0.0:".Length..];
        else if (controller.StartsWith("[::]:", StringComparison.Ordinal))
            controller = "127.0.0.1:" + controller["[::]:".Length..];

        if (!controller.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            controller = "http://" + controller;

        return Uri.TryCreate(controller, UriKind.Absolute, out var uri)
            ? new ClashEndpoint(uri, secret, source)
            : null;
    }

    private static ClashEndpoint? Parse(string path)
    {
        string? controller = null;
        string secret = string.Empty;

        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            int colon = line.IndexOf(':');
            if (colon <= 0) continue;

            string key = line[..colon].Trim();
            string value = Unquote(line[(colon + 1)..].Trim());

            switch (key)
            {
                case "external-controller":
                    controller = value;
                    break;
                case "secret":
                    secret = value;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(controller)) return null;
        return BuildEndpoint(controller, secret, path);
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }
}
