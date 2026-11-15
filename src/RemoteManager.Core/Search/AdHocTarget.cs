using System.Net;
using System.Net.Sockets;
using RemoteManager.Core.Model;

namespace RemoteManager.Core.Search;

/// <summary>What the host part of an <see cref="AdHocTarget"/> is.</summary>
public enum AdHocHostKind
{
    HostName,
    IPv4,
    IPv6,
}

/// <summary>
/// A target typed into the quick launcher that is not a saved connection: a host name, an IPv4 or IPv6
/// address, optionally with "user@" in front, ":port" behind, and a type in front ("rdp ", "ssh ", "vnc ",
/// "sftp ", or as a scheme, "ssh://"). The launcher offers to connect to it once, without saving anything.
/// </summary>
/// <param name="Host">Host name or address as typed, an IPv6 address without its brackets.</param>
/// <param name="Port">The typed port, or 0 for the type's default.</param>
/// <param name="UserName">The part before "@" ("root", "admin@corp.example"), without a domain prefix; may be empty.</param>
/// <param name="Domain">The domain of "DOMAIN\user@host"; may be empty.</param>
/// <param name="Type">The type named in front of the target, or null when none was.</param>
/// <param name="Kind">Host name, IPv4, or IPv6.</param>
/// <param name="IsExplicit">
/// The text can hardly be anything but a target: an address, a name with a dot, or a type, user, or port
/// was given. A single word such as "kasse" is not explicit: it is a valid host name, but more likely a search.
/// </param>
public sealed record AdHocTarget(string Host, int Port, string UserName, string Domain, ConnectionType? Type, AdHocHostKind Kind, bool IsExplicit)
{
    private const int MaxHostLength = 253;
    private const int MaxLabelLength = 63;

    private static readonly (string Name, ConnectionType Type)[] Prefixes =
    [
        ("rdp", ConnectionType.Rdp), ("ssh", ConnectionType.Ssh), ("vnc", ConnectionType.Vnc), ("sftp", ConnectionType.Sftp),
    ];

    /// <summary>Host with the port when one was typed: "dc01", "10.0.0.5:3390", "[fe80::1]:22".</summary>
    public string Address => Port == 0 ? Host : Kind == AdHocHostKind.IPv6 ? $"[{Host}]:{Port}" : $"{Host}:{Port}";

    /// <summary>The whole target as one reads it: "root@10.0.0.5:2222", "CORP\admin@dc01".</summary>
    public string Label => UserName.Length == 0 ? Address : Domain.Length == 0 ? $"{UserName}@{Address}" : $"{Domain}\\{UserName}@{Address}";

    /// <summary>
    /// The connection types to offer, most likely first. A type in front of the target leaves only that one.
    /// Otherwise RDP, SSH, VNC, led by the type a well-known port points to, or by SSH for a bare "user@host".
    /// </summary>
    public IReadOnlyList<ConnectionType> SuggestedTypes
    {
        get
        {
            if (Type is { } named)
                return [named];

            ConnectionType? likely = Port switch
            {
                3389 => ConnectionType.Rdp,
                22 or 2222 => ConnectionType.Ssh,
                >= 5900 and <= 5999 => ConnectionType.Vnc,
                0 when UserName.Length > 0 && Domain.Length == 0 && !UserName.Contains('@') => ConnectionType.Ssh,
                _ => null,
            };
            ConnectionType[] all = [ConnectionType.Rdp, ConnectionType.Ssh, ConnectionType.Vnc];
            return likely is { } first ? [first, .. all.Where(t => t != first)] : all;
        }
    }

    /// <summary>
    /// A connection for this target with a new id. It is meant to be opened once and never added to the
    /// library; the name is the address, the port stays 0 when it is the type's default.
    /// </summary>
    public Connection ToConnection(ConnectionType type) => new()
    {
        Name = Address,
        Type = type,
        Host = Host,
        Port = Port == DefaultPorts.For(type) ? 0 : Port,
        UserName = UserName,
        Domain = Domain,
    };

