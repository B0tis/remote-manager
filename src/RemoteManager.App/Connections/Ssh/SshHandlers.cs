using Avalonia.Controls;
using Avalonia.Media;
using RemoteManager.App.Resources;
using RemoteManager.App.Views;
using RemoteManager.Core.Model;

namespace RemoteManager.App.Connections.Ssh;

/// <summary>SSH terminal (SSH.NET + xterm.js), one <see cref="SshTerminalWindow"/> per connection. Status: TCP check of the SSH port.</summary>
public sealed class SshHandler : ConnectionHandler
{
    public override ConnectionType Type => ConnectionType.Ssh;
    public override string DisplayName => "SSH";
    public override string Initials => "SSH";
    public override Color AccentColor => Color.FromRgb(0x10, 0xB9, 0x81);
    public override string PasswordHint => Strings.Editor_PasswordHint_Ssh;

    public override ISettingsEditor? CreateSettingsEditor() => new SshSettingsEditor(sftp: false);

    public override Window? Open(ConnectionItem connection, IConnectionHost host)
    {
        var window = new SshTerminalWindow(connection);
        window.SessionConnected += (_, _) => host.MarkConnected(connection);
        window.PasswordSaved += (_, _) => host.Save(connection);
        window.Show();
        return window;
    }
}

/// <summary>SFTP file browser (<see cref="SftpWindow"/>). Shares <see cref="Core.Model.Settings.SshSettings"/> with SSH.</summary>
public sealed class SftpHandler : ConnectionHandler
{
    public override ConnectionType Type => ConnectionType.Sftp;
    public override string DisplayName => "SFTP";
    public override string Initials => "FTP";
    public override Color AccentColor => Color.FromRgb(0x14, 0xB8, 0xA6);
    public override string PasswordHint => Strings.Editor_PasswordHint_Ssh;

    public override ISettingsEditor? CreateSettingsEditor() => new SshSettingsEditor(sftp: true);

    public override Window? Open(ConnectionItem connection, IConnectionHost host)
    {
        var window = new SftpWindow(connection);
        window.SessionConnected += (_, _) => host.MarkConnected(connection);
        window.PasswordSaved += (_, _) => host.Save(connection);
        window.Show();
        return window;
    }
}
