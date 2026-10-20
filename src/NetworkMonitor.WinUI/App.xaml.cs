using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Formatting;
using NetworkMonitor.Core.Monitoring;
using NetworkMonitor.Core.Process;
using NetworkMonitor.Core.Proxy;
using NetworkMonitor.Core.Sampling;
using NetworkMonitor.Core.Service;
using NetworkMonitor.Core.Storage;
using NetworkMonitor.WinUI.Services;

namespace NetworkMonitor.WinUI;

public partial class App : Application
{
    private const string MutexName = @"Local\NetworkMonitor.WinUI.Instance.v1";
    private const string ShowEventName = @"Local\NetworkMonitor.WinUI.ShowMain.v1";
    private const string ExitEventName = @"Local\NetworkMonitor.WinUI.Exit.v1";

    private DispatcherQueue _queue = null!;
    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private EventWaitHandle? _exitEvent;
    private RegisteredWaitHandle? _exitWait;

    private RemoteBackend? _backend;
    private MainWindow? _main;
    private FloatingWindow? _floating;
    private TaskbarOverlay? _taskbar;
    private SpeedSample? _lastSample;

    public App()
    {
        InitializeComponent();

        try
        {
            RequestedTheme = ApplicationTheme.Dark;
        }
        catch
        {
            // older SDKs: the root element's RequestedTheme still applies
        }

    }

    public static App Instance => (App)Current;

    public AppSettings Settings { get; private set; } = new();

    public bool IsExiting { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _queue = DispatcherQueue.GetForCurrentThread();
        LogSink.Handler = Log.Write;

        bool exitRequested = Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--exit", StringComparison.OrdinalIgnoreCase));

        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            if (exitRequested) SignalExitExistingInstance();
            else SignalExistingInstance();

            Exit();
            return;
        }

        if (exitRequested)
        {
            Exit();
            return;
        }

        Settings = AppSettings.Load();
        ByteFormatter.UseDecimalUnits = Settings.DecimalUnits;

        Settings.LaunchAtStartup = StartupRegistration.IsEnabled();
        if (Settings.LaunchAtStartup) StartupRegistration.Set(true);

        _backend = new RemoteBackend(Settings, action => _queue.TryEnqueue(() => action()));
        _backend.Start();
        _backend.SpeedTick += OnSpeed;

        _main = new MainWindow(_backend, Settings);
        _main.Activate();

        if (Settings.ShowFloatingWindow) SetFloatingVisible(true);
        if (Settings.ShowTaskbarWindow) SetTaskbarVisible(true);

