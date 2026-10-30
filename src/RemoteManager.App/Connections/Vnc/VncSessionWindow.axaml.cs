using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MarcusW.VncClient;
using MarcusW.VncClient.Protocol;
using MarcusW.VncClient.Protocol.Implementation.Services.Transports;
using MarcusW.VncClient.Protocol.SecurityTypes;
using MarcusW.VncClient.Security;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteManager.App.Credentials;
using RemoteManager.App.Resources;
using RemoteManager.App.Views;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;

namespace RemoteManager.App.Connections.Vnc;

/// <summary>
/// One embedded VNC session (Community.MarcusW.VncClient, see docs/vnc-and-vaults.md). The password is fetched
/// from its source (Credential Manager, KeePass, 1Password) only when the server asks for one, and only kept
/// for automatic reconnects of this window. "Extern öffnen" hands the connection to an installed viewer.
/// </summary>
public partial class VncSessionWindow : Window, ISessionWindow
{
    private static readonly VncClient Client = new(NullLoggerFactory.Instance);

    private readonly IConnectionHost? host;
    private RfbConnection? rfb;
    private CancellationTokenSource? connecting;
    private bool closing;

    // Required by the XAML loader; the app always uses the ConnectionItem overload.
    public VncSessionWindow() : this(new ConnectionItem(new Connection { Name = "VNC", Type = ConnectionType.Vnc }, AppHost.Current), null)
    {
    }

    public VncSessionWindow(ConnectionItem connection, IConnectionHost? host)
    {
        InitializeComponent();
        Connection = connection;
        this.host = host;
        Title = connection.Name;

        NameText.Text = connection.Name;
        HostText.Text = connection.HostLabel;
        DescriptionText.Text = connection.Description;
        DescriptionText.IsVisible = connection.Description.Length > 0;
        TypeBadge.Background = connection.TypeBrush;
        TypeBadgeText.Text = connection.TypeInitials;

        VncSettings settings = connection.Model.SettingsAs<VncSettings>();
        ViewOnlyButton.IsChecked = settings.ViewOnly;
        ScaleButton.IsChecked = settings.ScaleToWindow;
        ApplyViewOptions();
        connection.RefreshCredentialState();
        PasswordBox.PlaceholderText = connection.HasStoredPassword ? Strings.Vnc_PasswordPlaceholderStored : Strings.Field_Password;

        ConnectButton.Click += (_, _) => _ = ConnectAsync();
        DisconnectButton.Click += (_, _) => Disconnect(Strings.Vnc_Disconnected);
        CtrlAltDelButton.Click += (_, _) =>
        {
            View.SendCtrlAltDel();
            View.Focus();
        };
        ViewOnlyButton.IsCheckedChanged += (_, _) => ApplyViewOptions();
        ScaleButton.IsCheckedChanged += (_, _) => ApplyViewOptions();
        ExternalButton.Click += (_, _) => OpenExternally();
        CardExternalButton.Click += (_, _) => OpenExternally();
        Opened += (_, _) => _ = ConnectAsync();
        Closing += (_, _) => OnClosing();
    }

    public ConnectionItem Connection { get; }

    /// <summary>The view (used by the self-test).</summary>
    public VncView Vnc => View;

    public bool IsConnected => rfb?.ConnectionState == ConnectionState.Connected;

    /// <summary>The last status or error text (used by the self-test).</summary>
    public string Status => StatusText.Text ?? "";

    public string? CardError => CardMessage.IsVisible ? CardMessage.Text : null;

    public TextBox Password => PasswordBox;

    public bool IsCardShown => ConnectCard.IsVisible;

    /// <summary>Whether the password of the last connect came from the stored source (not the box).</summary>
    public bool UsedStoredPassword { get; private set; }

    public event EventHandler<string>? StatusLogged;

    /// <summary>Raised after the preview image was written (the handler records it on the connection).</summary>
    public event EventHandler? SnapshotSaved;

    public event EventHandler? SessionConnected;

