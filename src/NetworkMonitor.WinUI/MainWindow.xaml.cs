using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Formatting;
using NetworkMonitor.Core.Monitoring;
using NetworkMonitor.Core.Proxy;
using NetworkMonitor.WinUI.Controls;
using NetworkMonitor.Core.Sampling;
using NetworkMonitor.Core.Service;
using NetworkMonitor.Core.Storage;
using NetworkMonitor.WinUI.Interop;
using NetworkMonitor.WinUI.Services;
using Windows.Graphics;

namespace NetworkMonitor.WinUI;

public sealed partial class MainWindow : Window
{
    private const int HistoryCapacity = 120;

    // Minimum window size in DIP. Also enforced by WM_GETMINMAXINFO, so a broken
    // size can never be persisted and re-used on the next launch.
    private const int MinWindowWidthDip = 560;
    private const int MinWindowHeightDip = 560;
    private const int DefaultWindowWidthDip = 640;
    private const int DefaultWindowHeightDip = 800;

    private readonly IMonitoringSource _monitor;
    private readonly AppSettings _settings;
    private readonly List<double> _downHistory = new(HistoryCapacity);
    private readonly List<double> _upHistory = new(HistoryCapacity);

    private UsageReport? _report;
    private int _rangeDays = 7;
    private Native.WindowProc? _windowProc;
    private IntPtr _previousWindowProc;
    private IntPtr _windowHandle;
    private readonly DispatcherTimer _homeTrafficTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly UpdateService _updates = new();
    private bool _checkingUpdate;
    private bool _syncingAutoUpdate;
    private bool _dragging;
    private bool _pendingTrafficPage;
    private bool _pendingProcessPage;
    private bool _syncingRefresh;
    private PageMode _page = PageMode.Home;
    private bool _navReady;

    private enum PageMode
    {
        Home,
        Traffic,
        Process,
        Settings,
    }
    private int _dragStartX;
    private int _dragStartY;
    private int _windowStartX;
    private int _windowStartY;
    private bool _syncingCombo;
    private bool _syncingToggles;

    private readonly DispatcherTimer _boundsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private SizeInt32 _initialSize;
    private PointInt32 _initialPosition;
    private bool _initialUsesOuterSize;
    private bool _boundsReady;

    public MainWindow(IMonitoringSource monitor, AppSettings settings)
    {
        InitializeComponent();

        Root.Loaded += (_, _) =>
        {
            Native.UseDarkFrame(this);
            Native.SetDarkBorder(this);
            PreventMaximize();

            // AppWindow.ResizeClient can race with the first layout pass; re-apply once
            // the window is loaded so a stale/default size can never win.
            ApplyInitialBounds();
            if (_pendingTrafficPage) Navigate(PageMode.Traffic);
            if (_pendingProcessPage) Navigate(PageMode.Process);

            // Only start persisting pane state once the first layout pass is done:
            // NavigationView reports a spurious PaneClosing while it materialises.
            _navReady = true;

        };

        AppWindow.Changed += (_, args) =>
        {
            if (args.DidSizeChange || args.DidVisibilityChange)
                PreventMaximize();

            if (args.DidPositionChange || args.DidSizeChange)
                ScheduleBoundsSave();

            if (AppWindow.Presenter is OverlappedPresenter presenter
                && presenter.State == OverlappedPresenterState.Maximized)
            {
                presenter.Restore();
            }
        };

        _boundsTimer.Tick += (_, _) =>
        {
            _boundsTimer.Stop();
            PersistBounds();
        };

        _monitor = monitor;
        _settings = settings;

        SetupWindow();
        SetupTray();

        _monitor.SpeedTick += HandleSpeed;
        _monitor.UsageRefreshed += HandleUsage;
        _monitor.InterfacesChanged += HandleInterfacesChanged;

        AppWindow.Closing += OnWindowClosing;

        RebuildInterfaceCombo();
        SyncToggles();
        UpdateServiceStatus();

        ProxyControllerBox.Text = _settings.ClashController ?? string.Empty;
        ProxySecretBox.Password = _settings.ClashSecret ?? string.Empty;
        VersionText.Text = $"当前版本 {_updates.CurrentVersion}";
        UpdateStatusText.Text = _updates.IsConfigured ? "未检查更新" : "未配置更新仓库（RepositoryUrl）";
        _syncingAutoUpdate = true;
        AutoUpdateSwitch.IsOn = _settings.AutoCheckUpdates;
        _syncingAutoUpdate = false;

        _syncingRefresh = true;
        RefreshCombo.Items.Clear();
        foreach (int seconds in new[] { 1, 2, 5 })
            RefreshCombo.Items.Add(new ComboBoxItem { Content = $"{seconds} 秒", Tag = seconds });

        _syncingToggles = true;
        PopulateFloatingCombos();
        _syncingToggles = false;
        SyncFloatingControls();
        RefreshCombo.SelectedIndex = _settings.RefreshIntervalSeconds switch
        {
            2 => 1,
            5 => 2,
            _ => 0,
        };
        _syncingRefresh = false;

        App.Instance.ProxyTraffic.SnapshotUpdated += OnProxySnapshot;

        TrafficPanel.Attach(App.Instance.ProxyTraffic);
        ProcessPanel.Attach(App.Instance.ProcessTraffic);
        SetRefreshInterval(_settings.RefreshIntervalSeconds);

        NavView.IsPaneOpen = !_settings.NavCollapsed;
        SyncNavSelection();
        _homeTrafficTimer.Tick += async (_, _) => await RefreshHomeTrafficAsync();
        _homeTrafficTimer.Start();
        _ = RefreshHomeTrafficAsync();

        if (_monitor.Latest is { } latest) HandleUsage(latest);
        else FootNote.Text = "正在读取流量台账…";
    }