        bool startMinimized = Environment.GetCommandLineArgs()
            .Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));
        if (startMinimized) _main.AppWindow.Hide();

        if (!startMinimized && Settings.AutoCheckUpdates)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(4));
                _queue.TryEnqueue(async () =>
                {
                    try
                    {
                        if (_main is not null) await _main.CheckForUpdatesAsync(interactive: false);
                    }
                    catch (Exception ex)
                    {
                        Log.Write("自动检查更新失败", ex);
                    }
                });
            });
        }

        if (Environment.GetCommandLineArgs()
            .Any(a => string.Equals(a, "--traffic", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                ShowTrafficWindow();
            }
            catch (Exception ex)
            {
                Log.Write("打开应用流量窗口失败", ex);
            }
        }

        if (Environment.GetCommandLineArgs()
            .Any(a => string.Equals(a, "--process", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                ShowProcessWindow();
            }
            catch (Exception ex)
            {
                Log.Write("打开进程流量窗口失败", ex);
            }
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => _queue.TryEnqueue(ShowMain), null, Timeout.Infinite, executeOnlyOnce: false);

        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitEvent,
            (_, _) => _queue.TryEnqueue(ExitApplication), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    private void OnSpeed(SpeedSample sample)
    {
        _lastSample = sample;
        _floating?.SetSpeed(sample);
        _taskbar?.SetSpeed(sample);
    }

    public IProxyTrafficSource ProxyTraffic =>
        _backend ?? throw new InvalidOperationException("background service client is not ready");

    public IProcessTrafficSource ProcessTraffic =>
        _backend ?? throw new InvalidOperationException("background service client is not ready");

    public bool ServiceOnline => _backend?.IsOnline ?? false;

    public RemoteBackend? Backend => _backend;

    public void ShowTrafficWindow()
    {
        ShowMain();
        _main?.ShowTrafficTab();
    }

    public void ShowProcessWindow()
    {
        ShowMain();
        _main?.ShowProcessTab();
    }

    public void RefreshProxy() => _backend?.RefreshEndpoint();

    public void ApplyRefreshInterval(int seconds)
    {
        _backend?.SetRefreshInterval(seconds);
        _main?.SetRefreshInterval(seconds);
    }

    public void ShowMain()
    {
        if (_main is null) return;
        _main.AppWindow.Show();
        _main.Activate();
    }

    public void SetFloatingVisible(bool visible)
    {
        Settings.ShowFloatingWindow = visible;
        Settings.Save();

        if (visible)
        {
            if (_floating is null)
            {
                _floating = new FloatingWindow(Settings);
                _floating.AppWindow.Closing += (sender, args) =>
                {
                    if (IsExiting) return;
                    args.Cancel = true;
                    sender.Hide();
                    SetFloatingVisible(false);
                };
            }

            if (_lastSample is { } sample) _floating.SetSpeed(sample);
            _floating.AppWindow.Show();
            _floating.Activate();
        }
        else
        {
            _floating?.AppWindow.Hide();
        }

        _main?.SyncToggles();
    }

    public void SetTaskbarVisible(bool visible)
    {
        Settings.ShowTaskbarWindow = visible;
        Settings.Save();

        if (visible)
        {
            _taskbar ??= new TaskbarOverlay();
            if (_lastSample is { } sample) _taskbar.SetSpeed(sample);
            _taskbar.Show();
        }
        else
        {
            _taskbar?.Hide();
        }

        _main?.SyncToggles();
    }

    public void SetStartup(bool enabled)
    {
        Settings.LaunchAtStartup = enabled;
        StartupRegistration.Set(enabled);
        Settings.Save();
        _main?.SyncToggles();
    }

    public void ExitApplication()
    {
        if (IsExiting) return;
        IsExiting = true;

        try { _backend?.Dispose(); } catch (Exception ex) { Log.Write("退出时断开后台服务失败", ex); }
        try { Settings.Save(); } catch (Exception ex) { Log.Write("退出时保存设置失败", ex); }
        try { _taskbar?.Dispose(); } catch (Exception ex) { Log.Write("退出时关闭任务栏条失败", ex); }
        try { _floating?.Close(); } catch (Exception ex) { Log.Write("退出时关闭悬浮窗失败", ex); }
        try { _main?.Close(); } catch (Exception ex) { Log.Write("退出时关闭主窗口失败", ex); }

        _backend = null;
        _taskbar = null;
        _floating = null;
        _main = null;

        try { _showWait?.Unregister(null); } catch { }
        try { _showEvent?.Dispose(); } catch { }
        try { _exitWait?.Unregister(null); } catch { }
        try { _exitEvent?.Dispose(); } catch { }

        try { _mutex?.ReleaseMutex(); } catch { }
        try { _mutex?.Dispose(); } catch { }
        _mutex = null;

        try { Exit(); } catch { }

        // Last-resort guard: some WinUI/tray teardown paths can leave the process alive.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            Environment.Exit(0);
        });
    }

    /// <summary>
    /// Path of the service executable: next to the UI in installer/publish layouts, or in the
    /// sibling project's output folder when running from a development build.
    /// </summary>
    public string? FindServiceExecutable()
    {
        foreach (string direct in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "NetworkMonitor.Service.exe"),
                     Path.Combine(AppContext.BaseDirectory, "service", "NetworkMonitor.Service.exe"),
                 })
        {
            if (File.Exists(direct)) return direct;
        }

        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        for (int depth = 0; depth < 7 && dir is not null; depth++, dir = dir.Parent)
        {
            foreach (string configuration in new[] { "Release", "Debug" })
            {
                foreach (string platform in new[] { "x64", "" })
                {
                    string path = Path.Combine(dir.FullName, "NetworkMonitor.Service", "bin", platform,
                        configuration, "net10.0-windows", "NetworkMonitor.Service.exe");
                    if (File.Exists(path)) return path;
                }
            }
        }

        return null;
    }

    /// <summary>Runs the service installer elevated (UAC). Returns a user-facing status line.</summary>
    public async Task<string> InstallServiceAsync()
    {
        string? exe = FindServiceExecutable();
        if (exe is null) return "找不到 NetworkMonitor.Service.exe，请先执行 dotnet publish 或重新安装。";

        try
        {
            var info = new ProcessStartInfo(exe, "--install")
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
            };

            using var process = Process.Start(info);
            if (process is null) return "无法启动安装程序。";

            await process.WaitForExitAsync();
            return process.ExitCode == 0
                ? "后台服务已安装并启动。"
                : $"安装未完成（退出码 {process.ExitCode}），可在命令行用 --install 查看详情。";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "已取消安装（需要管理员权限）。";
        }
        catch (Exception ex)
        {
            Log.Write("安装后台服务失败", ex);
            return "安装后台服务失败：" + ex.Message;
        }
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(ShowEventName);
            handle.Set();
        }
        catch
        {
            // first instance is already gone
        }
    }

    private static void SignalExitExistingInstance()
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(ExitEventName);
            handle.Set();
        }
        catch
        {
            // first instance is already gone
        }
    }
}
