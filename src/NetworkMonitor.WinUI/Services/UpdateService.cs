using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetworkMonitor.WinUI.Services;

public enum UpdateCheckStatus
{
    NotConfigured,
    UpToDate,
    Available,
    Failed,
}

public enum UpdateKind
{
    /// <summary>Full Inno Setup installer, always available and always the fallback.</summary>
    Installer,

    /// <summary>Archive holding only the files that changed since the installed version.</summary>
    Patch,
}

public sealed record UpdateInfo(
    string Version,
    string Tag,
    string Notes,
    string DownloadUrl,
    string? Sha256,
    long Size,
    UpdateKind Kind = UpdateKind.Installer,
    string? FallbackUrl = null,
    string? FallbackSha256 = null,
    long FallbackSize = 0);

public sealed record UpdateCheckResult(UpdateCheckStatus Status, string Message, UpdateInfo? Info = null);

/// <summary>A downloaded update that is ready to be applied on restart.</summary>
public sealed record PreparedUpdate(UpdateKind Kind, string Path, string Version);

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

            string? installerUrl = null;
            string? installerSha = null;
            long installerSize = 0;

            string? patchUrl = null;
            string? patchSha = null;
            long patchSize = 0;

            // The release publishes one patch per supported previous version, so a client that
            // skipped a release simply finds no patch for its version and uses the installer.
            string patchSuffix = $"patch-{CurrentVersion}-to-{versionText}.zip";

            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
                    string? downloadUrl = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;
                    long size = asset.TryGetProperty("size", out var sizeElement) ? sizeElement.GetInt64() : 0;
                    string? sha256 = null;

                    if (asset.TryGetProperty("digest", out var digestElement))
                    {
                        string? digest = digestElement.GetString();
                        if (digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true)
                            sha256 = digest["sha256:".Length..];
                    }

                    if (name.EndsWith(patchSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        patchUrl = downloadUrl;
                        patchSha = sha256;
                        patchSize = size;
                    }
                    else if (installerUrl is null
                             && name.Contains("Setup", StringComparison.OrdinalIgnoreCase)
                             && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        installerUrl = downloadUrl;
                        installerSha = sha256;
                        installerSize = size;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(installerUrl))
                return new UpdateCheckResult(UpdateCheckStatus.Failed, $"v{versionText} 没有找到安装包");

            bool usePatch = !string.IsNullOrWhiteSpace(patchUrl) && patchSize > 0;

            return new UpdateCheckResult(
                UpdateCheckStatus.Available,
                $"发现新版本 v{versionText}",
                new UpdateInfo(
                    versionText,
                    tag,
                    notes,
                    usePatch ? patchUrl! : installerUrl!,
                    usePatch ? patchSha : installerSha,
                    usePatch ? patchSize : installerSize,
                    usePatch ? UpdateKind.Patch : UpdateKind.Installer,
                    installerUrl,
                    installerSha,
                    installerSize));
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, "检查更新失败：" + ex.Message);
        }
    }

    /// <summary>
    /// Downloads the update and, for a patch, extracts and verifies it. Any patch problem
    /// silently falls back to the full installer so the update never gets stuck.
    /// </summary>
    public async Task<PreparedUpdate> PrepareAsync(UpdateInfo info, IProgress<double>? progress, IProgress<string>? status, CancellationToken ct)
    {
        if (info.Kind == UpdateKind.Patch)
        {
            try
            {
                return await DownloadAndVerifyPatchAsync(info, progress, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                status?.Report("增量更新不可用，改为下载完整安装包（" + ex.Message + "）");
                info = info with
                {
                    Kind = UpdateKind.Installer,
                    DownloadUrl = info.FallbackUrl ?? info.DownloadUrl,
                    Sha256 = info.FallbackSha256 ?? info.Sha256,
                    Size = info.FallbackSize > 0 ? info.FallbackSize : info.Size,
                };
            }
        }

        string installer = await DownloadFileAsync(info.DownloadUrl, info.Sha256, info.Size, $"NetworkMonitor-Setup-{info.Version}.exe", progress, ct);
        return new PreparedUpdate(UpdateKind.Installer, installer, info.Version);
    }

    private async Task<PreparedUpdate> DownloadAndVerifyPatchAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        string archive = await DownloadFileAsync(info.DownloadUrl, info.Sha256, info.Size, $"NetworkMonitor-patch-to-{info.Version}.zip", progress, ct);

        string stage = Path.Combine(Path.GetTempPath(), "NetworkMonitor", "update", $"patch-{info.Version}");
        if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        ZipFile.ExtractToDirectory(archive, stage);

        string metaPath = Path.Combine(stage, "_patch.json");
        if (!File.Exists(metaPath))
            throw new InvalidOperationException("增量包缺少清单");

        PatchManifest manifest;
        await using (var stream = File.OpenRead(metaPath))
        {
            manifest = await JsonSerializer.DeserializeAsync<PatchManifest>(stream, JsonOptions, ct)
                       ?? throw new InvalidOperationException("增量包清单无效");
        }

        if (!string.Equals(manifest.From, CurrentVersion, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"增量包版本不匹配（{manifest.From}）");

        foreach (var file in manifest.Files)
        {
            string target = ResolveInside(stage, file.Path);
            if (!File.Exists(target))
                throw new InvalidOperationException("增量包缺少文件：" + file.Path);

            await using var stream = File.OpenRead(target);
            byte[] hash = await SHA256.HashDataAsync(stream, ct);
            if (!Convert.ToHexString(hash).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("增量包文件校验失败：" + file.Path);
        }

        foreach (string path in manifest.Delete)
            _ = ResolveInside(stage, path);

        return new PreparedUpdate(UpdateKind.Patch, stage, info.Version);
    }

    private static string ResolveInside(string root, string relative)
    {
        string full = Path.GetFullPath(Path.Combine(root, relative));
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("增量包包含非法路径：" + relative);

        return full;
    }

    private async Task<string> DownloadFileAsync(string url, string? sha256, long expectedSize, string fileName, IProgress<double>? progress, CancellationToken ct)
    {
        string directory = Path.Combine(Path.GetTempPath(), "NetworkMonitor", "update");
        Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, fileName);
        string temporary = path + ".download";

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? expectedSize;
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

        if (!string.IsNullOrWhiteSpace(sha256))
        {
            await using var file = File.OpenRead(temporary);
            byte[] hash = await SHA256.HashDataAsync(file, ct);
            string actual = Convert.ToHexString(hash);
            if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(temporary);
                throw new InvalidOperationException("下载包校验失败（SHA256 不匹配）");
            }
        }

        File.Move(temporary, path, overwrite: true);
        return path;
    }

    /// <summary>
    /// Hands the update to a helper that runs after this process exits. Full installers are
    /// silent Inno Setup runs; patches stop the service, swap the changed files, and roll
    /// back if anything fails.
    /// </summary>
    public void ApplyAndRestart(PreparedUpdate update)
    {
        string? executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
            throw new InvalidOperationException("无法获取程序路径");

        if (update.Kind == UpdateKind.Installer)
        {
            InstallAndRestart(update.Path, executable);
            return;
        }

        ApplyPatchAndRestart(update, executable);
    }

    private static void InstallAndRestart(string installerPath, string executable)
    {
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

    private void ApplyPatchAndRestart(PreparedUpdate update, string executable)
    {
        string installDir = Path.GetDirectoryName(executable)
                            ?? throw new InvalidOperationException("无法获取安装目录");

        string work = Path.Combine(Path.GetTempPath(), "NetworkMonitor", "update");
        Directory.CreateDirectory(work);

        string script = Path.Combine(work, $"apply-patch-{Guid.NewGuid():N}.ps1");
        string backup = Path.Combine(work, "backup", CurrentVersion);
        string log = Path.Combine(work, "apply-patch.log");

        File.WriteAllText(script, PatchScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        string arguments =
            "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden " +
            $"-File \"{script}\" " +
            $"-UiPid {Environment.ProcessId} " +
            $"-InstallDir \"{installDir}\" " +
            $"-StageDir \"{update.Path}\" " +
            $"-ExePath \"{executable}\" " +
            $"-BackupDir \"{backup}\" " +
            $"-LogPath \"{log}\"";

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = arguments,
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException("需要管理员权限才能替换后台服务文件，更新已取消");
        }
    }

    private sealed record PatchFile(string Path, string Sha256);

    private sealed record PatchManifest(string From, string To, IReadOnlyList<string> Delete, IReadOnlyList<PatchFile> Files);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Runs elevated after the UI exits: stop the service, back up and replace the changed
    /// files, restore everything if a hash does not match, then restart service and app.
    /// Written for Windows PowerShell 5.1, which is always present.
    /// </summary>
    private const string PatchScript = """
        param(
            [int]$UiPid,
            [string]$InstallDir,
            [string]$StageDir,
            [string]$ExePath,
            [string]$BackupDir,
            [string]$LogPath
        )

        $ErrorActionPreference = 'Stop'
        $serviceName = 'NetworkMonitorService'
        $updating = $false
        $serviceWasRunning = $false
        $restore = @()
        $created = @()
        $removed = @()

        function Write-Log([string]$message) {
            try {
                Add-Content -LiteralPath $LogPath -Value ((Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + '  ' + $message) -Encoding UTF8
            } catch { }
        }

        function Restart-Things {
            if ($serviceWasRunning) {
                try {
                    Start-Service -Name $serviceName -ErrorAction Stop
                    Write-Log '后台服务已启动'
                } catch {
                    Write-Log ('后台服务启动失败：' + $_.Exception.Message)
                }
            }
            try {
                Start-Process -FilePath $ExePath
            } catch {
                Write-Log ('启动主程序失败：' + $_.Exception.Message)
            }
        }

        Write-Log ('开始应用增量更新，等待主程序退出 PID=' + $UiPid)
        try { Wait-Process -Id $UiPid -Timeout 30 -ErrorAction Stop } catch { }
        Start-Sleep -Milliseconds 1200

        try {
            $meta = Get-Content -LiteralPath (Join-Path $StageDir '_patch.json') -Raw | ConvertFrom-Json
            $files = @($meta.files)
            $deletes = @($meta.delete)

            $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
            if ($null -ne $service -and $service.Status -ne 'Stopped') {
                $serviceWasRunning = $true
                Stop-Service -Name $serviceName -Force
                (Get-Service -Name $serviceName).WaitForStatus('Stopped', (New-TimeSpan -Seconds 30))
                Write-Log '后台服务已停止'
            }

            New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
            $updating = $true

            foreach ($file in $files) {
                $target = Join-Path $InstallDir $file.path
                $source = Join-Path $StageDir $file.path
                $parent = Split-Path -Parent $target
                if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }

                if (Test-Path -LiteralPath $target) {
                    $backup = Join-Path $BackupDir $file.path
                    $backupParent = Split-Path -Parent $backup
                    if ($backupParent) { New-Item -ItemType Directory -Force -Path $backupParent | Out-Null }
                    Copy-Item -LiteralPath $target -Destination $backup -Force
                    $restore += $file.path
                } else {
                    $created += $file.path
                }

                Copy-Item -LiteralPath $source -Destination $target -Force
            }

            foreach ($path in $deletes) {
                $target = Join-Path $InstallDir $path
                if (Test-Path -LiteralPath $target) {
                    $backup = Join-Path $BackupDir $path
                    $backupParent = Split-Path -Parent $backup
                    if ($backupParent) { New-Item -ItemType Directory -Force -Path $backupParent | Out-Null }
                    Move-Item -LiteralPath $target -Destination $backup -Force
                    $removed += $path
                }
            }

            foreach ($file in $files) {
                $target = Join-Path $InstallDir $file.path
                $actual = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($actual -ne $file.sha256) {
                    throw ('文件校验失败：' + $file.path)
                }
            }

            Write-Log ('更新完成，' + $files.Count + ' 个文件已替换')
        } catch {
            if (-not $updating) {
                Write-Log ('更新准备阶段失败：' + $_.Exception.Message)
            } else {
                Write-Log ('更新失败，开始回滚：' + $_.Exception.Message)
                foreach ($path in $created) {
                    Remove-Item -LiteralPath (Join-Path $InstallDir $path) -Force -ErrorAction SilentlyContinue
                }
                foreach ($path in $restore) {
                    $backup = Join-Path $BackupDir $path
                    if (Test-Path -LiteralPath $backup) {
                        Copy-Item -LiteralPath $backup -Destination (Join-Path $InstallDir $path) -Force -ErrorAction SilentlyContinue
                    }
                }
                foreach ($path in $removed) {
                    $backup = Join-Path $BackupDir $path
                    if (Test-Path -LiteralPath $backup) {
                        Copy-Item -LiteralPath $backup -Destination (Join-Path $InstallDir $path) -Force -ErrorAction SilentlyContinue
                    }
                }
                Write-Log '回滚完成'
            }
        }

        Remove-Item -LiteralPath $StageDir -Recurse -Force -ErrorAction SilentlyContinue
        Restart-Things
        """;
}
