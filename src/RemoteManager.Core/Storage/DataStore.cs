using RemoteManager.Core.Credentials;
using RemoteManager.Core.ImportExport;
using RemoteManager.Core.Library;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;

namespace RemoteManager.Core.Storage;

/// <summary>How the store was opened, for the log and the self-test.</summary>
public sealed record DataStoreReport(string DatabasePath, IReadOnlyList<int> AppliedMigrations, int MigratedFromJson, string? JsonBackupPath, bool Seeded);

/// <summary>
/// Opens the database in a data folder and initializes it once: from the old connections.json when
/// there is one (renamed to connections.json.bak afterwards), otherwise with sample connections.
/// </summary>
public static class DataStore
{
    public const string DatabaseFileName = "remote-manager.db";
    public const string LegacyJsonFileName = "connections.json";

    /// <summary>app_settings key; set once the store got its first content (migration or samples).</summary>
    public const string InitializedSetting = "store.initialized";

    /// <param name="credentials">Used to check which migrated connections already have a stored password.</param>
    public static async Task<(ConnectionLibrary Library, DataStoreReport Report)> OpenAsync(
        string dataFolder, ICredentialSource credentials, bool seedSamples = true, CancellationToken cancellationToken = default)
    {
        var store = new SqliteConnectionStore(Path.Combine(dataFolder, DatabaseFileName));
        int migrated = 0;
        string? backup = null;
        bool seeded = false;

        if (store.GetSetting(InitializedSetting) is null)
        {
            string json = Path.Combine(dataFolder, LegacyJsonFileName);
            if (File.Exists(json))
            {
                migrated = await MigrateJsonAsync(store, json, credentials, cancellationToken).ConfigureAwait(false);
                backup = BackupPath(json);
                File.Move(json, backup);
                store.SetSetting(InitializedSetting, $"json-migration {DateTime.UtcNow:O}");
            }
            else
            {
                if (seedSamples && store.LoadConnections().Count == 0)
                {
                    SampleData.Create(out List<ConnectionGroup> groups, out List<Connection> connections);
                    store.SaveAll(groups, connections);
                    seeded = true;
                }

                store.SetSetting(InitializedSetting, $"{(seeded ? "samples" : "empty")} {DateTime.UtcNow:O}");
            }
        }

        return (new ConnectionLibrary(store), new DataStoreReport(store.FilePath, store.AppliedMigrations, migrated, backup, seeded));
    }

    /// <summary>Imports the old JSON file into <paramref name="store"/> in one transaction. Returns the number of connections.</summary>
    public static async Task<int> MigrateJsonAsync(IConnectionStore store, string jsonPath, ICredentialSource credentials, CancellationToken cancellationToken = default)
    {
        ImportResult result = await new LegacyJsonImporter().ImportAsync(jsonPath, cancellationToken).ConfigureAwait(false);
        foreach (Connection connection in result.Connections)
        {
            // Passwords were keyed by connection id; the reference makes that link explicit.
            if (await credentials.ExistsAsync(connection.Id, cancellationToken).ConfigureAwait(false))
                connection.Credential = new CredentialRef(credentials.Id, connection.Id);
        }

        store.SaveAll(result.Groups, result.Connections);
        return result.Connections.Count;
    }

    private static string BackupPath(string json)
    {
        string backup = json + ".bak";
        return File.Exists(backup) ? $"{json}.{DateTime.Now:yyyyMMdd-HHmmss}.bak" : backup;
    }
}

/// <summary>Sample connections for a first start without data.</summary>
internal static class SampleData
{
    public static void Create(out List<ConnectionGroup> groups, out List<Connection> connections)
    {
        var kundeA = new ConnectionGroup { Name = "Kunde A", SortOrder = 0, UserName = "admin", Domain = "CORP" };
        var laden = new ConnectionGroup { Name = "Laden", SortOrder = 1 };
        var homelab = new ConnectionGroup { Name = "Homelab", SortOrder = 2 };
        groups = [kundeA, laden, homelab];

        connections =
        [
            new() { Name = "dc01", Host = "10.0.0.10", Description = "Domänencontroller", GroupId = kundeA.Id, Tags = ["prod", "ad"], IsFavorite = true },
            new() { Name = "fileserver", Host = "10.0.0.20", Description = "Freigaben, Backup-Ziel", GroupId = kundeA.Id, Tags = ["prod"] },
            new() { Name = "Buchhaltung-PC", Type = ConnectionType.TeamViewer, Host = "123 456 789", Description = "Nur nach Absprache", GroupId = kundeA.Id, Tags = ["kunde-a"] },
            new() { Name = "Kasse 2", Type = ConnectionType.AnyDesk, Host = "987 654 321", Description = "Laden, unbeaufsichtigter Zugriff", GroupId = laden.Id, Tags = ["kasse"], IsFavorite = true },
            new() { Name = "hyperv01", Host = "192.168.1.10", Description = "Hyper-V-Host", GroupId = homelab.Id, Tags = ["homelab", "hyper-v"] },
            new() { Name = "nas", Type = ConnectionType.RustDesk, Host = "1 234 567 890", Description = "Synology, RustDesk-Relay", GroupId = homelab.Id, Tags = ["homelab"] },
            new() { Name = "dev-vm", Host = "192.168.1.50", Description = "Test-VM", GroupId = homelab.Id, Tags = ["dev", "test", "local", "vm"], Settings = new RdpSettings { Display = RdpDisplayMode.SmartSizing } },
        ];
    }
}
