using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.Rdp;
using RemoteManager.Core.Resources;

namespace RemoteManager.Core.ImportExport;

/// <summary>
/// 1Remote (formerly PRemoteM): its database (1Remote.db) or a JSON export (.prma / .json array).
/// 1Remote 1.x keeps one JSON document per connection in table Servers (column Json, next to Configs and
/// Credentials); PRemoteM used table Server with column JsonConfigString. Name, address, port, user, tags,
/// and notes are taken over unchanged; RDP options as far as they have an equivalent. 1Remote encrypts
/// passwords (and stored private keys) with its own key, so they are not taken over (reported in the
/// summary). 1Remote has no folders: its tags stay tags.
/// </summary>
/// <remarks>
/// 1Remote keeps its database open while it runs. The file is therefore opened with every share mode and
/// read from a copy in a temporary folder, never in place. A row that cannot be read is skipped with a warning.
/// </remarks>
public sealed class OneRemoteImporter : IImporter
{
    private static readonly byte[] SqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");

    /// <summary>Known layouts, newest first: (table, column with the JSON document).</summary>
    private static readonly (string Table, string Column)[] Layouts = [("Servers", "Json"), ("Server", "JsonConfigString"), ("Server", "Json")];

    public string Id => "1remote";
    public string FormatName => "1Remote / PRemoteM (.db, .prma)";
    public IReadOnlyList<string> FileExtensions { get; } = [".db", ".prma", ".json"];

