using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Devolutions.MsRdpEx.Avalonia;
using MSTSCLib;

namespace RemoteManager.App;

/// <summary>
/// A top-level window with exactly one embedded <see cref="RdpClientView"/>.
/// Closing the window disposes the session (and its native window).
/// </summary>
public partial class SessionWindow : Window
{
    // A periodic in-memory frame lets a server-initiated disconnect still produce a
    // thumbnail: by the time Disconnected fires, the control may already show nothing.
    private static readonly TimeSpan FrameCaptureInterval = TimeSpan.FromSeconds(10);

    // RDP disconnect reasons for a rejected logon (SSL/CredSSP class 0x07): wrong password, unknown
    // user, account restricted, locked, or expired, password expired or must be changed.
    private static readonly HashSet<int> LogonFailureReasons = [2055, 2567, 2823, 3335, 3591, 3847, 4615];

    private readonly DispatcherTimer frameTimer;
    private SessionFrame? lastGoodFrame;
    private bool disposed;
    private bool connectWhenReady;
    private bool usedStoredPassword;
    // A typed password with "Passwort speichern": held until logon succeeds, so a wrong one is never saved.
    private string? passwordToSave;

    // Workaround for MsRdpEx 2026.10.26: RdpClientView.Disconnected only fires for the
    // DisconnectedWithDetails event, which the RDP v11 control (classic IMsTscAxEvents sink,
    // DISPID 4) never raises. Its IsConnectionActive/IsLoginCompleted therefore stay true after
    // a disconnect. We subscribe to the raw OnDisconnected ourselves and keep our own state.
    private RdpClientEventSubscription? clientEvents;

    // Required by the XAML loader; the app always uses the Connection overload.
    public SessionWindow() : this(new Connection { Name = "Session" })
    {
    }

