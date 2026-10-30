using System.Globalization;
using Microsoft.Data.Sqlite;

namespace RemoteManager.Core.History;

/// <summary>How a recorded connection attempt went. Stored by name.</summary>
public enum HistoryResult
{
    /// <summary>Logged on (embedded session).</summary>
    Connected,
    /// <summary>Handed to an external program.</summary>
    Launched,
    Failed,
}

public sealed record HistoryEntry(long Id, string ConnectionId, DateTime StartedAt, DateTime? EndedAt, HistoryResult Result)
{
    public TimeSpan? Duration => EndedAt is { } end ? end - StartedAt : null;
}

/// <summary>How often and how recently a connection was used (quick launcher ranking).</summary>
public sealed record UsageStats(int Count, DateTime LastUsed);

/// <summary>
/// The connection history in the app database (table connection_history, migration 0002). Rows go
/// with their connection (ON DELETE CASCADE). Like the connection store it opens a short connection per call.
/// </summary>
public sealed class ConnectionHistory
{
    private readonly string connectionString;

    public ConnectionHistory(string databasePath)
    {
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            ForeignKeys = true,
        }.ToString();
    }

    /// <summary>Records the start of a connection; returns the entry id for <see cref="End"/>.</summary>
    public long Start(string connectionId, HistoryResult result, DateTime? startedAt = null)
    {
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "INSERT INTO connection_history (connection_id, started_at, result) VALUES ($id, $at, $result); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$id", connectionId);
        command.Parameters.AddWithValue("$at", FormatTime(startedAt ?? DateTime.UtcNow));
        command.Parameters.AddWithValue("$result", result.ToString());
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Sets the end time of an entry that has none yet.</summary>
    public void End(long entryId, DateTime? endedAt = null)
    {
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "UPDATE connection_history SET ended_at = $at WHERE id = $id AND ended_at IS NULL";
        command.Parameters.AddWithValue("$id", entryId);
        command.Parameters.AddWithValue("$at", FormatTime(endedAt ?? DateTime.UtcNow));
        command.ExecuteNonQuery();
    }

    /// <summary>The newest entries of a connection, newest first.</summary>
    public IReadOnlyList<HistoryEntry> Recent(string connectionId, int limit = 20)
    {
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT id, connection_id, started_at, ended_at, result FROM connection_history " +
            "WHERE connection_id = $id ORDER BY started_at DESC, id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$id", connectionId);
        command.Parameters.AddWithValue("$limit", limit);
        using SqliteDataReader reader = command.ExecuteReader();
        var entries = new List<HistoryEntry>();
        while (reader.Read())
        {
            entries.Add(new HistoryEntry(reader.GetInt64(0), reader.GetString(1), ParseTime(reader.GetString(2)),
                reader.IsDBNull(3) ? null : ParseTime(reader.GetString(3)),
                Enum.TryParse(reader.GetString(4), out HistoryResult result) ? result : HistoryResult.Connected));
        }

        return entries;
    }

    /// <summary>Use count and last use per connection since <paramref name="since"/>.</summary>
    public IReadOnlyDictionary<string, UsageStats> Usage(DateTime since)
    {
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT connection_id, COUNT(*), MAX(started_at) FROM connection_history " +
            "WHERE started_at >= $since AND result <> $failed GROUP BY connection_id";
        command.Parameters.AddWithValue("$since", FormatTime(since));
        command.Parameters.AddWithValue("$failed", nameof(HistoryResult.Failed));
        using SqliteDataReader reader = command.ExecuteReader();
        var usage = new Dictionary<string, UsageStats>();
        while (reader.Read())
            usage[reader.GetString(0)] = new UsageStats(reader.GetInt32(1), ParseTime(reader.GetString(2)));
        return usage;
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }

    private static string FormatTime(DateTime time) => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTime ParseTime(string text) =>
        DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
}
