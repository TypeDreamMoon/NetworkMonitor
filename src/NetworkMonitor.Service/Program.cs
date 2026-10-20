using System.Net;
using System.Reflection;
using Microsoft.Extensions.Hosting.WindowsServices;
using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Monitoring;
using NetworkMonitor.Core.Process;
using NetworkMonitor.Core.Proxy;
using NetworkMonitor.Core.Service;
using NetworkMonitor.Service;

// Elevation-only verbs (--install/--uninstall/--status) run before any host/DI setup,
// so they also work when SCM starts the exe with no console attached.
if (args.Any(a => string.Equals(a, "--install", StringComparison.OrdinalIgnoreCase)))
    return ServiceInstaller.Install();

if (args.Any(a => string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase)))
    return ServiceInstaller.Uninstall();

if (args.Any(a => string.Equals(a, "--status", StringComparison.OrdinalIgnoreCase)))
    return ServiceInstaller.Status();

FileLog.Attach();
DirectoryAcl.EnsureInteractiveUsersCanWrite();

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Host.UseWindowsService(options => options.ServiceName = ServiceConfig.ServiceName);
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, ServiceConfig.Port));
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);

builder.Services.AddSingleton<BackendState>();
builder.Services.AddHostedService<BackendWorker>();

var app = builder.Build();

string token = ServiceConfig.EnsureToken();
string version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.1.4";
DateTimeOffset startedAt = DateTimeOffset.Now;

app.Use(async (context, next) =>
{
    if (!context.Request.Headers.TryGetValue(ServiceConfig.TokenHeader, out var provided)
        || !ServiceConfig.FixedTimeEquals(provided.ToString(), token))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new ApiError("unauthorized"));
        return;
    }

    await next();
});

app.MapGet("/api/health", () => new ServiceHealth(
    true,
    version,
    AppPaths.DataDirectory,
    Environment.ProcessId,
    startedAt));

app.MapGet("/api/live", (BackendState state) => state.Snapshot());

app.MapGet("/api/usage", (BackendState state) => state.BuildUsage());

app.MapGet("/api/interfaces", (BackendState state) => state.Monitor.Interfaces);

app.MapGet("/api/proxy/report", async (string? scope, BackendState state) =>
{
    if (!Enum.TryParse<ProxyScope>(scope, ignoreCase: true, out var parsed)) parsed = ProxyScope.Today;
    return await state.Proxy.BuildReportAsync(parsed);
});

app.MapGet("/api/process/report", async (string? scope, BackendState state) =>
{
    if (!Enum.TryParse<ProcessScope>(scope, ignoreCase: true, out var parsed)) parsed = ProcessScope.Today;
    return await state.ProcessTraffic.BuildReportAsync(parsed);
});

app.MapPost("/api/process/enabled", (ProcessEnabledRequest request, BackendState state) =>
{
    state.EnableProcessTraffic(request.Enabled);
    return Results.NoContent();
});

app.MapPost("/api/interfaces/auto", (BackendState state) =>
{
    state.SelectInterface(null);
    return Results.NoContent();
});

app.MapPost("/api/interfaces/select", (SelectInterfaceRequest request, BackendState state) =>
{
    state.SelectInterface(request.Key);
    return Results.NoContent();
});

app.MapPost("/api/refresh-interval", (RefreshIntervalRequest request, BackendState state) =>
{
    state.Monitor.SetRefreshInterval(request.Seconds);
    return Results.NoContent();
});

app.MapPost("/api/proxy/refresh", (BackendState state) =>
{
    state.Proxy.RefreshEndpoint();
    return Results.NoContent();
});

app.MapPost("/api/proxy/config", (ProxyConfigRequest request, BackendState state) =>
{
    state.ApplyProxyConfig(request.Controller, request.Secret);
    return Results.NoContent();
});

app.MapPost("/api/proxy/multiplier", (MultiplierRequest request, BackendState state) =>
{
    state.SetMultiplier(request.Node, request.Value);
    return Results.NoContent();
});

LogSink.Write($"NetworkMonitor 后台服务监听 {ServiceConfig.BaseAddress}（数据目录 {AppPaths.DataDirectory}）");

try
{
    await app.RunAsync();
}
catch (Exception ex)
{
    LogSink.Write("后台服务异常退出", ex);
    return 1;
}

return 0;

internal static class FileLog
{
    private static readonly object Gate = new();

    public static void Attach()
    {
        LogSink.Handler = (message, exception) =>
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(AppPaths.DataDirectory);
                    File.AppendAllText(
                        AppPaths.LogPath,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{(exception is null ? "" : " " + exception)}{Environment.NewLine}");
                }
            }
            catch
            {
                // logging must never take the service down
            }
        };
    }
}

internal static class DirectoryAcl
{
    /// <summary>
    /// The shared ledger lives in %ProgramData%; make sure the interactive user can write settings.json
    /// even when the service created the folder first.
    /// </summary>
    public static void EnsureInteractiveUsersCanWrite()
    {
        try
        {
            var info = new DirectoryInfo(AppPaths.DataDirectory);
            if (!info.Exists) return;

            var security = info.GetAccessControl();
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null),
                System.Security.AccessControl.FileSystemRights.Modify,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit |
                System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None,
                System.Security.AccessControl.AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        catch
        {
            // Non-elevated console runs cannot set ACLs; the first interactive user still owns the folder.
        }
    }
}

internal sealed class BackendWorker : IHostedService
{
    private readonly BackendState _state;

    public BackendWorker(BackendState state) => _state = state;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _state.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _state.Dispose();
        return Task.CompletedTask;
    }
}
