using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace NetworkMonitor.WinUI.Interop;

internal static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_NOACTIVATE = 0x08000000;

    public const int SW_HIDE = 0;
    public const int SW_SHOWNOACTIVATE = 4;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_SHOWWINDOW = 0x0040;

    public static readonly IntPtr HWND_TOPMOST = new(-1);

    private const uint MonitorDefaultToNearest = 2;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmBorderColor = 34;
    private const int DwmRound = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr hdc, int x, int y);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static long GetStyle(IntPtr hwnd) => GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();

    public static void SetStyle(IntPtr hwnd, long style) =>
        SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));

    public static long GetExStyle(IntPtr hwnd) => GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();

    public static void SetExStyle(IntPtr hwnd, long style) =>
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));

    public static bool IsDirectChildOf(IntPtr hwnd, IntPtr parent) =>
        GetAncestor(hwnd, GA_PARENT) == parent;

    public delegate IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    public static IntPtr SetWindowProc(IntPtr hwnd, WindowProc proc) =>
        SetWindowLongPtr(hwnd, GWL_WNDPROC, Marshal.GetFunctionPointerForDelegate(proc));

    public static IntPtr CallPreviousWindowProc(IntPtr previous, IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam) =>
        CallWindowProc(previous, hwnd, message, wParam, lParam);

    /// <summary>Removes WS_MAXIMIZEBOX so the system caption no longer draws a maximize button.</summary>
    public static void RemoveMaximizeBox(Window window)
    {
        IntPtr hwnd = Handle(window);
        if (hwnd == IntPtr.Zero) return;

        long style = GetStyle(hwnd);
        if ((style & WS_MAXIMIZEBOX) == 0) return;

        SetStyle(hwnd, style & ~WS_MAXIMIZEBOX);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    /// <summary>Reads the composited desktop pixel (sRGB). Used to match the taskbar tint.</summary>
    public static bool TryGetScreenColor(int x, int y, out Windows.UI.Color color)
    {
        color = default;
        IntPtr dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return false;

        try
        {
            uint value = GetPixel(dc, x, y);
            if (value == 0xFFFFFFFF) return false;

            color = Windows.UI.Color.FromArgb(255,
                (byte)(value & 0xFF),
                (byte)((value >> 8) & 0xFF),
                (byte)((value >> 16) & 0xFF));
            return true;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
    }

    public static IntPtr Handle(Window window) =>
        WinRT.Interop.WindowNative.GetWindowHandle(window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    public static void BeginDrag(Window window)
    {
        IntPtr hwnd = Handle(window);
        if (hwnd == IntPtr.Zero) return;

        ReleaseCapture();
        SendMessage(hwnd, 0x00A1, new IntPtr(2), IntPtr.Zero); // WM_NCLBUTTONDOWN / HTCAPTION
    }

    public static void MakeToolWindow(Window window, bool noActivate)
    {
        IntPtr hwnd = Handle(window);
        if (hwnd == IntPtr.Zero) return;

        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        style |= WS_EX_TOOLWINDOW;
        if (noActivate) style |= WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
    }

    public static void RoundCorners(Window window)
    {
        IntPtr hwnd = Handle(window);
        if (hwnd == IntPtr.Zero) return;
        int preference = DwmRound;
        DwmSetWindowAttribute(hwnd, DwmWindowCornerPreference, ref preference, sizeof(int));
    }

    public static void UseDarkFrame(Window window)
    {
        IntPtr hwnd = Handle(window);
        if (hwnd == IntPtr.Zero) return;

        int on = 1;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    public static void SetDarkBorder(Window window) => SetBorderColor(window, 0x003D2B20);

    public const int GWL_STYLE = -16;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const long WS_CHILD = 0x40000000L;
    public const long WS_POPUP = 0x80000000L;
    private const int GWL_WNDPROC = -4;
    private const uint GA_PARENT = 1;
    private const long WS_CAPTION = 0x00C00000L;
    private const long WS_DLGFRAME = 0x00400000L;
    private const long WS_THICKFRAME = 0x00040000L;
    private const long WS_BORDER = 0x00800000L;
    private const long WS_SYSMENU = 0x00080000L;
    private const long WS_MINIMIZEBOX = 0x00020000L;
    private const long WS_MAXIMIZEBOX = 0x00010000L;

    /// <summary>
    /// WinUI's SetBorderAndTitleBar(false, false) leaves WS_DLGFRAME behind, which draws a
    /// bright 1-2px frame around the window (the "white ring"). Strip the frame bits entirely.
    /// </summary>
    public static void StripWindowFrame(Window window)
    {
        IntPtr hwnd = Handle(window);
        if (hwnd == IntPtr.Zero) return;

        long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        long stripped = style & ~(WS_CAPTION | WS_DLGFRAME | WS_THICKFRAME | WS_BORDER
                                  | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
        SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(stripped));

        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    public static void SetBorderColor(Window window, int colorRef)
    {
        IntPtr hwnd = Handle(window);
        if (hwnd == IntPtr.Zero) return;
        DwmSetWindowAttribute(hwnd, DwmBorderColor, ref colorRef, sizeof(int));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    public static (int X, int Y) CursorPosition()
    {
        GetCursorPos(out var point);
        return (point.X, point.Y);
    }

    /// <summary>Finds the primary taskbar and the left edge of its notification area.</summary>
    public static bool TryGetTaskbar(out RECT taskbar, out int anchorLeft)
    {
        taskbar = default;
        anchorLeft = 0;

        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return false;
        if (!GetWindowRect(tray, out taskbar)) return false;

        anchorLeft = taskbar.Right;
        IntPtr notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify != IntPtr.Zero && GetWindowRect(notify, out var notifyRect) && notifyRect.Width > 0)
            anchorLeft = notifyRect.Left;

        return true;
    }

    public static bool IsTaskbarOnScreen(in RECT taskbar)
    {
        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return false;

        IntPtr monitor = MonitorFromWindow(tray, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return false;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;

        var screen = info.rcMonitor;
        return taskbar.Bottom > screen.Top + 2
               && taskbar.Top < screen.Bottom - 2
               && taskbar.Right > screen.Left + 2
               && taskbar.Left < screen.Right - 2;
    }
}
