using Avalonia.Controls;
using Avalonia.Media;
using RemoteManager.App.Resources;
using RemoteManager.App.Views;
using RemoteManager.Core.Model;

namespace RemoteManager.App.Connections.Vnc;

/// <summary>VNC, embedded or with an external viewer (Phase 3). Status: TCP check of the VNC port.</summary>
public sealed class VncHandler : ConnectionHandler
{
    public override ConnectionType Type => ConnectionType.Vnc;
    public override string DisplayName => "VNC";
    public override string Initials => "VNC";
    public override Color AccentColor => Color.FromRgb(0xF5, 0x9E, 0x0B);

    /// <summary>VNC authenticates with a password only.</summary>
    public override bool UsesUserName => false;

    public override Window? Open(ConnectionItem connection, IConnectionHost host)
    {
        PlannedConnectionDialog.Show(connection, host, Strings.Planned_Phase3);
        return null;
    }
}
