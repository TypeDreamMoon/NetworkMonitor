using Microsoft.UI.Xaml;
using Microsoft.Win32;
using NetworkMonitor.Core.Formatting;
using NetworkMonitor.Core.Sampling;
using NetworkMonitor.WinUI.Interop;

namespace NetworkMonitor.WinUI.Services;

/// <summary>
/// Background-free taskbar speed strip: a native layered window that renders only
/// text, so the taskbar itself shows through.
/// </summary>
public sealed class TaskbarOverlay : IDisposable
{
    private const int StripWidthDip = 180;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(800) };

    private LayeredStrip? _strip;
    private bool _disposed;
    private bool _shouldShow;
    private SpeedSample? _lastSample;

    public TaskbarOverlay()
    {
        _timer.Tick += (_, _) => Reposition();
    }

    public void Show()
    {
        if (_disposed) return;
        _shouldShow = true;
        EnsureStrip();
        _timer.Start();
        Reposition();
    }

    public void Hide()
    {
        _shouldShow = false;
        _timer.Stop();
        _strip?.Hide();
    }

    public void SetSpeed(SpeedSample sample)
    {
        _lastSample = sample;
        ApplyText();
    }

    private void EnsureStrip()
    {
        if (_strip is not null) return;

        _strip = new LayeredStrip();
        _strip.Clicked += () => App.Instance.ShowMain();
    }

    private void ApplyText()
    {
        if (_strip is null || _lastSample is not { } sample) return;

        _strip.SetText(
            Compact(ByteFormatter.Rate(sample.DownBps)),
            Compact(ByteFormatter.Rate(sample.UpBps)));
    }

    private void Reposition()
    {
        if (_disposed || !_shouldShow) return;

        try
        {
            IntPtr taskbar = Native.FindWindow("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero
                || !Native.TryGetTaskbar(out var taskbarRect, out int anchorLeft)
                || taskbarRect.Width < taskbarRect.Height * 2
                || !Native.IsTaskbarOnScreen(taskbarRect))
            {
                _strip?.Hide();
                return;
            }

            double scale = Native.GetDpiForWindow(taskbar) / 96.0;
            if (scale <= 0) scale = 1;

            int width = Math.Max(100, (int)Math.Round(StripWidthDip * scale));
            int x = anchorLeft - width - 4;
            if (x <= taskbarRect.Left + 8)
            {
                _strip?.Hide();
                return;
            }

            bool light = IsLightTaskbar();
            int sampleX = x - 12;
            if (sampleX > taskbarRect.Left
                && Native.TryGetScreenColor(sampleX, taskbarRect.Top + taskbarRect.Height / 2, out var sample))
            {
                light = 0.2126 * sample.R + 0.7152 * sample.G + 0.0722 * sample.B > 140;
            }

            EnsureStrip();
            _strip!.SetBounds(x, taskbarRect.Top, width, taskbarRect.Height, scale, light);
            ApplyText();
            _strip.Show();
        }
        catch (Exception ex)
        {
            Log.Write("任务栏条定位失败", ex);
            _strip?.Hide();
        }
    }

    private static string Compact(string text) => text.Replace(" ", string.Empty);

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
        _strip?.Dispose();
        _strip = null;
    }
}
