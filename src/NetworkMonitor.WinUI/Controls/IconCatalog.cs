using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace NetworkMonitor.WinUI.Controls;

internal static class IconCatalog
{
    public static readonly Color Down = Color.FromArgb(255, 0x3E, 0xDC, 0xC4);
    public static readonly Color Up = Color.FromArgb(255, 0xFF, 0xB4, 0x54);
    public static readonly Color Accent = Color.FromArgb(255, 0x5B, 0x9D, 0xFF);

    private static readonly Dictionary<string, SvgImageSource> Sources = new(StringComparer.Ordinal);

    private static SvgImageSource Source(string file)
    {
        if (Sources.TryGetValue(file, out var cached)) return cached;

        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", file + ".svg");
        var source = new SvgImageSource(new Uri(path));
        Sources[file] = source;
        return source;
    }

    private static string FileFor(string key) => key switch
    {
        "IconBrowser" => "browser_5B9DFF",
        "IconDev" => "dev_A78BFA",
        "IconGame" => "game_FF7A59",
        "IconSocial" => "social_34D399",
        "IconMedia" => "media_F472B6",
        "IconDownload" => "download_22D3EE",
        "IconAI" => "ai_C084FC",
        "IconSystem" => "system_94A3B8",
        "IconOther" => "other_64748B",
        "IconBilled" => "billed_FFB454",
        _ => "other_64748B",
    };

    public static Image Create(string key, double size = 12, Color? color = null)
    {
        _ = color;
        return new Image
        {
            Source = Source(FileFor(key)),
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    public static (string Icon, Color Color) ForCategory(string category) => category switch
    {
        "浏览器" => ("IconBrowser", Color.FromArgb(255, 0x5B, 0x9D, 0xFF)),
        "开发" => ("IconDev", Color.FromArgb(255, 0xA7, 0x8B, 0xFA)),
        "游戏" => ("IconGame", Color.FromArgb(255, 0xFF, 0x7A, 0x59)),
        "社交" => ("IconSocial", Color.FromArgb(255, 0x34, 0xD3, 0x99)),
        "媒体" => ("IconMedia", Color.FromArgb(255, 0xF4, 0x72, 0xB6)),
        "下载" => ("IconDownload", Color.FromArgb(255, 0x22, 0xD3, 0xEE)),
        "AI" => ("IconAI", Color.FromArgb(255, 0xC0, 0x84, 0xFC)),
        "系统" => ("IconSystem", Color.FromArgb(255, 0x94, 0xA3, 0xB8)),
        _ => ("IconOther", Color.FromArgb(255, 0x64, 0x74, 0x8B)),
    };
}