    /// <summary>Never throws: a file that exists but cannot be read now is accepted by extension, so the import reports why.</summary>
    public bool CanImport(string path)
    {
        string extension;
        try
        {
            if (!File.Exists(path))
                return false;
            extension = Path.GetExtension(path).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }

        try
        {
            if (extension == ".db")
                return IsSqlite(path);
            if (extension is not (".prma" or ".json"))
                return false;
            string head = ReadHead(path);
            return head.TrimStart().StartsWith('[') && head.Contains("\"DisplayName\"", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return extension is ".db" or ".prma" or ".json";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or DecoderFallbackException)
        {
            return false;
        }
    }

    public async Task<ImportResult> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = new ImportResult();
        bool sqlite;
        try
        {
            sqlite = IsSqlite(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(ImportStrings.OneRemote_FileLocked, ex);
        }

        IReadOnlyList<string?> documents = sqlite
            ? await ReadDatabaseAsync(path, cancellationToken).ConfigureAwait(false)
            : await ReadExportAsync(path, cancellationToken).ConfigureAwait(false);
        int passwords = 0;
        int order = 0;
        for (int i = 0; i < documents.Count; i++)
        {
            if (ParseRow(documents[i], i + 1, result) is not { } document)
                continue;
            using (document)
            {
                if (Read(document.RootElement, order, result) is { } connection)
                {
                    result.Connections.Add(connection);
                    order++;
                    if (Str(document.RootElement, "Password").Length > 0)
                        passwords++;
                }
            }
        }

        if (passwords > 0)
            result.Warnings.Add(string.Format(CultureInfo.CurrentCulture, ImportStrings.OneRemote_EncryptedPasswords, passwords));
        return result;
    }

    /// <summary>One entry as a JSON object, or null (with a warning) when it is empty, not JSON, or not an object.</summary>
    private static JsonDocument? ParseRow(string? json, int number, ImportResult result)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                JsonDocument document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                    return document;
                document.Dispose();
            }
            catch (JsonException)
            {
            }
        }

        result.Warnings.Add(string.Format(CultureInfo.CurrentCulture, ImportStrings.OneRemote_UnreadableEntry, number));
        return null;
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
            result.Warnings.Add(string.Format(CultureInfo.CurrentCulture, ImportStrings.Import_UnsupportedProtocol, name, protocol));
            return null;
        }

        string host = Str(e, "Address");
        if (host.Length == 0)
        {
            result.Warnings.Add(string.Format(CultureInfo.CurrentCulture, ImportStrings.Import_NoAddress, name));
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
        {
            // 1Remote stores a key file's path, but encrypts the field in its database like a password.
            if (LooksLikeKeyFilePath(key))
                connection.Settings = new SshSettings { AuthMethod = SshAuthMethod.KeyFile, KeyPath = key };
            else
                result.Warnings.Add(string.Format(CultureInfo.CurrentCulture, ImportStrings.OneRemote_PrivateKeyNotAPath, connection.Name));
        }

        return connection;
    }

    /// <summary>
    /// Whether a PrivateKey value is a Windows file path (C:\..., \\server\share\..., %VAR%\..., ~\...) rather than
    /// an encrypted value or the key itself. Base64 may start with "/", so a bare "/" does not count.
    /// </summary>
    internal static bool LooksLikeKeyFilePath(string value)
    {
        if (value.Length is < 4 or > 1024 || value.Any(c => char.IsControl(c) || c is '<' or '>' or '"' or '|' or '?' or '*'))
            return false;
        bool drive = char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '\\' or '/';
        bool unc = value.StartsWith(@"\\", StringComparison.Ordinal) && value.Length > 2 && value[2] is not ('\\' or '/');
        bool variable = value[0] == '%' && value.IndexOf('%', 1) is > 1 and var close && close + 1 < value.Length && value[close + 1] is '\\' or '/';
        bool home = value[0] == '~' && value[1] is '\\' or '/';
        return drive || unc || variable || home;
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

    /// <summary>
    /// The JSON documents of a 1Remote or PRemoteM database, read from a copy (with its -wal file) so a running
    /// 1Remote neither blocks nor sees the import. The copy is deleted afterwards.
    /// </summary>
    private static async Task<IReadOnlyList<string?>> ReadDatabaseAsync(string path, CancellationToken cancellationToken)
    {
        string folder = Path.Combine(Path.GetTempPath(), "rm-1remote-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string copy = Path.Combine(folder, "1Remote.db");
            try
            {
                await CopySharedAsync(path, copy, cancellationToken).ConfigureAwait(false);
                // The -wal file holds committed changes not yet in the main file. The -shm index is not copied:
                // SQLite rebuilds it from the log, while a copied index could disagree with the copied log.
                if (File.Exists(path + "-wal"))
                    await CopySharedAsync(path + "-wal", copy + "-wal", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException(ImportStrings.OneRemote_FileLocked, ex);
            }

            try
            {
                return ReadDocuments(copy);
            }
            catch (SqliteException ex)
            {
                throw new InvalidDataException(string.Format(CultureInfo.CurrentCulture, ImportStrings.OneRemote_DatabaseUnreadable, ex.Message), ex);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover copy in %TEMP% is harmless; it holds no secret in clear text.
            }
        }
    }

    private static List<string?> ReadDocuments(string path)
    {
        // Read-write on the private copy: SQLite may have to fold a copied -wal file in.
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (SqliteCommand list = connection.CreateCommand())
        {
            list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
            using SqliteDataReader reader = list.ExecuteReader();
            while (reader.Read())
                tables.Add(reader.GetString(0));
        }

        foreach ((string table, string column) in Layouts)
        {
            if (!tables.Contains(table) || !HasColumn(connection, table, column))
                continue;

            var documents = new List<string?>();
            using SqliteCommand select = connection.CreateCommand();
            // Table and column come from the fixed list above, never from the file.
            select.CommandText = $"SELECT \"{column}\" FROM \"{table}\" ORDER BY rowid";
            using SqliteDataReader rows = select.ExecuteReader();
            while (rows.Read())
                documents.Add(rows.IsDBNull(0) ? null : rows.GetValue(0) as string);
            return documents;
        }

        throw new InvalidDataException(ImportStrings.OneRemote_NotADatabase);
    }

    private static bool HasColumn(SqliteConnection connection, string table, string column)
    {
        using SqliteCommand info = connection.CreateCommand();
        info.CommandText = "SELECT 1 FROM pragma_table_info($table) WHERE name = $column COLLATE NOCASE";
        info.Parameters.AddWithValue("$table", table);
        info.Parameters.AddWithValue("$column", column);
        return info.ExecuteScalar() is not null;
    }

    /// <summary>Copies a file another process holds open (1Remote, SQLite), retrying briefly while it is locked for a write.</summary>
    private static async Task CopySharedAsync(string source, string target, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using FileStream input = OpenShared(source);
                await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (IOException) when (attempt < 3)
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Opens for reading without blocking (or being blocked by) a writer that also shares the file.</summary>
    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 81920, useAsync: true);

    private static async Task<IReadOnlyList<string?>> ReadExportAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = OpenShared(path);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException(ImportStrings.OneRemote_ExportNotAnArray);
        return [.. document.RootElement.EnumerateArray().Select(e => e.GetRawText())];
    }

    private static bool IsSqlite(string path)
    {
        byte[] head = new byte[SqliteHeader.Length];
        using FileStream stream = OpenShared(path);
        return stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length && head.AsSpan().SequenceEqual(SqliteHeader);
    }

    private static string ReadHead(string path)
    {
        using var reader = new StreamReader(OpenShared(path));
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
