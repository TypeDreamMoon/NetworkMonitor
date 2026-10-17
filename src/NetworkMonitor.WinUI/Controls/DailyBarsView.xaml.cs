using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using NetworkMonitor.Core.Formatting;
using NetworkMonitor.Core.Storage;
using Windows.UI;

namespace NetworkMonitor.WinUI.Controls;

public sealed partial class DailyBarsView : UserControl
{
    private IReadOnlyList<DailyUsage> _data = [];

    public DailyBarsView()
    {
        InitializeComponent();
    }

    public Color BarColor { get; set; } = Color.FromArgb(255, 0x2F, 0xB9, 0xA8);

    public Color TodayColor { get; set; } = Color.FromArgb(255, 0x5B, 0x9D, 0xFF);

    public Color LabelColor { get; set; } = Color.FromArgb(255, 0x7C, 0x88, 0x99);

    public void SetData(IReadOnlyList<DailyUsage> data)
    {
        _data = data ?? [];
        Redraw();
    }

    private void Host_SizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void Redraw()
    {
        Plot.Children.Clear();

        double w = Host.ActualWidth;
        double h = Host.ActualHeight;
        if (w < 20 || h < 20) return;

        const double labelHeight = 20;
        double baseline = h - labelHeight;

        Plot.Children.Add(new Line
        {
            X1 = 0,
            Y1 = baseline,
            X2 = w,
            Y2 = baseline,
            Stroke = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)),
            StrokeThickness = 1,
        });

        if (_data.Count == 0) return;

        double max = 1;
        foreach (var day in _data)
            if (day.Total > max) max = day.Total;

        int n = _data.Count;
        double gap = n <= 10 ? 9 : 3;
        double barWidth = Math.Clamp((w - gap * (n - 1)) / n, 3, 34);
        double totalWidth = barWidth * n + gap * (n - 1);
        double offsetX = (w - totalWidth) / 2;

        var labelBrush = new SolidColorBrush(LabelColor);
        int labelStep = n <= 7 ? 1 : n <= 14 ? 2 : n <= 20 ? 3 : 5;

        for (int i = 0; i < n; i++)
        {
            var day = _data[i];
            bool isToday = i == n - 1;

            double barHeight = day.Total <= 0 ? 0 : Math.Max(2, day.Total / max * (baseline - 18));
            double x = offsetX + i * (barWidth + gap);

            if (barHeight > 0)
            {
                Color color = isToday ? TodayColor : BarColor;
                byte alpha = isToday ? (byte)255 : (byte)165;
                var rect = new Rectangle
                {
                    Width = barWidth,
                    Height = barHeight,
                    RadiusX = 4,
                    RadiusY = 4,
                    Fill = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)),
                };
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, baseline - barHeight);
                Plot.Children.Add(rect);
            }

            if (i % labelStep == 0 || isToday)
            {
                var label = new TextBlock
                {
                    Text = isToday ? "今天" : day.Date.ToString("M/d", CultureInfo.InvariantCulture),
                    FontSize = 10,
                    Foreground = labelBrush,
                };
                label.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(label, x + barWidth / 2 - label.DesiredSize.Width / 2);
                Canvas.SetTop(label, baseline + 5);
                Plot.Children.Add(label);
            }
        }

        var peak = new TextBlock
        {
            Text = ByteFormatter.Size((long)max),
            FontSize = 10,
            Foreground = labelBrush,
            Width = 90,
            TextAlignment = TextAlignment.Right,
        };
        Canvas.SetLeft(peak, Math.Max(0, w - 90));
        Canvas.SetTop(peak, 0);
        Plot.Children.Add(peak);
    }
}
