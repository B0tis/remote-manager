using System.Net.Sockets;
using RemoteManager.Core.Model;

namespace RemoteManager.Core.Status;

public enum OnlineStatus { Unknown, Online, Offline }

/// <summary>
/// Finds out whether a connection's target is reachable. One probe per connection type, chosen by
/// the type's handler in the app. Must not throw; anything unclear is <see cref="OnlineStatus.Unknown"/>.
/// </summary>
public interface IStatusProbe
{
    Task<OnlineStatus> ProbeAsync(Connection connection, CancellationToken cancellationToken);
}

/// <summary>
/// TCP connect to <see cref="Connection.EffectivePort"/>. Connected: online. Refused: the host
/// answers but nothing listens, offline. Timeout, DNS failure, unreachable network: unknown, because
/// firewalls often drop silently and a blocked check must not look like a dead host.
/// </summary>
public sealed class TcpPortProbe : IStatusProbe
{
    public static readonly TcpPortProbe Instance = new();

    public async Task<OnlineStatus> ProbeAsync(Connection connection, CancellationToken cancellationToken)
    {
        string host = connection.Host.Trim();
        int port = connection.EffectivePort;
        if (host.Length == 0 || port is <= 0 or > 65535)
            return OnlineStatus.Unknown;

        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            return OnlineStatus.Online;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return OnlineStatus.Offline;
        }
        catch (Exception)
        {
            return OnlineStatus.Unknown;
        }
    }
}
