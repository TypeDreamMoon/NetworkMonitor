using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NetworkMonitor.Core.Formatting;
using NetworkMonitor.Core.Process;
using NetworkMonitor.Core.Proxy;
using Windows.UI;

namespace NetworkMonitor.WinUI.Controls;

public sealed partial class ProcessTrafficView : UserControl
{
    private static readonly SolidColorBrush PrimaryBrush = new(Color.FromArgb(255, 0xE9, 0xEF, 0xF8));
    private static readonly SolidColorBrush SecondaryBrush = new(Color.FromArgb(255, 0xA9, 0xB6, 0xCA));
    private static readonly SolidColorBrush MutedBrush = new(Color.FromArgb(255, 0x7C, 0x88, 0x99));
    private static readonly SolidColorBrush DownBrush = new(Color.FromArgb(255, 0x3E, 0xDC, 0xC4));

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);

    private IProcessTrafficSource? _service;
    private ProcessScope _scope = ProcessScope.Today;
    private ProcessTrafficSnapshot? _session;
    private bool _refreshing;
    private bool _syncingToggle;

    public ProcessTrafficView()
    {
        InitializeComponent();

        _timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += (_, _) =>
        {
            _timer.Start();
            _ = RefreshAsync();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    public void SetRefreshInterval(int seconds) =>
        _timer.Interval = TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 10));

    public void Attach(IProcessTrafficSource service)
    {
        if (_service is not null) _service.ProcessSnapshotUpdated -= OnSnapshot;

        _service = service;
        _service.ProcessSnapshotUpdated += OnSnapshot;

        if (IsLoaded) _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_service is null || _refreshing) return;
        _refreshing = true;

        try
        {
            var report = await _service.BuildReportAsync(_scope);
            Render(report);
        }
        catch (Exception ex)
        {
            StatusText.Text = "读取失败：" + ex.Message;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void OnSnapshot(ProcessTrafficSnapshot snapshot)
    {
        if (DispatcherQueue.HasThreadAccess)
            ApplySession(snapshot);
        else
            DispatcherQueue.TryEnqueue(() => ApplySession(snapshot));
    }

    private void ApplySession(ProcessTrafficSnapshot snapshot)
    {
        _session = snapshot;
        SessionText.Text = ByteFormatter.Size(snapshot.SessionRx + snapshot.SessionTx);
        SessionSplit.Text = $"↓ {ByteFormatter.Size(snapshot.SessionRx)}   ↑ {ByteFormatter.Size(snapshot.SessionTx)}";
    }

    private void Render(ProcessUsageReport report)
    {
        if (_service is null) return;

        bool capturing = _service.IsCapturing;
        StatusText.Text = _service.CaptureStatus;
        StatusText.Foreground = capturing ? DownBrush : SecondaryBrush;

        _syncingToggle = true;
        CaptureToggle.IsOn = capturing;
        _syncingToggle = false;

        if (!capturing)
        {
            EmptyHint.Visibility = Visibility.Visible;
            EmptyHint.Text = "进程流量采集尚未运行。它需要管理员权限启动系统内核 ETW 会话——把程序以管理员身份运行，或确认后台服务已安装并处于运行状态，然后点「刷新」。";
        }
        else if (report.Apps.Count == 0)
        {
            EmptyHint.Visibility = Visibility.Visible;
            EmptyHint.Text = "采集已开启，这段时间内还没有进程产生对外流量。";
        }
        else
        {
            EmptyHint.Visibility = Visibility.Collapsed;
        }

        ScopeLabel.Text = report.Scope switch
        {
            ProcessScope.Week => "本周进程流量",
            ProcessScope.Month => "本月进程流量",
            _ => "今日进程流量",
        };

        TotalText.Text = ByteFormatter.Size(report.Rx + report.Tx);
        TotalSplit.Text = $"↓ {ByteFormatter.Size(report.Rx)}   ↑ {ByteFormatter.Size(report.Tx)}";

        if (_session is not null) ApplySession(_session);

        CountLabel.Text = report.Apps.Count > 0 ? $"· {report.Apps.Count} 个" : string.Empty;

        BuildPie(report.Categories);
        BuildApps(report.Apps);
    }

    private void BuildPie(IReadOnlyList<ProcessCategoryUsage> categories)
    {
        PieLegend.Children.Clear();

        var groups = categories
            .Where(group => group.Total > 0)
            .OrderByDescending(group => group.Total)
            .ToList();

        long total = groups.Sum(group => group.Total);
        if (total <= 0)
        {
            PieCard.Visibility = Visibility.Collapsed;
            return;
        }

        PieCard.Visibility = Visibility.Visible;

        var slices = groups
            .Select(group => new DonutSlice(
                group.Category,
                group.Total,
                IconCatalog.ForCategory(group.Category).Color))
            .ToList();

        CategoryPie.SetData(slices, ByteFormatter.Size(total), "总流量");

        var secondary = (Brush)Application.Current.Resources["TextSecondaryBrush"];
        var muted = (Brush)Application.Current.Resources["TextMutedBrush"];

        foreach (var group in groups)
        {
            var color = IconCatalog.ForCategory(group.Category).Color;

            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = new SolidColorBrush(color),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var label = new TextBlock
            {
                Text = group.Category,
                FontSize = 11.5,
                Foreground = secondary,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var amount = new TextBlock
            {
                Text = $"{ByteFormatter.Size(group.Total)}   {(double)group.Total / total:P0}",
                FontSize = 10.5,
                Foreground = muted,
                VerticalAlignment = VerticalAlignment.Center,
            };

            Grid.SetColumn(label, 1);
            Grid.SetColumn(amount, 2);
            row.Children.Add(dot);
            row.Children.Add(label);
            row.Children.Add(amount);
            PieLegend.Children.Add(row);
        }
    }

    private void BuildApps(IReadOnlyList<ProcessUsageItem> apps)
    {
        AppsPanel.Children.Clear();

        if (apps.Count == 0)
        {
            AppsPanel.Children.Add(MutedLabel("暂无数据"));
            return;
        }

        foreach (var group in apps
                     .GroupBy(app => app.Category)
                     .OrderBy(group => CategoryIndex(group.Key))
                     .ThenByDescending(group => group.Sum(app => app.Total)))
        {
            long total = group.Sum(app => app.Total);
            var visual = IconCatalog.ForCategory(group.Key);
            var color = new SolidColorBrush(visual.Color);

            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Margin = new Thickness(2, 6, 0, 2),
            };
            header.Children.Add(IconCatalog.Create(visual.Icon, 11, visual.Color));
            header.Children.Add(new TextBlock
            {
                Text = $"{group.Key} · {ByteFormatter.Size(total)}",
                FontSize = 11,
                Foreground = color,
            });

            var rows = new StackPanel { Spacing = 2 };

            foreach (var app in group.OrderByDescending(app => app.Total))
                rows.Children.Add(BuildAppRow(app));

            var expander = new Expander
            {
                Header = header,
                Content = rows,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                IsExpanded = !_collapsed.Contains(group.Key),
            };

            expander.Expanding += (_, _) => _collapsed.Remove(group.Key);
            expander.Collapsed += (_, _) => _collapsed.Add(group.Key);

            AppsPanel.Children.Add(expander);
        }
    }

    private static FrameworkElement BuildAppRow(ProcessUsageItem app)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var visual = IconCatalog.ForCategory(app.Category);

        var left = new StackPanel { Spacing = 2 };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        titleRow.Children.Add(IconCatalog.Create(visual.Icon, 12, visual.Color));
        titleRow.Children.Add(new TextBlock
        {
            Text = app.DisplayName,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = PrimaryBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        left.Children.Add(titleRow);
        left.Children.Add(new TextBlock
        {
            Text = $"{app.Category} · {app.Process} · ↓ {ByteFormatter.Size(app.Rx)}   ↑ {ByteFormatter.Size(app.Tx)}",
            FontSize = 11,
            Foreground = MutedBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        });

        var total = new TextBlock
        {
            Text = ByteFormatter.Size(app.Total),
            FontSize = 13.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = PrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(total, 1);

        grid.Children.Add(left);
        grid.Children.Add(total);
        return grid;
    }

    private static TextBlock MutedLabel(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = MutedBrush,
        Margin = new Thickness(2, 2, 0, 0),
    };

    private static int CategoryIndex(string category)
    {
        int index = Array.IndexOf(AppCatalog.CategoryOrder, category);
        return index < 0 ? int.MaxValue : index;
    }

    private void Scope_Changed(object sender, SelectionChangedEventArgs e)
    {
        _scope = ScopeSegment.SelectedIndex switch
        {
            1 => ProcessScope.Week,
            2 => ProcessScope.Month,
            _ => ProcessScope.Today,
        };

        _ = RefreshAsync();
    }

    private async void Capture_Toggled(object sender, RoutedEventArgs e)
    {
        if (_service is null || _syncingToggle) return;

        await _service.SetEnabledAsync(CaptureToggle.IsOn);
        await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
}
