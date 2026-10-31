using System.Text.RegularExpressions;
using RemoteManager.Core.Credentials;
using RemoteManager.Core.Library;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.Storage;

namespace RemoteManager.Core.Tests;

public sealed class LibraryAndCredentialTests : IDisposable
{
    private readonly TempFolder folder = new();
    private readonly ConnectionLibrary library;
    private readonly MemoryCredentialSource source = new();
    private readonly CredentialService credentials;

    public LibraryAndCredentialTests()
    {
        library = new ConnectionLibrary(new SqliteConnectionStore(folder.File("a.db")));
        credentials = new CredentialService([source], library);
    }

    public void Dispose() => folder.Dispose();

    [Fact]
    public void EnsureGroupPathCreatesMissingParentsOnce()
    {
        ConnectionGroup leaf = library.EnsureGroupPath("Kunde A / Standort 1 / Keller")!;
        ConnectionGroup same = library.EnsureGroupPath("kunde a/standort 1/keller")!;

        Assert.Same(leaf, same);
        Assert.Equal(3, library.Groups.Count);
        Assert.Equal("Kunde A / Standort 1 / Keller", library.PathOf(leaf.Id));
        Assert.Null(library.EnsureGroupPath("  "));
    }

    [Fact]
    public void IsInGroupIncludesSubgroups()
    {
        ConnectionGroup child = library.EnsureGroupPath("A / B")!;
        var connection = new Connection { Name = "x", GroupId = child.Id };

        Assert.True(library.IsInGroup(connection, child.ParentId!));
        Assert.True(library.IsInGroup(connection, child.Id));
        Assert.False(library.IsInGroup(connection, library.EnsureGroupPath("C")!.Id));
    }

    [Fact]
    public void RemovingAGroupMovesItsContentUp()
    {
        ConnectionGroup middle = library.EnsureGroupPath("A / B")!;
        ConnectionGroup leaf = library.EnsureGroupPath("A / B / C")!;
        var connection = new Connection { Name = "x", GroupId = middle.Id };
        library.Add(connection);

        library.RemoveGroup(middle);

        Assert.Equal(middle.ParentId, leaf.ParentId);
        Assert.Equal(middle.ParentId, connection.GroupId);
        Assert.Equal("A / C", library.PathOf(leaf.Id));
    }

    [Fact]
    public void AGroupCannotMoveIntoItsOwnSubgroup()
    {
        ConnectionGroup parent = library.EnsureGroupPath("A")!;
        ConnectionGroup child = library.EnsureGroupPath("A / B")!;
        parent.ParentId = child.Id;

        Assert.Throws<InvalidOperationException>(() => library.UpdateGroup(parent));
    }

    [Fact]
    public void ConnectionWithoutCredentialsInheritsFromNearestGroup()
    {
        ConnectionGroup customer = library.EnsureGroupPath("Kunde A")!;
        customer.UserName = "admin";
        customer.Domain = "CORP";
        customer.Credential = new CredentialRef(source.Id, "group-a");
        ConnectionGroup site = library.EnsureGroupPath("Kunde A / Standort 1")!;
        var connection = new Connection { Name = "x", GroupId = site.Id };

        EffectiveCredential effective = credentials.Resolve(connection);

        Assert.Equal(("admin", "CORP"), (effective.UserName, effective.Domain));
        Assert.Equal(customer.Credential, effective.Secret);
        Assert.Same(customer, effective.InheritedFrom);

        // A nearer group with its own user wins.
        site.UserName = "local";
        Assert.Same(site, credentials.Resolve(connection).InheritedFrom);
        Assert.Null(credentials.Resolve(connection).Secret);
    }

    [Fact]
    public void OwnUserNameStopsInheritanceOfTheGroupPassword()
    {
        ConnectionGroup group = library.EnsureGroupPath("Kunde A")!;
        group.UserName = "admin";
        group.Credential = new CredentialRef(source.Id, "group-a");
        var connection = new Connection { Name = "x", GroupId = group.Id, UserName = "other" };

        EffectiveCredential effective = credentials.Resolve(connection);

        Assert.False(effective.IsInherited);
        Assert.Equal("other", effective.UserName);
        Assert.Null(effective.Secret);
    }

