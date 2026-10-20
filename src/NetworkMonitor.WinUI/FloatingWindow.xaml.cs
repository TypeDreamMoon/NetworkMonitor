using Microsoft.UI;
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
using Windows.UI;

namespace NetworkMonitor.WinUI;

public sealed partial class FloatingWindow : Window
{
    private const double BaseRateFontSize = 13;
    private const double BaseSessionFontSize = 10.5;

    private const double HorizontalPadding = 14;
    private const double VerticalPadding = 6;

    private static readonly Color DownFallback = Color.FromArgb(255, 0x3E, 0xDC, 0xC4);
    private static readonly Color UpFallback = Color.FromArgb(255, 0xFF, 0xB4, 0x54);

    private readonly AppSettings _settings;
    private SpeedSample _lastSample;
    private bool _dragging;
    private int _dragStartX;
    private int _dragStartY;
    private int _windowStartX;
    private int _windowStartY;

    public FloatingWindow(AppSettings settings)
    {
        InitializeComponent();

        _settings = settings;

        Root.Loaded += (_, _) =>
        {
            Native.UseDarkFrame(this);
            Native.StripWindowFrame(this);
            Native.RoundCorners(this);
            ResizeToContent(firstTime: true);
        };

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
            presenter.IsAlwaysOnTop = _settings.FloatingTopmost;
        }

        Native.MakeToolWindow(this, noActivate: false);
        Native.RoundCorners(this);

        ApplySettings();

