using Avalonia.Controls;
using Avalonia.Media;
using RemoteManager.App.Resources;
using RemoteManager.App.Views;
using RemoteManager.Core.Model;

namespace RemoteManager.App.Connections.Rdp;

/// <summary>RDP in an embedded MsRdpEx control, one <see cref="SessionWindow"/> per connection.</summary>
public sealed class RdpHandler : ConnectionHandler
{
    public override ConnectionType Type => ConnectionType.Rdp;
    public override string DisplayName => "RDP";
    public override string Initials => "RDP";
    public override Color AccentColor => Color.FromRgb(0x3B, 0x82, 0xF6);
    public override string PasswordHint => Strings.Editor_PasswordHint_Rdp;
    public override string StatusHint => Strings.Status_Hint_Rdp;

    public override ISettingsEditor? CreateSettingsEditor() => new RdpSettingsEditor();

    public override Window? Open(ConnectionItem connection, IConnectionHost host)
    {
        var window = new SessionWindow(connection);
        window.SnapshotSaved += (_, _) =>
        {
            connection.Model.PreviewPath ??= Path.GetFileName(connection.Model.ResolvedPreviewPath);
            connection.ReloadPreview();
            host.Save(connection);
        };
        window.PasswordSaved += (_, _) => host.Save(connection);
        window.SessionLoggedIn += (_, _) => host.MarkConnected(connection);
        window.Show();
        return window;
    }
}
