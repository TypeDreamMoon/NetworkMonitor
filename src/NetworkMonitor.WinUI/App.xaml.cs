using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Formatting;
using NetworkMonitor.Core.Monitoring;
using NetworkMonitor.Core.Proxy;
using NetworkMonitor.Core.Sampling;
using NetworkMonitor.Core.Storage;
using NetworkMonitor.WinUI.Services;

namespace NetworkMonitor.WinUI;

public partial class App : Application
{
    private const string MutexName = @"Local\NetworkMonitor.WinUI.Instance.v1";
    private const string ShowEventName = @"Local\NetworkMonitor.WinUI.ShowMain.v1";

    private DispatcherQueue _queue = null!;
    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;

    private MonitoringService? _monitor;
    private ProxyTrafficService? _proxyTraffic;
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

        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            SignalExistingInstance();
            Exit();
            return;
        }

        Settings = AppSettings.Load();
        ByteFormatter.UseDecimalUnits = Settings.DecimalUnits;

        Settings.LaunchAtStartup = StartupRegistration.IsEnabled();
        if (Settings.LaunchAtStartup) StartupRegistration.Set(true);

        _monitor = new MonitoringService(Settings, action => _queue.TryEnqueue(() => action()));
        _monitor.Initialize();
        _monitor.SpeedTick += OnSpeed;

        _proxyTraffic = new ProxyTrafficService(Settings, action => _queue.TryEnqueue(() => action()));
        _proxyTraffic.Start();

        _main = new MainWindow(_monitor, Settings);
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

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => _queue.TryEnqueue(ShowMain), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    private void OnSpeed(SpeedSample sample)
    {
        _lastSample = sample;
        _floating?.SetSpeed(sample);
        _taskbar?.SetSpeed(sample);
    }

    public ProxyTrafficService ProxyTraffic =>
        _proxyTraffic ?? throw new InvalidOperationException("proxy traffic service is not ready");

    public void ShowTrafficWindow()
    {
        ShowMain();
        _main?.ShowTrafficTab();
    }

    public void RefreshProxy() => _proxyTraffic?.RefreshEndpoint();

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

        try
        {
            _monitor?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Write("退出时释放监控失败", ex);
        }

        Settings.Save();

        _proxyTraffic?.Dispose();
        _proxyTraffic = null;

        _taskbar?.Dispose();
        _taskbar = null;

        _floating?.Close();
        _floating = null;

        _main?.Close();
        _main = null;

        _showWait?.Unregister(null);
        _showEvent?.Dispose();

        try
        {
            _mutex?.ReleaseMutex();
        }
        catch
        {
            // not owned
        }

        _mutex?.Dispose();
        _mutex = null;

        Exit();
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
}
