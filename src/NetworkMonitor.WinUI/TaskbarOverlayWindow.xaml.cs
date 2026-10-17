using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NetworkMonitor.Core.Formatting;
using NetworkMonitor.Core.Sampling;
using NetworkMonitor.WinUI.Interop;
using Windows.UI;


namespace NetworkMonitor.WinUI;

public sealed partial class TaskbarOverlayWindow : Window
{
    private static readonly Color DarkBackground = Color.FromArgb(255, 0x20, 0x20, 0x20);
    private static readonly Color LightBackground = Color.FromArgb(255, 0xF3, 0xF3, 0xF3);
    private static readonly Color DarkText = Color.FromArgb(255, 0xE9, 0xEF, 0xF8);
    private static readonly Color LightText = Color.FromArgb(255, 0x1A, 0x1A, 0x1A);

    public TaskbarOverlayWindow()
    {
        InitializeComponent();

        Root.Loaded += (_, _) =>
        {
            Native.UseDarkFrame(this);
            Native.StripWindowFrame(this);
            Native.RoundCorners(this);
        };

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        Native.MakeToolWindow(this, noActivate: true);
        Native.RoundCorners(this);
    }

    public void SetSpeed(SpeedSample sample)
    {
        DownText.Text = Compact(ByteFormatter.Rate(sample.DownBps));
        UpText.Text = Compact(ByteFormatter.Rate(sample.UpBps));
    }

    /// <summary>Docked mode: a plain strip that blends into the taskbar (no DWM backdrop on child windows).</summary>
    public void ApplyDockedAppearance(Color background)
    {
        try
        {
            SystemBackdrop = null;
        }
        catch
        {
            // nothing to remove
        }

        // Child windows cannot show the DWM backdrop, so paint the sampled taskbar tint
        // instead — visually the strip has no background of its own.
        Root.Background = new SolidColorBrush(background);

        double luma = 0.2126 * background.R + 0.7152 * background.G + 0.0722 * background.B;
        var text = new SolidColorBrush(luma > 140 ? LightText : DarkText);
        DownText.Foreground = text;
        UpText.Foreground = text;
    }

    /// <summary>Fallback mode: keep the acrylic look when docking is not possible.</summary>
    public void ApplyOverlayAppearance()
    {
        Root.Background = new SolidColorBrush(Color.FromArgb(0xB3, 0x0B, 0x0F, 0x16));
        var text = new SolidColorBrush(DarkText);
        DownText.Foreground = text;
        UpText.Foreground = text;

        try
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }
        catch
        {
            // opaque fallback is fine
        }
    }

    private static string Compact(string text) => text.Replace(" ", string.Empty);

    private void Root_Tapped(object sender, TappedRoutedEventArgs e) => App.Instance.ShowMain();
}
