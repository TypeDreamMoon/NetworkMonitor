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
    private readonly Dictionary<string, GroupVisual> _groupVisuals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RowVisual> _rowVisuals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LegendVisual> _legendVisuals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _rowOrder = new(StringComparer.Ordinal);
    private readonly List<string> _legendOrder = [];
    private TextBlock? _emptyLabel;

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

        // 图例增量更新：只重建真正变化的行，避免每次刷新整块闪一下。
        var current = groups.Select(group => group.Category).ToList();
        if (!SameKeys(current, _legendOrder))
        {
            foreach (var stale in _legendVisuals.Keys.Where(key => !current.Contains(key)).ToList())
            {
                PieLegend.Children.Remove(_legendVisuals[stale].Root);
                _legendVisuals.Remove(stale);
            }

            _legendOrder.Clear();
            _legendOrder.AddRange(current);
        }

        foreach (var group in groups)
        {
            if (!_legendVisuals.TryGetValue(group.Category, out var legend))
            {
                legend = CreateLegendRow(group.Category);
                _legendVisuals[group.Category] = legend;
            }

            legend.Amount.Text = $"{ByteFormatter.Size(group.Total)}   {(double)group.Total / total:P0}";
        }

        PieLegend.Children.Clear();
        foreach (var name in _legendOrder) PieLegend.Children.Add(_legendVisuals[name].Root);
    }

    private void BuildApps(IReadOnlyList<ProcessUsageItem> apps)
    {
        if (apps.Count == 0)
        {
            AppsPanel.Children.Clear();
            _groupVisuals.Clear();
            _rowVisuals.Clear();
            _rowOrder.Clear();
            _emptyLabel ??= MutedLabel("暂无数据");
            AppsPanel.Children.Add(_emptyLabel);
            return;
        }

        if (_emptyLabel is not null) AppsPanel.Children.Remove(_emptyLabel);

        var groups = apps
            .GroupBy(app => app.Category)
            .OrderBy(group => CategoryIndex(group.Key))
            .ThenByDescending(group => group.Sum(app => app.Total))
            .ToList();

        foreach (var stale in _groupVisuals.Keys.Where(key => groups.All(g => g.Key != key)).ToList())
        {
            _groupVisuals.Remove(stale);
            _rowOrder.Remove(stale);
        }

        var live = apps.Select(app => app.Process).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in _rowVisuals.Keys.Where(key => !live.Contains(key)).ToList())
            _rowVisuals.Remove(stale);

        foreach (var group in groups)
        {
            long total = group.Sum(app => app.Total);
            var ordered = group.OrderByDescending(app => app.Total).ToList();

            if (!_groupVisuals.TryGetValue(group.Key, out var visual))
            {
                visual = CreateGroupVisual(group.Key, total);
                _groupVisuals[group.Key] = visual;
            }
            else
            {
                visual.Title.Text = $"{group.Key} · {ByteFormatter.Size(total)}";
            }

            foreach (var app in ordered)
            {
                if (!_rowVisuals.TryGetValue(app.Process, out var row))
                {
                    row = CreateRowVisual(app);
                    _rowVisuals[app.Process] = row;
                }

                row.Detail.Text =
                    $"{app.Category} · {app.Process} · ↓ {ByteFormatter.Size(app.Rx)}   ↑ {ByteFormatter.Size(app.Tx)}";
                row.Total.Text = ByteFormatter.Size(app.Total);
            }

            var names = ordered.Select(app => app.Process).ToList();
            if (!_rowOrder.TryGetValue(group.Key, out var previous) || previous.Count == 0 || previous.Count != names.Count)
            {
                _rowOrder[group.Key] = names;
            }
            else
            {
                // 集合不变时沿用既有顺序，避免数值微调导致整列跳动。
                var previousSet = previous.ToHashSet(StringComparer.Ordinal);
                if (!names.All(previousSet.Contains)) _rowOrder[group.Key] = names;
            }

            visual.Rows.Children.Clear();
            foreach (var name in _rowOrder[group.Key])
            {
                if (_rowVisuals.TryGetValue(name, out var row)) visual.Rows.Children.Add(row.Root);
            }
        }

        AppsPanel.Children.Clear();
        foreach (var group in groups) AppsPanel.Children.Add(_groupVisuals[group.Key].Expander);
    }

    private GroupVisual CreateGroupVisual(string category, long total)
    {
        var visual = IconCatalog.ForCategory(category);
        var color = new SolidColorBrush(visual.Color);

        var title = new TextBlock
        {
            Text = $"{category} · {ByteFormatter.Size(total)}",
            FontSize = 11,
            Foreground = color,
        };

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            Margin = new Thickness(2, 6, 0, 2),
        };
        header.Children.Add(IconCatalog.Create(visual.Icon, 11, visual.Color));
        header.Children.Add(title);

        var rows = new StackPanel { Spacing = 2 };

        var expander = new Expander
        {
            Header = header,
            Content = rows,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsExpanded = !_collapsed.Contains(category),
        };

        expander.Expanding += (_, _) => _collapsed.Remove(category);
        expander.Collapsed += (_, _) => _collapsed.Add(category);

        return new GroupVisual { Expander = expander, Title = title, Rows = rows };
    }

    private static RowVisual CreateRowVisual(ProcessUsageItem app)
    {
        var visual = IconCatalog.ForCategory(app.Category);

        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

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

        var detail = new TextBlock
        {
            Text = $"{app.Category} · {app.Process} · ↓ {ByteFormatter.Size(app.Rx)}   ↑ {ByteFormatter.Size(app.Tx)}",
            FontSize = 11,
            Foreground = MutedBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };

        left.Children.Add(titleRow);
        left.Children.Add(detail);

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

        return new RowVisual { Root = grid, Detail = detail, Total = total };
    }

    private static LegendVisual CreateLegendRow(string category)
    {
        var color = IconCatalog.ForCategory(category).Color;
        var secondary = (Brush)Application.Current.Resources["TextSecondaryBrush"];
        var muted = (Brush)Application.Current.Resources["TextMutedBrush"];

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
            Text = category,
            FontSize = 11.5,
            Foreground = secondary,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var amount = new TextBlock
        {
            FontSize = 10.5,
            Foreground = muted,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Grid.SetColumn(label, 1);
        Grid.SetColumn(amount, 2);
        row.Children.Add(dot);
        row.Children.Add(label);
        row.Children.Add(amount);

        return new LegendVisual { Root = row, Amount = amount };
    }

    private static bool SameKeys(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal)) return false;
        }

        return true;
    }

    private static TextBlock MutedLabel(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = MutedBrush,
        Margin = new Thickness(2, 2, 0, 0),
    };

    private sealed class GroupVisual
    {
        public required Expander Expander { get; init; }

        public required TextBlock Title { get; init; }

        public required StackPanel Rows { get; init; }
    }

    private sealed class RowVisual
    {
        public required Grid Root { get; init; }

        public required TextBlock Detail { get; init; }

        public required TextBlock Total { get; init; }
    }

    private sealed class LegendVisual
    {
        public required Grid Root { get; init; }

        public required TextBlock Amount { get; init; }
    }

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
