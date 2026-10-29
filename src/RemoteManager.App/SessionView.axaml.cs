using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Devolutions.MsRdpEx.Avalonia;
using FluentAvalonia.UI.Controls;
using MSTSCLib;

namespace RemoteManager.App;

/// <summary>
/// One tab: a small connection form plus an embedded <see cref="RdpClientView"/>.
/// </summary>
public partial class SessionView : UserControl, IDisposable
{
    // A periodic in-memory frame lets a server-initiated disconnect still produce a
    // thumbnail: by the time Disconnected fires, the control may already show nothing.
    private static readonly TimeSpan FrameCaptureInterval = TimeSpan.FromSeconds(10);

    private readonly DispatcherTimer frameTimer;
    private SessionFrame? lastGoodFrame;
    private bool disposed;

    // Workaround for MsRdpEx 2026.10.26: RdpClientView.Disconnected only fires for the
    // DisconnectedWithDetails event, which the RDP v11 control (classic IMsTscAxEvents sink,
    // DISPID 4) never raises. Its IsConnectionActive/IsLoginCompleted therefore stay true after
    // a disconnect. We subscribe to the raw OnDisconnected ourselves and keep our own state.
    private RdpClientEventSubscription? clientEvents;

    public SessionView() : this("Session")
    {
    }

    public SessionView(string title)
    {
        InitializeComponent();
        Title = title;
        Id = Guid.NewGuid().ToString("N")[..8];

        frameTimer = new DispatcherTimer { Interval = FrameCaptureInterval };
        frameTimer.Tick += (_, _) => CaptureFrameToMemory();

        PrefillFromEnvironment();

        ConnectButton.Click += (_, _) => Connect();
        DisconnectButton.Click += (_, _) => Disconnect();
        SnapshotButton.Click += (_, _) => SaveSnapshot("manual");
        FullScreenButton.Click += (_, _) => RdpView.FullScreen = true;
        InWindowDialogButton.Click += OnInWindowDialogClick;
        WindowDialogButton.Click += OnWindowDialogClick;

        RdpView.ClientReady += (_, _) =>
        {
            Log("RDP client ready.");
            clientEvents = RdpView.GetClient<IMsRdpClient>().Subscribe();
            clientEvents.Disconnected += OnDisconnected;
        };
        RdpView.StatusChanged += (_, e) => SetStatus(e.Message);
        RdpView.LoginCompleted += OnLoginCompleted;
        // Kept in case a later MsRdpEx raises it; OnDisconnected ignores the duplicate.
        RdpView.Disconnected += (_, _) => OnDisconnected(reason: -1);
        RdpView.RemoteDesktopSizeChanged += (_, e) =>
        {
            LastRemoteDesktopSize = new PixelSize(e.Width, e.Height);
            Log($"Remote desktop size: {e.Width}x{e.Height} (viewport {RdpView.ViewportPixelSize.Width}x{RdpView.ViewportPixelSize.Height}).");
        };
    }

    public string Id { get; }
    public string Title { get; private set; }
    public event EventHandler? TitleChanged;

    /// <summary>Raised for every status line (connection events, errors, spike diagnostics). Never contains secrets.</summary>
    public event EventHandler<string>? StatusLogged;
    public event EventHandler? SessionLoggedIn;
    public event EventHandler? SessionDisconnected;

    public RdpClientView Rdp => RdpView;

    /// <summary>Between Connect and the disconnect event (our own state, see clientEvents).</summary>
    public bool IsSessionActive { get; private set; }

    /// <summary>Logon finished and the session has not disconnected since.</summary>
    public bool IsLoggedIn { get; private set; }

    public int LastDisconnectReason { get; private set; }
    public PixelSize LastRemoteDesktopSize { get; private set; }
    public string? LastThumbnailPath { get; private set; }
    public string LastStatus => StatusText.Text ?? string.Empty;

    public string Host { get => HostBox.Text ?? ""; set => HostBox.Text = value; }

    /// <summary>Called by the window when this tab becomes the visible one.</summary>
    public void OnActivated()
    {
        if (IsLoggedIn)
            Dispatcher.UIThread.Post(() => RdpView.FocusSession(), DispatcherPriority.Background);
    }