    private void SetupWindow()
    {
        try
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }
        catch (Exception ex)
        {
            Log.Write("亚克力背景不可用", ex);
        }

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            // Keep the DWM border (rounded corners + shadow) but drop the system title bar
            // entirely, so there is no maximize button at all. The window stays resizable
            // by dragging its DWM edges.
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = true;
        }

        try
        {
            AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "networkmonitor.ico"));
        }
        catch (Exception ex)
        {
            Log.Write("设置窗口图标失败", ex);
        }

        IntPtr hwnd = Native.Handle(this);
        double scale = Native.GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0) scale = 1;

        int width = (int)Math.Round(DefaultWindowWidthDip * scale);
        int height = (int)Math.Round(DefaultWindowHeightDip * scale);
        bool hasSavedSize = false;

        // Stored as the outer window size in DIP, so the round trip is exact.
        if (_settings.MainWidth is double savedWidth && _settings.MainHeight is double savedHeight
            && savedWidth >= MinWindowWidthDip && savedHeight >= MinWindowHeightDip)
        {
            width = (int)Math.Round(savedWidth * scale);
            height = (int)Math.Round(savedHeight * scale);
            hasSavedSize = true;
        }

        var primary = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int x = _settings.MainLeft is double savedLeft
            ? (int)Math.Round(savedLeft)
            : primary.X + (primary.Width - width) / 2;
        int y = _settings.MainTop is double savedTop
            ? (int)Math.Round(savedTop)
            : primary.Y + (primary.Height - height) / 2;

        // Restored bounds may point at a monitor that no longer exists / changed resolution.
        var area = DisplayArea.GetFromPoint(new PointInt32(x, y), DisplayAreaFallback.Nearest).WorkArea;
        width = Math.Clamp(width, (int)Math.Round(MinWindowWidthDip * scale), area.Width);
        height = Math.Clamp(height, (int)Math.Round(MinWindowHeightDip * scale), area.Height);
        x = Math.Clamp(x, area.X, Math.Max(area.X, area.X + area.Width - width));
        y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Y + area.Height - height));

        _initialSize = new SizeInt32(width, height);
        _initialPosition = new PointInt32(x, y);
        _initialUsesOuterSize = hasSavedSize;
        ApplyInitialBounds(ready: false);
    }

    private void ApplyInitialBounds(bool ready = true)
    {
        if (_initialUsesOuterSize) AppWindow.Resize(_initialSize);
        else AppWindow.ResizeClient(_initialSize);

        AppWindow.Move(_initialPosition);
        if (ready) _boundsReady = true;
    }

    private void ScheduleBoundsSave()
    {
        if (!_boundsReady) return;
        _boundsTimer.Stop();
        _boundsTimer.Start();
    }

    private void PersistBounds()
    {
        if (!_boundsReady) return;

        try
        {
            var size = AppWindow.Size;
            // Ignore leftovers from a transient/default layout pass; anything smaller
            // than half the minimum is not a real user size.
            if (size.Width < MinWindowWidthDip / 2 || size.Height < MinWindowHeightDip / 2) return;

            double scale = Native.GetDpiForWindow(Native.Handle(this)) / 96.0;
            if (scale <= 0) scale = 1;

            _settings.MainWidth = size.Width / scale;
            _settings.MainHeight = size.Height / scale;
            _settings.MainLeft = AppWindow.Position.X;
            _settings.MainTop = AppWindow.Position.Y;
            _settings.Save();
        }
        catch (Exception ex)
        {
            Log.Write("保存窗口位置失败", ex);
        }
    }

    private void PreventMaximize()
    {
        if (_windowHandle == IntPtr.Zero)
        {
            _windowHandle = Native.Handle(this);
            if (_windowHandle != IntPtr.Zero)
            {
                _windowProc = WindowProc;
                _previousWindowProc = Native.SetWindowProc(_windowHandle, _windowProc);
            }
        }

        Native.RemoveMaximizeBox(this);
    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        const uint WmSysCommand = 0x0112;
        const uint ScMaximize = 0xF030;
        const uint WmNcLButtonDblClk = 0x00A3;
        const int HtCaption = 2;
        const uint WmGetMinMaxInfo = 0x0024;

        switch (message)
        {
            case WmSysCommand when ((uint)wParam & 0xFFF0) == ScMaximize:
                return IntPtr.Zero;

            case WmNcLButtonDblClk when (int)wParam == HtCaption:
                return IntPtr.Zero;

            case WmGetMinMaxInfo:
                ApplyTrackSize(hwnd, lParam);
                break;
        }

        return Native.CallPreviousWindowProc(_previousWindowProc, hwnd, message, wParam, lParam);
    }

    /// <summary>
    /// MaxTrackSize used to be clamped to the *current* rect. SetWindowPos honours
    /// ptMaxTrackSize, so if this message arrived before/while the startup ResizeClient
    /// ran, the window was locked to the system minimum size (the tiny 160x39 window)
    /// and, with IsResizable=false, could never be fixed. Derive the limits from the
    /// monitor work area and a fixed minimum instead.
    /// </summary>
    private static void ApplyTrackSize(IntPtr hwnd, IntPtr lParam)
    {
        double scale = Native.GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0) scale = 1;

        int minWidth = (int)Math.Round(MinWindowWidthDip * scale);
        int minHeight = (int)Math.Round(MinWindowHeightDip * scale);
        int maxWidth = minWidth;
        int maxHeight = minHeight;

        IntPtr monitor = Native.MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTOPRIMARY */);
        if (monitor != IntPtr.Zero)
        {
            var monitorInfo = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
            if (Native.GetMonitorInfo(monitor, ref monitorInfo))
            {
                var work = monitorInfo.rcWork;
                maxWidth = Math.Max(minWidth, work.Right - work.Left);
                maxHeight = Math.Max(minHeight, work.Bottom - work.Top);
            }
        }

        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        info.MinTrackSize.X = minWidth;
        info.MinTrackSize.Y = minHeight;
        info.MaxSize.X = maxWidth;
        info.MaxSize.Y = maxHeight;
        info.MaxTrackSize.X = maxWidth;
        info.MaxTrackSize.Y = maxHeight;
        Marshal.StructureToPtr(info, lParam, false);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    private void SetupTray()
    {
        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "networkmonitor.ico");
            if (File.Exists(iconPath))
                Tray.IconSource = new BitmapImage(new Uri(iconPath));
        }
        catch (Exception ex)
        {
            Log.Write("托盘图标加载失败", ex);
        }

        Tray.LeftClickCommand = new RelayCommand(() => App.Instance.ShowMain());
        Tray.DoubleClickCommand = new RelayCommand(() => App.Instance.ShowMain());
    }

    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        PersistBounds();
        if (App.Instance.IsExiting) return;
        args.Cancel = true;
        sender.Hide();
    }

    /// <summary>
    /// Stops every timer and monitor subscription so the teardown that runs while the app exits
    /// cannot reach back into a closed window.
    /// </summary>
    public void Detach()
    {
        _homeTrafficTimer.Stop();
        _boundsTimer.Stop();

        try { _monitor.SpeedTick -= HandleSpeed; } catch { }
        try { _monitor.UsageRefreshed -= HandleUsage; } catch { }
        try { _monitor.InterfacesChanged -= HandleInterfacesChanged; } catch { }
        try { App.Instance.ProxyTraffic.SnapshotUpdated -= OnProxySnapshot; } catch { }
    }

    // ---------- live speed ----------

    private void HandleSpeed(SpeedSample sample)
    {
        DownValue.Text = ByteFormatter.Rate(sample.DownBps);
        UpValue.Text = ByteFormatter.Rate(sample.UpBps);
        SessionText.Text = $"本次 ↓ {ByteFormatter.Size(sample.SessionDownBytes)}   ↑ {ByteFormatter.Size(sample.SessionUpBytes)}";

        Push(_downHistory, sample.DownBps);
        Push(_upHistory, sample.UpBps);
        DownSpark.SetValues(_downHistory.ToArray());
        UpSpark.SetValues(_upHistory.ToArray());

        StatusText.Text = !_monitor.IsOnline
            ? "后台服务未连接"
            : _monitor.SelectedKeys.Count > 0 ? "实时监控" : "无活动网卡";

        try
        {
            Tray.ToolTipText = $"NetworkMonitor  ↓ {ByteFormatter.Rate(sample.DownBps)}  ↑ {ByteFormatter.Rate(sample.UpBps)}";
        }
        catch
        {
            // shell may reject overly long tooltips; harmless
        }
    }

    private static void Push(List<double> history, double value)
    {
        history.Add(value);
        if (history.Count > HistoryCapacity) history.RemoveAt(0);
    }

    // ---------- usage ----------

    private void HandleUsage(UsageReport report)
    {
        _report = report;

        SetCard(TodayTotal, TodaySplit, report.Today);
        SetCard(WeekTotal, WeekSplit, report.Week);
        SetCard(MonthTotal, MonthSplit, report.Month);
        SetCard(TotalTotal, TotalSplit, report.Total);

        Bars.SetData(report.DailySeries.TakeLast(_rangeDays).ToList());

        string since = report.FirstTrackedDate is { } date ? $"统计自 {date:yyyy-MM-dd}" : "尚无历史数据";
        FootNote.Text = $"{since}{Environment.NewLine}{AppPaths.DataDirectory}";
    }

    private static void SetCard(TextBlock total, TextBlock split, UsageAmount amount)
    {
        total.Text = ByteFormatter.Size(amount.Total);
        split.Text = $"↓ {ByteFormatter.Size(amount.Rx)}   ↑ {ByteFormatter.Size(amount.Tx)}";
    }

    // ---------- interface picker ----------

    private void HandleInterfacesChanged() => RebuildInterfaceCombo();

    private void RebuildInterfaceCombo()
    {
        _syncingCombo = true;
        try
        {
            IfaceCombo.Items.Clear();
            IfaceCombo.Items.Add(new ComboBoxItem { Content = "自动（全部物理网卡）", Tag = string.Empty });

            int selectedIndex = 0;
            int index = 1;
            foreach (var nic in _monitor.Interfaces.Where(InterfaceFilter.IsCountable).OrderBy(n => n.DisplayName))
            {
                IfaceCombo.Items.Add(new ComboBoxItem { Content = $"{nic.DisplayName}（{nic.Kind}）", Tag = nic.Key });
                if (!_monitor.AutoSelect && _monitor.SelectedKeys.Contains(nic.Key)) selectedIndex = index;
                index++;
            }

            IfaceCombo.SelectedIndex = selectedIndex;
        }
        finally
        {
            _syncingCombo = false;
        }
    }

    private void IfaceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingCombo) return;
        if (IfaceCombo.SelectedItem is not ComboBoxItem item) return;

        string key = item.Tag as string ?? string.Empty;
        if (string.IsNullOrEmpty(key)) _monitor.SelectAutomatic();
        else _monitor.SelectInterface(key);
    }

    // ---------- chart range ----------

    private void RangeSegment_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _rangeDays = RangeSegment.SelectedIndex == 1 ? 30 : 7;
        if (_report is not null)
            Bars.SetData(_report.DailySeries.TakeLast(_rangeDays).ToList());
    }

    // ---------- toggles ----------

    private static readonly (string Label, string Hex)[] FloatingPalette =
    [
        ("青", "#3EDCC4"),
        ("蓝", "#5B9DFF"),
        ("琥珀", "#FFB454"),
        ("粉", "#F472B6"),
        ("绿", "#34D399"),
        ("紫", "#A78BFA"),
        ("白", "#E9EFF8"),
    ];

    private DispatcherTimer? _floatDebounce;

    private void PopulateFloatingCombos()
    {
        FloatScaleCombo.Items.Clear();
        foreach (var (label, value) in new (string, double)[] { ("紧凑", 0.85), ("标准", 1.0), ("大", 1.25) })
            FloatScaleCombo.Items.Add(new ComboBoxItem { Content = label, Tag = value });

        foreach (var combo in new[] { FloatDownColorCombo, FloatUpColorCombo })
        {
            combo.Items.Clear();
            foreach (var (label, hex) in FloatingPalette)
            {
                var item = new ComboBoxItem { Content = label, Tag = hex };
                item.PointerEntered += (sender, _) =>
                {
                    if (sender is ComboBoxItem entry) entry.Background = null;
                };
                combo.Items.Add(item);
            }
        }
    }

    private void SyncFloatingControls()
    {
        bool previous = _syncingToggles;
        _syncingToggles = true;
        try
        {
            FloatDownSwitch.IsOn = _settings.FloatingShowDown;
            FloatUpSwitch.IsOn = _settings.FloatingShowUp;
            FloatSessionSwitch.IsOn = _settings.FloatingShowSession;
            FloatAcrylicSwitch.IsOn = _settings.FloatingAcrylic;
            FloatClickThroughSwitch.IsOn = _settings.FloatingClickThrough;
            FloatLockSwitch.IsOn = _settings.FloatingLocked;
            FloatSnapSwitch.IsOn = _settings.FloatingSnapEdges;

            FloatOpacitySlider.Value = Math.Clamp(_settings.FloatingOpacity * 100, 30, 100);
            FloatOpacitySlider.Minimum = 30;

            SelectFloatingItem(FloatScaleCombo, Math.Round(_settings.FloatingScale, 2));
            SelectFloatingItem(FloatDownColorCombo, _settings.FloatingDownColor.ToUpperInvariant());
            SelectFloatingItem(FloatUpColorCombo, _settings.FloatingUpColor.ToUpperInvariant());
        }
        finally
        {
            _syncingToggles = previous;
        }
    }

    private static void SelectFloatingItem(ComboBox combo, object value)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (Equals(item.Tag, value))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        combo.SelectedIndex = 0;
    }

    private void FloatingToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingToggles || _settings is null || sender is not ToggleSwitch toggle) return;

        bool on = toggle.IsOn;
        switch (toggle.Tag as string)
        {
            case "down": _settings.FloatingShowDown = on; break;
            case "up": _settings.FloatingShowUp = on; break;
            case "session": _settings.FloatingShowSession = on; break;
            case "acrylic": _settings.FloatingAcrylic = on; break;
            case "clickthrough": _settings.FloatingClickThrough = on; break;
            case "lock": _settings.FloatingLocked = on; break;
            case "snap": _settings.FloatingSnapEdges = on; break;
            default: return;
        }

        _settings.Save();
        App.Instance.ApplyFloatingSettings();
    }

    private void FloatingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingToggles || _settings is null || sender is not ComboBox combo || combo.SelectedItem is not ComboBoxItem item) return;

        switch (combo.Tag as string)
        {
            case "scale" when item.Tag is double scale:
                _settings.FloatingScale = scale;
                break;
            case "downcolor" when item.Tag is string down:
                _settings.FloatingDownColor = down;
                break;
            case "upcolor" when item.Tag is string up:
                _settings.FloatingUpColor = up;
                break;
            default:
                return;
        }

        _settings.Save();
        App.Instance.ApplyFloatingSettings();
    }

    private void FloatingOpacity_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingToggles || _settings is null) return;

        _settings.FloatingOpacity = Math.Clamp(Math.Round(FloatOpacitySlider.Value), 30, 100) / 100.0;
        ScheduleFloatingApply();
    }

    // A slider drag fires many changes per second; coalesce them so resizing stays smooth.
    private void ScheduleFloatingApply()
    {
        if (_floatDebounce is null)
        {
            _floatDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _floatDebounce.Tick += (_, _) =>
            {
                _floatDebounce?.Stop();
                _settings.Save();
                App.Instance.ApplyFloatingSettings();
            };
        }

        _floatDebounce.Stop();
        _floatDebounce.Start();
    }

    public void SyncToggles()
    {
        _syncingToggles = true;
        try
        {
            FloatingSwitch.IsOn = _settings.ShowFloatingWindow;
            TaskbarSwitch.IsOn = _settings.ShowTaskbarWindow;
            StartupSwitch.IsOn = StartupRegistration.IsEnabled();

            TrayFloating.IsChecked = _settings.ShowFloatingWindow;
            TrayTaskbar.IsChecked = _settings.ShowTaskbarWindow;
            TrayStartup.IsChecked = StartupRegistration.IsEnabled();

            SyncFloatingControls();
        }
        finally
        {
            _syncingToggles = false;
        }
    }

    private void FloatingSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingToggles) return;
        App.Instance.SetFloatingVisible(FloatingSwitch.IsOn);
    }

    private void TaskbarSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingToggles) return;
        App.Instance.SetTaskbarVisible(TaskbarSwitch.IsOn);
    }

    private void StartupSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingToggles) return;
        App.Instance.SetStartup(StartupSwitch.IsOn);
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.DataDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write("打开数据目录失败", ex);
        }
    }

    private void FloatButton_Click(object sender, RoutedEventArgs e) =>
        App.Instance.SetFloatingVisible(!_settings.ShowFloatingWindow);

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer?.Tag is string tag && Enum.TryParse<PageMode>(tag, out var page))
            Navigate(page);
    }

    private void NavView_PaneOpening(NavigationView sender, object args) => PersistPaneState(false);

    private void NavView_PaneClosing(NavigationView sender, NavigationViewPaneClosingEventArgs args) =>
        PersistPaneState(true);

    /// <summary>Remembers whether the user collapsed the navigation pane.</summary>
    private void PersistPaneState(bool collapsed)
    {
        if (!_navReady || _settings.NavCollapsed == collapsed) return;

        _settings.NavCollapsed = collapsed;
        _settings.Save();
    }

    private void SyncNavSelection()
    {        NavView.SelectedItem = _page switch
        {
            PageMode.Traffic => NavTrafficItem,
            PageMode.Process => NavProcessItem,
            PageMode.Settings => NavSettingsItem,
            _ => NavHomeItem,
        };
    }

    private void ShowTrafficPage_Click(object sender, RoutedEventArgs e) => ShowTrafficTab();

    public void ShowTrafficTab()
    {
        if (!Root.IsLoaded)
        {
            _pendingTrafficPage = true;
            return;
        }

        Navigate(PageMode.Traffic);
    }

    public void ShowProcessTab()
    {
        if (!Root.IsLoaded)
        {
            _pendingProcessPage = true;
            return;
        }

        Navigate(PageMode.Process);
    }

    private void Navigate(PageMode page)
    {
        _pendingTrafficPage = false;
        _pendingProcessPage = false;

        _page = page;

        HomeScroll.Visibility = page == PageMode.Home ? Visibility.Visible : Visibility.Collapsed;
        TrafficScroll.Visibility = page == PageMode.Traffic ? Visibility.Visible : Visibility.Collapsed;
        ProcessScroll.Visibility = page == PageMode.Process ? Visibility.Visible : Visibility.Collapsed;
        SettingsScroll.Visibility = page == PageMode.Settings ? Visibility.Visible : Visibility.Collapsed;

        SyncNavSelection();

        switch (page)
        {
            case PageMode.Traffic:
                TrafficScroll.ChangeView(null, 0, null);
                _ = TrafficPanel.RefreshAsync();
                break;
            case PageMode.Process:
                ProcessScroll.ChangeView(null, 0, null);
                _ = ProcessPanel.RefreshAsync();
                break;
            case PageMode.Settings:
                SettingsScroll.ChangeView(null, 0, null);
                UpdateServiceStatus();
                break;
            default:
                _ = RefreshHomeTrafficAsync();
                break;
        }
    }

    private void RefreshCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingRefresh || RefreshCombo.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is not int seconds) return;

        _settings.RefreshIntervalSeconds = seconds;
        _settings.Save();
        ((App)Application.Current).ApplyRefreshInterval(seconds);
    }

    public void SetRefreshInterval(int seconds)
    {
        seconds = Math.Clamp(seconds, 1, 10);
        _homeTrafficTimer.Interval = TimeSpan.FromSeconds(seconds);
        TrafficPanel.SetRefreshInterval(seconds);
        ProcessPanel.SetRefreshInterval(seconds);
    }

    private async Task RefreshHomeTrafficAsync()
    {
        if (_page != PageMode.Home) return;

        try
        {
            var service = App.Instance.ProxyTraffic;
            var report = await service.BuildReportAsync(ProxyScope.Today);

            HomeTrafficList.Children.Clear();

            HomeNodeList.Children.Clear();

            if (!service.Connected)
            {
                HomeTrafficSummary.Text = "未连接代理控制器（FlClash / Clash Verge Rev 需开启 External controller）";
                return;
            }

            HomeTrafficSummary.Inlines.Clear();
            HomeTrafficSummary.Inlines.Add(new Run
            {
                Text = $"实际 ↓ {ByteFormatter.Size(report.Rx)}   ↑ {ByteFormatter.Size(report.Tx)}",
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(IconCatalog.Down),
            });
            HomeTrafficSummary.Inlines.Add(new Run
            {
                Text = $"   ·   计费 {ByteFormatter.Size(report.BilledRx + report.BilledTx)}   ·   {report.Apps.Count} 个应用",
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(IconCatalog.Up),
            });

            foreach (var app in report.Apps.Take(4))
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var visual = IconCatalog.ForCategory(app.Category);
                var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                nameRow.Children.Add(IconCatalog.Create(visual.Icon, 11, visual.Color));
                nameRow.Children.Add(new TextBlock
                {
                    Text = $"{app.DisplayName}  ·  {app.Category}",
                    FontSize = 12,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextSecondaryBrush"],
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });

                string amount = app.BilledTotal != app.Total
                    ? $"{ByteFormatter.Size(app.Total)}  /  计费 {ByteFormatter.Size(app.BilledTotal)}"
                    : ByteFormatter.Size(app.Total);

                var total = new TextBlock
                {
                    Text = amount,
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextPrimaryBrush"],
                };
                Grid.SetColumn(total, 1);
                row.Children.Add(nameRow);
                row.Children.Add(total);
                HomeTrafficList.Children.Add(row);
            }

            foreach (var node in report.Nodes.Take(3))
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var label = node.Multiplier != 1
                    ? $"{node.Node}   ×{node.Multiplier:0.##}"
                    : node.Node;

                var name = new TextBlock
                {
                    Text = label,
                    FontSize = 12,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextSecondaryBrush"],
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };

                string amount = node.BilledTotal != node.Total
                    ? $"{ByteFormatter.Size(node.Total)}  /  计费 {ByteFormatter.Size(node.BilledTotal)}"
                    : ByteFormatter.Size(node.Total);

                var total = new TextBlock
                {
                    Text = amount,
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextPrimaryBrush"],
                };
                Grid.SetColumn(total, 1);
                row.Children.Add(name);
                row.Children.Add(total);
                HomeNodeList.Children.Add(row);
            }
        }
        catch
        {
            // keep the previous summary on transient failures
        }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        => await CheckForUpdatesAsync(interactive: true);

    private void AutoUpdate_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingAutoUpdate) return;

        _settings.AutoCheckUpdates = AutoUpdateSwitch.IsOn;
        _settings.Save();
    }

    public async Task CheckForUpdatesAsync(bool interactive)
    {
        if (_checkingUpdate) return;
        _checkingUpdate = true;

        try
        {
            UpdateStatusText.Text = "正在检查更新…";
            var result = await _updates.CheckAsync();
            UpdateStatusText.Text = result.Message;

            switch (result.Status)
            {
                case UpdateCheckStatus.NotConfigured when interactive:
                case UpdateCheckStatus.Failed when interactive:
                    await ShowMessageAsync("更新", result.Message);
                    break;
                case UpdateCheckStatus.UpToDate when interactive:
                    await ShowMessageAsync("更新", result.Message);
                    break;
                case UpdateCheckStatus.Available when result.Info is { } info:
                    if (AppPaths.IsPortable)
                    {
                        if (interactive)
                            await ShowMessageAsync("发现新版本", "便携版请从 GitHub Releases 下载新的 zip 手动更新。");
                        return;
                    }

                    if (!interactive && _settings.SkippedUpdateVersion == info.Version) return;
                    await ShowUpdateDialogAsync(info);
                    break;
            }
        }
        finally
        {
            _checkingUpdate = false;
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "知道了",
            XamlRoot = Root.XamlRoot,
        };

        await dialog.ShowAsync();
    }

    private async Task ShowUpdateDialogAsync(UpdateInfo info)
    {
        string notes = string.IsNullOrWhiteSpace(info.Notes) ? "这个版本没有提供更新说明。" : info.Notes;

        var size = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = info.Kind == UpdateKind.Patch
                ? $"增量更新：只需下载 {ByteFormatter.Size(info.Size)}（完整安装包 {ByteFormatter.Size(info.FallbackSize)}）"
                : $"完整安装包：{ByteFormatter.Size(info.Size)}",
        };

        if (Application.Current.Resources.TryGetValue("TextSecondaryBrush", out object? secondary) && secondary is Brush brush)
        {
            size.Foreground = brush;
        }

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(size);
        content.Children.Add(new ScrollViewer
        {
            MaxHeight = 280,
            Content = new TextBlock { Text = notes, TextWrapping = TextWrapping.Wrap },
        });

        var dialog = new ContentDialog
        {
            Title = $"发现新版本 v{info.Version}",
            Content = content,
            PrimaryButtonText = "下载并安装",
            CloseButtonText = "稍后",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            await DownloadAndInstallAsync(info);
        }
        else
        {
            _settings.SkippedUpdateVersion = info.Version;
            _settings.Save();
            UpdateStatusText.Text = $"已跳过 v{info.Version}";
        }
    }

    private async Task DownloadAndInstallAsync(UpdateInfo info)
    {
        using var cancellation = new CancellationTokenSource();

        string prefix = info.Kind == UpdateKind.Patch ? "正在下载增量更新" : "正在下载更新";

        var progress = new ProgressBar { Minimum = 0, Maximum = 1, Width = 320 };
        var text = new TextBlock { Text = prefix + "…", TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(text);
        panel.Children.Add(progress);

        var dialog = new ContentDialog
        {
            Title = "下载更新",
            Content = panel,
            CloseButtonText = "取消",
            XamlRoot = Root.XamlRoot,
        };
        dialog.CloseButtonClick += (_, _) => cancellation.Cancel();

        _ = dialog.ShowAsync();

        try
        {
            var reporter = new Progress<double>(value => DispatcherQueue.TryEnqueue(() =>
            {
                progress.Value = value;
                text.Text = $"{prefix}… {value:P0}";
            }));

            var status = new Progress<string>(message => DispatcherQueue.TryEnqueue(() =>
            {
                text.Text = message;
                progress.Value = 0;
            }));

            PreparedUpdate prepared = await _updates.PrepareAsync(info, reporter, status, cancellation.Token);

            try { dialog.Hide(); } catch { /* already closed */ }
            UpdateStatusText.Text = prepared.Kind == UpdateKind.Patch
                ? "增量包准备完成，正在应用…"
                : "下载完成，正在安装…";

            _updates.ApplyAndRestart(prepared);
            ((App)Application.Current).ExitApplication();
        }
        catch (OperationCanceledException)
        {
            try { dialog.Hide(); } catch { /* already closed */ }
            UpdateStatusText.Text = "已取消更新";
        }
        catch (Exception ex)
        {
            try { dialog.Hide(); } catch { /* already closed */ }
            UpdateStatusText.Text = "更新失败：" + ex.Message;
        }
    }

    private void OnProxySnapshot(ProxyTrafficSnapshot snapshot)
    {
        ProxyStatusText.Text = snapshot.Connected
            ? $"已连接 {snapshot.Endpoint}"
            : snapshot.Status;
    }

    private void ProxySave_Click(object sender, RoutedEventArgs e)
    {
        string? controller = ProxyControllerBox.Text.Trim();
        string? secret = string.IsNullOrEmpty(ProxySecretBox.Password) ? null : ProxySecretBox.Password;

        App.Instance.Backend?.ApplyProxyConfig(
            string.IsNullOrEmpty(controller) ? null : controller, secret);
        ProxyStatusText.Text = "已保存，等待连接…";
    }

    private void ProxyRecheck_Click(object sender, RoutedEventArgs e)
    {
        App.Instance.RefreshProxy();
        ProxyStatusText.Text = "正在重新检测…";
    }

    // ---------- background service ----------

    private void RefreshServiceStatus_Click(object sender, RoutedEventArgs e) => UpdateServiceStatus();

    private async void InstallService_Click(object sender, RoutedEventArgs e)
    {
        ServiceStatusText.Text = "正在请求管理员权限…";
        ServiceStatusText.Text = await App.Instance.InstallServiceAsync();
        await Task.Delay(1200);
        UpdateServiceStatus();
    }

    private void UpdateServiceStatus()
    {
        ServiceStatusText.Text = App.Instance.ServiceOnline
            ? $"已连接 {ServiceConfig.BaseAddress}"
            : "后台服务未运行：点「安装 / 启动」后会弹出 UAC。";
    }

    // ---------- window chrome ----------

    private void TitleBar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(Content).Properties.IsLeftButtonPressed) return;

        var (x, y) = Native.CursorPosition();
        _dragging = true;
        _dragStartX = x;
        _dragStartY = y;
        _windowStartX = AppWindow.Position.X;
        _windowStartY = AppWindow.Position.Y;
        TitleBar.CapturePointer(e.Pointer);
    }

    private void TitleBar_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        var (x, y) = Native.CursorPosition();
        AppWindow.Move(new Windows.Graphics.PointInt32(
            _windowStartX + (x - _dragStartX),
            _windowStartY + (y - _dragStartY)));
    }

    private void TitleBar_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        TitleBar.ReleasePointerCapture(e.Pointer);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Minimize();
    }

    private void Hide_Click(object sender, RoutedEventArgs e) => AppWindow.Hide();

    private void ExitApp_Click(object sender, RoutedEventArgs e) => App.Instance.ExitApplication();

    // ---------- tray menu ----------

    private void TrayShow_Click(object sender, RoutedEventArgs e) => App.Instance.ShowMain();

    private void TrayFloating_Click(object sender, RoutedEventArgs e) => App.Instance.SetFloatingVisible(!_settings.ShowFloatingWindow);

    private void TrayTaskbar_Click(object sender, RoutedEventArgs e) => App.Instance.SetTaskbarVisible(!_settings.ShowTaskbarWindow);

    private void TrayStartup_Click(object sender, RoutedEventArgs e) => App.Instance.SetStartup(!_settings.LaunchAtStartup);

    private void TrayExit_Click(object sender, RoutedEventArgs e)
    {
        // Let the tray menu click finish before disposing the tray icon.
        DispatcherQueue.TryEnqueue(() => App.Instance.ExitApplication());
    }
}
