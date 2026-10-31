using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Connections.Ssh;
using RemoteManager.App.Resources;
using RemoteManager.App.Views;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.Ssh;

namespace RemoteManager.App.Connections.Rdp;

/// <summary>
/// RDP in an embedded MsRdpEx control, one <see cref="SessionWindow"/> per connection; with "open with
/// mstsc" or a RemoteApp through mstsc and a generated .rdp file (<see cref="ExternalRdpLauncher"/>).
/// </summary>
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
        RdpSettings settings = connection.Model.SettingsAs<RdpSettings>();
        if (settings.OpenExternally || settings.RemoteAppProgram.Trim().Length > 0)
        {
            _ = OpenExternallyAsync(connection, host);
            return null;
        }

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

    private static async Task OpenExternallyAsync(ConnectionItem connection, IConnectionHost host)
    {
        if (connection.Model.Host.Trim().Length == 0)
        {
            host.ShowToast(connection.Name, Strings.Session_NoHost, FAInfoBarSeverity.Warning);
            return;
        }

        // RDP over SSH: mstsc connects to the local end of the tunnel, which stays open until mstsc exits.
        string? jumpHostId = connection.Model.SettingsAs<RdpSettings>().JumpHostId is { Length: > 0 } id ? id : null;
        SshRoute? tunnel = null;
        try
        {
            (string Host, int Port)? tunnelEnd = null;
            if (jumpHostId is not null)
            {
                if (await SshConnect.OpenTunnelAsync(host.Owner, jumpHostId, connection.Model.Id,
                        connection.Model.Host.Trim(), connection.Model.EffectivePort, _ => { }) is not { } end)
                {
                    host.ShowToast(connection.Name, Strings.HostKey_Declined, FAInfoBarSeverity.Warning);
                    return;
                }

                tunnel = end.Route;
                tunnelEnd = (end.Host, end.Port);
            }

            ExternalRdpStart start = await ExternalRdpLauncher.PrepareAsync(connection.Model, host.App.Credentials, tunnelEnd);
            Process mstsc = ExternalRdpLauncher.Start(start);
            if (tunnel is not null)
            {
                SshRoute route = tunnel;
                mstsc.Exited += (_, _) => route.Dispose();
                tunnel = null;
            }

            host.MarkConnected(connection);
            host.ShowToast(connection.Name, start.WroteCredential ? Strings.Rdp_External_StartedWithPassword : Strings.Rdp_External_Started,
                FAInfoBarSeverity.Informational);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            host.ShowToast(connection.Name, string.Format(CultureInfo.CurrentCulture, Strings.Rdp_External_Failed, ex.Message), FAInfoBarSeverity.Error);
        }
        catch (Exception ex) when (jumpHostId is not null && ex is not OutOfMemoryException)
        {
            host.ShowToast(connection.Name, string.Format(CultureInfo.CurrentCulture, Strings.Rdp_SshTunnelFailed, SshConnect.Describe(ex)), FAInfoBarSeverity.Error);
        }
        finally
        {
            tunnel?.Dispose();
        }
    }
}
