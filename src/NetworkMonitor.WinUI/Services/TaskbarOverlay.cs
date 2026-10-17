using Microsoft.UI.Xaml;
using Microsoft.Win32;
using NetworkMonitor.Core.Sampling;
using NetworkMonitor.WinUI.Interop;
using Windows.UI;

namespace NetworkMonitor.WinUI.Services;

/// <summary>
/// Taskbar speed strip. Preferred mode: dock as a WS_CHILD of Shell_TrayWnd
/// (follows taskbar position/DPI/auto-hide automatically). If docking is not possible,
/// fall back to the previous topmost acrylic overlay.
/// </summary>
public sealed class TaskbarOverlay : IDisposable
{
    private const int StripWidthDip = 180;

    private readonly DispatcherTimer _timer = new();
    private TaskbarOverlayWindow? _window;
    private IntPtr _hwnd;

    private bool _disposed;
    private bool _shouldShow;
    private bool _docked;
    private bool? _appliedDockedAppearance;
    private IntPtr _attachedTaskbar;
    private long _originalStyle;
    private long _originalExStyle;
    private SpeedSample? _lastSample;
    private Color? _dockedBackground;
    private int _themeCheckCounter;

    public TaskbarOverlay()
    {
        _timer.Interval = TimeSpan.FromMilliseconds(800);
        _timer.Tick += (_, _) => Tick();
    }

    public void Show()
    {
        _shouldShow = true;
        _timer.Start();
        EnsureWindow();
        Tick();
    }

    public void Hide()
    {
        _shouldShow = false;
        _timer.Stop();
        Detach();

        try
        {
            _window?.AppWindow.Hide();
        }
        catch
        {
            // window may already be gone
        }
    }

    public void SetSpeed(SpeedSample sample)
    {
        _lastSample = sample;

        if (_window is not null && _hwnd != IntPtr.Zero && Native.IsWindow(_hwnd))
            _window.SetSpeed(sample);
    }

    private void EnsureWindow()
    {
        if (_window is null || _hwnd == IntPtr.Zero || !Native.IsWindow(_hwnd))
        {
            _window = new TaskbarOverlayWindow();
            _window.Activate();
            _hwnd = Native.Handle(_window);
        }

        if (_lastSample is { } sample) _window.SetSpeed(sample);
    }

    private void Tick()
    {
        if (_disposed || !_shouldShow) return;

        try
        {
            if (_hwnd == IntPtr.Zero || !Native.IsWindow(_hwnd))
            {
                Recreate();
                return;
            }

            if (_docked)
            {
                // Explorer restart destroys the parent taskbar (and this child with it).
                // The old WinUI Window can no longer be touched — even Close() faults the
                // process — so drop it and build a fresh window.
                if (!Native.IsWindow(_attachedTaskbar)
                    || !Native.IsDirectChildOf(_hwnd, _attachedTaskbar)
                    || Native.FindWindow("Shell_TrayWnd", null) != _attachedTaskbar)
                {
                    Recreate();
                    return;
                }

                PositionDocked();
            }
            else if (!TryDock())
            {
                PositionOverlay();
            }

            if (++_themeCheckCounter >= 8)
            {
                _themeCheckCounter = 0;
                UpdateDockedAppearance(force: false);
            }
        }
        catch (Exception ex)
        {
            Log.Write("任务栏窗口定位失败", ex);

            try
            {
                PositionOverlay();
            }
            catch
            {
                // give up this tick
            }
        }
    }

    private bool TryDock()
    {
        IntPtr taskbar = Native.FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return false;
        if (!Native.GetWindowRect(taskbar, out var taskbarRect)) return false;
        if (taskbarRect.Width < taskbarRect.Height * 2 || taskbarRect.Height <= 0) return false;

        IntPtr hwnd = _hwnd;
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return false;

        if (!_docked || _attachedTaskbar != taskbar)
        {
            Detach();

            _originalStyle = Native.GetStyle(hwnd);
            _originalExStyle = Native.GetExStyle(hwnd);

            Native.SetStyle(hwnd, (_originalStyle & ~Native.WS_POPUP) | Native.WS_CHILD);
            Native.SetExStyle(hwnd, _originalExStyle | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
            Native.SetParent(hwnd, taskbar);

            if (!Native.IsDirectChildOf(hwnd, taskbar))
            {
                RestoreStyles(hwnd);
                return false;
            }

            _docked = true;
            _attachedTaskbar = taskbar;
        }

        bool positioned = PositionDocked();
        UpdateDockedAppearance(force: true);
        return positioned;
    }

    private bool PositionDocked()
    {
        IntPtr hwnd = _hwnd;
        if (hwnd == IntPtr.Zero || !Native.GetWindowRect(_attachedTaskbar, out var taskbarRect)) return false;

        int anchorLeft = taskbarRect.Right;
        IntPtr notify = Native.FindWindowEx(_attachedTaskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify != IntPtr.Zero && Native.GetWindowRect(notify, out var notifyRect) && notifyRect.Width > 0)
            anchorLeft = notifyRect.Left;

        double scale = Native.GetDpiForWindow(_attachedTaskbar) / 96.0;
        if (scale <= 0) scale = 1;

        int width = Math.Max(100, (int)Math.Round(StripWidthDip * scale));
        int height = taskbarRect.Height;
        int x = (anchorLeft - taskbarRect.Left) - width - 4;

        if (x < 4)
        {
            Native.ShowWindow(hwnd, Native.SW_HIDE);
            return true;
        }

        // Raise above the taskbar's XAML composition bridge; otherwise a recreated child
        // stays behind it and is invisible.
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, 0, width, height,
            Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED | Native.SWP_SHOWWINDOW);
        return true;
    }

