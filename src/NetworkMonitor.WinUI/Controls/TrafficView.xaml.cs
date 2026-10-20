using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using NetworkMonitor.Core.Formatting;
using NetworkMonitor.Core.Proxy;
using Windows.UI;

namespace NetworkMonitor.WinUI.Controls;

public sealed partial class TrafficView : UserControl
{
    private static readonly SolidColorBrush PrimaryBrush = new(Color.FromArgb(255, 0xE9, 0xEF, 0xF8));
    private static readonly SolidColorBrush SecondaryBrush = new(Color.FromArgb(255, 0xA9, 0xB6, 0xCA));
    private static readonly SolidColorBrush MutedBrush = new(Color.FromArgb(255, 0x7C, 0x88, 0x99));
    private static readonly SolidColorBrush DownBrush = new(Color.FromArgb(255, 0x3E, 0xDC, 0xC4));
    private static readonly SolidColorBrush AccentBrush = new(Color.FromArgb(255, 0x5B, 0x9D, 0xFF));

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);

    private IProxyTrafficSource? _service;
    private ProxyScope _scope = ProxyScope.Today;
    private bool _refreshing;
    private bool _editingMultiplier;

    private readonly Dictionary<string, AppVisual> _appVisuals = new(StringComparer.Ordinal);
    private readonly List<string> _appOrder = [];

    private readonly Dictionary<string, NodeCardVisual> _nodeCards = new(StringComparer.Ordinal);
    private readonly List<string> _nodeOrder = [];

    private readonly Dictionary<string, LegendVisual> _legendVisuals = new(StringComparer.Ordinal);
    private readonly List<string> _legendOrder = [];

    private Grid? _nodesGrid;
    private TextBlock? _emptyAppsLabel;
    private TextBlock? _emptyNodesLabel;

    public TrafficView()
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

    public void Attach(IProxyTrafficSource service)
    {
        _service = service;
        if (IsLoaded) _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_service is null || _refreshing || _editingMultiplier) return;
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

    private void Render(ProxyUsageReport report)
    {
        if (_service is null) return;

        bool connected = _service.Connected;
        StatusText.Text = connected ? $"已连接 {_service.Endpoint}" : _service.Status;
        StatusText.Foreground = connected ? DownBrush : SecondaryBrush;

        EmptyHint.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        if (!connected)
        {
            EmptyHint.Text = "未连接代理控制器。FlClash / Clash Verge Rev 需要在设置里打开 External controller（默认 127.0.0.1:9090 / 9097），然后点「刷新」。";
        }
        else if (report.Apps.Count == 0)
        {
            EmptyHint.Visibility = Visibility.Visible;
            EmptyHint.Text = "已连接，应用还没有产生流量。";
        }

        ScopeLabel.Text = report.Scope switch
        {
            ProxyScope.Week => "本周应用流量",
            ProxyScope.Month => "本月应用流量",
            _ => "今日应用流量",
        };

        ActualText.Text = ByteFormatter.Size(report.Rx + report.Tx);
        ActualSplit.Text = $"↓ {ByteFormatter.Size(report.Rx)}   ↑ {ByteFormatter.Size(report.Tx)}";

        BilledText.Text = ByteFormatter.Size(report.BilledRx + report.BilledTx);
        BilledSplit.Text = $"↓ {ByteFormatter.Size(report.BilledRx)}   ↑ {ByteFormatter.Size(report.BilledTx)}";

        BuildPie(report.Apps);
        BuildApps(report.Apps);
        BuildNodes(report.Nodes);
    }

    private void BuildPie(IReadOnlyList<ProxyAppUsage> apps)
    {
        var groups = apps
            .GroupBy(app => app.Category)
            .Select(group => (Category: group.Key, Total: group.Sum(app => app.Total)))
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

        CategoryPie.SetData(slices, ByteFormatter.Size(total), "实际流量");

        // 图例增量更新：只新建真正变化的行，避免每次刷新整块闪一下。
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

    private void BuildApps(IReadOnlyList<ProxyAppUsage> apps)
    {
        if (apps.Count == 0)
        {
            AppsPanel.Children.Clear();
            _appVisuals.Clear();
            _appOrder.Clear();
            _emptyAppsLabel ??= MutedLabel("暂无数据");
            AppsPanel.Children.Add(_emptyAppsLabel);
            return;
        }

        if (_emptyAppsLabel is not null) AppsPanel.Children.Remove(_emptyAppsLabel);

        var live = apps.Select(app => app.Process).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in _appVisuals.Keys.Where(key => !live.Contains(key)).ToList())
            _appVisuals.Remove(stale);

        foreach (var app in apps)
        {
            if (!_appVisuals.TryGetValue(app.Process, out var visual))
            {
                visual = CreateAppVisual(app);
                _appVisuals[app.Process] = visual;
            }
            else
            {
                visual.Detail.Text = BuildAppDetail(app);
                visual.Total.Text = ByteFormatter.Size(app.Total);
            }

            UpdateAppNodes(visual, app);
        }

        var names = apps.Select(app => app.Process).ToList();
        if (_appOrder.Count != names.Count || !names.All(_appOrder.Contains))
        {
            _appOrder.Clear();
            _appOrder.AddRange(names);
        }

        AppsPanel.Children.Clear();
        foreach (var name in _appOrder)
        {
            if (_appVisuals.TryGetValue(name, out var visual)) AppsPanel.Children.Add(visual.Expander);
        }
    }

    private static string BuildAppDetail(ProxyAppUsage app)
    {
        string billed = app.BilledTotal != app.Total
            ? $"   ·   计费 {ByteFormatter.Size(app.BilledTotal)}"
            : string.Empty;

        return $"{app.Category} · ↓ {ByteFormatter.Size(app.Rx)}   ↑ {ByteFormatter.Size(app.Tx)}   ·   {app.Nodes.Count} 个节点{billed}";
    }

    private AppVisual CreateAppVisual(ProxyAppUsage app)
    {
        var header = new Grid { ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

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

        var detail = new TextBlock
        {
            Text = BuildAppDetail(app),
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
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = PrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(total, 1);

        header.Children.Add(left);
        header.Children.Add(total);

        var nodeRows = new StackPanel { Spacing = 4, Margin = new Thickness(0, 6, 0, 2) };

        var expander = new Expander
        {
            Header = header,
            Content = nodeRows,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsExpanded = _expanded.Contains(app.Process),
        };

        string process = app.Process;
        expander.Expanding += (_, _) => _expanded.Add(process);
        expander.Collapsed += (_, _) => _expanded.Remove(process);

        return new AppVisual
        {
            Expander = expander,
            Detail = detail,
            Total = total,
            NodeRows = nodeRows,
        };
    }

    private static void UpdateAppNodes(AppVisual visual, ProxyAppUsage app)
    {
        var order = app.Nodes.Select(node => node.Node).ToList();

        foreach (var node in app.Nodes)
        {
            if (!visual.Nodes.TryGetValue(node.Node, out var row))
            {
                row = CreateNodeRow(node, 11);
                visual.Nodes[node.Node] = row;
            }
            else
            {
                row.Name.Text = NodeLabel(node);
                row.Amount.Text = NodeAmount(node);
            }
        }

        foreach (var stale in visual.Nodes.Keys.Where(key => !order.Contains(key)).ToList())
            visual.Nodes.Remove(stale);

        if (visual.NodeOrder.Count != order.Count || !order.All(visual.NodeOrder.Contains))
        {
            visual.NodeOrder.Clear();
            visual.NodeOrder.AddRange(order);
        }

        visual.NodeRows.Children.Clear();
        foreach (var key in visual.NodeOrder)
        {
            if (visual.Nodes.TryGetValue(key, out var row)) visual.NodeRows.Children.Add(row.Root);
        }
    }

    private void BuildNodes(IReadOnlyList<ProxyNodeUsage> nodes)
    {
        if (nodes.Count == 0)
        {
            NodesPanel.Children.Clear();
            _nodeCards.Clear();
            _nodeOrder.Clear();
            _emptyNodesLabel ??= MutedLabel("暂无数据");
            NodesPanel.Children.Add(_emptyNodesLabel);
            return;
        }

        if (_emptyNodesLabel is not null) NodesPanel.Children.Remove(_emptyNodesLabel);

        var live = nodes.Select(node => node.Node).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in _nodeCards.Keys.Where(key => !live.Contains(key)).ToList())
            _nodeCards.Remove(stale);

        foreach (var node in nodes)
        {
            if (!_nodeCards.TryGetValue(node.Node, out var card))
            {
                card = CreateNodeCard(node);
                _nodeCards[node.Node] = card;
            }
            else
            {
                card.Badge.Text = $"×{node.Multiplier:0.##}";
                card.Badge.Visibility = Math.Abs(node.Multiplier - 1) < 0.001
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                card.Total.Text = ByteFormatter.Size(node.Total);
                card.Billed.Text = $"计费 ×{node.Multiplier:0.##} = {ByteFormatter.Size(node.BilledTotal)}";
                card.Actual.Text = $"实际 ↓ {ByteFormatter.Size(node.Rx)}   ↑ {ByteFormatter.Size(node.Tx)}";
            }
        }

        var names = nodes.Select(node => node.Node).ToList();
        if (_nodeOrder.Count != names.Count || !names.All(_nodeOrder.Contains))
        {
            _nodeOrder.Clear();
            _nodeOrder.AddRange(names);
        }

        if (_nodesGrid is null)
        {
            _nodesGrid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
            _nodesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _nodesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            NodesPanel.Children.Add(_nodesGrid);
        }

        _nodesGrid.RowDefinitions.Clear();
        _nodesGrid.Children.Clear();

        int index = 0;
        foreach (var name in _nodeOrder)
        {
            if (!_nodeCards.TryGetValue(name, out var card)) continue;

            if (index % 2 == 0) _nodesGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(card.Root, index / 2);
            Grid.SetColumn(card.Root, index % 2);
            _nodesGrid.Children.Add(card.Root);
            index++;
        }
    }

    private NodeCardVisual CreateNodeCard(ProxyNodeUsage node)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8, 8, 8),
            MinHeight = 92,
        };
        ToolTipService.SetToolTip(card, node.Node);

        var root = new StackPanel { Spacing = 2 };

        var top = new Grid { ColumnSpacing = 6 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = node.Node,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = PrimaryBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var badge = new TextBlock
        {
            Text = $"×{node.Multiplier:0.##}",
            FontSize = 10,
            Foreground = AccentBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Math.Abs(node.Multiplier - 1) < 0.001 ? Visibility.Collapsed : Visibility.Visible,
        };

        var gear = new Button
        {
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Content = new FontIcon { Glyph = "\uE713", FontSize = 10 },
            VerticalAlignment = VerticalAlignment.Top,
        };
        ToolTipService.SetToolTip(gear, "设置节点倍率");
        gear.Click += (_, _) => ShowMultiplierEditor(gear, node.Node);

        Grid.SetColumn(badge, 1);
        Grid.SetColumn(gear, 2);
        top.Children.Add(name);
        top.Children.Add(badge);
        top.Children.Add(gear);

        var total = new TextBlock
        {
            Text = ByteFormatter.Size(node.Total),
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = PrimaryBrush,
        };

        var billed = new TextBlock
        {
            Text = $"计费 ×{node.Multiplier:0.##} = {ByteFormatter.Size(node.BilledTotal)}",
            FontSize = 10.5,
            Foreground = new SolidColorBrush(IconCatalog.Up),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };

        var billedRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        billedRow.Children.Add(IconCatalog.Create("IconBilled", 10, IconCatalog.Up));
        billedRow.Children.Add(billed);

        var actual = new TextBlock
        {
            Text = $"实际 ↓ {ByteFormatter.Size(node.Rx)}   ↑ {ByteFormatter.Size(node.Tx)}",
            FontSize = 10.5,
            Foreground = MutedBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };

        root.Children.Add(top);
        root.Children.Add(total);
        root.Children.Add(billedRow);
        root.Children.Add(actual);

        card.Child = root;

        return new NodeCardVisual
        {
            Root = card,
            Badge = badge,
            Total = total,
            Billed = billed,
            Actual = actual,
        };
    }

    private static string NodeLabel(ProxyNodeUsage node) =>
        node.Multiplier != 1 ? $"{node.Node}   ×{node.Multiplier:0.##}" : node.Node;

    private static string NodeAmount(ProxyNodeUsage node) =>
        node.BilledTotal != node.Total
            ? $"↓ {ByteFormatter.Size(node.Rx)}   ↑ {ByteFormatter.Size(node.Tx)}   计费 {ByteFormatter.Size(node.BilledTotal)}"
            : $"↓ {ByteFormatter.Size(node.Rx)}   ↑ {ByteFormatter.Size(node.Tx)}";

    private static NodeRowVisual CreateNodeRow(ProxyNodeUsage node, double fontSize)
    {
        var grid = new Grid { ColumnSpacing = 8, Padding = new Thickness(2, 3, 2, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = NodeLabel(node),
            FontSize = fontSize,
            Foreground = SecondaryBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var amount = new TextBlock
        {
            Text = NodeAmount(node),
            FontSize = fontSize - 0.5,
            Foreground = MutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(amount, 1);

        grid.Children.Add(name);
        grid.Children.Add(amount);

        return new NodeRowVisual { Root = grid, Name = name, Amount = amount };
    }

    private static LegendVisual CreateLegendRow(string category)
    {
        var color = IconCatalog.ForCategory(category).Color;

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
            Foreground = SecondaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var amount = new TextBlock
        {
            FontSize = 10.5,
            Foreground = MutedBrush,
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

    private sealed class AppVisual
    {
        public required Expander Expander { get; init; }

        public required TextBlock Detail { get; init; }

        public required TextBlock Total { get; init; }

        public required StackPanel NodeRows { get; init; }

        public Dictionary<string, NodeRowVisual> Nodes { get; } = new(StringComparer.Ordinal);

        public List<string> NodeOrder { get; } = [];
    }

    private sealed class NodeRowVisual
    {
        public required Grid Root { get; init; }

        public required TextBlock Name { get; init; }

        public required TextBlock Amount { get; init; }
    }

    private sealed class NodeCardVisual
    {
        public required Border Root { get; init; }

        public required TextBlock Badge { get; init; }

        public required TextBlock Total { get; init; }

        public required TextBlock Billed { get; init; }

        public required TextBlock Actual { get; init; }
    }

    private sealed class LegendVisual
    {
        public required Grid Root { get; init; }

        public required TextBlock Amount { get; init; }
    }

    private void ShowMultiplierEditor(FrameworkElement anchor, string node)
    {
        if (_service is null) return;

        double current = _service.GetNodeMultiplier(node);
        _editingMultiplier = true;

        var box = new NumberBox
        {
            Minimum = 0,
            Maximum = 100,
            SmallChange = 0.1,
            Value = current,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };

        var presets = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (double value in new[] { 1, 1.5, 2, 3, 5 })
        {
            var preset = new Button { Content = $"{value:0.#}x", FontSize = 11, Padding = new Thickness(8, 3, 8, 3) };
            preset.Click += (_, _) => box.Value = value;
            presets.Children.Add(preset);
        }

        var save = new Button { Content = "保存", HorizontalAlignment = HorizontalAlignment.Stretch };

        var panel = new StackPanel { Spacing = 8, Width = 220 };
        panel.Children.Add(new TextBlock
        {
            Text = node,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "计费流量 = 实际流量 × 倍率",
            FontSize = 11,
            Foreground = MutedBrush,
        });
        panel.Children.Add(box);
        panel.Children.Add(presets);
        panel.Children.Add(save);

        var flyout = new Flyout
        {
            Content = panel,
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
        };

        save.Click += async (_, _) =>
        {
            double value = double.IsNaN(box.Value) ? 1 : Math.Max(0, box.Value);
            await _service.SetNodeMultiplierAsync(node, value);
            flyout.Hide();
        };

        flyout.Closed += (_, _) =>
        {
            _editingMultiplier = false;
            _ = RefreshAsync();
        };

        flyout.ShowAt(anchor);
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
            1 => ProxyScope.Week,
            2 => ProxyScope.Month,
            _ => ProxyScope.Today,
        };

        _ = RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _service?.RefreshEndpoint();
        await RefreshAsync();
    }
}
