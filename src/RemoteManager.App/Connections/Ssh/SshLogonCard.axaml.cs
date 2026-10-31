using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using RemoteManager.App.Resources;
using RemoteManager.App.Views;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.Ssh;
using Renci.SshNet;

namespace RemoteManager.App.Connections.Ssh;

/// <summary>
/// User and password (or key passphrase) for an SSH or SFTP connection, and the connect itself
/// (<see cref="ConnectAsync{T}"/>): host-key prompt, error text, and "Passwort speichern" after a
/// successful logon. The last working logon stays in memory for reconnects while the window is open.
/// </summary>
public partial class SshLogonCard : UserControl
{
    private ConnectionItem? connection;
    private SshLogin? lastLogin;

    public SshLogonCard()
    {
        InitializeComponent();
        ConnectButton.Click += (_, _) => ConnectRequested?.Invoke(this, EventArgs.Empty);
        SecretBox.TextChanged += (_, _) =>
        {
            if (SecretBox.Text?.Length > 0 && connection is { HasStoredPassword: false })
                SaveSecretBox.IsChecked = true;
        };
    }

    public event EventHandler? ConnectRequested;

    /// <summary>A typed password was stored after a successful logon.</summary>
    public event EventHandler? SecretSaved;

    public bool IsBusy { get; private set; }

    /// <summary>The logon that worked last (in memory only), for reconnects and the SFTP window.</summary>
    public SshLogin? LastLogin => lastLogin;

    public string MessageText => Message.IsVisible ? Message.Text ?? "" : "";

    private SshAuthMethod Method => connection?.Model.SettingsAs<SshSettings>().AuthMethod ?? SshAuthMethod.Password;

    /// <summary>Everything for a logon is there: a user, and a stored password unless a key or agent is used.</summary>
    public bool CanConnectWithoutInput =>
        connection is not null && lastLogin is not null
        || connection is not null && connection.Credentials.UserName.Length > 0 && (Method != SshAuthMethod.Password || connection.HasStoredPassword);

    public void Load(ConnectionItem item, SshLogin? knownLogin = null)
    {
        connection = item;
        lastLogin = knownLogin;
        NameText.Text = item.Name;
        HostText.Text = item.HostLabel;
        TypeBadge.Background = item.TypeBrush;
        TypeBadgeText.Text = item.TypeInitials;
        UserBox.Text = knownLogin?.UserName ?? item.Credentials.UserName;
        item.RefreshCredentialState();

        string? jumpId = item.Model.SettingsAs<SshSettings>().JumpHostId;
        if (jumpId is { Length: > 0 } && SshConnect.Find(jumpId) is { } jump)
        {
            RouteText.Text = string.Format(CultureInfo.CurrentCulture, Strings.Ssh_Card_Via, jump.Name);
            RouteText.IsVisible = true;
        }

        (string? hint, bool usesSecret) = Method switch
        {
            SshAuthMethod.KeyFile => (string.Format(CultureInfo.CurrentCulture, Strings.Ssh_Card_KeyHint, item.Model.SettingsAs<SshSettings>().KeyPath), true),
            SshAuthMethod.Agent => (Strings.Ssh_Card_AgentHint, false),
            _ => (null, true),
        };
        AuthHint.Text = hint;
        AuthHint.IsVisible = hint is not null;
        SecretBox.IsVisible = SaveSecretBox.IsVisible = usesSecret;
        SecretBox.PlaceholderText = Method == SshAuthMethod.KeyFile
            ? item.HasStoredPassword ? Strings.Ssh_Card_PassphraseStored : Strings.Ssh_Card_Passphrase
            : item.HasStoredPassword ? Strings.Session_PasswordPlaceholderStored : Strings.Field_Password;
    }

    public void ShowMessage(string message)
    {
        Message.Text = message;
        Message.IsVisible = message.Length > 0;
    }

    public void FocusInput() => (UserBox.Text?.Length > 0 && SecretBox.IsVisible ? SecretBox : UserBox).Focus();

    /// <summary>
    /// Connects with the card's input (a typed secret wins, otherwise the stored one, or the last working
    /// logon). Null when it failed or the user declined the host key; the card then shows why.
    /// </summary>
    public async Task<(SshRoute Route, T Client)?> ConnectAsync<T>(Window owner, Func<ConnectionInfo, T> create, Action<string> status)
        where T : BaseClient
    {
        if (connection is null || IsBusy)
            return null;

        Connection model = connection.Model;
        string typed = SecretBox.Text ?? "";
        string user = UserBox.Text?.Trim() ?? "";
        bool save = typed.Length > 0 && SaveSecretBox.IsChecked == true;
        SetBusy(true);
        ShowMessage("");
        try
        {
            SshLogin login = typed.Length == 0 && lastLogin is not null && lastLogin.UserName == user
                ? lastLogin with { KnownHostKey = model.SettingsAs<SshSettings>().HostKeyFingerprint }
                : await SshLogin.FromConnectionAsync(model, AppHost.Current.Credentials, typed);
            if (user.Length > 0)
                login = login with { UserName = user };

            (SshRoute Route, T Client)? result = await SshConnect.ConnectAsync(owner, model, login, create, status);
            if (result is null)
            {
                ShowMessage(Strings.HostKey_Declined);
                return null;
            }

            lastLogin = login;
            SecretBox.Text = "";
            if (save)
                SaveSecret(model, user, typed);
            return result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowMessage(SshConnect.Describe(ex));
            if (SshConnect.NeedsSecret(ex, model))
                lastLogin = null;
            return null;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SaveSecret(Connection model, string user, string secret)
    {
        try
        {
            // An own password needs the user it belongs to, otherwise the connection would stop inheriting the group's user.
            if (model.UserName.Length == 0)
                model.UserName = user;
            AppHost.Current.Credentials.SaveOwnSecretAsync(model, secret).AsTask().GetAwaiter().GetResult();
            SecretSaved?.Invoke(this, EventArgs.Empty);
        }
        catch (Win32Exception ex)
        {
            ShowMessage(string.Format(CultureInfo.CurrentCulture, Strings.Password_SaveFailed, ex.Message));
        }

        connection?.RefreshCredentialState();
    }

    private void SetBusy(bool busy)
    {
        IsBusy = busy;
        ConnectButton.IsEnabled = !busy;
        UserBox.IsEnabled = SecretBox.IsEnabled = !busy;
        ConnectButton.Content = busy ? Strings.Ssh_Card_Connecting : Strings.Action_Connect;
    }
}
