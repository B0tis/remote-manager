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

    // The same class, without a specific cause: network level authentication (CredSSP) failed. Servers that
    // are not Windows (GNOME Remote Desktop, KRdp) answer a wrong or missing user or password with it.
    private const int NlaFailedReason = 519;

    // How long after the transport is connected the session counts as up without the server's logon notification.
    private static readonly TimeSpan LogonNotificationWait = TimeSpan.FromSeconds(6);

    // The RDP view's status line once the transport is up (after network level authentication, if it is on).
    private const string TransportConnectedStatus = "Transport connected. Signing in...";

    private readonly DispatcherTimer frameTimer;
    private readonly DispatcherTimer logonFallback = new() { Interval = LogonNotificationWait };
    // After the window or the remote desktop changed size: time for the server to follow before the picture is scaled.
    private readonly DispatcherTimer fitCheck = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private bool credentialsCheckedByNla;
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

        Bar.Load(connection);
        CardHeader.Load(connection);
        DescriptionText.Text = connection.Description;
        DescriptionText.IsVisible = connection.Description.Length > 0;
        // Own credentials, or those inherited from the group.
        Core.Credentials.EffectiveCredential credentials = connection.Credentials;
        UserBox.Text = credentials.UserName;
        DomainBox.Text = credentials.Domain;
        connection.RefreshCredentialState();
        UpdatePasswordFields();
        if (connection.HasStoredPassword && credentials.UserName.Length > 0)
        {
            // Direct Windows logon: skip the card and connect as soon as the RDP client is ready.
            connectWhenReady = true;
            ConnectCard.IsVisible = false;
            Bar.Status = Strings.Session_ConnectingStored;
            Bar.Lamp = SessionLamp.Busy;
        }
        else if (connection.HasStoredPassword)
        {
            // A password without a user cannot log on; connecting anyway only earns an error from the server.
            ShowCardMessage(Strings.Session_UserMissing);
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
        SnapshotButton.Click += (_, _) => SaveSnapshot("manual");
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
        RdpView.LoginCompleted += (_, _) => MarkSessionUp(confirmedByServer: true);
        // Windows sends a logon notification. Servers built on FreeRDP (GNOME Remote Desktop, KRdp) and some xrdp
        // setups never do: the desktop is there, but LoginCompleted does not come. Without this the window would
        // stay at "signing in" with no preview, no history entry, and no "Aktive Sitzung".
        // (Started by the view's "transport connected" status line, see SetStatus.)
        logonFallback.Tick += (_, _) =>
        {
            logonFallback.Stop();
            if (IsSessionActive && !IsLoggedIn)
            {
                Log("No logon notification from the server; the connected session counts as logged on.");
                MarkSessionUp(confirmedByServer: false);
            }
        };
        // Kept in case a later MsRdpEx raises it; OnDisconnected ignores the duplicate.
        RdpView.Disconnected += (_, _) => OnDisconnected(reason: -1);
        RdpView.RemoteDesktopSizeChanged += (_, e) =>
        {
            LastRemoteDesktopSize = new PixelSize(e.Width, e.Height);
            Log($"Remote desktop size: {e.Width}x{e.Height} (viewport {RdpView.ViewportPixelSize.Width}x{RdpView.ViewportPixelSize.Height}).");
            RestartFitCheck();
        };
        RdpView.SizeChanged += (_, _) => RestartFitCheck();
        fitCheck.Tick += (_, _) => ScaleIfServerKeepsItsSize();
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
    public string LastStatus => Bar.Status;

    /// <summary>The bar at the top: lamp, status line, time connected (self-test).</summary>
    public SessionBar SessionBar => Bar;
    public bool IsConnectCardVisible => ConnectCard.IsVisible;
    public string CardMessageText => CardMessage.IsVisible ? CardMessage.Text ?? "" : "";
    /// <summary>The last connect used the password from the Credential Manager.</summary>
    public bool UsedStoredPassword => usedStoredPassword;

    /// <summary>
    /// "An Fenster anpassen" asked the server for the size of the window, but the server kept its own (a mirrored
    /// physical screen: GNOME desktop sharing, a shadowed console), so the picture is scaled to the window instead
    /// of showing scroll bars.
    /// </summary>
    public bool IsScaledToFit { get; private set; }

    private void RestartFitCheck()
    {
        fitCheck.Stop();
        if (IsLoggedIn && !IsScaledToFit)
            fitCheck.Start();
    }

    private void ScaleIfServerKeepsItsSize()
    {
        fitCheck.Stop();
        if (!IsLoggedIn || IsScaledToFit || WindowState == WindowState.Minimized
            || Connection.Model.SettingsAs<RdpSettings>().Display != Core.Model.Settings.RdpDisplayMode.FitToWindow)
            return;

        PixelSize viewport = RdpView.ViewportPixelSize;
        PixelSize remote = LastRemoteDesktopSize;
        if (remote.Width <= 0 || viewport.Width <= 0
            || (Math.Abs(remote.Width - viewport.Width) <= 2 && Math.Abs(remote.Height - viewport.Height) <= 2))
            return;

        try
        {
            RdpView.DisplayMode = Devolutions.MsRdpEx.Avalonia.RdpDisplayMode.SmartSizing;
            RdpView.GetClient<IMsRdpClient10>().AdvancedSettings9.SmartSizing = true;
            IsScaledToFit = true;
            Bar.Status = Strings.Session_ScaledToFit;
            Log($"The server keeps its desktop size {remote.Width}x{remote.Height}; scaling it to the window ({viewport.Width}x{viewport.Height}).");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException or InvalidOperationException)
        {
            Log($"Scaling to the window is not available: {ex.Message}");
        }
    }

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
            catch (Exception ex) when (Credentials.CredentialErrors.IsCredentialFailure(ex))
            {
                // Also KeePass / 1Password (locked, canceled, entry missing).
                ShowCardMessage(Credentials.CredentialErrors.Describe(ex));
                ShowCard();
                return;
            }
        }

        RdpSettings settings = Connection.Model.SettingsAs<RdpSettings>();
        if (settings.EnableNla && password.Length > 0 && string.IsNullOrWhiteSpace(UserBox.Text))
        {
            ShowCardMessage(Strings.Session_UserMissing);
            ShowCard();
            UserBox.Focus();
            return;
        }

        passwordToSave = typed.Length > 0 && SavePasswordBox.IsChecked == true ? typed : null;

        string gatewayPassword = "";
        if (settings.GatewayUsage != RdpGatewayUsage.None && !settings.GatewayUsesConnectionCredentials && settings.GatewayCredential is { } gatewayCredential)
        {
            try
            {
                gatewayPassword = await AppHost.Current.Credentials.ReadAsync(gatewayCredential) ?? "";
            }
            catch (Win32Exception ex)
            {
                Log($"Gateway password could not be read: {ex.Message}");
            }
        }

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

        credentialsCheckedByNla = settings.EnableNla;
        IsScaledToFit = false;
        RdpView.UseRemoteKeyboardShortcuts = settings.Keyboard == RdpKeyboardMode.Session;
        RdpView.DisplayMode = settings.Display == Core.Model.Settings.RdpDisplayMode.FitToWindow
            ? Devolutions.MsRdpEx.Avalonia.RdpDisplayMode.FitToWindow
            : Devolutions.MsRdpEx.Avalonia.RdpDisplayMode.SmartSizing;

        // Show the surface and lay it out first: the initial session size comes from the viewport.
        ShowSessionSurface();
        UpdateLayout();
        IsSessionActive = true;

        try
        {
            (int width, int height) = settings.Display == Core.Model.Settings.RdpDisplayMode.FixedResolution && settings.DesktopWidth > 0 && settings.DesktopHeight > 0
                ? (settings.DesktopWidth, settings.DesktopHeight)
                : (RdpView.ViewportPixelSize.Width, RdpView.ViewportPixelSize.Height);
            var logon = new RdpLogon(host, port, UserBox.Text?.Trim() ?? "", DomainBox.Text?.Trim() ?? "", password, gatewayPassword);
            RdpClientConfigurator.Apply(RdpView, settings, logon, width, height);
            Log(RdpClientConfigurator.Describe(settings));
            // Only fit-to-window follows the window size; the other modes keep the session size and scale.
            RdpView.ConnectConfigured(manageDynamicResolution: settings.Display == Core.Model.Settings.RdpDisplayMode.FitToWindow);
        }
        catch (Exception ex)
        {
            IsSessionActive = false;
            passwordToSave = null;
            CloseTunnel();
            Bar.Lamp = SessionLamp.Idle;
            SetStatus(string.Format(CultureInfo.CurrentCulture, Strings.Session_ConnectFailed, ex.Message));
            ShowCard();
            return;
        }
        finally
        {
            // The password was handed to the control (ClearTextPassword); drop our copy.
            PasswordBox.Text = "";
        }

        Bar.Lamp = SessionLamp.Busy;
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
        savedSnapshots++;
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

    /// <summary>
    /// The session is up: by the server's logon notification, or without one after
    /// <see cref="LogonNotificationWait"/>. A typed password is only saved once it is known to be right: the
    /// notification says so, and so does network level authentication, which checks it before the transport connects.
    /// </summary>
    private void MarkSessionUp(bool confirmedByServer)
    {
        logonFallback.Stop();
        if (!IsSessionActive)
            return;
        if (IsLoggedIn)
        {
            // The notification after all (a slow Windows logon): now the password is confirmed too.
            if (confirmedByServer)
                SavePendingPassword();
            return;
        }

        IsLoggedIn = true;
        Bar.Lamp = SessionLamp.Connected;
        Bar.Status = Strings.Session_Status_Connected;
        FullScreenButton.IsEnabled = true;
        SnapshotItem.IsEnabled = true;
        SnapshotButton.IsEnabled = true;
        FocusItem.IsEnabled = true;
        frameTimer.Start();
        RestartFitCheck();
        RdpView.FocusSession();
        if (confirmedByServer || credentialsCheckedByNla)
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

        bool logonRefused = !IsLoggedIn && (LogonFailureReasons.Contains(reason) || reason == NlaFailedReason);
        bool rejectedStoredPassword = logonRefused && usedStoredPassword;
        IsSessionActive = false;
        IsLoggedIn = false;
        passwordToSave = null;
        LastDisconnectReason = reason;
        CloseTunnel();
        frameTimer.Stop();
        logonFallback.Stop();
        fitCheck.Stop();
        // 1 to 3 are endings, not errors: by this window, by the user in the session, by the server.
        bool ended = reason is 1 or 2 or 3;
        SetStatus(ended
            ? reason == 3 ? Strings.Session_EndedByServer : Strings.Session_Ended
            : string.Format(CultureInfo.CurrentCulture, Strings.Session_Disconnected, DescribeDisconnect(reason)));

        // Server-side or network disconnect: the live surface may already be blank,
        // SaveSnapshot falls back to the last periodic frame.
        if (lastGoodFrame is not null && LastThumbnailPath is null)
            SaveSnapshot("server-disconnect");

        Bar.Lamp = SessionLamp.Idle;
        DisconnectButton.IsEnabled = false;
        FullScreenButton.IsEnabled = false;
        SnapshotItem.IsEnabled = false;
        SnapshotButton.IsEnabled = false;
        FocusItem.IsEnabled = false;
        ConnectButton.Content = Strings.Session_Reconnect;
        if (logonRefused && reason == NlaFailedReason)
        {
            // Not necessarily the password: the server refused the logon without saying why. A stored password
            // can be replaced from here all the same.
            SavePasswordBox.IsChecked = usedStoredPassword;
            ShowCardMessage(Strings.Session_NlaFailed);
            ShowCard();
        }
        else if (rejectedStoredPassword)
        {
            ShowStoredPasswordRejected();
        }
        else
        {
            // An ending needs no red note on the card; the bar says it.
            ShowCardMessage(ended ? "" : LastStatus);
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
        if (reason == NlaFailedReason)
            return Strings.Session_NlaFailedShort;

        try
        {
            var client = RdpView.GetClient<IMsRdpClient10>();
            string? text = client.GetErrorDescription((uint)reason, (uint)client.ExtendedDisconnectReason);
            string code = string.Format(CultureInfo.CurrentCulture, Strings.Session_DisconnectReason, reason);
            // Servers that are not Windows often leave the text empty; the logon refusals at least get one.
            if (string.IsNullOrWhiteSpace(text))
                text = DescribeLogonFailure(reason);
            return string.IsNullOrWhiteSpace(text) ? code : $"{text.Trim()} ({code})";
        }
        catch (Exception)
        {
            return string.Format(CultureInfo.CurrentCulture, Strings.Session_DisconnectReason, reason);
        }
    }

    private static string? DescribeLogonFailure(int reason) => reason switch
    {
        2055 => Strings.Session_Reason_LogonFailed,
        2567 => Strings.Session_Reason_NoAccount,
        2823 => Strings.Session_Reason_AccountDisabled,
        3335 => Strings.Session_Reason_AccountLocked,
        3591 => Strings.Session_Reason_AccountExpired,
        3847 => Strings.Session_Reason_PasswordExpired,
        4615 => Strings.Session_Reason_PasswordMustChange,
        _ => null,
    };

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

    private void SetStatus(string message)
    {
        // The log keeps the original line; the bar shows the German one (or nothing new for a line that is only
        // of interest in the log).
        if (Translate(message) is { } shown)
            Bar.Status = shown;
        if (message == TransportConnectedStatus && IsSessionActive && !IsLoggedIn)
            logonFallback.Start();
        StatusLogged?.Invoke(this, message);
    }

    /// <summary>
    /// The status lines of the RDP view (English, MsRdpEx) as the bar shows them. Lines the app writes itself are
    /// German already and pass unchanged; null keeps what the bar shows.
    /// </summary>
    private static string? Translate(string message) => message switch
    {
        "RDP native surface ready." => null,
        "Connecting..." => Strings.Session_Status_Connecting,
        TransportConnectedStatus => Strings.Session_Status_SigningIn,
        "Connected." => Strings.Session_Status_Connected,
        "Waiting for certificate confirmation..." => Strings.Session_Status_Certificate,
        "Disconnecting..." => Strings.Session_Status_Disconnecting,
        _ when message.StartsWith("Connecting to ", StringComparison.Ordinal) => Strings.Session_Status_Connecting,
        _ when message.StartsWith("RDP logon error: ", StringComparison.Ordinal) =>
            string.Format(CultureInfo.CurrentCulture, Strings.Session_Status_LogonError, message["RDP logon error: ".Length..]),
        // The view's own "Disconnected: ..." repeats what OnDisconnected reports with the Windows error text.
        _ when message.StartsWith("Disconnected: ", StringComparison.Ordinal) => null,
        _ => message,
    };

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
        logonFallback.Stop();
        fitCheck.Stop();
        clientEvents?.Dispose();
        clientEvents = null;
        RdpView.Dispose();
        CloseTunnel();
    }
}
