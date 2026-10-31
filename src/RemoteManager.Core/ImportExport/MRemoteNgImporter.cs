using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.Rdp;
using RemoteManager.Core.Resources;

namespace RemoteManager.Core.ImportExport;

/// <summary>
/// mRemoteNG confCons.xml (also exported XML). Containers become nested groups, RDP, SSH and VNC
/// nodes become connections; other protocols are reported. "Inherit user name" maps onto group
/// inheritance. Passwords are taken over when the file uses mRemoteNG's default master password;
/// with a custom one they are reported (and a fully encrypted file cannot be read).
/// </summary>
public sealed partial class MRemoteNgImporter : IImporter
{
    public string Id => "mremoteng";
    public string FormatName => "mRemoteNG (confCons.xml)";
    public IReadOnlyList<string> FileExtensions { get; } = [".xml"];

    public bool CanImport(string path)
    {
        if (!File.Exists(path) || !Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase))
            return false;
        using var reader = new StreamReader(path);
        char[] head = new char[1024];
        int read = reader.Read(head, 0, head.Length);
        return new string(head, 0, read).Contains("Connections", StringComparison.Ordinal);
    }

    public async Task<ImportResult> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        XDocument document;
        await using (FileStream stream = File.OpenRead(path))
            document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);

        XElement root = document.Root is { Name.LocalName: "Connections" } r ? r : throw new InvalidDataException("Not an mRemoteNG connection file.");
        var result = new ImportResult();

        bool gcm = root.Attribute("EncryptionEngine") is not null;
        int iterations = int.TryParse((string?)root.Attribute("KdfIterations"), CultureInfo.InvariantCulture, out int kdf) ? kdf : 1000;
        string? protectedValue = (string?)root.Attribute("Protected");
        // "Protected" holds "ThisIsNotProtected" (or "ThisIsProtected") encrypted with the master password.
        bool defaultPassword = protectedValue is null
            || MRemoteNgCrypto.Decrypt(protectedValue, MRemoteNgCrypto.DefaultPassword, gcm, iterations) is "ThisIsNotProtected";
        var context = new Context(result, defaultPassword, gcm, iterations);

        if (string.Equals((string?)root.Attribute("FullFileEncryption"), "true", StringComparison.OrdinalIgnoreCase))
        {
            string? inner = defaultPassword ? MRemoteNgCrypto.Decrypt(root.Value.Trim(), MRemoteNgCrypto.DefaultPassword, gcm, iterations) : null;
            if (inner is null)
                throw new InvalidDataException("The file is fully encrypted with a custom mRemoteNG master password. Export it from mRemoteNG without encryption and import that file.");
            root = XElement.Parse($"<Connections>{inner}</Connections>");
        }

        ReadChildren(root, parentId: null, context);
        if (!defaultPassword && context.SkippedPasswords > 0)
            result.Warnings.Add(string.Format(CultureInfo.CurrentCulture, ImportStrings.MRemoteNg_CustomMasterPassword, context.SkippedPasswords));
        return result;
    }

    private static void ReadChildren(XElement parent, string? parentId, Context context)
    {
        int order = 0;
        foreach (XElement node in parent.Elements().Where(e => e.Name.LocalName == "Node"))
        {
            string name = Attr(node, "Name");
            if (string.Equals(Attr(node, "Type"), "Container", StringComparison.OrdinalIgnoreCase))
            {
                var group = new ConnectionGroup
                {
                    Name = name.Length > 0 ? name : "Ordner",
                    ParentId = parentId,
                    Description = Attr(node, "Descr"),
                    SortOrder = order++,
                    IsCollapsed = string.Equals(Attr(node, "Expanded"), "false", StringComparison.OrdinalIgnoreCase),
                };
                if (!Bool(node, "InheritUsername") && Attr(node, "Username") is { Length: > 0 } user)
                {
                    (group.UserName, group.Domain) = UserAndDomain(user, Attr(node, "Domain"));
                    if (context.Password(node, group.Name) is { } secret)
                        context.Result.GroupSecrets[group.Id] = secret;
                }

                context.Result.Groups.Add(group);
                ReadChildren(node, group.Id, context);
            }
            else
            {
                ReadConnection(node, parentId, order++, context);
            }
        }
    }

    private static void ReadConnection(XElement node, string? groupId, int order, Context context)
    {
        string name = Attr(node, "Name"), protocol = Attr(node, "Protocol");
        ConnectionType? type = protocol.ToUpperInvariant() switch
        {
            "RDP" => ConnectionType.Rdp,
            "SSH1" or "SSH2" => ConnectionType.Ssh,
            "VNC" => ConnectionType.Vnc,
            _ => null,
        };
        if (type is not { } t)
        {
            context.Result.Warnings.Add(string.Format(CultureInfo.CurrentCulture, ImportStrings.Import_UnsupportedProtocol, name, protocol));
            return;
        }

        string host = Attr(node, "Hostname");
        if (host.Length == 0)
        {
            context.Result.Warnings.Add(string.Format(CultureInfo.CurrentCulture, ImportStrings.Import_NoHost, name));
            return;
        }

        var connection = new Connection
        {
            Name = name.Length > 0 ? name : host,
            Type = t,
            Host = host,
            Description = Attr(node, "Descr"),
            GroupId = groupId,
            SortOrder = order,
        };
        if (int.TryParse(Attr(node, "Port"), CultureInfo.InvariantCulture, out int port) && port is > 0 and < 65536 && port != DefaultPorts.For(t))
            connection.Port = port;

        // Inheriting the user name means: take the logon of the folder.
        if (!Bool(node, "InheritUsername"))
        {
            (connection.UserName, connection.Domain) = UserAndDomain(Attr(node, "Username"), Bool(node, "InheritDomain") ? "" : Attr(node, "Domain"));
            if (!Bool(node, "InheritPassword") && context.Password(node, connection.Name) is { } secret)
                context.Result.Secrets[connection.Id] = secret;
        }

        if (t == ConnectionType.Rdp)
            connection.Settings = ReadRdpSettings(node);
        context.Result.Connections.Add(connection);
    }

    private static RdpSettings ReadRdpSettings(XElement node)
    {
        string resolution = Attr(node, "Resolution");
        Match size = ResolutionPattern().Match(resolution);
        string gateway = Attr(node, "RDGatewayHostname");
        return new RdpSettings
        {
            Display = size.Success ? RdpDisplayMode.FixedResolution
                : resolution.Equals("SmartSize", StringComparison.OrdinalIgnoreCase) ? RdpDisplayMode.SmartSizing
                : RdpDisplayMode.FitToWindow,
            DesktopWidth = size.Success ? int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture) : 0,
            DesktopHeight = size.Success ? int.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture) : 0,
            Keyboard = Attr(node, "RedirectKeys").Length > 0 && !Bool(node, "RedirectKeys") ? RdpKeyboardMode.Local : RdpKeyboardMode.Session,
            EnableNla = Attr(node, "UseCredSsp").Length == 0 || Bool(node, "UseCredSsp"),
            AuthenticationLevel = Attr(node, "RDPAuthenticationLevel") switch
            {
                "NoAuth" => 0,
                "AuthRequired" => 1,
                _ => 2,
            },
            AdminSession = Bool(node, "ConnectToConsole"),
            RestrictedAdmin = Bool(node, "UseRestrictedAdmin"),
            RemoteCredentialGuard = Bool(node, "UseRCG"),
            GatewayHost = gateway,
            GatewayUsage = gateway.Length == 0 ? RdpGatewayUsage.None : Attr(node, "RDGatewayUsageMethod") switch
            {
                "Always" => RdpGatewayUsage.Always,
                "Detect" => RdpGatewayUsage.Detect,
                _ => RdpGatewayUsage.None,
            },
            GatewayUsesConnectionCredentials = Attr(node, "RDGatewayUseConnectionCredentials") is "" or "Yes",
            GatewayUserName = RdpFileMapping.FormatUser(Attr(node, "RDGatewayUsername"), Attr(node, "RDGatewayDomain")),
            Redirections = new RdpRedirections
            {
                Clipboard = Attr(node, "RedirectClipboard").Length == 0 || Bool(node, "RedirectClipboard"),
                Drives = Bool(node, "RedirectDiskDrives"),
                Printers = Bool(node, "RedirectPrinters"),
                Ports = Bool(node, "RedirectPorts"),
                SmartCards = Bool(node, "RedirectSmartCards"),
                Microphone = Bool(node, "RedirectAudioCapture"),
                Audio = Attr(node, "RedirectSound") switch
                {
                    "LeaveAtRemoteComputer" => RdpAudioMode.Remote,
                    "DoNotPlay" => RdpAudioMode.None,
                    _ => RdpAudioMode.Local,
                },
            },
            StartProgram = Attr(node, "RDPStartProgram"),
            WorkingDirectory = Attr(node, "RDPStartProgramWorkDir"),
        };
    }

    private static (string User, string Domain) UserAndDomain(string user, string domain)
    {
        (string name, string fromUser) = RdpFileMapping.SplitUser(user);
        return (name, domain.Length > 0 ? domain : fromUser);
    }

    private static string Attr(XElement node, string name) => ((string?)node.Attribute(name))?.Trim() ?? "";

    private static bool Bool(XElement node, string name) => bool.TryParse(Attr(node, name), out bool value) && value;

    [GeneratedRegex(@"^Res(\d+)x(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionPattern();

    private sealed class Context(ImportResult result, bool defaultPassword, bool gcm, int iterations)
    {
        public ImportResult Result => result;
        public int SkippedPasswords { get; private set; }

        public string? Password(XElement node, string label)
        {
            string encrypted = Attr(node, "Password");
            if (encrypted.Length == 0)
                return null;
            if (!defaultPassword)
            {
                SkippedPasswords++;
                return null;
            }

            string? plain = MRemoteNgCrypto.Decrypt(encrypted, MRemoteNgCrypto.DefaultPassword, gcm, iterations);
            if (plain is null)
                result.Warnings.Add(string.Format(CultureInfo.CurrentCulture, ImportStrings.MRemoteNg_PasswordNotDecrypted, label));
            return plain is { Length: > 0 } ? plain : null;
        }
    }
}
