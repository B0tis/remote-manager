using System.Globalization;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using RemoteManager.App.Resources;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.Ssh;

namespace RemoteManager.App.Connections.Ssh;

/// <summary>The SSH / SFTP part of the connection editor.</summary>
public partial class SshSettingsEditor : UserControl, ISettingsEditor
{
    private readonly bool sftp;
    private SshSettings loaded = new();
    private bool forgetHostKey;

    // Required by the XAML loader.
    public SshSettingsEditor() : this(sftp: false)
    {
    }

    public SshSettingsEditor(bool sftp)
    {
        this.sftp = sftp;
        InitializeComponent();
        AuthBox.SelectionChanged += (_, _) => UpdateAuthFields();
        BrowseButton.Click += async (_, _) => await BrowseKeyAsync();
        ForgetHostKeyButton.Click += (_, _) =>
        {
            forgetHostKey = true;
            UpdateHostKey();
        };
        // SFTP has no shell to forward with; SSH has no start folder.
        ForwardsPanel.IsVisible = !sftp;
        RemoteDirectoryBox.IsVisible = sftp;
    }

    public Control View => this;

    public void Load(ConnectionSettings settings)
    {
        loaded = (SshSettings)settings;
        forgetHostKey = false;
        AuthBox.SelectedIndex = (int)loaded.AuthMethod;
        KeyPathBox.Text = loaded.KeyPath;
        JumpHostPicker.Fill(JumpBox, loaded.JumpHostId);
        RemoteDirectoryBox.Text = loaded.RemoteDirectory;
        ForwardsBox.Text = PortForwardText.FormatAll(loaded.PortForwards);
        UpdateAuthFields();
        UpdateHostKey();
    }

    public string? Validate()
    {
        if ((SshAuthMethod)Math.Max(0, AuthBox.SelectedIndex) == SshAuthMethod.KeyFile && string.IsNullOrWhiteSpace(KeyPathBox.Text))
            return Strings.Ssh_Error_KeyPathEmpty;
        if (!sftp)
        {
            PortForwardText.ParseAll(ForwardsBox.Text, out int invalid);
            if (invalid > 0)
                return string.Format(CultureInfo.CurrentCulture, Strings.Ssh_Error_ForwardInvalid, invalid);
        }

        return null;
    }

    public ConnectionSettings Commit() => loaded with
    {
        AuthMethod = (SshAuthMethod)Math.Max(0, AuthBox.SelectedIndex),
        KeyPath = KeyPathBox.Text?.Trim() ?? "",
        JumpHostId = JumpHostPicker.SelectedId(JumpBox),
        RemoteDirectory = sftp ? RemoteDirectoryBox.Text?.Trim() ?? "" : loaded.RemoteDirectory,
        PortForwards = sftp ? loaded.PortForwards : PortForwardText.ParseAll(ForwardsBox.Text, out _),
        HostKeyFingerprint = forgetHostKey ? "" : loaded.HostKeyFingerprint,
    };

    private void UpdateAuthFields()
    {
        var method = (SshAuthMethod)Math.Max(0, AuthBox.SelectedIndex);
        KeyRow.IsVisible = method == SshAuthMethod.KeyFile;
        AuthHint.Text = method switch
        {
            SshAuthMethod.KeyFile => Strings.Ssh_Auth_KeyFileHint,
            SshAuthMethod.Agent => Strings.Ssh_Auth_AgentHint,
            _ => Strings.Ssh_Auth_PasswordHint,
        };
    }

    private void UpdateHostKey()
    {
        string fingerprint = forgetHostKey ? "" : loaded.HostKeyFingerprint;
        HostKeyText.Text = fingerprint.Length > 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.Ssh_HostKeyStored, fingerprint)
            : Strings.Ssh_HostKeyNone;
        ForgetHostKeyButton.IsVisible = fingerprint.Length > 0;
    }

    private async Task BrowseKeyAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
            return;
        string sshFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Ssh_KeyPickerTitle,
            SuggestedStartLocation = Directory.Exists(sshFolder) ? await top.StorageProvider.TryGetFolderFromPathAsync(new Uri(sshFolder)) : null,
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
            KeyPathBox.Text = path;
    }
}
