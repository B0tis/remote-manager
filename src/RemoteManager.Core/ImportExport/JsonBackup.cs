using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.Resources;

namespace RemoteManager.Core.ImportExport;

/// <summary>
/// The app's own backup: every group and connection with all fields and settings, ids kept.
/// Credential references are kept (they point into this PC's Credential Manager), passwords are not:
/// a backup file is safe to copy around. Restoring on the same PC re-attaches the stored passwords.
/// </summary>
public static class JsonBackup
{
    public const string Format = "remote-manager-backup";
    public const int Version = 1;

    internal static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static JsonObject ToJson(ExportData data, DateTime exportedAtUtc) => new()
    {
        ["format"] = Format,
        ["version"] = Version,
        ["exportedAt"] = exportedAtUtc.ToString("O"),
        ["groups"] = new JsonArray([.. data.Groups.Select(g => (JsonNode)new JsonObject
        {
            ["id"] = g.Id,
            ["name"] = g.Name,
            ["parentId"] = g.ParentId,
            ["description"] = g.Description,
            ["sortOrder"] = g.SortOrder,
            ["isCollapsed"] = g.IsCollapsed,
            ["userName"] = g.UserName,
            ["domain"] = g.Domain,
            ["credential"] = Reference(g.Credential),
        })]),
        ["connections"] = new JsonArray([.. data.Connections.Select(c => (JsonNode)new JsonObject
        {
            ["id"] = c.Id,
            ["name"] = c.Name,
            ["type"] = c.Type.ToString(),
            ["host"] = c.Host,
            ["port"] = c.Port,
            ["description"] = c.Description,
            ["notes"] = c.Notes,
            ["tags"] = new JsonArray([.. c.Tags.Select(t => (JsonNode)JsonValue.Create(t))]),
            ["isFavorite"] = c.IsFavorite,
            ["groupId"] = c.GroupId,
            ["sortOrder"] = c.SortOrder,
            ["lastConnectedAt"] = c.LastConnectedAt?.ToString("O"),
            ["userName"] = c.UserName,
            ["domain"] = c.Domain,
            ["credential"] = Reference(c.Credential),
            ["settings"] = JsonNode.Parse(ConnectionSettings.Serialize(c.Settings)),
        })]),
    };

    public static ImportResult FromJson(JsonNode root)
    {
        if ((string?)root["format"] != Format)
            throw new InvalidDataException("Not a Remote-Manager backup.");
        if ((int?)root["version"] is not { } version || version > Version)
            throw new InvalidDataException("The backup was written by a newer Remote-Manager.");

        var result = new ImportResult();
        foreach (JsonNode? g in root["groups"]?.AsArray() ?? [])
        {
            if (g is null || Text(g, "id") is not { Length: > 0 } id)
                continue;
            result.Groups.Add(new ConnectionGroup
            {
                Id = id,
                Name = Text(g, "name"),
                ParentId = (string?)g["parentId"],
                Description = Text(g, "description"),
                SortOrder = (int?)g["sortOrder"] ?? 0,
                IsCollapsed = (bool?)g["isCollapsed"] ?? false,
                UserName = Text(g, "userName"),
                Domain = Text(g, "domain"),
                Credential = Reference(g["credential"]),
            });
        }

        foreach (JsonNode? c in root["connections"]?.AsArray() ?? [])
        {
            if (c is null || Text(c, "id") is not { Length: > 0 } id)
                continue;
            if (!Enum.TryParse(Text(c, "type"), out ConnectionType type))
            {
                result.Warnings.Add(string.Format(CultureInfo.CurrentCulture, ImportStrings.Backup_UnknownType, Text(c, "name"), Text(c, "type")));
                continue;
            }

            result.Connections.Add(new Connection
            {
                Id = id,
                Name = Text(c, "name"),
                Type = type,
                Host = Text(c, "host"),
                Port = (int?)c["port"] ?? 0,
                Description = Text(c, "description"),
                Notes = Text(c, "notes"),
                Tags = [.. c["tags"]?.AsArray().Select(t => (string?)t).OfType<string>() ?? []],
                IsFavorite = (bool?)c["isFavorite"] ?? false,
                GroupId = (string?)c["groupId"],
                SortOrder = (int?)c["sortOrder"] ?? 0,
                LastConnectedAt = DateTime.TryParse((string?)c["lastConnectedAt"], null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime last) ? last : null,
                UserName = Text(c, "userName"),
                Domain = Text(c, "domain"),
                Credential = Reference(c["credential"]),
                Settings = ConnectionSettings.Deserialize(type, c["settings"]?.ToJsonString()),
            });
        }

        return result;
    }

    private static string Text(JsonNode node, string name) => (string?)node[name] ?? "";

    private static JsonObject? Reference(CredentialRef? reference) =>
        reference is null ? null : new JsonObject { ["source"] = reference.Source, ["key"] = reference.Key };

    private static CredentialRef? Reference(JsonNode? node) =>
        node is not null && (string?)node["source"] is { Length: > 0 } source && (string?)node["key"] is { Length: > 0 } key
            ? new CredentialRef(source, key)
            : null;
}

/// <summary>Writes a <see cref="JsonBackup"/> file.</summary>
public sealed class JsonBackupExporter : IExporter
{
    public string Id => "remote-manager-backup";
    public string FormatName => "Remote-Manager JSON";
    public string FileExtension => ".json";

    public async Task ExportAsync(ExportData data, Stream output, CancellationToken cancellationToken = default)
    {
        await using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        JsonBackup.ToJson(data, DateTime.UtcNow).WriteTo(writer, JsonBackup.Options);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Restores a <see cref="JsonBackup"/> file (ids kept: existing connections are replaced).</summary>
public sealed class JsonBackupImporter : IImporter
{
    public string Id => "remote-manager-backup";
    public string FormatName => "Remote-Manager JSON";
    public IReadOnlyList<string> FileExtensions { get; } = [".json"];

    public bool CanImport(string path)
    {
        if (!File.Exists(path) || !Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            return false;
        using var reader = new StreamReader(path);
        char[] buffer = new char[512];
        string head = new(buffer, 0, reader.Read(buffer, 0, buffer.Length));
        return head.Contains($"\"{JsonBackup.Format}\"", StringComparison.Ordinal);
    }

    public async Task<ImportResult> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        await using FileStream stream = File.OpenRead(path);
        JsonNode root = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Empty backup file.");
        return JsonBackup.FromJson(root);
    }
}
