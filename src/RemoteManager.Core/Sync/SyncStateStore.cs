using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace RemoteManager.Core.Sync;

/// <summary>
/// The merge bases and open conflicts in the app database (tables sync_base and sync_conflicts, migration 0003).
/// Like the connection store it opens a short connection per call.
/// </summary>
public sealed class SyncStateStore(string databasePath)
{
    private readonly string connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWrite,
        Pooling = false,
    }.ToString();

    public Dictionary<SyncKey, SyncRecord> LoadBases()
    {
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT kind, id, revision, history_json, fields_json, modified_at, device_id, previous_json FROM sync_base";
        using SqliteDataReader reader = command.ExecuteReader();
        var bases = new Dictionary<SyncKey, SyncRecord>();
        while (reader.Read())
        {
            var record = new SyncRecord
            {
                Kind = reader.GetString(0),
                Id = reader.GetString(1),
                Rev = reader.GetString(2),
                History = Deserialize<List<string>>(reader.GetString(3)),
                Fields = reader.IsDBNull(4) ? null : Deserialize<SyncFields>(reader.GetString(4)),
                Modified = ParseTime(reader.GetString(5)),
                Device = reader.GetString(6),
                Previous = reader.IsDBNull(7) ? null : Deserialize<SyncAncestor>(reader.GetString(7)),
            };
            bases[record.Key] = record;
        }

        return bases;
    }

    public Dictionary<SyncKey, SyncConflict> LoadConflicts()
    {
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT kind, id, local_json, remote_json, fields_json, detected_at FROM sync_conflicts";
        using SqliteDataReader reader = command.ExecuteReader();
        var conflicts = new Dictionary<SyncKey, SyncConflict>();
        while (reader.Read())
        {
            var key = new SyncKey(reader.GetString(0), reader.GetString(1));
            conflicts[key] = new SyncConflict(
                key,
                reader.IsDBNull(2) ? null : Deserialize<SyncFields>(reader.GetString(2)),
                Deserialize<SyncRecord>(reader.GetString(3)),
                Deserialize<List<string>>(reader.GetString(4)),
                ParseTime(reader.GetString(5)));
        }

        return conflicts;
    }

    /// <summary>Replaces all bases and conflicts in one transaction.</summary>
    public void Save(IEnumerable<SyncRecord> bases, IEnumerable<SyncConflict> conflicts)
    {
        using SqliteConnection db = Open();
        using SqliteTransaction transaction = db.BeginTransaction();
        Execute(db, transaction, "DELETE FROM sync_base");
        Execute(db, transaction, "DELETE FROM sync_conflicts");
        foreach (SyncRecord b in bases)
        {
            Execute(db, transaction,
                "INSERT INTO sync_base (kind, id, revision, history_json, fields_json, modified_at, device_id, previous_json) " +
                "VALUES ($kind, $id, $rev, $history, $fields, $modified, $device, $previous)",
                ("$kind", b.Kind), ("$id", b.Id), ("$rev", b.Rev), ("$history", Serialize(b.History)),
                ("$fields", b.Fields is null ? null : Serialize(b.Fields)), ("$modified", FormatTime(b.Modified)), ("$device", b.Device),
                ("$previous", b.Previous is null ? null : Serialize(b.Previous)));
        }

        foreach (SyncConflict c in conflicts)
        {
            Execute(db, transaction,
                "INSERT INTO sync_conflicts (kind, id, local_json, remote_json, fields_json, detected_at) VALUES ($kind, $id, $local, $remote, $fields, $detected)",
                ("$kind", c.Key.Kind), ("$id", c.Key.Id), ("$local", c.Local is null ? null : Serialize(c.Local)), ("$remote", Serialize(c.Remote)),
                ("$fields", Serialize(c.Fields)), ("$detected", FormatTime(c.DetectedAt)));
        }

        transaction.Commit();
    }

    /// <summary>Forgets everything (sync turned off and on again with another folder or passphrase).</summary>
    public void Clear() => Save([], []);

    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Json)!;

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }

    private static void Execute(SqliteConnection db, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static string FormatTime(DateTime time) => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTime ParseTime(string text) =>
        DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
}
