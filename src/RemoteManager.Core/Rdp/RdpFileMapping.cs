using System.Globalization;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;

namespace RemoteManager.Core.Rdp;

/// <summary>
/// Converts between an RDP connection and an .rdp file: <see cref="ToRdpFile"/> for the external start
/// with mstsc, <see cref="ToConnection"/> for the .rdp import. An .rdp file never carries a password:
/// mstsc reads it from the Credential Manager (TERMSRV/&lt;host&gt;).
/// </summary>
public static class RdpFileMapping
{
    /// <summary>The .rdp content for <paramref name="connection"/>, logging on as <paramref name="userName"/>.</summary>
    public static RdpFile ToRdpFile(Connection connection, string userName, string domain)
    {
        RdpSettings s = connection.SettingsAs<RdpSettings>();
        var file = new RdpFile()
            .Set("full address", FormatAddress(connection.Host.Trim(), connection.EffectivePort))
            .Set("screen mode id", 1);

        if (FormatUser(userName, domain) is { Length: > 0 } user)
            file.Set("username", user);
        // Use the TERMSRV credential when there is one; mstsc still asks when there is none.
        file.Set("prompt for credentials", 0);

        switch (s.Display)
        {
            case RdpDisplayMode.FitToWindow:
                file.Set("dynamic resolution", true).Set("smart sizing", false);
                break;
            case RdpDisplayMode.SmartSizing:
                file.Set("dynamic resolution", false).Set("smart sizing", true);
                break;
            case RdpDisplayMode.FixedResolution when s.DesktopWidth > 0 && s.DesktopHeight > 0:
                file.Set("dynamic resolution", false).Set("smart sizing", true)
                    .Set("desktopwidth", s.DesktopWidth).Set("desktopheight", s.DesktopHeight);
                break;
        }

        file.Set("use multimon", s.UseMultimon)
            .Set("keyboardhook", s.Keyboard switch { RdpKeyboardMode.Local => 0, RdpKeyboardMode.FullScreenOnly => 2, _ => 1 })
            .Set("enablecredsspsupport", s.EnableNla)
            .Set("authentication level", Math.Clamp(s.AuthenticationLevel, 0, 2))
            .Set("administrative session", s.AdminSession);

        if (s.GatewayUsage != RdpGatewayUsage.None && s.GatewayHost.Trim().Length > 0)
        {
            file.Set("gatewayhostname", s.GatewayHost.Trim())
                .Set("gatewayusagemethod", s.GatewayUsage == RdpGatewayUsage.Always ? 1 : 2)
                .Set("gatewayprofileusagemethod", 1)
                .Set("gatewaycredentialssource", 4)
                .Set("promptcredentialonce", s.GatewayUsesConnectionCredentials);
        }
        else
        {
            file.Set("gatewayusagemethod", 0).Set("gatewayprofileusagemethod", 1);
        }

        RdpRedirections r = s.Redirections;
        file.Set("redirectclipboard", r.Clipboard)
            .Set("redirectprinters", r.Printers)
            .Set("redirectsmartcards", r.SmartCards)
            .Set("redirectcomports", r.Ports)
            .Set("drivestoredirect", r.Drives ? "*" : "")
            .Set("audiomode", r.Audio switch { RdpAudioMode.Remote => 1, RdpAudioMode.None => 2, _ => 0 })
            .Set("audiocapturemode", r.Microphone);

        if (s.RemoteAppProgram.Trim().Length > 0)
        {
            file.Set("remoteapplicationmode", true)
                .Set("remoteapplicationprogram", s.RemoteAppProgram.Trim())
                .Set("remoteapplicationname", connection.Name)
                .Set("disableremoteappcapscheck", true);
        }
        else if (s.StartProgram.Trim().Length > 0)
        {
            file.Set("alternate shell", s.StartProgram.Trim());
        }

        if (s.WorkingDirectory.Trim().Length > 0)
            file.Set("shell working directory", s.WorkingDirectory.Trim());
        return file;
    }

    /// <summary>
    /// mstsc switches for options that have no reliable .rdp key: /admin, /restrictedAdmin, /remoteGuard,
    /// /multimon. Never a credential.
    /// </summary>
    public static IReadOnlyList<string> MstscSwitches(RdpSettings settings)
    {
        var switches = new List<string>();
        if (settings.AdminSession)
            switches.Add("/admin");
        if (settings.RestrictedAdmin)
            switches.Add("/restrictedAdmin");
        else if (settings.RemoteCredentialGuard)
            switches.Add("/remoteGuard");
        if (settings.UseMultimon)
            switches.Add("/multimon");
        return switches;
    }

