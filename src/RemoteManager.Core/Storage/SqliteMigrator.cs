using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace RemoteManager.Core.Storage;

/// <summary>
/// Applies the numbered scripts in Storage/Migrations (NNNN_name.sql, embedded) in order. The schema
/// version is PRAGMA user_version; each script runs in its own transaction together with the version bump.
/// To change the schema add the next number; never edit a script that has shipped.
/// </summary>
public static class SqliteMigrator
{
    private const string ResourcePrefix = "RemoteManager.Core.Migrations.";

    public sealed record Migration(int Version, string Name, string Sql);

    public static IReadOnlyList<Migration> All { get; } = LoadMigrations();

    public static int LatestVersion => All.Count == 0 ? 0 : All[^1].Version;

    public static int CurrentVersion(SqliteConnection db)
    {
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Brings the database up to <see cref="LatestVersion"/>. Returns the versions applied.</summary>
    public static IReadOnlyList<int> Migrate(SqliteConnection db)
    {
        int current = CurrentVersion(db);
        if (current > LatestVersion)
            throw new InvalidOperationException($"The database has schema version {current}; this build knows up to {LatestVersion}. Update Remote-Manager.");

        var applied = new List<int>();
        foreach (Migration migration in All.Where(m => m.Version > current))
        {
            using SqliteTransaction transaction = db.BeginTransaction();
            using (SqliteCommand command = db.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = migration.Sql;
                command.ExecuteNonQuery();
            }

            using (SqliteCommand version = db.CreateCommand())
            {
                version.Transaction = transaction;
                version.CommandText = $"PRAGMA user_version = {migration.Version.ToString(CultureInfo.InvariantCulture)}";
                version.ExecuteNonQuery();
            }

            transaction.Commit();
            applied.Add(migration.Version);
        }

        return applied;
    }

    private static List<Migration> LoadMigrations()
    {
        Assembly assembly = typeof(SqliteMigrator).Assembly;
        var migrations = new List<Migration>();
        foreach (string resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            string name = resource[ResourcePrefix.Length..];
            int underscore = name.IndexOf('_', StringComparison.Ordinal);
            if (underscore <= 0 || !int.TryParse(name[..underscore], NumberStyles.None, CultureInfo.InvariantCulture, out int version))
                throw new InvalidOperationException($"Migration {name} must be named NNNN_description.sql.");

            using Stream stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            migrations.Add(new Migration(version, name, reader.ReadToEnd()));
        }

        migrations.Sort((a, b) => a.Version.CompareTo(b.Version));
        for (int i = 0; i < migrations.Count; i++)
        {
            if (migrations[i].Version != i + 1)
                throw new InvalidOperationException($"Migrations must be numbered 1, 2, 3, ... without gaps; found {migrations[i].Name}.");
        }

        return migrations;
    }
}
