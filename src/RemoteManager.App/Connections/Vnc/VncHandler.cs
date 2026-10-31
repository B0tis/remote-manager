using Avalonia.Controls;
using Avalonia.Media;
using RemoteManager.App.Resources;
using RemoteManager.App.Views;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;

namespace RemoteManager.App.Connections.Vnc;

/// <summary>VNC in an embedded <see cref="VncSessionWindow"/>, or with an external viewer. Status: TCP check of the VNC port.</summary>
public sealed class VncHandler : ConnectionHandler
{
    public override ConnectionType Type => ConnectionType.Vnc;
    public override string DisplayName => "VNC";
    public override string Initials => "VNC";
    public override Color AccentColor => Color.FromRgb(0xF5, 0x9E, 0x0B);
    public override string PasswordHint => Strings.Editor_PasswordHint_Vnc;

    /// <summary>VNC authenticates with a password only.</summary>
    public override bool UsesUserName => false;

    public override ISettingsEditor? CreateSettingsEditor() => new VncSettingsEditor();

    public override Window? Open(ConnectionItem connection, IConnectionHost host)
    {
        if (connection.Model.SettingsAs<VncSettings>().OpenExternally)
        {
            _ = ExternalVncViewer.LaunchAsync(connection, host);
            return null;
        }

        var window = new VncSessionWindow(connection, host);
        window.SnapshotSaved += (_, _) =>
        {
            connection.Model.PreviewPath ??= Path.GetFileName(connection.Model.ResolvedPreviewPath);
            connection.ReloadPreview();
            host.Save(connection);
        };
        window.Show();
        return window;
    }
}
