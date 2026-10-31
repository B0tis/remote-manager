using System.Globalization;
using RemoteManager.Core.Credentials;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace RemoteManager.Core.Ssh;

/// <summary>
/// A chain of SSH connections through jump hosts. Each hop is reached through a local port forward
/// on the previous one (like ssh -J), so every hop authenticates and checks its own host key.
/// <see cref="Reach"/> opens a forward from the last hop to any TCP target (SSH, SFTP, RDP).
/// Disposing closes everything, innermost first.
/// </summary>
public sealed class SshRoute : IDisposable
{
    private readonly List<IDisposable> owned = [];
    private SshClient? lastHop;
    private bool disposed;

    /// <summary>Connects the jump hosts in order (outermost first). No hops: a direct route.</summary>
    public static async Task<SshRoute> OpenAsync(IReadOnlyList<SshLogin> hops, CancellationToken cancellationToken = default)
    {
        var route = new SshRoute();
        try
        {
            foreach (SshLogin hop in hops)
                route.lastHop = await route.ConnectAsync(hop, info => new SshClient(info), cancellationToken).ConfigureAwait(false);
            return route;
        }
        catch
        {
            route.Dispose();
            throw;
        }
    }

    public bool IsDirect => lastHop is null;

    /// <summary>Where to connect to get to <paramref name="host"/>:<paramref name="port"/>: the target itself, or a local forward through the last hop.</summary>
    public (string Host, int Port) Reach(string host, int port)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (lastHop is null)
            return (host, port);

        var forward = new ForwardedPortLocal("127.0.0.1", 0, host, (uint)port);
        lastHop.AddForwardedPort(forward);
        forward.Start();
        owned.Add(forward);
        return ("127.0.0.1", (int)forward.BoundPort);
    }

    /// <summary>
    /// Connects an SSH.NET client (SSH, SFTP) to <paramref name="login"/> along the route. The host key must
    /// match <see cref="SshLogin.KnownHostKey"/>; otherwise the handshake is aborted and
    /// <see cref="SshHostKeyException"/> tells the caller what the server presented.
    /// </summary>
    public async Task<T> ConnectAsync<T>(SshLogin login, Func<ConnectionInfo, T> create, CancellationToken cancellationToken = default)
        where T : BaseClient
    {
        (string host, int port) = Reach(login.Host, login.Port);
        T client = create(login.CreateConnectionInfo(host, port));
        owned.Add(client);

        SshHostKey? presented = null;
        HostKeyStatus status = HostKeyStatus.Unknown;
        client.HostKeyReceived += (_, e) =>
        {
            presented = SshHostKey.From(e);
            status = presented.CheckAgainst(login.KnownHostKey);
            e.CanTrust = status == HostKeyStatus.Trusted;
        };

        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SshConnectionException) when (presented is not null && status != HostKeyStatus.Trusted)
        {
            throw new SshHostKeyException(login, presented, status);
        }

        return client;
    }

    /// <summary>Starts the connection's own -L/-R/-D forwards on <paramref name="client"/>; returns one line per forward that failed.</summary>
    public static IReadOnlyList<string> StartForwards(SshClient client, IEnumerable<PortForward> forwards)
    {
        var problems = new List<string>();
        foreach (PortForward forward in forwards)
        {
            string bind = forward.BindAddress.Length > 0 ? forward.BindAddress : "127.0.0.1";
            ForwardedPort port = forward.Kind switch
            {
                PortForwardKind.Remote => new ForwardedPortRemote(bind, (uint)forward.BindPort, forward.TargetHost, (uint)forward.TargetPort),
                PortForwardKind.Dynamic => new ForwardedPortDynamic(bind, (uint)forward.BindPort),
                _ => new ForwardedPortLocal(bind, (uint)forward.BindPort, forward.TargetHost, (uint)forward.TargetPort),
            };
            try
            {
                client.AddForwardedPort(port);
                port.Start();
            }
            catch (Exception ex) when (ex is SshException or System.Net.Sockets.SocketException or InvalidOperationException)
            {
                problems.Add($"{PortForwardText.Format(forward)}: {ex.Message}");
                port.Dispose();
            }
        }

        return problems;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        for (int i = owned.Count - 1; i >= 0; i--)
        {
            try
            {
                owned[i].Dispose();
            }
            catch (Exception ex) when (ex is SshException or ObjectDisposedException or InvalidOperationException or System.Net.Sockets.SocketException)
            {
                // Already broken; nothing left to close.
            }
        }

        owned.Clear();
        lastHop = null;
    }
}

/// <summary>Follows <see cref="SshSettings.JumpHostId"/> / <see cref="RdpSettings.JumpHostId"/> / <see cref="VncSettings.JumpHostId"/> to the list of jump connections.</summary>
public static class SshJumpChain
{
    public const int MaxHops = 8;

