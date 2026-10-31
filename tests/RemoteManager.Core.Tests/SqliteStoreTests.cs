using Microsoft.Data.Sqlite;
using RemoteManager.Core.Library;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.Storage;

namespace RemoteManager.Core.Tests;

public sealed class SqliteStoreTests : IDisposable
{
    private readonly TempFolder folder = new();

    public void Dispose() => folder.Dispose();

    [Fact]
    public void NewDatabaseIsMigratedToTheLatestVersion()
    {
        var store = new SqliteConnectionStore(folder.File("a.db"));

        Assert.Equal(SqliteMigrator.LatestVersion, store.SchemaVersion);
        Assert.Equal(Enumerable.Range(1, SqliteMigrator.LatestVersion), store.AppliedMigrations);
        Assert.Empty(new SqliteConnectionStore(folder.File("a.db")).AppliedMigrations);
    }

    [Fact]
    public void MigrationsAreNumberedWithoutGaps()
    {
        Assert.NotEmpty(SqliteMigrator.All);
        Assert.Equal(Enumerable.Range(1, SqliteMigrator.All.Count), SqliteMigrator.All.Select(m => m.Version));
    }

    [Fact]
    public void ConnectionsAndGroupsRoundTrip()
    {
        var store = new SqliteConnectionStore(folder.File("a.db"));
        var parent = new ConnectionGroup { Name = "Kunde A", UserName = "admin", Domain = "CORP", Credential = new CredentialRef("wincred", "group-1"), IsCollapsed = true, SortOrder = 3 };
        var child = new ConnectionGroup { Name = "Standort 1", ParentId = parent.Id, Description = "Keller" };
        var connection = new Connection
        {
            Name = "dc01",
            Type = ConnectionType.Rdp,
            Host = "10.0.0.10",
            Port = 3390,
            Description = "Domänencontroller",
            Notes = "Wartung Sa 20 Uhr",
            Tags = ["prod", "ad"],
            IsFavorite = true,
            GroupId = child.Id,
            SortOrder = 7,
            LastConnectedAt = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc),
            PreviewPath = "dc01.png",
            UserName = "admin",
            Domain = "CORP",
            Credential = new CredentialRef("wincred", "abc"),
            Settings = new RdpSettings
            {
                Display = RdpDisplayMode.FixedResolution, DesktopWidth = 1920, DesktopHeight = 1080, GatewayHost = "gw.example.com",
                GatewayUsage = RdpGatewayUsage.Always, AdminSession = true, Redirections = new RdpRedirections { Drives = true, Audio = RdpAudioMode.None },
            },
        };
        var ssh = new Connection
        {
            Name = "web01", Type = ConnectionType.Ssh, Host = "web01",
            Settings = new SshSettings { AuthMethod = SshAuthMethod.KeyFile, KeyPath = @"C:\keys\id_ed25519", JumpHostId = "jump", PortForwards = [new PortForward(PortForwardKind.Local, "127.0.0.1", 8080, "localhost", 80)] },
        };

        store.SaveAll([child, parent], [connection, ssh]);
        var reopened = new SqliteConnectionStore(folder.File("a.db"));
        ConnectionGroup[] groups = [.. reopened.LoadGroups()];
        Connection loaded = reopened.LoadConnections().Single(c => c.Id == connection.Id);
        Connection loadedSsh = reopened.LoadConnections().Single(c => c.Id == ssh.Id);

        ConnectionGroup loadedParent = groups.Single(g => g.Id == parent.Id);
        Assert.True(loadedParent.IsCollapsed);
        Assert.Equal(3, loadedParent.SortOrder);
        Assert.Equal(parent.Credential, loadedParent.Credential);
        Assert.Equal(parent.Id, groups.Single(g => g.Id == child.Id).ParentId);