    public void Connect()
    {
        string host = HostBox.Text?.Trim() ?? "";
        if (host.Length == 0)
        {
            SetStatus("Host is required.");
            return;
        }

        if (!RdpView.IsClientReady)
        {
            SetStatus("RDP client is not ready yet.");
            return;
        }

        (int width, int height, RdpDisplayMode mode) = DisplayBox.SelectedIndex switch
        {
            1 => (0, 0, RdpDisplayMode.SmartSizing),
            2 => (1920, 1080, RdpDisplayMode.SmartSizing),
            3 => (1280, 720, RdpDisplayMode.SmartSizing),
            _ => (0, 0, RdpDisplayMode.FitToWindow),
        };

        RdpView.UseRemoteKeyboardShortcuts = KeyboardHookBox.IsChecked == true;
        RdpView.DisplayMode = mode;

        // Show the surface and lay it out first: the initial session size comes from the viewport.
        ShowSessionSurface();
        UpdateLayout();
        IsSessionActive = true;

        try
        {
            // Server authentication: warn on certificate problems instead of silently connecting.
            // RdpClientView.Connect leaves AuthenticationLevel untouched, so set it on the client first.
            RdpView.GetClient<MSTSCLib.IMsRdpClient10>().AdvancedSettings9.AuthenticationLevel = 2;

            var settings = new RdpConnectionSettings(
                HostName: host,
                UserName: UserBox.Text?.Trim() ?? "",
                Password: PasswordBox.Text ?? "",
                Domain: DomainBox.Text?.Trim() ?? "",
                DesktopWidth: width,
                DesktopHeight: height);

            RdpView.Connect(settings);
        }
        catch (Exception ex)
        {
            IsSessionActive = false;
            SetStatus($"Connect failed: {ex.Message}");
            ShowThumbnail();
            return;
        }
        finally
        {
            // The password was handed to the control (ClearTextPassword); drop our copy.
            PasswordBox.Text = "";
        }

        SetTitle(host);
        ConnectButton.IsEnabled = false;
        DisconnectButton.IsEnabled = true;
    }

    public void Disconnect()
    {
        // Capture while the session is still painting; after Disconnect the surface is gone.
        if (IsLoggedIn)
            SaveSnapshot("disconnect");

        RdpView.Disconnect();
    }

    public string? SaveSnapshot(string reason)
    {
        SessionFrame? frame = SessionCapture.Capture(RdpView.HostWindowHandle);
        if (frame is null || frame.IsBlank)
        {
            Log($"Snapshot ({reason}): live capture {(frame is null ? "failed" : "was blank")}, using last good frame.");
            frame = lastGoodFrame;
        }
        else
        {
            lastGoodFrame = frame;
        }

        if (frame is null)
        {
            Log($"Snapshot ({reason}): no frame available.");
            return null;
        }

        string path = Path.Combine(AppPaths.Thumbnails, $"{Id}.png");
        frame.SavePng(path);
        LastThumbnailPath = path;
        Log($"Snapshot ({reason}) saved: {path} ({frame.Width}x{frame.Height}, method {frame.Method}).");
        return path;
    }

    private void CaptureFrameToMemory()
    {
        if (!IsLoggedIn || !IsEffectivelyVisible)
            return;

        SessionFrame? frame = SessionCapture.Capture(RdpView.HostWindowHandle);
        if (frame is { IsBlank: false })
            lastGoodFrame = frame;
    }

    private void OnLoginCompleted(object? sender, EventArgs e)
    {
        IsLoggedIn = true;
        SnapshotButton.IsEnabled = true;
        FullScreenButton.IsEnabled = true;
        frameTimer.Start();
        RdpView.FocusSession();
        SessionLoggedIn?.Invoke(this, EventArgs.Empty);
    }

    private void OnDisconnected(int reason)
    {
        if (!IsSessionActive)
            return;

        IsSessionActive = false;
        IsLoggedIn = false;
        LastDisconnectReason = reason;
        frameTimer.Stop();
        SetStatus($"Disconnected: {DescribeDisconnect(reason)}");

        // Server-side or network disconnect: the live surface may already be blank,
        // SaveSnapshot falls back to the last periodic frame.
        if (lastGoodFrame is not null && LastThumbnailPath is null)
            SaveSnapshot("server-disconnect");

        ConnectButton.IsEnabled = true;
        DisconnectButton.IsEnabled = false;
        SnapshotButton.IsEnabled = false;
        FullScreenButton.IsEnabled = false;
        ShowThumbnail();
        SessionDisconnected?.Invoke(this, EventArgs.Empty);
    }

