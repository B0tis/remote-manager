using System.Globalization;
using Microsoft.Data.Sqlite;

namespace RemoteManager.Core.Updates;

/// <summary>
/// Copies of the database taken before an update is applied: "remote-manager-&lt;UTC time&gt;-v&lt;version&gt;.db"
/// in the backup folder, newest <see cref="DefaultKeep"/> kept. Uses SQLite's online backup, so the copy is
/// consistent even while the app has the file open.
/// </summary>
public static class DatabaseBackup
{
    public const int DefaultKeep = 3;
    private const string Prefix = "remote-manager-";

    /// <summary>Writes a backup of <paramref name="databasePath"/> and deletes all but the newest <paramref name="keep"/>. Returns the new file.</summary>
    public static string Create(string databasePath, string backupFolder, string fromVersion, int keep = DefaultKeep, DateTime? now = null)
    {
        Directory.CreateDirectory(backupFolder);
        string stamp = (now ?? DateTime.UtcNow).ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        string target = Path.Combine(backupFolder, $"{Prefix}{stamp}-v{Sanitize(fromVersion)}.db");

        using (var source = new SqliteConnection(ConnectionString(databasePath, SqliteOpenMode.ReadOnly)))
        using (var copy = new SqliteConnection(ConnectionString(target, SqliteOpenMode.ReadWriteCreate)))
        {
            source.Open();
            copy.Open();
            source.BackupDatabase(copy);
        }

        Prune(backupFolder, keep);
        return target;
    }

    /// <summary>The backups in <paramref name="backupFolder"/>, newest first.</summary>
    public static IReadOnlyList<string> List(string backupFolder) =>
        Directory.Exists(backupFolder)
            ? [.. Directory.GetFiles(backupFolder, Prefix + "*.db").OrderByDescending(Path.GetFileName, StringComparer.Ordinal)]
            : [];

    private static void Prune(string backupFolder, int keep)
    {
        foreach (string old in List(backupFolder).Skip(keep))
            File.Delete(old);
    }

    private static string ConnectionString(string path, SqliteOpenMode mode) =>
        new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString();

    private static string Sanitize(string version) =>
        new([.. version.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '_')]);
}