        Assert.Equal(connection.Name, loaded.Name);
        Assert.Equal(3390, loaded.Port);
        Assert.Equal(connection.Notes, loaded.Notes);
        Assert.Equal(["prod", "ad"], loaded.Tags);
        Assert.True(loaded.IsFavorite);
        Assert.Equal(child.Id, loaded.GroupId);
        Assert.Equal(7, loaded.SortOrder);
        Assert.Equal(connection.LastConnectedAt, loaded.LastConnectedAt);
        Assert.Equal("dc01.png", loaded.PreviewPath);
        Assert.Equal(connection.Credential, loaded.Credential);
        Assert.Equal(connection.Settings, loaded.Settings);
        Assert.Equal(new PortForward(PortForwardKind.Local, "127.0.0.1", 8080, "localhost", 80), loadedSsh.SettingsAs<SshSettings>().PortForwards.Single());
        Assert.Equal(22, loadedSsh.EffectivePort);
    }

    [Fact]
    public void SaveUpdatesAndDeleteRemovesTags()
    {
        var store = new SqliteConnectionStore(folder.File("a.db"));
        var connection = new Connection { Name = "a", Tags = ["x", "y"] };
        store.SaveConnection(connection);
        connection.Name = "b";
        connection.Tags = ["z"];
        store.SaveConnection(connection);

        Connection loaded = Assert.Single(store.LoadConnections());
        Assert.Equal("b", loaded.Name);
        Assert.Equal(["z"], loaded.Tags);

        store.DeleteConnection(connection.Id);
        Assert.Empty(store.LoadConnections());
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM connection_tags"));
    }

    [Fact]
    public void DeletingAGroupDetachesItsConnections()
    {
        var store = new SqliteConnectionStore(folder.File("a.db"));
        var group = new ConnectionGroup { Name = "g" };
        store.SaveGroup(group);
        store.SaveConnection(new Connection { Name = "a", GroupId = group.Id });

        store.DeleteGroup(group.Id);

        Assert.Null(Assert.Single(store.LoadConnections()).GroupId);
    }

    [Fact]
    public void UnknownTypeIsSkippedButKept()
    {
        var store = new SqliteConnectionStore(folder.File("a.db"));
        store.SaveConnection(new Connection { Name = "a" });
        Execute("UPDATE connections SET type = 'FutureProtocol'");

        Assert.Empty(store.LoadConnections());
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM connections"));
    }

    [Fact]
    public void UnreadableSettingsFallBackToDefaults()
    {
        var store = new SqliteConnectionStore(folder.File("a.db"));
        store.SaveConnection(new Connection { Name = "a" });
        Execute("UPDATE connections SET settings_json = 'not json'");

        Assert.Equal(new RdpSettings(), Assert.Single(store.LoadConnections()).Settings);
    }

    [Fact]
    public void AppSettingsRoundTrip()
    {
        var store = new SqliteConnectionStore(folder.File("a.db"));
        store.SetSetting("tools.anydesk", @"C:\AnyDesk.exe");
        Assert.Equal(@"C:\AnyDesk.exe", store.GetSetting("tools.anydesk"));
        store.SetSetting("tools.anydesk", null);
        Assert.Null(store.GetSetting("tools.anydesk"));
    }

    [Fact]
    public void LibraryWritesThrough()
    {
        var library = new ConnectionLibrary(new SqliteConnectionStore(folder.File("a.db")));
        ConnectionGroup group = library.EnsureGroupPath("Kunde A / Standort 1")!;
        var connection = new Connection { Name = "a", GroupId = group.Id };
        library.Add(connection);
        connection.IsFavorite = true;
        Assert.True(library.Update(connection));
        Assert.False(library.Update(new Connection { Name = "not in the library" }));

        var reopened = new ConnectionLibrary(new SqliteConnectionStore(folder.File("a.db")));
        Assert.True(Assert.Single(reopened.Connections).IsFavorite);
        Assert.Equal("Kunde A / Standort 1", reopened.PathOf(reopened.Connections[0].GroupId));
    }

    private void Execute(string sql)
    {
        using var db = new SqliteConnection($"Data Source={folder.File("a.db")};Pooling=False");
        db.Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var db = new SqliteConnection($"Data Source={folder.File("a.db")};Pooling=False");
        db.Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
