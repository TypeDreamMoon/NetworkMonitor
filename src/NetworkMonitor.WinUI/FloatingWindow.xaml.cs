using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NetworkMonitor.Core.Configuration;
using NetworkMonitor.Core.Formatting;
using NetworkMonitor.Core.Sampling;
using NetworkMonitor.WinUI.Interop;
using NetworkMonitor.WinUI.Services;
using Windows.Graphics;

namespace NetworkMonitor.WinUI;

public sealed partial class FloatingWindow : Window
{
    private readonly AppSettings _settings;
    private bool _dragging;
    private int _dragStartX;
    private int _dragStartY;
    private int _windowStartX;
    private int _windowStartY;

    public FloatingWindow(AppSettings settings)
    {
        InitializeComponent();


        Root.Loaded += (_, _) =>
        {
            Native.UseDarkFrame(this);
            Native.StripWindowFrame(this);
            Native.RoundCorners(this);
        };
        _settings = settings;

        try
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }
        catch (Exception ex)
        {
            Log.Write("悬浮窗亚克力不可用", ex);
        }

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        Native.MakeToolWindow(this, noActivate: false);
        Native.RoundCorners(this);

        IntPtr hwnd = Native.Handle(this);
        double scale = Native.GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0) scale = 1;

        int width = (int)(252 * scale);
        int height = (int)(56 * scale);
        AppWindow.ResizeClient(new SizeInt32(width, height));

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int x = _settings.FloatingLeft is double savedLeft ? (int)savedLeft : area.X + area.Width - width - 24;
        int y = _settings.FloatingTop is double savedTop ? (int)savedTop : area.Y + area.Height - height - 80;
        x = Math.Clamp(x, area.X, Math.Max(area.X, area.X + area.Width - width));
        y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Y + area.Height - height));
        AppWindow.Move(new PointInt32(x, y));

        AppWindow.Changed += (_, args) =>
        {
            if (args.DidPositionChange)
            {
                _settings.FloatingLeft = AppWindow.Position.X;
                _settings.FloatingTop = AppWindow.Position.Y;
            }
        };
    }

    public void SetSpeed(SpeedSample sample)
    {
        DownText.Text = ByteFormatter.Rate(sample.DownBps);
        UpText.Text = ByteFormatter.Rate(sample.UpBps);
    }

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed) return;

        var (x, y) = Native.CursorPosition();
        _dragging = true;
        _dragStartX = x;
        _dragStartY = y;
        _windowStartX = AppWindow.Position.X;
        _windowStartY = AppWindow.Position.Y;
        Root.CapturePointer(e.Pointer);
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        var (x, y) = Native.CursorPosition();
        AppWindow.Move(new PointInt32(_windowStartX + (x - _dragStartX), _windowStartY + (y - _dragStartY)));
    }

    private void Root_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Root.ReleasePointerCapture(e.Pointer);
    }

    private void Root_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => App.Instance.ShowMain();
}