    /// <summary>Reads <paramref name="query"/> as a target; null when it does not look like one.</summary>
    public static AdHocTarget? Parse(string? query)
    {
        string text = query?.Trim() ?? "";
        if (text.Length == 0)
            return null;

        ConnectionType? type = TakePrefix(ref text);
        if (text.Length == 0 || text.Any(char.IsWhiteSpace))
            return null;

        string user = "", domain = "";
        int at = text.LastIndexOf('@');
        if (at >= 0)
        {
            user = text[..at];
            text = text[(at + 1)..];

            // "user:password@host" would put a password into the search field and the window title: not accepted.
            if (user.Length == 0 || user.Contains(':') || user.Contains('/'))
                return null;
            int slash = user.IndexOf('\\');
            if (slash >= 0)
            {
                domain = user[..slash];
                user = user[(slash + 1)..];
                if (domain.Length == 0 || user.Length == 0 || user.Contains('\\'))
                    return null;
            }
        }

        if (!SplitPort(text, out string host, out int port, out bool portGiven))
            return null;
        if (Classify(ref host) is not { } kind)
            return null;

        bool isExplicit = type is not null || user.Length > 0 || portGiven || kind != AdHocHostKind.HostName || host.Contains('.');
        return new AdHocTarget(host, port, user, domain, type, kind, isExplicit);
    }

    /// <summary>Removes "ssh " or "ssh://" (any of the four types) from the front and returns the type.</summary>
    private static ConnectionType? TakePrefix(ref string text)
    {
        foreach ((string name, ConnectionType type) in Prefixes)
        {
            if (!text.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                continue;

            string rest = text[name.Length..];
            if (rest.StartsWith("://", StringComparison.Ordinal))
            {
                text = rest[3..].TrimEnd('/');
                return type;
            }

            if (rest.Length > 0 && char.IsWhiteSpace(rest[0]))
            {
                text = rest.TrimStart();
                return type;
            }
        }

        return null;
    }

    /// <summary>"host", "host:22", "[::1]", "[::1]:22", or a bare IPv6 address (which cannot carry a port).</summary>
    private static bool SplitPort(string text, out string host, out int port, out bool portGiven)
    {
        host = text;
        port = 0;
        portGiven = false;
        string portText;

        if (text.StartsWith('['))
        {
            int close = text.IndexOf(']');
            if (close < 0)
                return false;
            host = text[1..close];
            string rest = text[(close + 1)..];
            if (rest.Length == 0)
                return host.Contains(':');
            if (rest[0] != ':' || !host.Contains(':'))
                return false;
            portText = rest[1..];
        }
        else
        {
            int colon = text.IndexOf(':');
            if (colon < 0 || text.IndexOf(':', colon + 1) >= 0)
                return true;
            host = text[..colon];
            portText = text[(colon + 1)..];
        }

        // "host:" while the port is still being typed: the target stays, with the default port.
        portGiven = true;
        if (portText.Length == 0)
            return true;
        return portText.Length <= 5 && portText.All(char.IsAsciiDigit) && (port = int.Parse(portText, System.Globalization.CultureInfo.InvariantCulture)) is >= 1 and <= 65535;
    }

    private static AdHocHostKind? Classify(ref string host)
    {
        if (host.Contains(':'))
        {
            // The scope of a link-local address ("fe80::1%12") is part of the host.
            return IPAddress.TryParse(host, out IPAddress? address) && address.AddressFamily == AddressFamily.InterNetworkV6
                ? AdHocHostKind.IPv6
                : null;
        }

        if (host.EndsWith('.'))
            host = host[..^1];
        if (host.Length is 0 or > MaxHostLength)
            return null;

        string[] labels = host.Split('.');
        if (labels.All(l => l.Length > 0 && l.All(char.IsAsciiDigit)))
        {
            // Only digits: a full IPv4 address or nothing ("10.0.0" is still being typed, "123" is no host).
            return labels.Length == 4 && labels.All(l => l.Length <= 3 && int.Parse(l, System.Globalization.CultureInfo.InvariantCulture) <= 255)
                ? AdHocHostKind.IPv4
                : null;
        }

        foreach (string label in labels)
        {
            if (label.Length is 0 or > MaxLabelLength || label[0] == '-' || label[^1] == '-'
                || !label.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
            {
                return null;
            }
        }

        // A name never ends in a number ("server.12" is a typo of an address, not a host).
        return labels.Length > 1 && labels[^1].All(char.IsAsciiDigit) ? null : AdHocHostKind.HostName;
    }
}
