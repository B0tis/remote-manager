using System.Globalization;
using Avalonia.Controls;
using RemoteManager.App.Resources;
using RemoteManager.App.Views;
using RemoteManager.Core.Status;

namespace RemoteManager.App.Connections.RemoteTools;

/// <summary>
/// Shared base of TeamViewer, AnyDesk, and RustDesk: the target is a remote ID, the session runs in
/// the tool's own window. Launching arrives in Phase 2; until then Connect explains that and offers
/// to copy the password.
/// </summary>
public abstract class RemoteToolHandler : ConnectionHandler
{
    public override string HostPlaceholder => Strings.Editor_RemoteIdPlaceholder;
    public override bool HasPort => false;
    public override bool UsesUserName => false;
    public override string StatusHint => Strings.Status_Hint_RemoteTool;

    /// <summary>Online status comes from the tools' APIs (Phase 2); until then it stays unknown.</summary>
    public override IStatusProbe? StatusProbe => null;

    public override ISettingsEditor? CreateSettingsEditor() => new RemoteToolSettingsEditor();

    public override Window? Open(ConnectionItem connection, IConnectionHost host)
    {
        PlannedConnectionDialog.Show(connection, host,
            string.Format(CultureInfo.CurrentCulture, Strings.Planned_RemoteTool, DisplayName));
        return null;
    }
}
