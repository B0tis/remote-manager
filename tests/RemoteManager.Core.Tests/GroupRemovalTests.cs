using RemoteManager.Core.Library;
using RemoteManager.Core.Model;
using RemoteManager.Core.Storage;

namespace RemoteManager.Core.Tests;

/// <summary>Deleting a group (folder): empty, content moved up, content deleted, nested, persisted.</summary>
public sealed class GroupRemovalTests : IDisposable
{
    private readonly TempFolder folder = new();
    private readonly string databasePath;
    private readonly ConnectionLibrary library;

    public GroupRemovalTests()
    {
        databasePath = folder.File("a.db");
        library = new ConnectionLibrary(new SqliteConnectionStore(databasePath));
    }

    public void Dispose() => folder.Dispose();

    private ConnectionLibrary Reopened() => new(new SqliteConnectionStore(databasePath));

    private Connection Add(string name, ConnectionGroup? group)
    {
        var connection = new Connection { Name = name, Host = name + ".local", GroupId = group?.Id };
        library.Add(connection);
        return connection;
    }

    [Fact]
    public void AnEmptyGroupIsDeleted()
    {
        ConnectionGroup group = library.EnsureGroupPath("Leer")!;
        ConnectionGroup other = library.EnsureGroupPath("Bleibt")!;

        (IReadOnlyList<ConnectionGroup> groups, IReadOnlyList<Connection> connections) = library.RemoveGroup(group);

        Assert.Equal([group], groups);
        Assert.Empty(connections);
        Assert.Equal([other.Id], library.Groups.Select(g => g.Id));
        Assert.Equal([other.Id], Reopened().Groups.Select(g => g.Id));
    }

    [Fact]
    public void MovingContentUpPutsConnectionsOfATopLevelGroupAtTheTop()
    {
        ConnectionGroup group = library.EnsureGroupPath("Kunde")!;
        Connection a = Add("a", group), b = Add("b", group);

        library.RemoveGroup(group, GroupRemoval.MoveContentUp);

        Assert.Empty(library.Groups);
        Assert.All([a, b], c => Assert.Null(c.GroupId));
        ConnectionLibrary reopened = Reopened();
        Assert.Empty(reopened.Groups);
        Assert.Equal(2, reopened.Connections.Count);
        Assert.All(reopened.Connections, c => Assert.Null(c.GroupId));
    }

    [Fact]
    public void MovingContentUpKeepsNestedGroupsAndConnectionsUnderTheParent()
    {
        ConnectionGroup top = library.EnsureGroupPath("Kunde")!;
        ConnectionGroup site = library.EnsureGroupPath("Kunde / Standort")!;
        ConnectionGroup room = library.EnsureGroupPath("Kunde / Standort / Keller")!;
        Connection inSite = Add("dc01", site), inRoom = Add("nas", room), inTop = Add("fw", top);

        library.RemoveGroup(site);

        Assert.Equal(["Kunde", "Kunde / Keller"], library.Groups.Select(g => library.PathOf(g.Id)).Order());
        Assert.Equal(top.Id, inSite.GroupId);
        Assert.Equal(room.Id, inRoom.GroupId);
        Assert.Equal(top.Id, inTop.GroupId);
        ConnectionLibrary reopened = Reopened();
        Assert.Equal(["Kunde", "Kunde / Keller"], reopened.Groups.Select(g => reopened.PathOf(g.Id)).Order());
        Assert.Equal(top.Id, reopened.FindConnection(inSite.Id)!.GroupId);
        Assert.Equal(room.Id, reopened.FindConnection(inRoom.Id)!.GroupId);
    }

    [Fact]
    public void DeletingContentRemovesAllSubgroupsAndConnectionsBelow()
    {
        ConnectionGroup top = library.EnsureGroupPath("Kunde")!;
        ConnectionGroup site = library.EnsureGroupPath("Kunde / Standort")!;
        ConnectionGroup room = library.EnsureGroupPath("Kunde / Standort / Keller")!;
        ConnectionGroup other = library.EnsureGroupPath("Anderer Kunde")!;
        Connection inSite = Add("dc01", site), inRoom = Add("nas", room), inTop = Add("fw", top), elsewhere = Add("x", other), loose = Add("y", null);

        (IReadOnlyList<ConnectionGroup> groups, IReadOnlyList<Connection> connections) = library.RemoveGroup(site, GroupRemoval.DeleteContent);

        Assert.Equal([site, room], groups);
        Assert.Equal(new[] { inSite.Id, inRoom.Id }.Order(), connections.Select(c => c.Id).Order());
        Assert.Equal(["Anderer Kunde", "Kunde"], library.Groups.Select(g => g.Name).Order());
        Assert.Equal([inTop, elsewhere, loose], library.Connections.OrderBy(c => c.Name));
        ConnectionLibrary reopened = Reopened();
        Assert.Equal(["Anderer Kunde", "Kunde"], reopened.Groups.Select(g => g.Name).Order());
        Assert.Equal(["fw", "x", "y"], reopened.Connections.Select(c => c.Name).Order());
    }

    [Fact]
    public void ConnectionsUnderIncludesSubgroups()
    {
        ConnectionGroup top = library.EnsureGroupPath("A")!;
        ConnectionGroup child = library.EnsureGroupPath("A / B")!;
        Connection a = Add("a", top), b = Add("b", child);
        Add("c", null);

        Assert.Equal([a, b], library.ConnectionsUnder(top.Id).OrderBy(c => c.Name));
        Assert.Equal([b], library.ConnectionsUnder(child.Id));
    }

    [Fact]
    public void RemovingAGroupThatIsNotInTheLibraryChangesNothing()
    {
        ConnectionGroup kept = library.EnsureGroupPath("A")!;

        (IReadOnlyList<ConnectionGroup> groups, _) = library.RemoveGroup(new ConnectionGroup { Name = "fremd" }, GroupRemoval.DeleteContent);

        Assert.Empty(groups);
        Assert.Equal([kept], library.Groups);
    }
}
