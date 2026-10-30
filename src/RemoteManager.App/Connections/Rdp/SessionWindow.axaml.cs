using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Devolutions.MsRdpEx.Avalonia;
using MSTSCLib;
using RemoteManager.App.Resources;
using RemoteManager.App.Views;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;

namespace RemoteManager.App.Connections.Rdp;

/// <summary>
/// A top-level window with exactly one embedded <see cref="RdpClientView"/>.
/// Closing the window disposes the session (and its native window).
/// </summary>
public partial class SessionWindow : Window, ISessionWindow
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

    // Required by the XAML loader; the app always uses the ConnectionItem overload.
    public SessionWindow() : this(new ConnectionItem(new Connection { Name = "Session" }, AppHost.Current))
    {
    }

    public SessionWindow(ConnectionItem connection)
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
        // Own credentials, or those inherited from the group.
        Core.Credentials.EffectiveCredential credentials = connection.Credentials;
        UserBox.Text = credentials.UserName;
        DomainBox.Text = credentials.Domain;
        connection.RefreshCredentialState();
        UpdatePasswordFields();
        if (connection.HasStoredPassword)
        {
            // Direct Windows logon: skip the card and connect as soon as the RDP client is ready.
            connectWhenReady = true;
            ConnectCard.IsVisible = false;
            StatusText.Text = Strings.Session_ConnectingStored;
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

    public ConnectionItem Connection { get; }

    /// <summary>Raised for every status line (connection events, errors, diagnostics). Never contains secrets.</summary>
    public event EventHandler<string>? StatusLogged;
    public event EventHandler? SessionLoggedIn;
    public event EventHandler? SessionDisconnected;
    /// <summary>A snapshot PNG was written to the connection's preview path.</summary>
    public event EventHandler? SnapshotSaved;
    /// <summary>A typed password was stored after a successful logon; the connection's credential reference changed.</summary>
    public event EventHandler? PasswordSaved;

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

    /// <summary>Starts connecting (see <see cref="ConnectAsync"/>).</summary>
    public void Connect() => _ = ConnectAsync();

    public async Task ConnectAsync()
    {
        string host = Connection.Model.Host.Trim();
        if (host.Length == 0)
        {
            ShowCardMessage(Strings.Session_NoHost);
            return;
        }

        if (!RdpView.IsClientReady)
        {
            ShowCardMessage(Strings.Session_ClientNotReady);
            return;
        }

        // A typed password wins; an empty box means "use the saved one" (own or inherited from the group).
        string typed = PasswordBox.Text ?? "";
        string password = typed;
        usedStoredPassword = false;
        if (typed.Length == 0 && Connection.HasStoredPassword)
        {
            try
            {
                password = await AppHost.Current.Credentials.ReadAsync(Connection.Credentials.Secret) ?? "";
                usedStoredPassword = password.Length > 0;
            }
            catch (Win32Exception ex)
            {
                ShowCardMessage(string.Format(CultureInfo.CurrentCulture, Strings.Password_ReadFailed, ex.Message));
                ShowCard();
                return;
            }
        }

        passwordToSave = typed.Length > 0 && SavePasswordBox.IsChecked == true ? typed : null;

        RdpSettings settings = Connection.Model.SettingsAs<RdpSettings>();
        (int width, int height, Devolutions.MsRdpEx.Avalonia.RdpDisplayMode mode) = settings.Display switch
        {
            Core.Model.Settings.RdpDisplayMode.SmartSizing => (0, 0, Devolutions.MsRdpEx.Avalonia.RdpDisplayMode.SmartSizing),
            Core.Model.Settings.RdpDisplayMode.FixedResolution => (settings.DesktopWidth, settings.DesktopHeight, Devolutions.MsRdpEx.Avalonia.RdpDisplayMode.SmartSizing),
            _ => (0, 0, Devolutions.MsRdpEx.Avalonia.RdpDisplayMode.FitToWindow),
        };

        // RDP over SSH: open the tunnel first and connect to its local end.
        int port = Connection.Model.EffectivePort;
        if (settings.JumpHostId is { Length: > 0 } jumpHostId)
        {
            if (await OpenTunnelAsync(jumpHostId, host, port) is not { } end)
            {
                passwordToSave = null;
                ShowCard();
                return;
            }

            (host, port) = end;
        }

        RdpView.UseRemoteKeyboardShortcuts = settings.Keyboard == RdpKeyboardMode.Session;
        RdpView.DisplayMode = mode;

        // Show the surface and lay it out first: the initial session size comes from the viewport.
        ShowSessionSurface();
        UpdateLayout();
        IsSessionActive = true;

        try
        {
            // Server authentication: warn on certificate problems instead of silently connecting.
            // RdpClientView.Connect leaves AuthenticationLevel untouched, so set it on the client first.
            RdpView.GetClient<IMsRdpClient10>().AdvancedSettings9.AuthenticationLevel = (uint)Math.Clamp(settings.AuthenticationLevel, 0, 2);
            // Always set: a previous connect may have used a tunnel port.
            RdpView.GetClient<IMsRdpClient10>().AdvancedSettings9.RDPPort = port;

            var connectionSettings = new RdpConnectionSettings(
                HostName: host,
                UserName: UserBox.Text?.Trim() ?? "",
                Password: password,
                Domain: DomainBox.Text?.Trim() ?? "",
                DesktopWidth: width,
                DesktopHeight: height);

            RdpView.Connect(connectionSettings);
        }
        catch (Exception ex)
        {
            IsSessionActive = false;
            passwordToSave = null;
            CloseTunnel();
            SetStatus(string.Format(CultureInfo.CurrentCulture, Strings.Session_ConnectFailed, ex.Message));
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

        string path = Connection.Model.ResolvedPreviewPath;
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
        Connection model = Connection.Model;
        try
        {
            // An own password needs the user it belongs to; without one the connection would stop
            // inheriting the group's user and log on with none.
            if (model.UserName.Length == 0)
            {
                model.UserName = UserBox.Text?.Trim() ?? "";
                model.Domain = DomainBox.Text?.Trim() ?? "";
            }

            // The Credential Manager source completes synchronously.
            AppHost.Current.Credentials.SaveOwnSecretAsync(model, password).AsTask().GetAwaiter().GetResult();
            Log("Password saved to the Windows Credential Manager.");
            PasswordSaved?.Invoke(this, EventArgs.Empty);
        }
        catch (Win32Exception ex)
        {
            SetStatus(string.Format(CultureInfo.CurrentCulture, Strings.Password_SaveFailed, ex.Message));
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
        CloseTunnel();
        frameTimer.Stop();
        SetStatus(string.Format(CultureInfo.CurrentCulture, Strings.Session_Disconnected, DescribeDisconnect(reason)));

        // Server-side or network disconnect: the live surface may already be blank,
        // SaveSnapshot falls back to the last periodic frame.
        if (lastGoodFrame is not null && LastThumbnailPath is null)
            SaveSnapshot("server-disconnect");

        SetStatusDot(Color.FromRgb(0x9C, 0xA3, 0xAF));
        DisconnectButton.IsEnabled = false;
        FullScreenButton.IsEnabled = false;
        SnapshotItem.IsEnabled = false;
        FocusItem.IsEnabled = false;
        ConnectButton.Content = Strings.Session_Reconnect;
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
        ShowCardMessage(string.Format(CultureInfo.CurrentCulture, Strings.Session_StoredPasswordRejected, LastStatus));
        ShowCard();
    }

    private void UpdatePasswordFields()
    {
        PasswordBox.PlaceholderText = Connection.HasStoredPassword ? Strings.Session_PasswordPlaceholderStored : Strings.Field_Password;
    }

    private string DescribeDisconnect(int reason)
    {
        if (reason < 0)
            return Strings.Session_DisconnectReportedByView;

        try
        {
            var client = RdpView.GetClient<IMsRdpClient10>();
            string? text = client.GetErrorDescription((uint)reason, (uint)client.ExtendedDisconnectReason);
            string code = string.Format(CultureInfo.CurrentCulture, Strings.Session_DisconnectReason, reason);
            return string.IsNullOrWhiteSpace(text) ? code : $"{text.Trim()} ({code})";
        }
        catch (Exception)
        {
            return string.Format(CultureInfo.CurrentCulture, Strings.Session_DisconnectReason, reason);
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
        if (!string.IsNullOrWhiteSpace(host) && string.Equals(host, Connection.Model.Host, StringComparison.OrdinalIgnoreCase))
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
        CloseTunnel();
    }
}
