using System.Globalization;
using System.Text.Json;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.Resources;

namespace RemoteManager.Core.ImportExport;

/// <summary>
/// The backup of Microsoft's "Remotedesktop" Store app (Settings, "Sicherung": RemoteDesktopWorkspace.rdb): one
/// JSON document with connections, the user accounts they point at, groups, and gateways. The app's support
/// ended in 2025, so this is how its list moves on. Passwords are not in the file (they stay in the app's own
/// vault); the connections come in with their user and ask for the password at the first logon.
/// </summary>
public sealed class RemoteDesktopAppImporter : IImporter
{
    public string Id => "msrd-app";
    public string FormatName => "Microsoft Remotedesktop-App (.rdb)";
    public IReadOnlyList<string> FileExtensions { get; } = [".rdb"];

    public bool CanImport(string path) =>
        File.Exists(path) && Path.GetExtension(path).Equals(".rdb", StringComparison.OrdinalIgnoreCase);

    public async Task<ImportResult> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = new ImportResult();
        JsonDocument document;
        try
        {
            await using FileStream stream = File.OpenRead(path);
            document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { AllowTrailingCommas = true }, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            result.Warnings.Add(string.Format(CultureInfo.CurrentCulture, ImportStrings.RemoteDesktopApp_NotABackup, Path.GetFileName(path)));
            return result;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Connections", out JsonElement connections)
                || connections.ValueKind != JsonValueKind.Array)
            {
                result.Warnings.Add(string.Format(CultureInfo.CurrentCulture, ImportStrings.RemoteDesktopApp_NotABackup, Path.GetFileName(path)));
                return result;
            }

            // Everything refers to everything else by "PersistentModelId".
            Dictionary<string, (string User, string Domain)> users = [];
            foreach (JsonElement credential in Items(root, "Credentials"))
            {
                if (Text(credential, "PersistentModelId") is { Length: > 0 } id)
                    users[id] = SplitUser(Text(credential, "Username") ?? "");
            }

            Dictionary<string, string> gateways = [];
            foreach (JsonElement gateway in Items(root, "Gateways"))
            {
                if (Text(gateway, "PersistentModelId") is { Length: > 0 } id && Text(gateway, "HostName") is { Length: > 0 } host)
                    gateways[id] = host;
            }

            Dictionary<string, ConnectionGroup> groups = [];
            foreach (JsonElement group in Items(root, "Groups"))
            {
                string name = Text(group, "FriendlyName") ?? Text(group, "Name") ?? "";
                if (Text(group, "PersistentModelId") is not { Length: > 0 } id || name.Trim().Length == 0)
                    continue;
                var imported = new ConnectionGroup { Name = name.Trim(), SortOrder = result.Groups.Count };
                groups[id] = imported;
                result.Groups.Add(imported);
            }

            int withoutPassword = 0;
            foreach (JsonElement entry in connections.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                (string host, int port) = SplitAddress(Text(entry, "HostName") ?? "");
                if (host.Length == 0)
                {
                    result.Warnings.Add(string.Format(CultureInfo.CurrentCulture, ImportStrings.Import_NoHost,
                        Text(entry, "FriendlyName") is { Length: > 0 } label ? label : "?"));
                    continue;
                }

                string name = Text(entry, "FriendlyName")?.Trim() is { Length: > 0 } friendly ? friendly : host;
                var settings = new RdpSettings();
                if (entry.TryGetProperty("LocalResourcesSettings", out JsonElement local) && local.ValueKind == JsonValueKind.Object
                    && local.TryGetProperty("RedirectClipboard", out JsonElement clipboard)
                    && clipboard.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    settings = settings with { Redirections = settings.Redirections with { Clipboard = clipboard.GetBoolean() } };
                }

                if (Text(entry, "GatewayId") is { Length: > 0 } gatewayId && gateways.TryGetValue(gatewayId, out string? gatewayHost))
                    settings = settings with { GatewayHost = gatewayHost, GatewayUsage = RdpGatewayUsage.Detect };

                var connection = new Connection { Name = name, Type = ConnectionType.Rdp, Host = host, Port = port, Settings = settings };
                if (Text(entry, "CredentialsId") is { Length: > 0 } credentialId && users.TryGetValue(credentialId, out var user))
                {
                    connection.UserName = user.User;
                    connection.Domain = user.Domain;
                    withoutPassword++;
                }

                if (Text(entry, "GroupId") is { Length: > 0 } groupId && groups.TryGetValue(groupId, out ConnectionGroup? owner))
                {
                    connection.GroupId = owner.Id;
                    connection.SortOrder = result.Connections.Count(c => c.GroupId == owner.Id);
                }
                else
                {
                    connection.SortOrder = result.Connections.Count(c => c.GroupId is null);
                }

                result.Connections.Add(connection);
            }

            if (withoutPassword > 0)
                result.Warnings.Add(string.Format(CultureInfo.CurrentCulture, ImportStrings.RemoteDesktopApp_NoPasswords, withoutPassword));
        }

        return result;
    }

    /// <summary>"host", "host:3390", "[2001:db8::1]:3390"; a bare IPv6 address has no port.</summary>
    internal static (string Host, int Port) SplitAddress(string address)
    {
        address = address.Trim();
        if (address.StartsWith('['))
        {
            int close = address.IndexOf(']', StringComparison.Ordinal);
            if (close > 0)
            {
                string inner = address[1..close];
                return close + 2 < address.Length && address[close + 1] == ':' && TryPort(address[(close + 2)..], out int bracketed)
                    ? (inner, bracketed)
                    : (inner, 0);
            }
        }

        int colon = address.LastIndexOf(':');
        if (colon > 0 && address.IndexOf(':', StringComparison.Ordinal) == colon && TryPort(address[(colon + 1)..], out int port))
            return (address[..colon], port);
        return (address, 0);
    }

    /// <summary>The app writes "DOMAIN\user", "DOMAIN/user", or "user@domain" (kept whole: it is a logon name of its own).</summary>
    internal static (string User, string Domain) SplitUser(string value)
    {
        value = value.Trim();
        int separator = value.IndexOfAny(['\\', '/']);
        return separator > 0 && separator < value.Length - 1 ? (value[(separator + 1)..], value[..separator]) : (value, "");
    }

    private static bool TryPort(string text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and < 65536;

    private static IEnumerable<JsonElement> Items(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)
            : [];

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
