using System.Globalization;
using System.Net.Sockets;
using Avalonia.Controls;
using RemoteManager.App.Resources;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.Ssh;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace RemoteManager.App.Connections.Ssh;

/// <summary>
/// Opens SSH routes and clients for the windows: resolves jump hosts and logons, asks about unknown
/// or changed host keys (<see cref="HostKeyWindow"/>), stores accepted keys, and retries. Network work
/// runs off the UI thread; the prompts run on it.
/// </summary>
internal static class SshConnect
{
    /// <summary>
    /// Connects a client to <paramref name="target"/> through its jump hosts. Returns null when the user
    /// declined a host key; other failures throw (see <see cref="Describe"/>). The route owns the client.
    /// </summary>
    public static async Task<(SshRoute Route, T Client)?> ConnectAsync<T>(Window owner, Connection connection, SshLogin target,
        Func<ConnectionInfo, T> create, Action<string> status, CancellationToken cancellationToken = default)
        where T : BaseClient
    {
        while (true)
        {
            SshRoute? route = null;
            try
            {
                route = await OpenRouteCoreAsync(connection.SettingsAs<SshSettings>().JumpHostId, connection.Id, status, cancellationToken);
                status(string.Format(CultureInfo.CurrentCulture, Strings.Ssh_Status_Connecting, target.Host));
                SshRoute open = route;
                T client = await Task.Run(() => open.ConnectAsync(target, create, cancellationToken), cancellationToken);
                return (route, client);
            }
            catch (SshHostKeyException ex)
            {
                route?.Dispose();
                if (!await HostKeyWindow.AskAsync(owner, ex))
                    return null;
                // The target may be a connection that is not in the library yet (or a test one): update it directly.
                if (ex.Login.ConnectionId == connection.Id)
                {
                    TrustHostKey(connection, ex.Presented);
                    target = target with { KnownHostKey = ex.Presented.Sha256Fingerprint };
                }
                else
                {
                    TrustHostKey(ex.Login.ConnectionId, ex.Presented);
                }
            }
            catch
            {
                route?.Dispose();
                throw;
            }
        }
    }

    /// <summary>A route through <paramref name="jumpHostId"/> and its own jump hosts (RDP over SSH). Null when the user declined a host key.</summary>
    public static async Task<SshRoute?> OpenRouteAsync(Window owner, string jumpHostId, string? startId, Action<string> status, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            try
            {
                return await OpenRouteCoreAsync(jumpHostId, startId, status, cancellationToken);
            }
            catch (SshHostKeyException ex)
            {
                if (!await HostKeyWindow.AskAsync(owner, ex))
                    return null;
                TrustHostKey(ex.Login.ConnectionId, ex.Presented);
            }
        }
    }

    private static async Task<SshRoute> OpenRouteCoreAsync(string? jumpHostId, string? startId, Action<string> status, CancellationToken cancellationToken)
    {
        AppHost app = AppHost.Current;
        IReadOnlyList<SshLogin> hops = await SshJumpChain.LoginsAsync(jumpHostId, Find, app.Credentials, startId, cancellationToken);
        if (hops.Count > 0)
            status(string.Format(CultureInfo.CurrentCulture, Strings.Ssh_Status_JumpHost, string.Join(" → ", hops.Select(h => h.Name))));
        return await Task.Run(() => SshRoute.OpenAsync(hops, cancellationToken), cancellationToken);
    }

    /// <summary>Connections outside the library that may serve as jump hosts (the self-test's temporary ones).</summary>
    internal static Dictionary<string, Connection> TemporaryConnections { get; } = [];

    internal static Connection? Find(string id) =>
        AppHost.Current.Library.FindConnection(id) ?? TemporaryConnections.GetValueOrDefault(id);

    private static void TrustHostKey(string connectionId, SshHostKey key)
    {
        if (Find(connectionId) is { } connection)
            TrustHostKey(connection, key);
    }

    /// <summary>Stores the accepted fingerprint on the connection (and saves it when it is in the library).</summary>
    public static void TrustHostKey(Connection connection, SshHostKey key)
    {
        connection.Settings = connection.SettingsAs<SshSettings>() with { HostKeyFingerprint = key.Sha256Fingerprint };
        if (AppHost.Current.Library.Contains(connection))
            AppHost.Current.Library.Update(connection);
    }

    /// <summary>German message for a failed connect. Never contains a secret (SSH.NET messages do not either).</summary>
    public static string Describe(Exception exception) => exception switch
    {
        SshLoginException ex => DescribeLogin(ex),
        SshAuthenticationException ex => string.Format(CultureInfo.CurrentCulture, Strings.Ssh_Error_Auth, ex.Message),
        SshAgentException ex => string.Format(CultureInfo.CurrentCulture, Strings.Ssh_Error_Agent, ex.Message),
        SocketException ex => string.Format(CultureInfo.CurrentCulture, Strings.Ssh_Error_Network, ex.Message),
        SshOperationTimeoutException => Strings.Ssh_Error_Timeout,
        OperationCanceledException => Strings.Ssh_Error_Canceled,
        _ => string.Format(CultureInfo.CurrentCulture, Strings.Ssh_Error_Other, exception.Message),
    };

    private static string DescribeLogin(SshLoginException ex)
    {
        string name = ex.Login?.Name ?? ex.Detail;
        string format = ex.Problem switch
        {
            SshLoginProblem.MissingUser => Strings.Ssh_Error_MissingUser,
            SshLoginProblem.MissingPassword => Strings.Ssh_Error_MissingPassword,
            SshLoginProblem.KeyFileMissing => Strings.Ssh_Error_KeyFileMissing,
            SshLoginProblem.KeyFileUnreadable => Strings.Ssh_Error_KeyFileUnreadable,
            SshLoginProblem.PassphraseRequired => Strings.Ssh_Error_PassphraseRequired,
            SshLoginProblem.WrongPassphrase => Strings.Ssh_Error_WrongPassphrase,
            SshLoginProblem.NoAgentKeys => Strings.Ssh_Error_NoAgentKeys,
            SshLoginProblem.JumpHostMissing => Strings.Ssh_Error_JumpHostMissing,
            SshLoginProblem.JumpHostNotSsh => Strings.Ssh_Error_JumpHostNotSsh,
            _ => Strings.Ssh_Error_JumpHostLoop,
        };
        return string.Format(CultureInfo.CurrentCulture, format, name, ex.Login?.KeyPath ?? ex.Detail);
    }

    /// <summary>Whether the window can fix the problem by asking for a password or passphrase on its card.</summary>
    public static bool NeedsSecret(Exception exception, Connection connection) =>
        exception is SshLoginException { Problem: SshLoginProblem.MissingPassword or SshLoginProblem.PassphraseRequired or SshLoginProblem.WrongPassphrase } ex
            && ex.Login?.ConnectionId == connection.Id
        || exception is SshAuthenticationException && connection.SettingsAs<SshSettings>().AuthMethod == SshAuthMethod.Password;
}
