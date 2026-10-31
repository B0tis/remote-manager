using System.Globalization;
using Microsoft.Data.Sqlite;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;

namespace RemoteManager.Core.Storage;

/// <summary>
/// Groups and connections in a SQLite file (plain SQL, schema in Storage/Migrations). Opens a short
/// connection per call; the file is not held open between calls. No secrets are ever written here.
/// </summary>
public sealed class SqliteConnectionStore : IConnectionStore
{
    private const string ConnectionColumns =
        "id, name, type, host, port, description, notes, is_favorite, group_id, sort_order, last_connected_at, " +
        "preview_path, user_name, domain, credential_source, credential_key, settings_json, updated_at";

    private const string GroupColumns =
        "id, parent_id, name, description, sort_order, is_collapsed, user_name, domain, credential_source, credential_key, updated_at";

    private readonly string connectionString;

    /// <summary>Opens (and creates or migrates) the database at <paramref name="path"/>.</summary>
    public SqliteConnectionStore(string path)
    {
        FilePath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // No pooling: the file is released after every call (backups, tests, the self-test can touch it).
            Pooling = false,
            ForeignKeys = true,
        }.ToString();

        using SqliteConnection db = Open();
        AppliedMigrations = SqliteMigrator.Migrate(db);
    }

    public string FilePath { get; }

    /// <summary>Migration versions applied while opening (empty when the schema was current).</summary>
    public IReadOnlyList<int> AppliedMigrations { get; }

    public int SchemaVersion
    {
        get
        {
            using SqliteConnection db = Open();
            return SqliteMigrator.CurrentVersion(db);
        }
    }

    public IReadOnlyList<ConnectionGroup> LoadGroups()
    {
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = $"SELECT {GroupColumns} FROM connection_groups ORDER BY sort_order, name";
        using SqliteDataReader reader = command.ExecuteReader();
        var groups = new List<ConnectionGroup>();
        while (reader.Read())
        {
            groups.Add(new ConnectionGroup
            {
                Id = reader.GetString(0),
                ParentId = NullableString(reader, 1),
                Name = reader.GetString(2),
                Description = reader.GetString(3),
                SortOrder = reader.GetInt32(4),
                IsCollapsed = reader.GetInt64(5) != 0,
                UserName = reader.GetString(6),
                Domain = reader.GetString(7),
                Credential = Reference(reader, 8),
                UpdatedAt = ParseTime(reader.GetString(10)),
            });
        }

        return groups;
    }

    public IReadOnlyList<Connection> LoadConnections()
    {
        using SqliteConnection db = Open();
        Dictionary<string, List<string>> tags = LoadTags(db);
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = $"SELECT {ConnectionColumns} FROM connections ORDER BY sort_order, name";
        using SqliteDataReader reader = command.ExecuteReader();
        var connections = new List<Connection>();
        while (reader.Read())
        {
            // A type this build does not know (written by a newer one) stays in the file untouched.
            if (!Enum.TryParse(reader.GetString(2), out ConnectionType type))
                continue;

            string id = reader.GetString(0);
            connections.Add(new Connection
            {
                Id = id,
                Name = reader.GetString(1),
                Type = type,
                Host = reader.GetString(3),
                Port = reader.GetInt32(4),
                Description = reader.GetString(5),
                Notes = reader.GetString(6),
                IsFavorite = reader.GetInt64(7) != 0,
                GroupId = NullableString(reader, 8),
                SortOrder = reader.GetInt32(9),
                LastConnectedAt = NullableString(reader, 10) is { } at ? ParseTime(at) : null,
                PreviewPath = NullableString(reader, 11),
                UserName = reader.GetString(12),
                Domain = reader.GetString(13),
                Credential = Reference(reader, 14),
                Settings = ConnectionSettings.Deserialize(type, reader.GetString(16)),
                UpdatedAt = ParseTime(reader.GetString(17)),
                Tags = tags.GetValueOrDefault(id) ?? [],
            });
        }

        return connections;
    }

    public void SaveGroup(ConnectionGroup group)
    {
        using SqliteConnection db = Open();
        WriteGroup(db, null, group);
    }

    public void DeleteGroup(string groupId)
    {
        using SqliteConnection db = Open();
        Execute(db, null, "DELETE FROM connection_groups WHERE id = $id", ("$id", groupId));
    }

    public void SaveConnection(Connection connection)
    {
        using SqliteConnection db = Open();
        using SqliteTransaction transaction = db.BeginTransaction();
        WriteConnection(db, transaction, connection);
        transaction.Commit();
    }

    public void DeleteConnection(string connectionId)
    {
        using SqliteConnection db = Open();
        Execute(db, null, "DELETE FROM connections WHERE id = $id", ("$id", connectionId));
    }

    public void SaveAll(IEnumerable<ConnectionGroup> groups, IEnumerable<Connection> connections)
    {
        using SqliteConnection db = Open();
        using SqliteTransaction transaction = db.BeginTransaction();
        // Parents may come after their children in the input; check references at commit.
        Execute(db, transaction, "PRAGMA defer_foreign_keys = ON");
        foreach (ConnectionGroup group in groups)
            WriteGroup(db, transaction, group);
        foreach (Connection connection in connections)
            WriteConnection(db, transaction, connection);
        transaction.Commit();
    }

    public string? GetSetting(string key)
    {
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT value FROM app_settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string? value)
    {
        using SqliteConnection db = Open();
        if (value is null)
            Execute(db, null, "DELETE FROM app_settings WHERE key = $key", ("$key", key));
        else
            Execute(db, null, "INSERT INTO app_settings (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
                ("$key", key), ("$value", value));
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }

    private static void WriteGroup(SqliteConnection db, SqliteTransaction? transaction, ConnectionGroup g) =>
        Execute(db, transaction,
            $"INSERT INTO connection_groups ({GroupColumns}) VALUES ($id, $parent, $name, $description, $sort, $collapsed, $user, $domain, $credSource, $credKey, $updated) " +
            "ON CONFLICT(id) DO UPDATE SET parent_id = excluded.parent_id, name = excluded.name, description = excluded.description, " +
            "sort_order = excluded.sort_order, is_collapsed = excluded.is_collapsed, user_name = excluded.user_name, domain = excluded.domain, " +
            "credential_source = excluded.credential_source, credential_key = excluded.credential_key, updated_at = excluded.updated_at",
            ("$id", g.Id), ("$parent", g.ParentId), ("$name", g.Name), ("$description", g.Description), ("$sort", g.SortOrder),
            ("$collapsed", g.IsCollapsed ? 1 : 0), ("$user", g.UserName), ("$domain", g.Domain),
            ("$credSource", g.Credential?.Source), ("$credKey", g.Credential?.Key), ("$updated", FormatTime(g.UpdatedAt)));

    private static void WriteConnection(SqliteConnection db, SqliteTransaction transaction, Connection c)
    {
        Execute(db, transaction,
            $"INSERT INTO connections ({ConnectionColumns}) VALUES ($id, $name, $type, $host, $port, $description, $notes, $favorite, $group, " +
            "$sort, $lastConnected, $preview, $user, $domain, $credSource, $credKey, $settings, $updated) " +
            "ON CONFLICT(id) DO UPDATE SET name = excluded.name, type = excluded.type, host = excluded.host, port = excluded.port, " +
            "description = excluded.description, notes = excluded.notes, is_favorite = excluded.is_favorite, group_id = excluded.group_id, " +
            "sort_order = excluded.sort_order, last_connected_at = excluded.last_connected_at, preview_path = excluded.preview_path, " +
            "user_name = excluded.user_name, domain = excluded.domain, credential_source = excluded.credential_source, " +
            "credential_key = excluded.credential_key, settings_json = excluded.settings_json, updated_at = excluded.updated_at",
            ("$id", c.Id), ("$name", c.Name), ("$type", c.Type.ToString()), ("$host", c.Host), ("$port", c.Port),
            ("$description", c.Description), ("$notes", c.Notes), ("$favorite", c.IsFavorite ? 1 : 0), ("$group", c.GroupId),
            ("$sort", c.SortOrder), ("$lastConnected", c.LastConnectedAt is { } at ? FormatTime(at) : null), ("$preview", c.PreviewPath),
            ("$user", c.UserName), ("$domain", c.Domain), ("$credSource", c.Credential?.Source), ("$credKey", c.Credential?.Key),
            ("$settings", ConnectionSettings.Serialize(c.Settings)), ("$updated", FormatTime(c.UpdatedAt)));

        Execute(db, transaction, "DELETE FROM connection_tags WHERE connection_id = $id", ("$id", c.Id));
        int position = 0;
        foreach (string tag in c.Tags.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Execute(db, transaction, "INSERT INTO connection_tags (connection_id, tag, position) VALUES ($id, $tag, $position)",
                ("$id", c.Id), ("$tag", tag), ("$position", position++));
        }
    }

    private static Dictionary<string, List<string>> LoadTags(SqliteConnection db)
    {
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT connection_id, tag FROM connection_tags ORDER BY connection_id, position";
        using SqliteDataReader reader = command.ExecuteReader();
        var tags = new Dictionary<string, List<string>>();
        while (reader.Read())
        {
            string id = reader.GetString(0);
            if (!tags.TryGetValue(id, out List<string>? list))
                tags[id] = list = [];
            list.Add(reader.GetString(1));
        }

        return tags;
    }

    private static void Execute(SqliteConnection db, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static string? NullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static CredentialRef? Reference(SqliteDataReader reader, int sourceOrdinal) =>
        NullableString(reader, sourceOrdinal) is { } source && NullableString(reader, sourceOrdinal + 1) is { } key
            ? new CredentialRef(source, key)
            : null;

    private static string FormatTime(DateTime time) => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTime ParseTime(string text) =>
        DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
}
