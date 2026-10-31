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
