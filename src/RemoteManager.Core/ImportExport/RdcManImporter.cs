using System.Globalization;
using System.Xml.Linq;
using RemoteManager.Core.Credentials;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.Rdp;

namespace RemoteManager.Core.ImportExport;

/// <summary>
/// Remote Desktop Connection Manager (Sysinternals RDCMan) .rdg files, schema 1 (2.2) and 3 (2.7+).
/// The file and its groups become nested groups. Logon credentials map onto group inheritance: a
/// server or group with "inherit from parent" gets no own user. Passwords are DPAPI-encrypted for the
/// user who saved them; they are taken over when that is the current user, otherwise reported.
/// Setting sections (connection, gateway, display, resources, security) follow RDCMan's own
/// inheritance and are resolved per server.
/// </summary>
public sealed class RdcManImporter : IImporter
{
    public string Id => "rdcman";
    public string FormatName => "RDCMan (.rdg)";
    public IReadOnlyList<string> FileExtensions { get; } = [".rdg"];

    public bool CanImport(string path) =>
        File.Exists(path) && Path.GetExtension(path).Equals(".rdg", StringComparison.OrdinalIgnoreCase);

    public async Task<ImportResult> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        XDocument document;
        await using (FileStream stream = File.OpenRead(path))
            document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);

        XElement file = document.Root?.Element("file") ?? throw new InvalidDataException("Not an RDCMan file: <RDCMan><file> is missing.");
        var reader = new Reader(path, new ImportResult());
        reader.ReadGroup(file, parentId: null, Path.GetFileNameWithoutExtension(path));
        return reader.Result;
    }

    private sealed class Reader(string path, ImportResult result)
    {
        public ImportResult Result => result;

        public void ReadGroup(XElement node, string? parentId, string fallbackName)
        {
            string name = Text(node, "name") is { Length: > 0 } n ? n : fallbackName;
            var group = new ConnectionGroup
            {
                Name = name,
                ParentId = parentId,
                Description = Text(node, "comment"),
                SortOrder = result.Groups.Count(g => g.ParentId == parentId),
                IsCollapsed = string.Equals(Text(node, "expanded"), "False", StringComparison.OrdinalIgnoreCase),
            };
            if (OwnLogon(node, name) is { } logon)
            {
                (group.UserName, group.Domain) = (logon.User, logon.Domain);
                if (logon.Password is { Length: > 0 } password)
                    result.GroupSecrets[group.Id] = password;
            }

            result.Groups.Add(group);

            int order = 0;
            foreach (XElement child in node.Elements())
            {
                switch (child.Name.LocalName)
                {
                    case "group":
                        ReadGroup(child, group.Id, "Gruppe");
                        break;
                    case "server":
                        ReadServer(child, group.Id, order++);
                        break;
                    case "smartGroup":
                        result.Warnings.Add($"Smart group \"{Text(child, "name")}\" skipped (its rules have no equivalent).");
                        break;
                }
            }
        }

        private void ReadServer(XElement server, string groupId, int order)
        {
            string host = Text(server, "name");
            if (host.Length == 0)
            {
                result.Warnings.Add($"{Path.GetFileName(path)}: server without a name skipped.");
                return;
            }

            string display = Text(server, "displayName");
            var connection = new Connection
            {
                Name = display.Length > 0 ? display : host,
                Type = ConnectionType.Rdp,
                Host = host,
                Description = Text(server, "comment"),
                GroupId = groupId,
                SortOrder = order,
            };

            if (OwnLogon(server, connection.Name) is { } logon)
            {
                (connection.UserName, connection.Domain) = (logon.User, logon.Domain);
                if (logon.Password is { Length: > 0 } password)
                    result.Secrets[connection.Id] = password;
            }

            XElement? settings = Section(server, "connectionSettings");
            if (Int(settings, "port") is > 0 and < 65536 and var port && port != DefaultPorts.For(ConnectionType.Rdp))
                connection.Port = port;
            connection.Settings = ReadSettings(server, settings);
            result.Connections.Add(connection);
        }

        private RdpSettings ReadSettings(XElement server, XElement? connectionSettings)
        {
            var s = new RdpSettings
            {
                AdminSession = Bool(connectionSettings, "connectToConsole") ?? false,
                StartProgram = Value(connectionSettings, "startProgram"),
                WorkingDirectory = Value(connectionSettings, "workingDir"),
            };

            if (Section(server, "gatewaySettings") is { } gateway && Bool(gateway, "enabled") == true && Value(gateway, "hostName") is { Length: > 0 } gatewayHost)
            {
                s = s with
                {
                    GatewayHost = gatewayHost,
                    GatewayUsage = Bool(gateway, "localBypass") == true ? RdpGatewayUsage.Detect : RdpGatewayUsage.Always,
                    GatewayUsesConnectionCredentials = Bool(gateway, "credSharing") ?? true,
                    GatewayUserName = RdpFileMapping.FormatUser(Value(gateway, "userName"), Value(gateway, "domain")),
                };
            }

            if (Section(server, "remoteDesktop") is { } display)
            {
                // "1024 x 768"; sameSizeAsClientArea means the session follows the window.
                string[] size = Value(display, "size").Split('x', StringSplitOptions.TrimEntries);
                if (Bool(display, "sameSizeAsClientArea") != true && size.Length == 2
                    && int.TryParse(size[0], CultureInfo.InvariantCulture, out int width) && int.TryParse(size[1], CultureInfo.InvariantCulture, out int height))
                {
                    s = s with { Display = RdpDisplayMode.FixedResolution, DesktopWidth = width, DesktopHeight = height };
                }
            }

            if (Section(server, "localResources") is { } resources)
            {
                s = s with
                {
                    Keyboard = Value(resources, "keyboardHook") switch
                    {
                        "Client" or "0" => RdpKeyboardMode.Local,
                        "FullScreenClient" or "2" => RdpKeyboardMode.FullScreenOnly,
                        _ => RdpKeyboardMode.Session,
                    },
                    Redirections = new RdpRedirections
                    {
                        Clipboard = Bool(resources, "redirectClipboard") ?? true,
                        Drives = Bool(resources, "redirectDrives") ?? false,
                        Printers = Bool(resources, "redirectPrinters") ?? false,
                        Ports = Bool(resources, "redirectPorts") ?? false,
                        SmartCards = Bool(resources, "redirectSmartCards") ?? false,
                        Audio = Value(resources, "audioRedirection") switch
                        {
                            "Remote" or "1" => RdpAudioMode.Remote,
                            "NoSound" or "2" => RdpAudioMode.None,
                            _ => RdpAudioMode.Local,
                        },
                        Microphone = Value(resources, "audioCaptureRedirection") is "Record" or "1",
                    },
                };
            }

            if (Section(server, "securitySettings") is { } security)
            {
                s = s with
                {
                    AuthenticationLevel = Value(security, "authentication") switch
                    {
                        "None" or "Connect" or "0" => 0,
                        "DoNotConnect" or "1" => 1,
                        _ => 2,
                    },
                };
            }

            return s;
        }

        /// <summary>The node's own logon (null when it inherits from its parent or sets nothing).</summary>
        private (string User, string Domain, string? Password)? OwnLogon(XElement node, string label)
        {
            XElement? logon = node.Element("logonCredentials");
            if (logon is null || Inherits(logon))
                return null;

            // A named profile lives in a <credentialsProfiles> section of this or an outer node.
            string profile = Value(logon, "profileName");
            if (profile.Length > 0 && !profile.Equals("Custom", StringComparison.OrdinalIgnoreCase))
            {
                XElement? found = node.AncestorsAndSelf().Select(a => a.Element("credentialsProfiles")).OfType<XElement>()
                    .SelectMany(p => p.Elements("credentialsProfile"))
                    .FirstOrDefault(p => string.Equals(Value(p, "profileName"), profile, StringComparison.OrdinalIgnoreCase));
                if (found is null)
                {
                    result.Warnings.Add($"{label}: credential profile \"{profile}\" is not in the file (stored in RDCMan's settings), logon not taken over.");
                    return null;
                }

                logon = found;
            }

            string user = Value(logon, "userName"), domain = Value(logon, "domain");
            (user, string userDomain) = RdpFileMapping.SplitUser(user);
            if (domain.Length == 0)
                domain = userDomain;

            string? password = null;
            if (logon.Element("password") is { Value.Length: > 0 } stored)
            {
                password = string.Equals((string?)stored.Attribute("storeAsClearText"), "True", StringComparison.OrdinalIgnoreCase)
                    ? stored.Value
                    : Dpapi.TryUnprotectBase64(stored.Value);
                if (password is null)
                    result.Warnings.Add($"{label}: the password was saved by another Windows user or PC and could not be decrypted; please enter it again.");
            }

            return user.Length == 0 && password is null ? null : (user, domain, password);
        }

        /// <summary>The nearest section with own values: the server's, else its groups', else the file's.</summary>
        private static XElement? Section(XElement server, string name) =>
            server.AncestorsAndSelf().Select(n => n.Element(name)).OfType<XElement>().FirstOrDefault(s => !Inherits(s));

        private static bool Inherits(XElement section) =>
            string.Equals((string?)section.Attribute("inherit"), "FromParent", StringComparison.OrdinalIgnoreCase);

        /// <summary>A property of a group or server: under &lt;properties&gt; (schema 3) or directly (schema 1).</summary>
        private static string Text(XElement node, string name) =>
            ((string?)node.Element("properties")?.Element(name) ?? (string?)node.Element(name))?.Trim() ?? "";

        private static string Value(XElement? section, string name) => ((string?)section?.Element(name))?.Trim() ?? "";

        private static bool? Bool(XElement? section, string name) => bool.TryParse(Value(section, name), out bool value) ? value : null;

        private static int? Int(XElement? section, string name) =>
            int.TryParse(Value(section, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;
    }
}
