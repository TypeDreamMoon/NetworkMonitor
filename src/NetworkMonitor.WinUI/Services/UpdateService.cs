using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace NetworkMonitor.WinUI.Services;

public enum UpdateCheckStatus
{
    NotConfigured,
    UpToDate,
    Available,
    Failed,
}

public sealed record UpdateInfo(
    string Version,
    string Tag,
    string Notes,
    string DownloadUrl,
    string? Sha256,
    long Size);

public sealed record UpdateCheckResult(UpdateCheckStatus Status, string Message, UpdateInfo? Info = null);

public sealed class UpdateService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string? _owner;
    private readonly string? _repo;

    public UpdateService()
    {
        CurrentVersion = GetCurrentVersion();
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NetworkMonitor", CurrentVersion));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        string? repository = Assembly.GetEntryAssembly()?
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "RepositoryUrl")?.Value;

        if (Uri.TryCreate(repository, UriKind.Absolute, out var uri)
            && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                _owner = parts[0];
                _repo = parts[1].Replace(".git", string.Empty, StringComparison.OrdinalIgnoreCase);
            }
        }

        if (_owner is "OWNER") _owner = null;
    }

    public string CurrentVersion { get; }

    public bool IsConfigured => _owner is not null && _repo is not null;

    private static string GetCurrentVersion()
    {
        Version? version = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
        return $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            return new UpdateCheckResult(UpdateCheckStatus.NotConfigured, "未配置更新仓库（RepositoryUrl）");

        try
        {
            string url = $"https://api.github.com/repos/{_owner}/{_repo}/releases/latest";
            using var response = await _http.GetAsync(url, ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate, "暂无发布版本");

            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = document.RootElement;

            string tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() ?? string.Empty : string.Empty;
            string notes = root.TryGetProperty("body", out var bodyElement) ? bodyElement.GetString() ?? string.Empty : string.Empty;
            string versionText = tag.TrimStart('v', 'V');

            if (!Version.TryParse(versionText, out var latest) || !Version.TryParse(CurrentVersion, out var current))
                return new UpdateCheckResult(UpdateCheckStatus.Failed, $"无法解析版本号：{tag}");

            if (latest <= current)
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate, "已是最新版本");

            string? downloadUrl = null;
            string? sha256 = null;
            long size = 0;

            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
                    bool isInstaller = name.Contains("Setup", StringComparison.OrdinalIgnoreCase)
                                       && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

                    if (!isInstaller) continue;

                    downloadUrl = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;
                    size = asset.TryGetProperty("size", out var sizeElement) ? sizeElement.GetInt64() : 0;
                    if (asset.TryGetProperty("digest", out var digestElement))
                    {
                        string? digest = digestElement.GetString();
                        if (digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true)
                            sha256 = digest["sha256:".Length..];
                    }

                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(downloadUrl))
                return new UpdateCheckResult(UpdateCheckStatus.Failed, $"v{versionText} 没有找到安装包");

            return new UpdateCheckResult(
                UpdateCheckStatus.Available,
                $"发现新版本 v{versionText}",
                new UpdateInfo(versionText, tag, notes, downloadUrl, sha256, size));
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, "检查更新失败：" + ex.Message);
        }
    }

    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        string directory = Path.Combine(Path.GetTempPath(), "NetworkMonitor", "update");
        Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, $"NetworkMonitor-Setup-{info.Version}.exe");
        string temporary = path + ".download";

        using var response = await _http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? info.Size;
        long received = 0;

        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var target = File.Create(temporary))
        {
            byte[] buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                if (total > 0) progress?.Report((double)received / total);
            }
        }

        if (!string.IsNullOrWhiteSpace(info.Sha256))
        {
            await using var file = File.OpenRead(temporary);
            byte[] hash = await SHA256.HashDataAsync(file, ct);
            string actual = Convert.ToHexString(hash);
            if (!actual.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(temporary);
                throw new InvalidOperationException("安装包校验失败（SHA256 不匹配）");
            }
        }

        File.Move(temporary, path, overwrite: true);
        return path;
    }

    public void InstallAndRestart(string installerPath)
    {
        string? executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
            throw new InvalidOperationException("无法获取程序路径");

        string script = Path.Combine(Path.GetTempPath(), $"NetworkMonitor-update-{Guid.NewGuid():N}.cmd");
        string content =
            "@echo off\r\n" +
            "timeout /t 2 /nobreak >nul\r\n" +
            $"start \"\" /wait \"{installerPath}\" /SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS\r\n" +
            $"start \"\" \"{executable}\"\r\n" +
            "del \"%~f0\"\r\n";

        File.WriteAllText(script, content);

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{script}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }
}
