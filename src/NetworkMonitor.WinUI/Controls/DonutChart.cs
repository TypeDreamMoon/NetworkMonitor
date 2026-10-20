using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace NetworkMonitor.WinUI.Controls;

public sealed record DonutSlice(string Label, double Value, Color Color);

/// <summary>Small dependency-free donut chart built from ArcSegments.</summary>
public sealed class DonutChart : UserControl
{
    private readonly Canvas _canvas = new();
    private readonly TextBlock _valueText = new()
    {
        FontSize = 17,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        HorizontalAlignment = HorizontalAlignment.Center,
        Foreground = new SolidColorBrush(Color.FromArgb(255, 0xE9, 0xEF, 0xF8)),
    };

    private readonly TextBlock _labelText = new()
    {
        FontSize = 10,
        HorizontalAlignment = HorizontalAlignment.Center,
        Foreground = new SolidColorBrush(Color.FromArgb(255, 0x7C, 0x88, 0x99)),
    };

    private IReadOnlyList<DonutSlice> _slices = [];
    private string _signature = string.Empty;

    public DonutChart()
    {
        var root = new Grid();
        root.Children.Add(_canvas);

        var center = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 1,
        };
        center.Children.Add(_valueText);
        center.Children.Add(_labelText);
        root.Children.Add(center);

        Content = root;
        SizeChanged += (_, _) => Redraw();
    }

    public Color GapColor { get; set; } = Color.FromArgb(255, 0x12, 0x18, 0x26);

    public void SetData(IReadOnlyList<DonutSlice> slices, string centerValue, string centerLabel)
    {
        _slices = slices;
        _valueText.Text = centerValue;
        _labelText.Text = centerLabel;

        // 数据没变就不重画，避免每次轮询清掉重绘圆环造成闪烁。
        var signature = centerValue + "|" + string.Join('|', slices.Select(slice => slice.Value + ":" + slice.Color));
        if (signature == _signature) return;

        _signature = signature;
        Redraw();
    }

    private void Redraw()
    {
        _canvas.Children.Clear();

        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 8) return;

        double radius = size / 2 - 2;
        double inner = radius * 0.62;
        var center = new Point(size / 2, size / 2);
        double total = _slices.Sum(slice => Math.Max(0, slice.Value));
        if (total <= 0) return;

        if (_slices.Count == 1)
        {
            var outer = new Ellipse
            {
                Width = radius * 2,
                Height = radius * 2,
                Fill = new SolidColorBrush(_slices[0].Color),
            };
            Canvas.SetLeft(outer, center.X - radius);
            Canvas.SetTop(outer, center.Y - radius);
            _canvas.Children.Add(outer);
            var hole = new Ellipse
            {
                Width = inner * 2,
                Height = inner * 2,
                Fill = new SolidColorBrush(GapColor),
            };
            Canvas.SetLeft(hole, center.X - inner);
            Canvas.SetTop(hole, center.Y - inner);
            _canvas.Children.Add(hole);
            return;
        }

        double angle = -90;
        foreach (var slice in _slices)
        {
            double value = Math.Max(0, slice.Value);
            if (value <= 0) continue;

            double sweep = value / total * 360;
            bool large = sweep > 180;

            var figure = new PathFigure { StartPoint = PointAt(center, radius, angle) };
            figure.Segments.Add(new ArcSegment
            {
                Point = PointAt(center, radius, angle + sweep),
                Size = new Size(radius, radius),
                IsLargeArc = large,
                SweepDirection = SweepDirection.Clockwise,
            });
            figure.Segments.Add(new LineSegment { Point = PointAt(center, inner, angle + sweep) });
            figure.Segments.Add(new ArcSegment
            {
                Point = PointAt(center, inner, angle),
                Size = new Size(inner, inner),
                IsLargeArc = large,
                SweepDirection = SweepDirection.Counterclockwise,
            });
            figure.IsClosed = true;

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);

            _canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = geometry,
                Fill = new SolidColorBrush(slice.Color),
                Stroke = new SolidColorBrush(GapColor),
                StrokeThickness = 1.5,
            });

            angle += sweep;
        }
    }

    private static Point PointAt(Point center, double radius, double angleDegrees)
    {
        double radians = angleDegrees * Math.PI / 180;
        return new Point(
            center.X + radius * Math.Cos(radians),
            center.Y + radius * Math.Sin(radians));
    }
}
