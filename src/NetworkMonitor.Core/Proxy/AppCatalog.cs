using System.Collections.Concurrent;
using System.Diagnostics;

namespace NetworkMonitor.Core.Proxy;

public sealed record AppIdentity(string DisplayName, string Category);

/// <summary>
/// Turns raw process names into a friendly application name + category.
/// Known apps get a curated name; unknown ones fall back to the file description
/// and a keyword-based category guess.
/// </summary>
public static class AppCatalog
{
    public static readonly string[] CategoryOrder =
    [
        "浏览器", "开发", "游戏", "社交", "媒体", "下载", "AI", "系统", "其他",
    ];

    private static readonly ConcurrentDictionary<string, AppIdentity> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, AppIdentity> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = new("Google Chrome", "浏览器"),
        ["msedge"] = new("Microsoft Edge", "浏览器"),
        ["firefox"] = new("Firefox", "浏览器"),
        ["brave"] = new("Brave", "浏览器"),
        ["opera"] = new("Opera", "浏览器"),
        ["vivaldi"] = new("Vivaldi", "浏览器"),
        ["code"] = new("Visual Studio Code", "开发"),
        ["devenv"] = new("Visual Studio", "开发"),
        ["rider64"] = new("JetBrains Rider", "开发"),
        ["idea64"] = new("IntelliJ IDEA", "开发"),
        ["pycharm64"] = new("PyCharm", "开发"),
        ["webstorm64"] = new("WebStorm", "开发"),
        ["clion64"] = new("CLion", "开发"),
        ["goland64"] = new("GoLand", "开发"),
        ["sublime_text"] = new("Sublime Text", "开发"),
        ["wezterm-gui"] = new("WezTerm", "开发"),
        ["windowsterminal"] = new("Windows Terminal", "开发"),
        ["cursor"] = new("Cursor", "AI"),
        ["node"] = new("Node.js", "开发"),
        ["python"] = new("Python", "开发"),
        ["python3"] = new("Python", "开发"),
        ["java"] = new("Java", "开发"),
        ["javaw"] = new("Java", "开发"),
        ["pwsh"] = new("PowerShell", "开发"),
        ["powershell"] = new("PowerShell", "开发"),
        ["git"] = new("Git", "开发"),
        ["chatgpt"] = new("ChatGPT", "AI"),
        ["claude"] = new("Claude", "AI"),
        ["steam"] = new("Steam", "游戏"),
        ["wegame"] = new("WeGame", "游戏"),
        ["epicgameslauncher"] = new("Epic Games", "游戏"),
        ["battle.net"] = new("Battle.net", "游戏"),
        ["qq"] = new("QQ", "社交"),
        ["wechat"] = new("微信", "社交"),
        ["weixin"] = new("微信", "社交"),
        ["telegram"] = new("Telegram", "社交"),
        ["discord"] = new("Discord", "社交"),
        ["dingtalk"] = new("钉钉", "社交"),
        ["feishu"] = new("飞书", "社交"),
        ["spotify"] = new("Spotify", "媒体"),
        ["splayer"] = new("SPlayer", "媒体"),
        ["vlc"] = new("VLC", "媒体"),
        ["potplayermini64"] = new("PotPlayer", "媒体"),
        ["mpv"] = new("mpv", "媒体"),
        ["bilibili"] = new("哔哩哔哩", "媒体"),
        ["thunder"] = new("迅雷", "下载"),
        ["qbittorrent"] = new("qBittorrent", "下载"),
        ["idman"] = new("IDM", "下载"),
        ["aria2c"] = new("aria2", "下载"),
        ["svchost"] = new("Windows 服务", "系统"),
        ["explorer"] = new("Windows 资源管理器", "系统"),
        ["runtimebroker"] = new("Runtime Broker", "系统"),
        ["dllhost"] = new("COM 代理", "系统"),
        ["searchhost"] = new("Windows 搜索", "系统"),
        ["sihost"] = new("Shell 基础结构", "系统"),
        ["ctfmon"] = new("输入法", "系统"),
        ["sogoucloud"] = new("搜狗输入法", "系统"),
    };

    public static AppIdentity Resolve(string process, string? path = null)
    {
        string key = process.Trim();
        if (key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            key = key[..^4];

        string cacheKey = key + "|" + (path ?? string.Empty);
        if (Cache.TryGetValue(cacheKey, out var cached)) return cached;

        AppIdentity identity;
        if (Known.TryGetValue(key, out var known))
        {
            identity = known;
        }
        else
        {
            identity = new AppIdentity(
                ResolveDisplayName(key, path),
                GuessCategory(key, path));
        }

        if (Cache.Count < 2048) Cache[cacheKey] = identity;
        return identity;
    }

    private static string ResolveDisplayName(string key, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                string? description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(description))
                {
                    description = description.Trim();
                    if (description.Length is > 0 and <= 48
                        && !description.Contains("Microsoft® Windows®", StringComparison.OrdinalIgnoreCase)
                        && !description.StartsWith("Windows ", StringComparison.OrdinalIgnoreCase))
                    {
                        return description;
                    }
                }
            }
            catch
            {
                // missing / inaccessible file — use the process name
            }
        }

        return key;
    }

    private static string GuessCategory(string key, string? path)
    {
        string text = (key + " " + (path ?? string.Empty)).ToLowerInvariant();

        if (ContainsAny(text, "chrome", "edge", "firefox", "brave", "opera", "vivaldi", "browser"))
            return "浏览器";

        if (ContainsAny(text, "code", "devenv", "rider", "idea", "pycharm", "webstorm", "clion",
                "goland", "sublime", "terminal", "wezterm", "git", "node", "python", "java",
                "docker", "postman", "fiddler", "powershell"))
            return "开发";

        if (ContainsAny(text, "steam", "epic games", "wegame", "game", "riot", "battle.net",
                "ubisoft", "genshin", "starrail", "minecraft"))
            return "游戏";

        if (ContainsAny(text, "qq", "wechat", "weixin", "telegram", "discord", "dingtalk",
                "feishu", "slack", "whatsapp", "line"))
            return "社交";

        if (ContainsAny(text, "spotify", "vlc", "potplayer", "mpv", "music", "video",
                "netflix", "bilibili", "splayer"))
            return "媒体";

        if (ContainsAny(text, "thunder", "qbittorrent", "transmission", "idman", "aria2", "download"))
            return "下载";

        if (ContainsAny(text, "chatgpt", "claude", "cursor"))
            return "AI";

        if (ContainsAny(text, "svchost", "system", "explorer", "runtimebroker", "dllhost",
                "searchhost", "sihost", "ctfmon", "sogou", "input", "driver"))
            return "系统";

        return "其他";
    }

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.Ordinal));
}
