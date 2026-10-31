using System.Globalization;
using RemoteManager.App.Connections.Ssh;
using RemoteManager.App.Resources;
using RemoteManager.Core.Ssh;

namespace RemoteManager.App.Connections.Rdp;

/// <summary>
/// RDP over SSH (<see cref="Core.Model.Settings.RdpSettings.JumpHostId"/>): before connecting, a local
/// port is forwarded through the jump host (and its own jump hosts) to the RDP host, and the RDP
/// client connects to 127.0.0.1:port. The tunnel closes with the session.
/// </summary>
public partial class SessionWindow
{
    private SshRoute? tunnel;

    /// <summary>The session runs through an SSH tunnel.</summary>
    public bool IsTunneled => tunnel is not null;

    /// <summary>Local end of the tunnel to <paramref name="host"/>:<paramref name="port"/>; null (with a message on the card) when it failed or a host key was declined.</summary>
    private async Task<(string Host, int Port)?> OpenTunnelAsync(string jumpHostId, string host, int port)
    {
        CloseTunnel();
        ConnectButton.IsEnabled = false;
        try
        {
            SshRoute? route = await SshConnect.OpenRouteAsync(this, jumpHostId, Connection.Id, SetStatus);
            if (route is null)
            {
                ShowCardMessage(Strings.HostKey_Declined);
                return null;
            }

            tunnel = route;
            (string localHost, int localPort) = await Task.Run(() => route.Reach(host, port));
            Log($"SSH tunnel to {host}:{port} listens on {localHost}:{localPort}.");
            return (localHost, localPort);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CloseTunnel();
            ShowCardMessage(string.Format(CultureInfo.CurrentCulture, Strings.Rdp_SshTunnelFailed, SshConnect.Describe(ex)));
            return null;
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private void CloseTunnel()
    {
        tunnel?.Dispose();
        tunnel = null;
    }
}