    [Fact]
    public async Task GroupCanTakeItsPasswordFromAPasswordManager()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        var vault = new MemoryCredentialSource("vault", writable: false) { Secrets = { ["entry"] = "from-vault" } };
        var service = new CredentialService([source, vault], library);
        ConnectionGroup group = library.EnsureGroupPath("Kunde A")!;
        group.UserName = "admin";
        await service.SaveGroupSecretAsync(group, "own", cancel);
        var connection = new Connection { Name = "x", GroupId = library.EnsureGroupPath("Kunde A / Standort 1")!.Id };

        // The vault replaces the app's own copy, which is deleted; the subgroup's connection inherits the vault entry.
        await service.UseGroupReferenceAsync(group, new CredentialRef(vault.Id, "entry"), cancel);
        Assert.Empty(source.Secrets);
        EffectiveCredential effective = service.Resolve(connection);
        Assert.Same(group, effective.InheritedFrom);
        Assert.Equal("from-vault", await service.ReadAsync(effective.Secret, cancel));

        // Removing drops the reference and never touches the read-only vault.
        await service.RemoveGroupSecretAsync(group, cancel);
        Assert.Null(group.Credential);
        Assert.Equal("from-vault", vault.Secrets["entry"]);

        await Assert.ThrowsAsync<ArgumentException>(() => service.UseGroupReferenceAsync(group, new CredentialRef(source.Id, "k"), cancel).AsTask());
    }

    [Fact]
    public void NoGroupAndNoCredentialsResolvesToNone()
    {
        Assert.Same(EffectiveCredential.None, credentials.Resolve(new Connection { Name = "x" }));
    }

    [Fact]
    public async Task OwnSecretIsWrittenToTheSourceAndReferenced()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        var connection = new Connection { Name = "x" };

        await credentials.SaveOwnSecretAsync(connection, "pw", cancel);
        Assert.Equal(new CredentialRef(source.Id, connection.Id), connection.Credential);
        Assert.Equal("pw", await credentials.ReadAsync(connection.Credential, cancel));

        await credentials.RemoveOwnSecretAsync(connection, cancel);
        Assert.Null(connection.Credential);
        Assert.Empty(source.Secrets);
        Assert.Null(await credentials.ReadAsync(new CredentialRef("unknown-source", "k"), cancel));
    }

    [Fact]
    public void ChangingTheTypeReplacesSettingsOfAnotherType()
    {
        var connection = new Connection { Name = "x", Settings = new RdpSettings { AdminSession = true } };

        connection.Type = ConnectionType.Ssh;

        Assert.IsType<SshSettings>(connection.Settings);
        Assert.Throws<ArgumentException>(() => connection.Settings = new RdpSettings());
    }

    [Theory]
    [InlineData(ConnectionType.Rdp, typeof(RdpSettings))]
    [InlineData(ConnectionType.TeamViewer, typeof(RemoteToolSettings))]
    [InlineData(ConnectionType.AnyDesk, typeof(RemoteToolSettings))]
    [InlineData(ConnectionType.RustDesk, typeof(RemoteToolSettings))]
    [InlineData(ConnectionType.Ssh, typeof(SshSettings))]
    [InlineData(ConnectionType.Sftp, typeof(SshSettings))]
    [InlineData(ConnectionType.Vnc, typeof(VncSettings))]
    public void EveryTypeHasSettingsThatSurviveSerialization(ConnectionType type, Type settingsType)
    {
        ConnectionSettings defaults = ConnectionSettings.CreateDefault(type);
        Assert.IsType(settingsType, defaults);

        string json = ConnectionSettings.Serialize(defaults);
        // No settings property may hold a secret (enum values like SshAuthMethod.Password are fine).
        Assert.DoesNotMatch(new Regex("\"[a-z]*(password|secret)[a-z]*\"\\s*:", RegexOptions.IgnoreCase), json);
        Assert.Equal(json, ConnectionSettings.Serialize(ConnectionSettings.Deserialize(type, json)));
    }

    [Fact]
    public void DuplicateGetsANewIdAndNoSecret()
    {
        var original = new Connection { Name = "x", Tags = ["a"], Credential = new CredentialRef("wincred", "x") };

        Connection copy = original.Duplicate("x (Kopie)");

        Assert.NotEqual(original.Id, copy.Id);
        Assert.Null(copy.Credential);
        Assert.Equal(original.Tags, copy.Tags);
        Assert.NotSame(original.Tags, copy.Tags);
    }
}
