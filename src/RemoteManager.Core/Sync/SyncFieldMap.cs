using System.Text.Json;
using System.Text.Json.Nodes;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;

namespace RemoteManager.Core.Sync;

/// <summary>
/// Which fields of groups and connections sync, and how they map to <see cref="SyncFields"/>. Each type
/// option is its own field ("settings.display"), so edits to different options merge. Not synced: last
/// connected time (history), preview, collapsed state, status; secrets are never part of the model.
/// </summary>
public static class SyncFieldMap
{
    public const string SettingsPrefix = "settings.";

    public static SyncFields FromConnection(Connection c)
    {
        var fields = new SyncFields
        {
            ["name"] = Json(c.Name),
            ["type"] = Json(c.Type.ToString()),
            ["host"] = Json(c.Host),
            ["port"] = Json(c.Port),
            ["description"] = Json(c.Description),
            ["notes"] = Json(c.Notes),
            ["tags"] = Json(c.Tags),
            ["favorite"] = Json(c.IsFavorite),
            ["group"] = Json(c.GroupId),
            ["sortOrder"] = Json(c.SortOrder),
            ["userName"] = Json(c.UserName),
            ["domain"] = Json(c.Domain),
            ["credential"] = Json(c.Credential),
        };
        JsonObject settings = JsonNode.Parse(ConnectionSettings.Serialize(c.Settings))!.AsObject();
        foreach ((string name, JsonNode? value) in settings)
            fields[SettingsPrefix + name] = value?.ToJsonString() ?? "null";
        return fields;
    }

    public static SyncFields FromGroup(ConnectionGroup g) => new()
    {
        ["name"] = Json(g.Name),
        ["parent"] = Json(g.ParentId),
        ["description"] = Json(g.Description),
        ["sortOrder"] = Json(g.SortOrder),
        ["userName"] = Json(g.UserName),
        ["domain"] = Json(g.Domain),
        ["credential"] = Json(g.Credential),
    };

    /// <summary>Writes the fields onto a connection; missing fields keep their current value.</summary>
    public static void ApplyTo(Connection c, SyncFields fields)
    {
        // A type this build does not know (written by a newer one) keeps the connection's current type.
        if (Read<string>(fields, "type") is { } typeName && Enum.TryParse(typeName, out ConnectionType type))
            c.Type = type;
        c.Name = Read(fields, "name", c.Name);
        c.Host = Read(fields, "host", c.Host);
        c.Port = Read(fields, "port", c.Port);
        c.Description = Read(fields, "description", c.Description);
        c.Notes = Read(fields, "notes", c.Notes);
        c.Tags = Read(fields, "tags", c.Tags);
        c.IsFavorite = Read(fields, "favorite", c.IsFavorite);
        c.GroupId = Read(fields, "group", c.GroupId);
        c.SortOrder = Read(fields, "sortOrder", c.SortOrder);
        c.UserName = Read(fields, "userName", c.UserName);
        c.Domain = Read(fields, "domain", c.Domain);
        c.Credential = Read(fields, "credential", c.Credential);

        var settings = new JsonObject();
        foreach ((string name, string value) in fields.Where(f => f.Key.StartsWith(SettingsPrefix, StringComparison.Ordinal)))
            settings[name[SettingsPrefix.Length..]] = JsonNode.Parse(value);
        c.Settings = ConnectionSettings.Deserialize(c.Type, settings.ToJsonString());
    }

    public static void ApplyTo(ConnectionGroup g, SyncFields fields)
    {
        g.Name = Read(fields, "name", g.Name);
        g.ParentId = Read(fields, "parent", g.ParentId);
        g.Description = Read(fields, "description", g.Description);
        g.SortOrder = Read(fields, "sortOrder", g.SortOrder);
        g.UserName = Read(fields, "userName", g.UserName);
        g.Domain = Read(fields, "domain", g.Domain);
        g.Credential = Read(fields, "credential", g.Credential);
    }

    /// <summary>A field value as plain text for display ("dc01", "3389", "prod, ad"); null when the field is not set.</summary>
    public static string? Display(string? json)
    {
        if (json is null)
            return null;
        JsonNode? node = JsonNode.Parse(json);
        return node switch
        {
            null => "",
            JsonValue value when value.TryGetValue(out string? text) => text,
            JsonArray array => string.Join(", ", array.Select(item => item is JsonValue v && v.TryGetValue(out string? s) ? s : item?.ToJsonString())),
            _ => node.ToJsonString(),
        };
    }

    public static T? Read<T>(SyncFields fields, string name) =>
        fields.Get(name) is { } json ? JsonSerializer.Deserialize<T>(json, Options) : default;

    private static T Read<T>(SyncFields fields, string name, T current) =>
        fields.Get(name) is { } json ? JsonSerializer.Deserialize<T>(json, Options)! : current;

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, Options);

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}
