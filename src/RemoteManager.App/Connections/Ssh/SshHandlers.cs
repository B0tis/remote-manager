using Avalonia.Controls;
using Avalonia.Media;
using RemoteManager.App.Resources;
using RemoteManager.App.Views;
using RemoteManager.Core.Model;

namespace RemoteManager.App.Connections.Ssh;

/// <summary>SSH terminal (Phase 3: SSH.NET + xterm.js). Status: TCP check of the SSH port.</summary>
public sealed class SshHandler : ConnectionHandler
{
    public override ConnectionType Type => ConnectionType.Ssh;
    public override string DisplayName => "SSH";
    public override string Initials => "SSH";
    public override Color AccentColor => Color.FromRgb(0x10, 0xB9, 0x81);

    public override Window? Open(ConnectionItem connection, IConnectionHost host)
    {
        PlannedConnectionDialog.Show(connection, host, Strings.Planned_Phase3);
        return null;
    }
}

/// <summary>SFTP file browser (Phase 3). Shares <see cref="Core.Model.Settings.SshSettings"/> with SSH.</summary>
public sealed class SftpHandler : ConnectionHandler
{
    public override ConnectionType Type => ConnectionType.Sftp;
    public override string DisplayName => "SFTP";
    public override string Initials => "FTP";
    public override Color AccentColor => Color.FromRgb(0x14, 0xB8, 0xA6);

    public override Window? Open(ConnectionItem connection, IConnectionHost host)
    {
        PlannedConnectionDialog.Show(connection, host, Strings.Planned_Phase3);
        return null;
    }
}