    private void PositionOverlay()
    {
        IntPtr hwnd = _hwnd;
        if (hwnd == IntPtr.Zero) return;

        if (!Native.TryGetTaskbar(out var taskbar, out int anchorLeft)
            || taskbar.Width < taskbar.Height * 2
            || !Native.IsTaskbarOnScreen(taskbar))
        {
            Native.ShowWindow(hwnd, Native.SW_HIDE);
            return;
        }

        double scale = Native.GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0) scale = 1;

        int width = Math.Max(100, (int)Math.Round(StripWidthDip * scale));
        int x = anchorLeft - width - 4;
        if (x <= taskbar.Left + 8)
        {
            Native.ShowWindow(hwnd, Native.SW_HIDE);
            return;
        }

        ApplyAppearance(docked: false);
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, taskbar.Top, width, taskbar.Height,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    private void Recreate()
    {
        IntPtr oldTaskbar = _attachedTaskbar;
        IntPtr oldHwnd = _hwnd;
        TaskbarOverlayWindow? oldWindow = _window;

        _docked = false;
        _attachedTaskbar = IntPtr.Zero;
        _appliedDockedAppearance = null;
        _dockedBackground = null;
        _window = null;
        _hwnd = IntPtr.Zero;

        // Only close a window that still exists. After an Explorer restart the old window is
        // already destroyed, and WinUI's Close() would fault the process.
        if (oldWindow is not null && oldHwnd != IntPtr.Zero && Native.IsWindow(oldHwnd))
        {
            try
            {
                oldWindow.Close();
            }
            catch
            {
                // best effort
            }
        }

        _window = new TaskbarOverlayWindow();
        _window.Activate();
        _hwnd = Native.Handle(_window);
        if (_lastSample is { } sample) _window.SetSpeed(sample);

        if (!TryDock())
            PositionOverlay();

        Log.Write($"任务栏条重建（旧任务栏={(oldTaskbar == IntPtr.Zero ? "无" : oldTaskbar.ToString())} 旧窗口={(oldHwnd == IntPtr.Zero ? "无" : oldHwnd.ToString())}）");
    }

    private void Detach()
    {
        if (!_docked) return;

        IntPtr hwnd = _hwnd;
        if (hwnd != IntPtr.Zero && Native.IsWindow(hwnd))
        {
            Native.SetParent(hwnd, IntPtr.Zero);
            RestoreStyles(hwnd);
            Native.ShowWindow(hwnd, Native.SW_HIDE);
        }

        _docked = false;
        _attachedTaskbar = IntPtr.Zero;
        _appliedDockedAppearance = null;
        _dockedBackground = null;
    }

    private void RestoreStyles(IntPtr hwnd)
    {
        Native.SetStyle(hwnd, _originalStyle);
        Native.SetExStyle(hwnd, _originalExStyle);
        Native.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER
            | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
    }

    private void ApplyAppearance(bool docked)
    {
        if (_appliedDockedAppearance == docked) return;
        _appliedDockedAppearance = docked;

        if (_window is null) return;

        if (docked)
            UpdateDockedAppearance(force: true);
        else
            _window.ApplyOverlayAppearance();
    }

    /// <summary>
    /// Child windows cannot show DWM acrylic, so sample the taskbar tint just left of the
    /// strip and paint the same colour. Visually the strip has no background of its own.
    /// </summary>
    private void UpdateDockedAppearance(bool force)
    {
        if (!_docked || _window is null || _hwnd == IntPtr.Zero) return;

        bool light = IsLightTaskbar();
        Color color = light
            ? Color.FromArgb(255, 0xF3, 0xF3, 0xF3)
            : Color.FromArgb(255, 0x20, 0x20, 0x20);

        if (Native.GetWindowRect(_hwnd, out var strip) && strip.Width > 0
            && Native.IsWindow(_attachedTaskbar)
            && Native.GetWindowRect(_attachedTaskbar, out var taskbar))
        {
            if (SampleTaskbarColor(strip, taskbar) is { } sampled)
                color = sampled;
        }

        if (!force && _dockedBackground is { } previous && ColorDistance(previous, color) <= 3)
            return;

        _dockedBackground = color;
        _appliedDockedAppearance = true;
        _window.ApplyDockedAppearance(color);
    }

    private static Color? SampleTaskbarColor(Native.RECT strip, Native.RECT taskbar)
    {
        int y = strip.Top + strip.Height / 2;
        var samples = new List<Color>(3);

        foreach (int offset in new[] { 10, 24, 38 })
        {
            int x = strip.Left - offset;
            if (x <= taskbar.Left + 2) break;
            if (Native.TryGetScreenColor(x, y, out var color)) samples.Add(color);
        }

        if (samples.Count == 0) return null;

        samples.Sort((a, b) => Luma(a).CompareTo(Luma(b)));
        return samples[samples.Count / 2];
    }

    private static double Luma(Color color) =>
        0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B;

    private static int ColorDistance(Color a, Color b) =>
        Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

    private static bool IsLightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        Detach();

        try
        {
            if (_window is not null && _hwnd != IntPtr.Zero && Native.IsWindow(_hwnd))
                _window.Close();
        }
        catch
        {
            // already closed
        }

        _window = null;
        _hwnd = IntPtr.Zero;
    }
}
