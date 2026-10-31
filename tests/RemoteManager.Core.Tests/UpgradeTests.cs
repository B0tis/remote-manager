using RemoteManager.Core.Credentials;
using RemoteManager.Core.History;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.Storage;

namespace RemoteManager.Core.Tests;

/// <summary>
/// A database written by the p1-foundation build (schema 1, its sample connections, plus an SSH jump host,
/// a VNC connection through it, and a group logon) must open in the current build without losing anything.
/// </summary>
public sealed class UpgradeTests : IDisposable
{
    private readonly TempFolder folder = new();

    public UpgradeTests() =>
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Upgrade", "p1-foundation.db"), folder.File(DataStore.DatabaseFileName));

    public void Dispose() => folder.Dispose();

    [Fact]
    public async Task FoundationDatabaseUpgradesInPlace()
    {
        (var library, DataStoreReport report) = await DataStore.OpenAsync(folder.Path, new MemoryCredentialSource("wincred"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Enumerable.Range(2, SqliteMigrator.LatestVersion - 1), report.AppliedMigrations);
        Assert.False(report.Seeded);
        Assert.Equal(9, library.Connections.Count);
        Assert.Equal(["Homelab", "Kunde A", "Laden"], library.Groups.Select(g => g.Name).Order());

        // Settings written by the old build read into the current records, new options at their defaults.
        Connection teamViewer = library.Connections.Single(c => c.Type == ConnectionType.TeamViewer);
        RemoteToolSettings tool = teamViewer.SettingsAs<RemoteToolSettings>();
        Assert.Equal(RemoteToolMode.Control, tool.Mode);
        Assert.Equal(-1, tool.AccessControl);
        Assert.False(tool.CredentialOnCommandLine);
        Assert.Equal(RemoteToolSource.Manual, tool.Source);

        Assert.Equal(RdpDisplayMode.SmartSizing, library.Connections.Single(c => c.Name == "dev-vm").SettingsAs<RdpSettings>().Display);

        Connection ssh = library.Connections.Single(c => c.Type == ConnectionType.Ssh);
        SshSettings sshSettings = ssh.SettingsAs<SshSettings>();
        Assert.Equal(SshAuthMethod.Agent, sshSettings.AuthMethod);
        Assert.Equal(new PortForward(PortForwardKind.Local, "127.0.0.1", 8080, "web", 80), Assert.Single(sshSettings.PortForwards));

        Connection vnc = library.Connections.Single(c => c.Type == ConnectionType.Vnc);
        VncSettings vncSettings = vnc.SettingsAs<VncSettings>();
        Assert.True(vncSettings.ViewOnly);
        Assert.Equal(ssh.Id, vncSettings.JumpHostId);
        Assert.Equal(["pi"], vnc.Tags);

        // The group logon still resolves (inheritance) and the new history table works.
        var credentials = new CredentialService([new MemoryCredentialSource("wincred")], library);
        EffectiveCredential logon = credentials.Resolve(vnc);
        Assert.True(logon.IsInherited);
        Assert.Equal("admin", logon.UserName);
        Assert.Equal("LAB", logon.Domain);

        var history = new ConnectionHistory(report.DatabasePath);
        history.End(history.Start(vnc.Id, HistoryResult.Connected));
        Assert.Single(history.Recent(vnc.Id));

        // Opening again applies nothing.
        Assert.Empty(new SqliteConnectionStore(report.DatabasePath).AppliedMigrations);
    }
}

/// <summary>
/// A database written by the Phase 3 build on main (schema 2: samples, a subgroup, notes, a credential reference,
/// history) upgrades to the sync schema in place and can sync right away.
/// </summary>
public sealed class MainUpgradeTests : IDisposable
{
    private readonly TempFolder folder = new();

    public MainUpgradeTests()
    {
        Directory.CreateDirectory(folder.File("data"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Upgrade", "main-phase3.db"), Path.Combine(folder.File("data"), DataStore.DatabaseFileName));
    }

    public void Dispose() => folder.Dispose();

    [Fact]
    public async Task MainDatabaseUpgradesToSyncSchemaWithoutLosingData()
    {
        (var library, DataStoreReport report) = await DataStore.OpenAsync(folder.File("data"), new MemoryCredentialSource("wincred"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([3], report.AppliedMigrations);
        Assert.False(report.Seeded);
        Assert.Equal(7, library.Connections.Count);
        Assert.Equal(["Homelab", "Kunde A", "Laden", "Standort 1"], library.Groups.Select(g => g.Name).Order());
        Connection dc = library.Connections.Single(c => c.Name == "dc01");
        Assert.Equal("Wartung dienstags", dc.Notes);
        Assert.Equal(new CredentialRef("wincred", dc.Id), dc.Credential);
        Assert.NotNull(dc.LastConnectedAt);
        Assert.Single(new ConnectionHistory(report.DatabasePath).Recent(dc.Id));

        var sync = new RemoteManager.Core.Sync.SyncStateStore(report.DatabasePath);
        Assert.Empty(sync.LoadBases());
        Assert.Empty(sync.LoadConflicts());

        Directory.CreateDirectory(folder.File("sync"));
        var engine = new RemoteManager.Core.Sync.SyncEngine(library, sync, new RemoteManager.Core.Sync.FolderSyncTransport(folder.File("sync")),
            new RemoteManager.Core.Sync.SyncDevice("upgrade", "PC"));
        RemoteManager.Core.Sync.SyncResult result = await engine.SyncAsync("Upgrade-Passphrase-1", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(7 + 4, result.Pushed);
        Assert.Equal(11, sync.LoadBases().Count);
    }
}
