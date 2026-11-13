using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;
using RemoteManager.App.Services;

namespace RemoteManager.App.Views;

/// <summary>
/// The note that appears once when the overview first hides in the notification area: a small window of the
/// app's own in the lower right corner of the screen the overview was on (no Windows balloon). It takes no
/// focus and closes by itself.
/// </summary>
public partial class TrayHintWindow : Window
{
    private static readonly TimeSpan ShownFor = TimeSpan.FromSeconds(14);
    private const int ScreenMargin = 16;

    private readonly DispatcherTimer timer = new() { Interval = ShownFor };
    private Screen? screen;

    public TrayHintWindow()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
        timer.Tick += (_, _) => Close();
        // The height follows the text; the place in the corner is only known once it is laid out.
        Opened += (_, _) =>
        {
            MoveToCorner();
            timer.Start();
        };
        Closed += (_, _) => timer.Stop();
    }

    /// <summary>Shows the note on the screen <paramref name="main"/> is on; <paramref name="hotkey"/> is the launcher's global hotkey, if one is registered.</summary>
    internal void ShowFor(MainWindow main, KeyGesture? hotkey)
    {
        HotkeyLine.IsVisible = hotkey is not null;
        HotkeyKeys.Children.Clear();
        foreach (string cap in hotkey is null ? [] : KeyCaps.For(hotkey))
        {
            var key = new Border { Child = new TextBlock { Text = cap } };
            key.Classes.Add("keycap");
            HotkeyKeys.Children.Add(key);
        }

        screen = main.Screens.ScreenFromWindow(main) ?? main.Screens.Primary;
        if (screen is not null)
        {
            // A first guess for the height, so the window does not flash somewhere else before it is laid out.
            PixelRect area = screen.WorkingArea;
            Position = new PixelPoint(
                area.Right - (int)((Width + ScreenMargin) * screen.Scaling),
                area.Bottom - (int)((150 + ScreenMargin) * screen.Scaling));
        }

        Show();
    }

    private void MoveToCorner()
    {
        if (screen is null)
            return;
        PixelRect area = screen.WorkingArea;
        Size size = FrameSize ?? ClientSize;
        Position = new PixelPoint(
            area.Right - (int)((size.Width + ScreenMargin) * screen.Scaling),
            area.Bottom - (int)((size.Height + ScreenMargin) * screen.Scaling));
    }
}
