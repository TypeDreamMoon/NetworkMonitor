using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace NetworkMonitor.WinUI.Controls;

public sealed partial class SparklineView : UserControl
{
    private double[] _values = [];

    public SparklineView()
    {
        InitializeComponent();
    }

    public Color StrokeColor { get; set; } = Color.FromArgb(255, 0x3E, 0xDC, 0xC4);

    public double MinScale { get; set; } = 64 * 1024;

    public void SetValues(double[] values)
    {
        _values = values ?? [];
        Redraw();
    }

    private void Host_SizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void Redraw()
    {
        Plot.Children.Clear();

        double w = Host.ActualWidth;
        double h = Host.ActualHeight;
        if (w < 4 || h < 4) return;

        Plot.Children.Add(new Line
        {
            X1 = 0,
            Y1 = h - 1,
            X2 = w,
            Y2 = h - 1,
            Stroke = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)),
            StrokeThickness = 1,
        });

        if (_values.Length < 2) return;

        double max = MinScale;
        foreach (double value in _values)
            if (value > max) max = value;

        var line = new PointCollection();
        for (int i = 0; i < _values.Length; i++)
        {
            double x = i * (w / (_values.Length - 1));
            double y = h - (_values[i] / max) * (h - 3) - 1.5;
            line.Add(new Point(x, y));
        }

        var area = new PointCollection { new Point(0, h - 1) };
        foreach (var point in line) area.Add(point);
        area.Add(new Point(w, h - 1));

        var fill = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
        };
        fill.GradientStops.Add(new GradientStop
        {
            Color = Color.FromArgb(64, StrokeColor.R, StrokeColor.G, StrokeColor.B),
            Offset = 0,
        });
        fill.GradientStops.Add(new GradientStop
        {
            Color = Color.FromArgb(0, StrokeColor.R, StrokeColor.G, StrokeColor.B),
            Offset = 1,
        });

        Plot.Children.Add(new Polygon { Points = area, Fill = fill });
        Plot.Children.Add(new Polyline
        {
            Points = line,
            Stroke = new SolidColorBrush(StrokeColor),
            StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        });

        var last = line[line.Count - 1];
        var dot = new Ellipse
        {
            Width = 5,
            Height = 5,
            Fill = new SolidColorBrush(StrokeColor),
        };
        Canvas.SetLeft(dot, last.X - 2.5);
        Canvas.SetTop(dot, last.Y - 2.5);
        Plot.Children.Add(dot);
    }
}
