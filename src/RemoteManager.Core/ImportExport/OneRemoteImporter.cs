using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.Rdp;

namespace RemoteManager.Core.ImportExport;

/// <summary>
/// 1Remote (formerly PRemoteM): its database (1Remote.db, table Server with one JSON document per
/// connection) or a JSON export (.prma / .json array). Name, address, port, user, tags, and notes are
/// taken over unchanged; RDP options as far as they have an equivalent. 1Remote encrypts passwords
/// with its own key, so they are not taken over (reported in the summary). 1Remote has no folders:
/// its tags stay tags.
/// </summary>
public sealed class OneRemoteImporter : IImporter
{
    private static readonly byte[] SqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");

    public string Id => "1remote";
    public string FormatName => "1Remote / PRemoteM (.db, .prma)";
    public IReadOnlyList<string> FileExtensions { get; } = [".db", ".prma", ".json"];

    public bool CanImport(string path)
    {
        if (!File.Exists(path))
            return false;
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".db")
            return IsSqlite(path);
        if (extension is not (".prma" or ".json"))
            return false;
        string head = ReadHead(path);
        return head.TrimStart().StartsWith('[') && head.Contains("\"DisplayName\"", StringComparison.Ordinal);
    }

    public async Task<ImportResult> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = new ImportResult();
        IEnumerable<string> documents = IsSqlite(path) ? ReadDatabase(path) : await ReadExportAsync(path, cancellationToken).ConfigureAwait(false);
        int passwords = 0;
        int order = 0;
        foreach (string json in documents)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (Read(document.RootElement, order, result) is { } connection)
            {
                result.Connections.Add(connection);
                order++;
                if (Str(document.RootElement, "Password").Length > 0)
                    passwords++;
            }
        }

        if (passwords > 0)
            result.Warnings.Add($"{passwords} passwords are encrypted with 1Remote's own key and were not taken over; enter them once in the editor or at the first connect.");
        return result;
    }

    private static Connection? Read(JsonElement e, int order, ImportResult result)
    {
        string name = Str(e, "DisplayName"), protocol = Str(e, "Protocol");
        ConnectionType? type = protocol.ToUpperInvariant() switch
        {
            "RDP" => ConnectionType.Rdp,
            "SSH" => ConnectionType.Ssh,
            "SFTP" => ConnectionType.Sftp,
            "VNC" => ConnectionType.Vnc,
            _ => null,
        };
        if (type is not { } t)
        {
            result.Warnings.Add($"{name}: protocol {protocol} is not supported, skipped.");
            return null;
        }

        string host = Str(e, "Address");
        if (host.Length == 0)
        {
            result.Warnings.Add($"{name}: no address, skipped.");
            return null;
        }

        (string user, string domain) = RdpFileMapping.SplitUser(Str(e, "UserName"));
        if (Str(e, "Domain") is { Length: > 0 } ownDomain)
            domain = ownDomain;

        var connection = new Connection
        {
            Name = name.Length > 0 ? name : host,
            Type = t,
            Host = host,
            // 1Remote keeps the port as text.
            Port = int.TryParse(Str(e, "Port"), CultureInfo.InvariantCulture, out int port) && port is > 0 and < 65536 && port != DefaultPorts.For(t) ? port : 0,
            UserName = user,
            Domain = domain,
            Notes = Str(e, "Note"),
            SortOrder = order,
            Tags = e.TryGetProperty("Tags", out JsonElement tags) && tags.ValueKind == JsonValueKind.Array
                ? [.. tags.EnumerateArray().Select(tag => tag.ToString().Trim()).Where(tag => tag.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)]
                : [],
        };

        if (t == ConnectionType.Rdp)
            connection.Settings = ReadRdpSettings(e);
        else if (t is ConnectionType.Ssh or ConnectionType.Sftp && Str(e, "PrivateKey") is { Length: > 0 } key)
            connection.Settings = new SshSettings { AuthMethod = SshAuthMethod.KeyFile, KeyPath = key };
        return connection;
    }

    private static RdpSettings ReadRdpSettings(JsonElement e)
    {
        // ERdpWindowResizeMode: AutoResize 0, FixedFullScreen 1, Stretch 2, StretchFullScreen 3, Fixed 4.
        string resize = Str(e, "RdpWindowResizeMode");
        int width = Int(e, "RdpWidth"), height = Int(e, "RdpHeight");
        bool fixedSize = resize is "Fixed" or "4" or "Stretch" or "2";
        string gateway = Str(e, "GatewayHostName");
        return new RdpSettings
        {
            Display = fixedSize && width > 0 && height > 0 ? RdpDisplayMode.FixedResolution
                : resize is "Stretch" or "2" or "StretchFullScreen" or "3" ? RdpDisplayMode.SmartSizing
                : RdpDisplayMode.FitToWindow,
            DesktopWidth = fixedSize ? width : 0,
            DesktopHeight = fixedSize ? height : 0,
            UseMultimon = Str(e, "RdpFullScreenFlag") is "EnableFullAllScreens" or "2",
            Keyboard = Bool(e, "EnableKeyCombinations") == false ? RdpKeyboardMode.Local : RdpKeyboardMode.Session,
            AdminSession = Bool(e, "IsAdministrativePort") == true,
            // EGatewayMode: AutomaticallyDetect 0, UseTheFollowing 1, DoNotUse 2.
            GatewayHost = gateway,
            GatewayUsage = gateway.Length == 0 ? RdpGatewayUsage.None : Str(e, "GatewayMode") switch
            {
                "UseTheFollowingGatewayServerSettings" or "1" => RdpGatewayUsage.Always,
                "AutomaticallyDetectGatewayServerSettings" or "0" => RdpGatewayUsage.Detect,
                _ => RdpGatewayUsage.None,
            },
            GatewayUsesConnectionCredentials = Str(e, "GatewayUserName").Length == 0,
            GatewayUserName = Str(e, "GatewayUserName"),
            Redirections = new RdpRedirections
            {
                Clipboard = Bool(e, "EnableClipboard") ?? true,
                Drives = Bool(e, "EnableDiskDrives") ?? false,
                Printers = Bool(e, "EnablePrinters") ?? false,
                Ports = Bool(e, "EnablePorts") ?? false,
                SmartCards = Bool(e, "EnableSmartCardsAndWinHello") ?? false,
                Microphone = Bool(e, "EnableAudioCapture") ?? false,
                // EAudioRedirectionMode: RedirectToLocal 0, LeaveOnRemote 1, Disabled 2.
                Audio = Str(e, "AudioRedirectionMode") switch
                {
                    "LeaveOnRemote" or "1" => RdpAudioMode.Remote,
                    "Disabled" or "2" => RdpAudioMode.None,
                    _ => RdpAudioMode.Local,
                },
            },
        };
    }

    private static List<string> ReadDatabase(string path)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        // 1Remote: Server(Id, Protocol, ClassVersion, Json); PRemoteM: Server(..., JsonConfigString).
        string? column = null;
        using (SqliteCommand info = connection.CreateCommand())
        {
            info.CommandText = "SELECT name FROM pragma_table_info('Server')";
            using SqliteDataReader reader = info.ExecuteReader();
            while (reader.Read())
            {
                string name = reader.GetString(0);
                if (name.Equals("Json", StringComparison.OrdinalIgnoreCase) || name.Equals("JsonConfigString", StringComparison.OrdinalIgnoreCase))
                    column = name;
            }
        }

        if (column is null)
            throw new InvalidDataException("Not a 1Remote database: table Server with a Json column is missing.");

        var documents = new List<string>();
        using SqliteCommand select = connection.CreateCommand();
        select.CommandText = $"SELECT \"{column}\" FROM Server ORDER BY rowid";
        using SqliteDataReader rows = select.ExecuteReader();
        while (rows.Read())
        {
            if (!rows.IsDBNull(0))
                documents.Add(rows.GetString(0));
        }

        return documents;
    }

    private static async Task<IEnumerable<string>> ReadExportAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("A 1Remote export is a JSON array.");
        return [.. document.RootElement.EnumerateArray().Select(e => e.GetRawText())];
    }

    private static bool IsSqlite(string path)
    {
        byte[] head = new byte[SqliteHeader.Length];
        using FileStream stream = File.OpenRead(path);
        return stream.Read(head, 0, head.Length) == head.Length && head.AsSpan().SequenceEqual(SqliteHeader);
    }

    private static string ReadHead(string path)
    {
        using var reader = new StreamReader(path);
        char[] buffer = new char[4096];
        return new string(buffer, 0, reader.Read(buffer, 0, buffer.Length));
    }

    /// <summary>A property as trimmed text; numbers and booleans as their JSON text, missing or null as "".</summary>
    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()!.Trim(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => "",
        } : "";

    private static int Int(JsonElement e, string name) => int.TryParse(Str(e, name), CultureInfo.InvariantCulture, out int value) ? value : 0;

    private static bool? Bool(JsonElement e, string name) => Str(e, name).ToLowerInvariant() switch
    {
        "true" or "1" => true,
        "false" or "0" => false,
        _ => null,
    };
}
