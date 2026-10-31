using System.Diagnostics;
using Avalonia.Controls;
using RemoteManager.App.Resources;
using RemoteManager.App.Views;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.Status;

namespace RemoteManager.App.Connections.RemoteTools;

/// <summary>
/// Shared base of TeamViewer, AnyDesk, and RustDesk: the target is a remote ID, the session runs in
/// the tool's own window. Connect starts the tool through <see cref="RemoteToolLauncher"/>.
/// </summary>
public abstract class RemoteToolHandler : ConnectionHandler, IFileTransferHandler
{
    public override string HostPlaceholder => Strings.Editor_RemoteIdPlaceholder;
    public override bool HasPort => false;
    public override bool UsesUserName => false;
    public override string PasswordHint => Strings.RemoteTool_PasswordHint;
    public override string StatusHint => Strings.Status_Hint_RemoteTool;

    /// <summary>AnyDesk and RustDesk have no status source without a license API: unknown.</summary>
    public override IStatusProbe? StatusProbe => null;

    public override ISettingsEditor? CreateSettingsEditor() => new RemoteToolSettingsEditor(Type);

    public override Window? Open(ConnectionItem connection, IConnectionHost host)
    {
        Launch(connection, host, null);
        return null;
    }

    public void OpenFileTransfer(ConnectionItem connection, IConnectionHost host) =>
        Launch(connection, host, RemoteToolMode.FileTransfer);

    private static async void Launch(ConnectionItem connection, IConnectionHost host, RemoteToolMode? mode)
    {
        using Process? process = await RemoteToolLauncher.LaunchAsync(connection, host, mode);
    }
}