    /// <summary>A connection from an .rdp file; the file name (without extension) becomes the name.</summary>
    public static Connection ToConnection(RdpFile file, string name)
    {
        (string host, int port) = ParseAddress(file.GetString("full address") ?? file.GetString("alternate full address") ?? "");
        if (port == 0 && file.GetInt("server port") is > 0 and < 65536 and var serverPort)
            port = serverPort;

        (string user, string domain) = SplitUser(file.GetString("username") ?? "");
        if (domain.Length == 0)
            domain = file.GetString("domain")?.Trim() ?? "";

        var settings = new RdpSettings();
        int width = file.GetInt("desktopwidth") ?? 0, height = file.GetInt("desktopheight") ?? 0;
        settings = file.GetBool("dynamic resolution", false) ? settings with { Display = RdpDisplayMode.FitToWindow }
            : width > 0 && height > 0 ? settings with { Display = RdpDisplayMode.FixedResolution, DesktopWidth = width, DesktopHeight = height }
            : file.GetBool("smart sizing", false) ? settings with { Display = RdpDisplayMode.SmartSizing }
            : settings;

        string gateway = file.GetString("gatewayhostname")?.Trim() ?? "";
        settings = settings with
        {
            UseMultimon = file.GetBool("use multimon", false),
            Keyboard = file.GetInt("keyboardhook") switch { 0 => RdpKeyboardMode.Local, 2 => RdpKeyboardMode.FullScreenOnly, _ => RdpKeyboardMode.Session },
            EnableNla = file.GetBool("enablecredsspsupport", true),
            AuthenticationLevel = Math.Clamp(file.GetInt("authentication level") ?? 2, 0, 2),
            AdminSession = file.GetBool("administrative session", false),
            RestrictedAdmin = file.GetBool("restricted administration mode", false),
            RemoteCredentialGuard = file.GetBool("redirectedauthentication", false) || file.GetBool("remote credential guard", false),
            GatewayHost = gateway,
            GatewayUsage = gateway.Length == 0 ? RdpGatewayUsage.None : file.GetInt("gatewayusagemethod") switch
            {
                1 => RdpGatewayUsage.Always,
                2 => RdpGatewayUsage.Detect,
                _ => RdpGatewayUsage.None,
            },
            GatewayUsesConnectionCredentials = file.GetBool("promptcredentialonce", true),
            Redirections = new RdpRedirections
            {
                Clipboard = file.GetBool("redirectclipboard", true),
                Printers = file.GetBool("redirectprinters", false),
                SmartCards = file.GetBool("redirectsmartcards", false),
                Ports = file.GetBool("redirectcomports", false),
                Drives = file.GetString("drivestoredirect")?.Trim().Length > 0 || file.GetBool("redirectdrives", false),
                Audio = file.GetInt("audiomode") switch { 1 => RdpAudioMode.Remote, 2 => RdpAudioMode.None, _ => RdpAudioMode.Local },
                Microphone = file.GetBool("audiocapturemode", false),
            },
            RemoteAppProgram = file.GetBool("remoteapplicationmode", false) ? file.GetString("remoteapplicationprogram")?.Trim() ?? "" : "",
            StartProgram = file.GetString("alternate shell")?.Trim() ?? "",
            WorkingDirectory = file.GetString("shell working directory")?.Trim() ?? "",
        };

        return new Connection
        {
            Name = name,
            Type = ConnectionType.Rdp,
            Host = host,
            Port = port == DefaultPorts.For(ConnectionType.Rdp) ? 0 : port,
            UserName = user,
            Domain = domain,
            Settings = settings,
        };
    }

    /// <summary>"host", "host:3390", "[::1]:3390", or a bare IPv6 address; port 0 when none is given.</summary>
    public static (string Host, int Port) ParseAddress(string address)
    {
        address = address.Trim();
        if (address.StartsWith('['))
        {
            int end = address.IndexOf(']', StringComparison.Ordinal);
            if (end > 0)
            {
                string rest = address[(end + 1)..];
                return (address[1..end], rest.StartsWith(':') && TryPort(rest[1..], out int p) ? p : 0);
            }
        }

        int colon = address.LastIndexOf(':');
        // More than one colon without brackets is a bare IPv6 address.
        if (colon > 0 && address.IndexOf(':', StringComparison.Ordinal) == colon && TryPort(address[(colon + 1)..], out int port))
            return (address[..colon], port);
        return (address, 0);
    }

    public static string FormatAddress(string host, int port)
    {
        string h = host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[') ? $"[{host}]" : host;
        return port > 0 && port != DefaultPorts.For(ConnectionType.Rdp) ? $"{h}:{port.ToString(CultureInfo.InvariantCulture)}" : h;
    }

    /// <summary>"DOMAIN\user" as (user, DOMAIN); a UPN or a plain name stays the user.</summary>
    public static (string User, string Domain) SplitUser(string value)
    {
        value = value.Trim();
        int slash = value.IndexOf('\\', StringComparison.Ordinal);
        return slash > 0 ? (value[(slash + 1)..], value[..slash]) : (value, "");
    }

    public static string FormatUser(string user, string domain) =>
        user.Length == 0 ? "" : domain.Length > 0 ? $"{domain}\\{user}" : user;

    private static bool TryPort(string text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and < 65536;
}