        AppWindow.Changed += (_, args) =>
        {
            if (!args.DidPositionChange) return;
            _settings.FloatingLeft = AppWindow.Position.X;
            _settings.FloatingTop = AppWindow.Position.Y;
        };
    }

    /// <summary>Re-applies every floating-window preference and re-sizes the window around the content.</summary>
    public void ApplySettings()
    {
        ApplyScale();
        ApplyVisibility();
        ApplyColors();
        ApplyBackground();
        ApplyInteraction();
        UpdateSessionText();
        ResizeToContent(firstTime: _settings.FloatingLeft is null);
        RestoreOrClampPosition();
    }

    public void SetSpeed(SpeedSample sample)
    {
        _lastSample = sample;

        // Mirror ApplyVisibility: when both rates are off the download row stays as the fallback,
        // so it has to keep showing real numbers.
        bool showDown = _settings.FloatingShowDown || !_settings.FloatingShowUp;
        DownText.Text = showDown ? ByteFormatter.Rate(sample.DownBps) : "—";
        UpText.Text = _settings.FloatingShowUp ? ByteFormatter.Rate(sample.UpBps) : "—";
        UpdateSessionText();
    }

    private void ApplyScale()
    {
        double scale = Math.Clamp(_settings.FloatingScale, 0.7, 2.0);

        DownArrow.FontSize = BaseRateFontSize * scale;
        UpArrow.FontSize = BaseRateFontSize * scale;
        DownText.FontSize = BaseRateFontSize * scale;
        UpText.FontSize = BaseRateFontSize * scale;
        SessionText.FontSize = BaseSessionFontSize * scale;

        ContentPanel.Spacing = scale > 1.1 ? 4 : 2;
    }

    private void ApplyVisibility()
    {
        bool showDown = _settings.FloatingShowDown;
        bool showUp = _settings.FloatingShowUp;

        // Never leave an empty window behind: if both rates are hidden, keep the download.
        if (!showDown && !showUp) showDown = true;

        DownGroup.Visibility = showDown ? Visibility.Visible : Visibility.Collapsed;
        UpGroup.Visibility = showUp ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyColors()
    {
        DownArrow.Foreground = new SolidColorBrush(ParseColor(_settings.FloatingDownColor, DownFallback));
        UpArrow.Foreground = new SolidColorBrush(ParseColor(_settings.FloatingUpColor, UpFallback));
    }

    private void ApplyBackground()
    {
        double opacity = Math.Clamp(_settings.FloatingOpacity, 0.30, 1.0);
        byte alpha = (byte)Math.Round(opacity * 255);
        Root.Background = new SolidColorBrush(Color.FromArgb(alpha, 0x0B, 0x0F, 0x16));

        try
        {
            SystemBackdrop = _settings.FloatingAcrylic ? new DesktopAcrylicBackdrop() : null;
        }
        catch (Exception ex)
        {
            Log.Write("悬浮窗亚克力不可用", ex);
            SystemBackdrop = null;
        }
    }

    private void ApplyInteraction()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = _settings.FloatingTopmost;
        }

        Native.SetClickThrough(this, _settings.FloatingClickThrough);
    }

    private void UpdateSessionText()
    {
        bool show = _settings.FloatingShowSession;
        SessionText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        SessionText.Text =
            $"本次 ↓ {ByteFormatter.Size(_lastSample.SessionDownBytes)}   ↑ {ByteFormatter.Size(_lastSample.SessionUpBytes)}";
    }

    private void ResizeToContent(bool firstTime)
    {
        ContentPanel.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = ContentPanel.DesiredSize;

        IntPtr hwnd = Native.Handle(this);
        double scale = hwnd == IntPtr.Zero ? 1 : Native.GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0) scale = 1;

        int width = (int)Math.Ceiling((desired.Width + HorizontalPadding * 2) * scale);
        int height = (int)Math.Ceiling((desired.Height + VerticalPadding * 2) * scale);
        width = Math.Max(width, 96);
        height = Math.Max(height, 28);

        if (!firstTime && width == AppWindow.Size.Width && height == AppWindow.Size.Height) return;

        AppWindow.ResizeClient(new SizeInt32(width, height));
    }

    private void RestoreOrClampPosition()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = AppWindow.Size;

        int x = _settings.FloatingLeft is double savedLeft ? (int)savedLeft : area.X + area.Width - size.Width - 24;
        int y = _settings.FloatingTop is double savedTop ? (int)savedTop : area.Y + area.Height - size.Height - 80;

        x = Math.Clamp(x, area.X, Math.Max(area.X, area.X + area.Width - size.Width));
        y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Y + area.Height - size.Height));
        AppWindow.Move(new PointInt32(x, y));
    }

    private void SnapToEdges()
    {
        if (!_settings.FloatingSnapEdges) return;

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var size = AppWindow.Size;
        const int threshold = 24;

        int x = AppWindow.Position.X;
        int y = AppWindow.Position.Y;

        if (Math.Abs(x - area.X) <= threshold) x = area.X;
        else if (Math.Abs(area.X + area.Width - (x + size.Width)) <= threshold) x = area.X + area.Width - size.Width;

        if (Math.Abs(y - area.Y) <= threshold) y = area.Y;
        else if (Math.Abs(area.Y + area.Height - (y + size.Height)) <= threshold) y = area.Y + area.Height - size.Height;

        if (x == AppWindow.Position.X && y == AppWindow.Position.Y) return;

        AppWindow.Move(new PointInt32(x, y));
        _settings.FloatingLeft = x;
        _settings.FloatingTop = y;
    }

    private static Color ParseColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;

        string value = hex.Trim().TrimStart('#');
        if (value.Length == 8) value = value[2..];
        if (value.Length != 6) return fallback;

        if (!byte.TryParse(value[..2], System.Globalization.NumberStyles.HexNumber, null, out byte r)) return fallback;
        if (!byte.TryParse(value[2..4], System.Globalization.NumberStyles.HexNumber, null, out byte g)) return fallback;
        if (!byte.TryParse(value[4..], System.Globalization.NumberStyles.HexNumber, null, out byte b)) return fallback;

        return Color.FromArgb(255, r, g, b);
    }

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed) return;
        if (_settings.FloatingLocked) return;

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
        SnapToEdges();
    }

    private void Root_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => App.Instance.ShowMain();
}