    public SessionWindow(Connection connection)
    {
        InitializeComponent();
        Connection = connection;
        Title = connection.Name;

        NameText.Text = connection.Name;
        HostText.Text = connection.HostLabel;
        DescriptionText.Text = connection.Description;
        DescriptionText.IsVisible = connection.Description.Length > 0;
        TypeBadge.Background = connection.TypeBrush;
        TypeBadgeText.Text = connection.TypeInitials;
        UserBox.Text = connection.UserName;
        DomainBox.Text = connection.Domain;
        connection.RefreshCredentialState();
        UpdatePasswordFields();
        if (connection.HasStoredPassword)
        {
            // Direct Windows logon: skip the card and connect as soon as the RDP client is ready.
            connectWhenReady = true;
            ConnectCard.IsVisible = false;
            StatusText.Text = "Connecting with the saved password…";
        }
        else
        {
            PrefillPasswordFromEnvironment();
        }

        frameTimer = new DispatcherTimer { Interval = FrameCaptureInterval };
        frameTimer.Tick += (_, _) => CaptureFrameToMemory();

        ConnectButton.Click += (_, _) => Connect();
        DisconnectButton.Click += (_, _) => Disconnect();
        FullScreenButton.Click += (_, _) => RdpView.FullScreen = true;
        SnapshotItem.Click += (_, _) => SaveSnapshot("manual");
        FocusItem.Click += (_, _) => RdpView.FocusSession();
        Opened += (_, _) => (UserBox.Text?.Length > 0 ? PasswordBox : UserBox).Focus();
        Activated += (_, _) =>
        {
            if (IsLoggedIn)
                Dispatcher.UIThread.Post(() => RdpView.FocusSession(), DispatcherPriority.Background);
        };

        RdpView.ClientReady += (_, _) =>
        {
            Log("RDP client ready.");
            clientEvents = RdpView.GetClient<IMsRdpClient>().Subscribe();
            clientEvents.Disconnected += OnDisconnected;
            if (connectWhenReady)
            {
                connectWhenReady = false;
                Connect();
            }
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

    public Connection Connection { get; }

    /// <summary>Raised for every status line (connection events, errors, diagnostics). Never contains secrets.</summary>
    public event EventHandler<string>? StatusLogged;
    public event EventHandler? SessionLoggedIn;
    public event EventHandler? SessionDisconnected;
    /// <summary>A snapshot PNG was written to <see cref="Connection.ThumbnailPath"/>.</summary>
    public event EventHandler? SnapshotSaved;

    public RdpClientView Rdp => RdpView;
    public Button MenuButton => MoreButton;

    /// <summary>Between Connect and the disconnect event (our own state, see clientEvents).</summary>
    public bool IsSessionActive { get; private set; }

    /// <summary>Logon finished and the session has not disconnected since.</summary>
    public bool IsLoggedIn { get; private set; }

    public int LastDisconnectReason { get; private set; }
    public PixelSize LastRemoteDesktopSize { get; private set; }
    public string? LastThumbnailPath { get; private set; }
    public string LastStatus => StatusText.Text ?? string.Empty;
    public bool IsConnectCardVisible => ConnectCard.IsVisible;
    public string CardMessageText => CardMessage.IsVisible ? CardMessage.Text ?? "" : "";
    /// <summary>The last connect used the password from the Credential Manager.</summary>
    public bool UsedStoredPassword => usedStoredPassword;

    public void Connect()
    {
        string host = Connection.Host.Trim();
        if (host.Length == 0)
        {
            ShowCardMessage("This connection has no host.");
            return;
        }

        if (!RdpView.IsClientReady)
        {
            ShowCardMessage("The RDP client is not ready yet. Try again in a moment.");
            return;
        }

        // A typed password wins; an empty box means "use the saved one".
        string typed = PasswordBox.Text ?? "";
        string password = typed;
        usedStoredPassword = false;
        if (typed.Length == 0 && Connection.HasStoredPassword)
        {
            try
            {
                password = CredentialStore.Read(Connection.Id) ?? "";
                usedStoredPassword = password.Length > 0;
            }
            catch (Win32Exception ex)
            {
                ShowCardMessage($"Gespeichertes Passwort konnte nicht gelesen werden: {ex.Message}");
                ShowCard();
                return;
            }
        }

        passwordToSave = typed.Length > 0 && SavePasswordBox.IsChecked == true ? typed : null;

        (int width, int height, RdpDisplayMode mode) = Connection.DisplayMode switch
        {
            1 => (0, 0, RdpDisplayMode.SmartSizing),
            2 => (1920, 1080, RdpDisplayMode.SmartSizing),
            3 => (1280, 720, RdpDisplayMode.SmartSizing),
            _ => (0, 0, RdpDisplayMode.FitToWindow),
        };

        RdpView.UseRemoteKeyboardShortcuts = Connection.WinKeysToSession;
        RdpView.DisplayMode = mode;

        // Show the surface and lay it out first: the initial session size comes from the viewport.
        ShowSessionSurface();
        UpdateLayout();
        IsSessionActive = true;

        try
        {
            // Server authentication: warn on certificate problems instead of silently connecting.
            // RdpClientView.Connect leaves AuthenticationLevel untouched, so set it on the client first.
            RdpView.GetClient<IMsRdpClient10>().AdvancedSettings9.AuthenticationLevel = 2;
            if (Connection.Port is > 0 and not 3389)
                RdpView.GetClient<IMsRdpClient10>().AdvancedSettings9.RDPPort = Connection.Port;

            var settings = new RdpConnectionSettings(
                HostName: host,
                UserName: UserBox.Text?.Trim() ?? "",
                Password: password,
                Domain: DomainBox.Text?.Trim() ?? "",
                DesktopWidth: width,
                DesktopHeight: height);

            RdpView.Connect(settings);
        }
        catch (Exception ex)
        {
            IsSessionActive = false;
            passwordToSave = null;
            SetStatus($"Connect failed: {ex.Message}");
            ShowCard();
            return;
        }
        finally
        {
            // The password was handed to the control (ClearTextPassword); drop our copy.
            PasswordBox.Text = "";
        }

        SetStatusDot(Color.FromRgb(0xF5, 0x9E, 0x0B));
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

        string path = Connection.ThumbnailPath;
        frame.SavePng(path);
        LastThumbnailPath = path;
        Log($"Snapshot ({reason}) saved: {path} ({frame.Width}x{frame.Height}, method {frame.Method}).");
        SnapshotSaved?.Invoke(this, EventArgs.Empty);
        return path;
    }

    private void CaptureFrameToMemory()
    {
        if (!IsLoggedIn || WindowState == WindowState.Minimized)
            return;

        SessionFrame? frame = SessionCapture.Capture(RdpView.HostWindowHandle);
        if (frame is { IsBlank: false })
            lastGoodFrame = frame;
    }

    private void OnLoginCompleted(object? sender, EventArgs e)
    {
        IsLoggedIn = true;
        SetStatusDot(Color.FromRgb(0x22, 0xC5, 0x5E));
        FullScreenButton.IsEnabled = true;
        SnapshotItem.IsEnabled = true;
        FocusItem.IsEnabled = true;
        frameTimer.Start();
        RdpView.FocusSession();
        SavePendingPassword();
        SessionLoggedIn?.Invoke(this, EventArgs.Empty);
    }

    private void SavePendingPassword()
    {
        if (passwordToSave is not { } password)
            return;

        passwordToSave = null;
        try
        {
            CredentialStore.Write(Connection.Id, password);
            Log("Password saved to the Windows Credential Manager.");
        }
        catch (Win32Exception ex)
        {
            SetStatus($"Passwort konnte nicht gespeichert werden: {ex.Message}");
        }

        Connection.RefreshCredentialState();
        UpdatePasswordFields();
    }

    private void OnDisconnected(int reason)
    {
        if (!IsSessionActive)
            return;

        bool rejectedStoredPassword = !IsLoggedIn && usedStoredPassword && LogonFailureReasons.Contains(reason);
        IsSessionActive = false;
        IsLoggedIn = false;
        passwordToSave = null;
        LastDisconnectReason = reason;
        frameTimer.Stop();
        SetStatus($"Disconnected: {DescribeDisconnect(reason)}");

        // Server-side or network disconnect: the live surface may already be blank,
        // SaveSnapshot falls back to the last periodic frame.
        if (lastGoodFrame is not null && LastThumbnailPath is null)
            SaveSnapshot("server-disconnect");

        SetStatusDot(Color.FromRgb(0x9C, 0xA3, 0xAF));
        DisconnectButton.IsEnabled = false;
        FullScreenButton.IsEnabled = false;
        SnapshotItem.IsEnabled = false;
        FocusItem.IsEnabled = false;
        ConnectButton.Content = "Reconnect";
        if (rejectedStoredPassword)
        {
            ShowStoredPasswordRejected();
        }
        else
        {
            ShowCardMessage(LastStatus);
            ShowCard();
        }

        SessionDisconnected?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The server refused the saved password: ask for it again and offer to replace the saved one.</summary>
    internal void ShowStoredPasswordRejected()
    {
        SavePasswordBox.IsChecked = true;
        ShowCardMessage($"Das gespeicherte Passwort wurde abgelehnt. Bitte neu eingeben; mit „Passwort speichern“ wird es ersetzt.\n{LastStatus}");
        ShowCard();
    }

    private void UpdatePasswordFields()
    {
        PasswordBox.PlaceholderText = Connection.HasStoredPassword ? "Password (empty = use saved one)" : "Password";
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
        ConnectCard.IsVisible = false;
        CardMessage.IsVisible = false;
        RdpView.IsVisible = true;
    }

    private void ShowCard()
    {
        // Hide the native surface so the (Avalonia-drawn) card and thumbnail are not covered by it.
        RdpView.IsVisible = false;
        if (LastThumbnailPath is not null && File.Exists(LastThumbnailPath))
        {
            using FileStream stream = File.OpenRead(LastThumbnailPath);
            ThumbnailImage.Source = new Bitmap(stream);
            ThumbnailImage.IsVisible = true;
        }

        ConnectCard.IsVisible = true;
        PasswordBox.Focus();
    }

    private void ShowCardMessage(string message)
    {
        CardMessage.Text = message;
        CardMessage.IsVisible = message.Length > 0;
    }

    private void PrefillPasswordFromEnvironment()
    {
        // Local testing convenience only (see docs/spike-results.md); the value is never logged.
        string? host = Environment.GetEnvironmentVariable("RM_SPIKE_HOST");
        if (!string.IsNullOrWhiteSpace(host) && string.Equals(host, Connection.Host, StringComparison.OrdinalIgnoreCase))
            PasswordBox.Text = Environment.GetEnvironmentVariable("RM_SPIKE_PASSWORD") ?? "";
    }

    private void SetStatusDot(Color color) => StatusDot.Fill = new SolidColorBrush(color);

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        StatusLogged?.Invoke(this, message);
    }

    private void Log(string message) => StatusLogged?.Invoke(this, message);

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Keep the last picture of a live session as the tile preview before the surface goes away.
        if (IsLoggedIn)
            SaveSnapshot("close");
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        DisposeSession();
        base.OnClosed(e);
    }

    /// <summary>
    /// MsRdpEx deliberately keeps detached views alive (for docking), so the view must be disposed
    /// explicitly; this destroys its HWND.
    /// </summary>
    private void DisposeSession()
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
