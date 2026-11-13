using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using RemoteManager.App.Resources;
using RemoteManager.App.Views;

namespace RemoteManager.App.Connections;

/// <summary>What the lamp of a session bar shows.</summary>
public enum SessionLamp
{
    /// <summary>Not connected (before the first connect, after a disconnect).</summary>
    Idle,

    /// <summary>Connecting or logging on.</summary>
    Busy,

    /// <summary>The session is up; the clock runs.</summary>
    Connected,
}

/// <summary>
/// The bar at the top of a session window (RDP, VNC, SSH terminal, SFTP): lamp, type, name, host, status line,
/// time connected, and the window's actions (<see cref="Actions"/>). The windows extend their content into the
/// title bar, so the bar also is what the window is dragged by.
/// </summary>
public partial class SessionBar : UserControl
{
    public static readonly StyledProperty<object?> ActionsProperty =
        AvaloniaProperty.Register<SessionBar, object?>(nameof(Actions));

    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime connectedAt;
    private SessionLamp lamp;

    public SessionBar()
    {
        InitializeComponent();
        ActionsHost[!ContentControl.ContentProperty] = this[!ActionsProperty];
        clock.Tick += (_, _) => UpdateClock();
        DetachedFromVisualTree += (_, _) => clock.Stop();
    }

    /// <summary>The window's own buttons, right of the status line.</summary>
    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    /// <summary>The status line. Never contains secrets (the windows only put connection events there).</summary>
    public string Status
    {
        get => StatusLabel.Text ?? "";
        set => StatusLabel.Text = value;
    }

    public SessionLamp Lamp
    {
        get => lamp;
        set
        {
            lamp = value;
            LampDot[!Avalonia.Controls.Shapes.Shape.FillProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(value switch
            {
                SessionLamp.Connected => "RmOnlineBrush",
                SessionLamp.Busy => "RmPendingBrush",
                _ => "RmUnknownBrush",
            });

            if (value == SessionLamp.Connected && !clock.IsEnabled)
            {
                connectedAt = DateTime.UtcNow;
                UpdateClock();
                clock.Start();
            }
            else if (value != SessionLamp.Connected)
            {
                clock.Stop();
            }

            ClockLabel.IsVisible = value == SessionLamp.Connected;
        }
    }

    /// <summary>Time since the lamp turned to <see cref="SessionLamp.Connected"/>; zero when it is not.</summary>
    public TimeSpan Connected => lamp == SessionLamp.Connected ? DateTime.UtcNow - connectedAt : TimeSpan.Zero;

    /// <summary>The clock text ("12:05", "1:02:33"), empty while not connected (self-test).</summary>
    public string ClockText => ClockLabel.IsVisible ? ClockLabel.Text ?? "" : "";

    /// <summary>Shows which connection the window belongs to.</summary>
    public void Load(ConnectionItem connection)
    {
        TypeChip.Background = connection.TypeBrush;
        TypeChipText.Text = connection.TypeLabel;
        NameLabel.Text = connection.Name;
        HostLabel.Text = connection.HostLabel;
        if (Status.Length == 0)
            Status = Strings.Session_NotConnected;
    }

    private void UpdateClock()
    {
        TimeSpan time = Connected;
        ClockLabel.Text = time.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{time.Minutes:00}:{time.Seconds:00}");
    }
}