    private string DescribeDisconnect(int reason)
    {
        if (reason < 0)
            return "reported by RdpClientView";

        try
        {
            var client = RdpView.GetClient<IMsRdpClient10>();
            string? text = client.GetErrorDescription((uint)reason, (uint)client.ExtendedDisconnectReason);
            return string.IsNullOrWhiteSpace(text) ? $"reason {reason}" : $"{text.Trim()} (reason {reason})";
        }
        catch (Exception)
        {
            return $"reason {reason}";
        }
    }

    private void ShowSessionSurface()
    {
        LastThumbnailPath = null;
        ThumbnailImage.IsVisible = false;
        PlaceholderText.IsVisible = false;
        RdpView.IsVisible = true;
    }

    private void ShowThumbnail()
    {
        // Hide the native surface so the (Avalonia-drawn) thumbnail or placeholder is not covered by it.
        RdpView.IsVisible = false;
        if (LastThumbnailPath is null || !File.Exists(LastThumbnailPath))
        {
            PlaceholderText.Text = $"Disconnected. {LastStatus}";
            PlaceholderText.IsVisible = true;
            return;
        }

        ThumbnailImage.Source = new Bitmap(LastThumbnailPath);
        ThumbnailImage.IsVisible = true;
    }

    private async void OnInWindowDialogClick(object? sender, RoutedEventArgs e) =>
        await ShowInWindowDialog(useAirspaceWorkaround: true).Closed;

    /// <summary>
    /// FAContentDialog renders in the window's overlay layer, i.e. below the RDP child HWND.
    /// The workaround from the plan hides the native view (showing its snapshot instead)
    /// while the dialog is open.
    /// </summary>
    public (FAContentDialog Dialog, Task Closed) ShowInWindowDialog(bool useAirspaceWorkaround)
    {
        bool hideSurface = useAirspaceWorkaround && RdpView.IsVisible;
        if (hideSurface)
        {
            if (IsLoggedIn)
                SaveSnapshot("dialog");
            RdpView.IsVisible = false;
            if (LastThumbnailPath is not null)
            {
                ThumbnailImage.Source = new Bitmap(LastThumbnailPath);
                ThumbnailImage.IsVisible = true;
            }
        }

        var dialog = new FAContentDialog
        {
            Title = "In-window dialog",
            Content = hideSurface
                ? "The session surface is hidden (snapshot shown instead) so this dialog is visible."
                : "Shown without the airspace workaround.",
            CloseButtonText = "Close",
        };

        async Task ShowAndRestore()
        {
            await dialog.ShowAsync();
            if (hideSurface)
            {
                ThumbnailImage.IsVisible = false;
                RdpView.IsVisible = true;
            }
        }

        return (dialog, ShowAndRestore());
    }

    private async void OnWindowDialogClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;

        var dialog = new Window
        {
            Title = "Dialog in its own window",
            Width = 380,
            Height = 160,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBlock
            {
                Margin = new Thickness(16),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Text = "A separate top-level window is always above the RDP child HWND.",
            },
        };
        await dialog.ShowDialog(owner);
    }

    private void PrefillFromEnvironment()
    {
        // Local testing convenience only; values are never logged. See docs/spike-results.md.
        HostBox.Text = Environment.GetEnvironmentVariable("RM_SPIKE_HOST") ?? "";
        UserBox.Text = Environment.GetEnvironmentVariable("RM_SPIKE_USER") ?? "";
        DomainBox.Text = Environment.GetEnvironmentVariable("RM_SPIKE_DOMAIN") ?? "";
        PasswordBox.Text = Environment.GetEnvironmentVariable("RM_SPIKE_PASSWORD") ?? "";
    }

    private void SetTitle(string title)
    {
        Title = title;
        TitleChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        StatusLogged?.Invoke(this, message);
    }

    private void Log(string message) => StatusLogged?.Invoke(this, message);

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        frameTimer.Stop();
        clientEvents?.Dispose();
        clientEvents = null;
        RdpView.Dispose();
    }
}