    public async Task ConnectAsync()
    {
        if (rfb is not null || connecting is not null || closing)
            return;
        if (string.IsNullOrWhiteSpace(Connection.Model.Host))
        {
            ShowCard(Strings.Session_NoHost);
            return;
        }

        VncSettings settings = Connection.Model.SettingsAs<VncSettings>();
        string typed = PasswordBox.Text ?? "";
        PasswordBox.Text = "";
        var authentication = new PasswordHandler(this, typed);
        var parameters = new ConnectParameters
        {
            TransportParameters = new TcpTransportParameters { Host = Connection.Model.Host.Trim(), Port = Connection.Model.EffectivePort },
            AuthenticationHandler = authentication,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            MaxReconnectAttempts = 3,
            ReconnectDelay = TimeSpan.FromSeconds(3),
            JpegQualityLevel = settings.Quality switch
            {
                VncQuality.Low => 30,
                VncQuality.Medium => 60,
                VncQuality.High => 95,
                _ => 80,
            },
            PreferredCompressionLevel = settings.Quality == VncQuality.Low ? 9 : -1,
            InitialRenderTarget = View,
            InitialOutputHandler = View,
        };

        connecting = new CancellationTokenSource();
        HideCard();
        SetStatus(string.Format(CultureInfo.CurrentCulture, Strings.Vnc_Connecting, Connection.HostLabel), "#F59E0B");
        try
        {
            RfbConnection connection = await Client.ConnectAsync(parameters, connecting.Token);
            if (closing)
            {
                connection.Dispose();
                return;
            }

            rfb = connection;
            UsedStoredPassword = authentication.UsedStored;
            View.Connection = connection;
            connection.ConnectionStateChanged += (_, e) => Dispatcher.UIThread.Post(() => OnStateChanged(connection, e.CurrentState));
            OnStateChanged(connection, connection.ConnectionState);
            SessionConnected?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (closing || connecting.IsCancellationRequested)
        {
            // Window closed while connecting.
        }
        catch (Exception ex)
        {
            ShowCard(DescribeFailure(ex, authentication));
            Log($"Connect failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            connecting?.Dispose();
            connecting = null;
        }
    }

    public void Disconnect(string message)
    {
        if (rfb is not { } connection)
            return;
        SaveSnapshot();
        rfb = null;
        View.Connection = null;
        connection.Dispose();
        ShowCard(null);
        SetStatus(message, "#9CA3AF");
        UpdateButtons();
    }

    private void OnStateChanged(RfbConnection connection, ConnectionState state)
    {
        if (!ReferenceEquals(connection, rfb))
            return;
        switch (state)
        {
            case ConnectionState.Connected:
                SetStatus(string.Format(CultureInfo.CurrentCulture, Strings.Vnc_Connected,
                    connection.DesktopName is { Length: > 0 } name ? name : Connection.HostLabel,
                    connection.RemoteFramebufferSize.Width, connection.RemoteFramebufferSize.Height), "#22C55E");
                host?.MarkConnected(Connection);
                Scroller.IsVisible = true;
                View.Focus();
                break;
            case ConnectionState.Interrupted:
            case ConnectionState.Reconnecting:
                SetStatus(Strings.Vnc_Reconnecting, "#F59E0B");
                break;
            case ConnectionState.ReconnectFailed:
            case ConnectionState.Closed:
                string reason = connection.InterruptionCause is { } cause ? cause.Message : "";
                Disconnect(reason.Length > 0
                    ? string.Format(CultureInfo.CurrentCulture, Strings.Vnc_ConnectionLost, reason)
                    : Strings.Vnc_Disconnected);
                return;
        }

        UpdateButtons();
    }

    private string DescribeFailure(Exception ex, PasswordHandler authentication)
    {
        if (CredentialErrors.Find(ex) is { } credential)
            return CredentialErrors.Describe(credential);
        if (authentication.Missing)
            return Strings.Vnc_PasswordRequired;
        if (authentication.Asked && ex is HandshakeFailedException)
            return authentication.UsedStored ? Strings.Vnc_StoredPasswordRejected : Strings.Vnc_AuthFailed;
        string detail = ex is SocketException or TimeoutException || ex.InnerException is SocketException
            ? (ex.InnerException ?? ex).Message
            : ex.Message;
        return string.Format(CultureInfo.CurrentCulture, Strings.Vnc_ConnectFailed, detail);
    }

    private void ApplyViewOptions()
    {
        View.ViewOnly = ViewOnlyButton.IsChecked == true;
        bool scale = ScaleButton.IsChecked == true;
        View.ScaleToFit = scale;
        // Scaling needs a finite size; 1:1 scrolls.
        Scroller.HorizontalScrollBarVisibility = scale ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        Scroller.VerticalScrollBarVisibility = scale ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool connected = IsConnected;
        DisconnectButton.IsEnabled = rfb is not null;
        CtrlAltDelButton.IsEnabled = connected && ViewOnlyButton.IsChecked != true;
    }

    private void ShowCard(string? message)
    {
        Scroller.IsVisible = rfb is not null;
        ConnectCard.IsVisible = true;
        CardMessage.Text = message;
        CardMessage.IsVisible = message is { Length: > 0 };
        if (message is { Length: > 0 })
            SetStatus(message, "#EF4444");
        PasswordBox.Focus();
    }

    private void HideCard()
    {
        ConnectCard.IsVisible = false;
        CardMessage.IsVisible = false;
    }

    private void SetStatus(string text, string color)
    {
        StatusText.Text = text;
        StatusDot.Fill = new SolidColorBrush(Avalonia.Media.Color.Parse(color));
        Log(text);
    }

    private void OpenExternally()
    {
        if (host is not null)
            _ = ExternalVncViewer.LaunchAsync(Connection, host);
    }

    /// <summary>Writes the current frame as the connection's preview tile.</summary>
    private void SaveSnapshot()
    {
        if (View.FrameCount == 0 || View.Snapshot() is not { } frame)
            return;
        try
        {
            string path = Connection.Model.ResolvedPreviewPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (frame)
                frame.Save(path, new PngBitmapEncoderOptions());
            SnapshotSaved?.Invoke(this, EventArgs.Empty);
        }
        catch (IOException ex)
        {
            Log($"Snapshot failed: {ex.Message}");
        }
    }

    private void OnClosing()
    {
        closing = true;
        connecting?.Cancel();
        Disconnect(Strings.Vnc_Disconnected);
        View.Dispose();
    }

    private void Log(string message)
    {
        Trace.WriteLine($"[VNC {Connection.Name}] {message}");
        StatusLogged?.Invoke(this, message);
    }

    /// <summary>
    /// Answers the server's password request: the typed password, otherwise the stored one (read now, at
    /// connect time). Kept by the library for the automatic reconnects of this connection only.
    /// </summary>
    private sealed class PasswordHandler(VncSessionWindow window, string typed) : IAuthenticationHandler
    {
        public bool Asked { get; private set; }
        public bool Missing { get; private set; }
        public bool UsedStored { get; private set; }

        public async Task<TInput> ProvideAuthenticationInputAsync<TInput>(RfbConnection connection, ISecurityType securityType, IAuthenticationInputRequest<TInput> request)
            where TInput : class, IAuthenticationInput
        {
            if (typeof(TInput) != typeof(PasswordAuthenticationInput))
                throw new UnsupportedProtocolFeatureException($"Authentication input {typeof(TInput).Name} is not supported.");

            Asked = true;
            string password = typed;
            if (password.Length == 0)
            {
                // The source may show a dialog (KeePass unlock) and reads the connection on the UI thread.
                password = await Dispatcher.UIThread.InvokeAsync(async () =>
                    window.Connection.HasStoredPassword
                        ? await AppHost.Current.Credentials.ReadAsync(window.Connection.Credentials.Secret) ?? ""
                        : "");
                UsedStored = password.Length > 0;
            }

            if (password.Length == 0)
            {
                Missing = true;
                throw new HandshakeFailedException("The server requires a password.");
            }

            return (TInput)(IAuthenticationInput)new PasswordAuthenticationInput(password);
        }
    }
}
