using System.Text;
using RemoteManager.Core.Credentials;
using RemoteManager.Core.Library;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.Storage;

namespace RemoteManager.Core.Tests;

public sealed class LegacyMigrationTests : IDisposable
{
    // The shape connections.json had on main before the SQLite store.
    private const string LegacyJson = """
        [
          { "Id": "805e28365e744cb586935e4564dacb96", "Name": "dc01", "Type": "Rdp", "Host": "10.0.0.10", "Port": 3389,
            "Description": "Domain controller", "Group": "Kunde A", "Tags": ["prod", "ad"], "UserName": "admin", "Domain": "CORP",
            "DisplayMode": 2, "WinKeysToSession": false, "LastConnectedAt": "2026-10-30T19:00:00Z", "IsFavorite": true },
          { "Id": "c8dbee0e549f461ba7eee697dfedbfa1", "Name": "fileserver", "Type": "Rdp", "Host": "10.0.0.20", "Port": 3390,
            "Description": "", "Group": "Kunde A", "Tags": [], "UserName": "", "Domain": "", "DisplayMode": 1, "WinKeysToSession": true,
            "LastConnectedAt": null, "IsFavorite": false },
          { "Id": "a1", "Name": "Kasse 2", "Type": "AnyDesk", "Host": "987 654 321", "Port": 0, "Description": "Laden",
            "Group": "Laden", "Tags": ["kasse"], "UserName": "", "Domain": "", "DisplayMode": 0, "WinKeysToSession": true,
            "LastConnectedAt": null, "IsFavorite": true }
        ]
        """;

    private readonly TempFolder folder = new();

    public void Dispose() => folder.Dispose();

    [Fact]
    public async Task MigratesJsonOnceAndKeepsABackup()
    {
        string json = folder.File(DataStore.LegacyJsonFileName);
        await File.WriteAllTextAsync(json, LegacyJson, TestContext.Current.CancellationToken);
        const string secret = "Geheim-äß€-123";
        var credentials = new MemoryCredentialSource();
        await credentials.WriteAsync("805e28365e744cb586935e4564dacb96", secret, TestContext.Current.CancellationToken);

        (ConnectionLibrary library, DataStoreReport report) = await DataStore.OpenAsync(folder.Path, credentials, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, report.MigratedFromJson);
        Assert.False(report.Seeded);
        Assert.False(File.Exists(json));
        Assert.Equal(json + ".bak", report.JsonBackupPath);
        Assert.True(File.Exists(json + ".bak"));

        Connection dc01 = library.FindConnection("805e28365e744cb586935e4564dacb96")!;
        Assert.Equal("dc01", dc01.Name);
        Assert.Equal("10.0.0.10", dc01.Host);
        Assert.Equal(0, dc01.Port);
        Assert.Equal(3389, dc01.EffectivePort);
        Assert.Equal(["prod", "ad"], dc01.Tags);
        Assert.Equal("admin", dc01.UserName);
        Assert.Equal("CORP", dc01.Domain);
        Assert.True(dc01.IsFavorite);
        Assert.Equal(new DateTime(2026, 10, 30, 19, 0, 0, DateTimeKind.Utc), dc01.LastConnectedAt);
        Assert.Equal("Kunde A", library.PathOf(dc01.GroupId));
        Assert.Equal(new CredentialRef(credentials.Id, dc01.Id), dc01.Credential);
        RdpSettings rdp = dc01.SettingsAs<RdpSettings>();
        Assert.Equal((RdpDisplayMode.FixedResolution, 1920, 1080), (rdp.Display, rdp.DesktopWidth, rdp.DesktopHeight));
        Assert.Equal(RdpKeyboardMode.Local, rdp.Keyboard);

        Connection fileserver = library.FindConnection("c8dbee0e549f461ba7eee697dfedbfa1")!;
        Assert.Equal(3390, fileserver.EffectivePort);
        Assert.Null(fileserver.Credential);
        Assert.Equal(RdpDisplayMode.SmartSizing, fileserver.SettingsAs<RdpSettings>().Display);
        Assert.Equal(dc01.GroupId, fileserver.GroupId);

        Connection kasse = library.FindConnection("a1")!;
        Assert.Equal(ConnectionType.AnyDesk, kasse.Type);
        Assert.IsType<RemoteToolSettings>(kasse.Settings);
        Assert.Equal("Laden", library.PathOf(kasse.GroupId));
        Assert.Equal(2, library.Groups.Count);

        // The secret is in the credential source only, never in the database file.
        byte[] db = await File.ReadAllBytesAsync(report.DatabasePath, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(secret, Encoding.UTF8.GetString(db), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, Encoding.Unicode.GetString(db), StringComparison.Ordinal);

        // A connections.json that shows up again (an old build ran) is not imported a second time.
        await File.WriteAllTextAsync(json, LegacyJson, TestContext.Current.CancellationToken);
        (ConnectionLibrary again, DataStoreReport second) = await DataStore.OpenAsync(folder.Path, credentials, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, second.MigratedFromJson);
        Assert.Equal(3, again.Connections.Count);
    }

    [Fact]
    public async Task FirstStartWithoutJsonSeedsSamplesOnce()
    {
        var credentials = new MemoryCredentialSource();
        (ConnectionLibrary library, DataStoreReport report) = await DataStore.OpenAsync(folder.Path, credentials, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(report.Seeded);
        Assert.NotEmpty(library.Connections);
        Assert.Contains(library.Connections, c => c.Type != ConnectionType.Rdp);

        library.Remove(library.Connections[0]);
        (ConnectionLibrary reopened, DataStoreReport second) = await DataStore.OpenAsync(folder.Path, credentials, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(second.Seeded);
        Assert.Equal(library.Connections.Count, reopened.Connections.Count);
    }
}
