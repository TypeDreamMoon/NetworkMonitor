using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace NetworkMonitor.WinUI.Services;

/// <summary>
/// A native layered window that draws only text (no background), so the taskbar
/// shows through completely.
/// </summary>
internal sealed class LayeredStrip : IDisposable
{
    private const string ClassName = "NetworkMonitor.TaskbarStrip";
    private const uint WsPopup = 0x80000000;
    private const long WsExLayered = 0x00080000;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExNoActivate = 0x08000000;
    private const uint UlwAlpha = 0x00000002;
    private const uint SwShowNoActivate = 4;
    private const uint SwHide = 0;
    private const uint WmDestroy = 0x0002;
    private const uint WmLButtonUp = 0x0202;

    private static readonly object ClassGate = new();
    private static bool _classRegistered;
    private static readonly ConcurrentDictionary<IntPtr, LayeredStrip> Instances = new();
    private static readonly WndProcDelegate StaticWndProc = WindowProc;
    private static readonly IntPtr ModuleHandle = GetModuleHandle(null);

    private IntPtr _hwnd;
    private int _x;
    private int _y;
    private int _width;
    private int _height;
    private double _scale = 1;
    private bool _light;
    private string _down = string.Empty;
    private string _up = string.Empty;
    private bool _visible;

    public event Action? Clicked;

    public LayeredStrip()
    {
        EnsureClass();
        _hwnd = CreateWindowExW(
            (uint)(WsExLayered | WsExToolWindow | WsExNoActivate),
            ClassName,
            string.Empty,
            WsPopup,
            0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, ModuleHandle, IntPtr.Zero);

        if (_hwnd != IntPtr.Zero)
            Instances[_hwnd] = this;
    }

    public void SetBounds(int x, int y, int width, int height, double scale, bool light)
    {
        if (_hwnd == IntPtr.Zero || width <= 0 || height <= 0) return;

        bool changed = x != _x || y != _y || width != _width || height != _height
                       || Math.Abs(scale - _scale) > 0.01 || light != _light;
        _x = x;
        _y = y;
        _width = width;
        _height = height;
        _scale = scale;
        _light = light;

        if (changed) Render();
    }

    public void SetText(string down, string up)
    {
        if (_down == down && _up == up) return;
        _down = down;
        _up = up;
        Render();
    }

    public void Show()
    {
        if (_hwnd == IntPtr.Zero) return;
        ShowWindow(_hwnd, SwShowNoActivate);
        _visible = true;
    }

    public void Hide()
    {
        if (_hwnd == IntPtr.Zero) return;
        ShowWindow(_hwnd, SwHide);
        _visible = false;
    }

    private void Render()
    {
        if (_hwnd == IntPtr.Zero || _width <= 1 || _height <= 1) return;

        using var bitmap = new Bitmap(_width, _height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAlias;

            Color textColor = _light ? Color.FromArgb(0x1A, 0x1A, 0x1A) : Color.FromArgb(0xE9, 0xEF, 0xF8);
            Color downColor = Color.FromArgb(0x3E, 0xDC, 0xC4);
            Color upColor = Color.FromArgb(0xFF, 0xB4, 0x54);

            float fontPx = Math.Max(9f, 11.5f * (float)_scale);
            using var regular = new Font("Segoe UI", fontPx, FontStyle.Regular, GraphicsUnit.Pixel);
            using var bold = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel);

            var parts = new (string Text, Font Font, Color Color)[]
            {
                ("↓", bold, downColor),
                (" " + _down, regular, textColor),
                ("   ↑", bold, upColor),
                (" " + _up, regular, textColor),
            };

            using var format = new StringFormat(StringFormat.GenericTypographic)
            {
                FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
            };

            float total = 0;
            foreach (var part in parts)
                total += graphics.MeasureString(part.Text, part.Font, PointF.Empty, format).Width;

            float x = Math.Max(0, (_width - total) / 2f);
            float y = (_height - regular.GetHeight(graphics)) / 2f;

            using var shadow = new SolidBrush(Color.FromArgb(_light ? 0 : 150, 0, 0, 0));

            foreach (var part in parts)
            {
                if (!_light)
                    graphics.DrawString(part.Text, part.Font, shadow, new PointF(x + 1, y + 1), format);

                using var brush = new SolidBrush(part.Color);
                graphics.DrawString(part.Text, part.Font, brush, new PointF(x, y), format);
                x += graphics.MeasureString(part.Text, part.Font, PointF.Empty, format).Width;
            }
        }

        UpdateLayered(bitmap);

        if (!_visible) Show();
    }

    private void UpdateLayered(Bitmap bitmap)
    {
        int stride = _width * 4;
        var pixels = new byte[stride * _height];

        var data = bitmap.LockBits(
            new Rectangle(0, 0, _width, _height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < _height; y++)
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * stride, stride);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        // UpdateLayeredWindow wants premultiplied alpha.
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte alpha = pixels[i + 3];
            if (alpha == 255) continue;
            pixels[i] = (byte)(pixels[i] * alpha / 255);
            pixels[i + 1] = (byte)(pixels[i + 1] * alpha / 255);
            pixels[i + 2] = (byte)(pixels[i + 2] * alpha / 255);
        }

        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return;

        IntPtr memoryDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;

        try
        {
            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = _width,
                    Height = -_height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0,
                },
            };

            hBitmap = CreateDIBSection(screenDc, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (hBitmap == IntPtr.Zero || bits == IntPtr.Zero) return;

            Marshal.Copy(pixels, 0, bits, pixels.Length);
            previous = SelectObject(memoryDc, hBitmap);

            var destination = new Point32 { X = _x, Y = _y };
            var size = new Size32 { Width = _width, Height = _height };
            var source = new Point32();
            var blend = new BlendFunction
            {
                BlendOp = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = 1,
            };

            UpdateLayeredWindow(_hwnd, screenDc, ref destination, ref size,
                memoryDc, ref source, 0, ref blend, UlwAlpha);
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memoryDc, previous);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static void EnsureClass()
    {
        lock (ClassGate)
        {
            if (_classRegistered) return;

            var windowClass = new WndClassEx
            {
                Size = (uint)Marshal.SizeOf<WndClassEx>(),
                Style = 0,
                WindowProc = Marshal.GetFunctionPointerForDelegate(StaticWndProc),
                Instance = ModuleHandle,
                Cursor = LoadCursorW(IntPtr.Zero, new IntPtr(32649)),
                ClassName = ClassName,
            };

            RegisterClassExW(ref windowClass);
            _classRegistered = true;
        }
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WmLButtonUp:
                if (Instances.TryGetValue(hwnd, out var strip)) strip.Clicked?.Invoke();
                return IntPtr.Zero;
            case WmDestroy:
                Instances.TryRemove(hwnd, out _);
                return IntPtr.Zero;
            default:
                return DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            Instances.TryRemove(_hwnd, out _);
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public IntPtr WindowProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Size32
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WndClassEx windowClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr cursorName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr screenDc, ref Point32 destination, ref Size32 size,
        IntPtr sourceDc, ref Point32 source, uint colorKey, ref BlendFunction blend, uint flags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
}