    /// <summary>The first jump host of <paramref name="connection"/> (SSH, SFTP, RDP, VNC), or null when it connects directly.</summary>
    public static string? JumpHostOf(Connection connection) => connection.Settings switch
    {
        SshSettings s => s.JumpHostId,
        RdpSettings r => r.JumpHostId,
        VncSettings v => v.JumpHostId,
        _ => null,
    } is { Length: > 0 } id ? id : null;

    /// <summary>
    /// The jump hosts to pass, outermost first, starting at <paramref name="jumpHostId"/>. Every jump host
    /// must be an SSH connection; a missing one, a loop, or more than <see cref="MaxHops"/> hops is an error.
    /// </summary>
    public static IReadOnlyList<Connection> Resolve(string? jumpHostId, Func<string, Connection?> find, string? startId = null)
    {
        var chain = new List<Connection>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (startId is not null)
            seen.Add(startId);

        string? next = jumpHostId;
        while (next is { Length: > 0 })
        {
            Connection jump = find(next) ?? throw new SshLoginException(SshLoginProblem.JumpHostMissing, null, next);
            if (jump.Type is not (ConnectionType.Ssh or ConnectionType.Sftp))
                throw new SshLoginException(SshLoginProblem.JumpHostNotSsh, null, jump.Name);
            if (!seen.Add(jump.Id) || chain.Count >= MaxHops)
                throw new SshLoginException(SshLoginProblem.JumpHostLoop, null, jump.Name);
            chain.Add(jump);
            next = jump.SettingsAs<SshSettings>().JumpHostId;
        }

        chain.Reverse();
        return chain;
    }

    /// <summary>The logons of the jump hosts (stored credentials only), outermost first.</summary>
    public static async Task<IReadOnlyList<SshLogin>> LoginsAsync(string? jumpHostId, Func<string, Connection?> find, CredentialService credentials,
        string? startId = null, CancellationToken cancellationToken = default)
    {
        var logins = new List<SshLogin>();
        foreach (Connection jump in Resolve(jumpHostId, find, startId))
            logins.Add(await SshLogin.FromConnectionAsync(jump, credentials, cancellationToken: cancellationToken).ConfigureAwait(false));
        return logins;
    }
}

/// <summary>Port forwards as text, one per line: "L [bind:]port:host:hostport", "R [bind:]port:host:hostport", "D [bind:]port".</summary>
public static class PortForwardText
{
    public static string Format(PortForward forward)
    {
        string bind = forward.BindAddress.Length > 0 ? $"{forward.BindAddress}:" : "";
        return forward.Kind switch
        {
            PortForwardKind.Dynamic => $"D {bind}{forward.BindPort}",
            PortForwardKind.Remote => $"R {bind}{forward.BindPort}:{forward.TargetHost}:{forward.TargetPort}",
            _ => $"L {bind}{forward.BindPort}:{forward.TargetHost}:{forward.TargetPort}",
        };
    }

    public static string FormatAll(IEnumerable<PortForward> forwards) => string.Join(Environment.NewLine, forwards.Select(Format));

    /// <summary>Parses one line; also accepts "-L 8080:host:80" and lowercase letters. Null when it is not valid.</summary>
    public static PortForward? Parse(string line)
    {
        string text = line.Trim();
        if (text.StartsWith('-'))
            text = text[1..];
        if (text.Length < 3 || !char.IsWhiteSpace(text[1]))
            return null;

        PortForwardKind? kind = char.ToUpperInvariant(text[0]) switch
        {
            'L' => PortForwardKind.Local,
            'R' => PortForwardKind.Remote,
            'D' => PortForwardKind.Dynamic,
            _ => null,
        };
        if (kind is null)
            return null;

        string[] parts = text[2..].Trim().Split(':');
        if (kind == PortForwardKind.Dynamic)
        {
            return parts.Length switch
            {
                1 when TryPort(parts[0], out int p) => new PortForward(PortForwardKind.Dynamic, "", p, "", 0),
                2 when parts[0].Length > 0 && TryPort(parts[1], out int p) => new PortForward(PortForwardKind.Dynamic, parts[0], p, "", 0),
                _ => null,
            };
        }

        return parts.Length switch
        {
            3 when TryPort(parts[0], out int bp) && parts[1].Length > 0 && TryPort(parts[2], out int tp) => new PortForward(kind.Value, "", bp, parts[1], tp),
            4 when parts[0].Length > 0 && TryPort(parts[1], out int bp) && parts[2].Length > 0 && TryPort(parts[3], out int tp) => new PortForward(kind.Value, parts[0], bp, parts[2], tp),
            _ => null,
        };
    }

    /// <summary>Parses every non-empty line; <paramref name="invalidLine"/> is the first bad one (1-based), or 0.</summary>
    public static IReadOnlyList<PortForward> ParseAll(string? text, out int invalidLine)
    {
        var result = new List<PortForward>();
        invalidLine = 0;
        string[] lines = (text ?? "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0)
                continue;
            if (Parse(lines[i]) is { } forward)
                result.Add(forward);
            else if (invalidLine == 0)
                invalidLine = i + 1;
        }

        return result;
    }

    private static bool TryPort(string text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;
}
