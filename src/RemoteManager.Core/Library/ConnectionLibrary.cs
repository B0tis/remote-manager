using RemoteManager.Core.Model;
using RemoteManager.Core.Storage;

namespace RemoteManager.Core.Library;

/// <summary>
/// The groups and connections in memory, written through to the <see cref="IConnectionStore"/> on
/// every change. Not thread-safe: use it from the UI thread.
/// </summary>
public sealed class ConnectionLibrary
{
    /// <summary>Separator of group paths as shown and typed in the UI ("Kunde A / Standort 1").</summary>
    public const string PathSeparator = " / ";

    private readonly IConnectionStore store;
    private readonly List<Connection> connections = [];
    private readonly List<ConnectionGroup> groups = [];

    public ConnectionLibrary(IConnectionStore store)
    {
        this.store = store;
        Reload();
    }

    public IConnectionStore Store => store;
    public IReadOnlyList<Connection> Connections => connections;
    public IReadOnlyList<ConnectionGroup> Groups => groups;

    /// <summary>Raised after any change made through the library.</summary>
    public event EventHandler? Changed;

    public void Reload()
    {
        groups.Clear();
        groups.AddRange(store.LoadGroups());
        connections.Clear();
        connections.AddRange(store.LoadConnections());
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Connection? FindConnection(string id) => connections.Find(c => c.Id == id);
    public ConnectionGroup? FindGroup(string? id) => id is null ? null : groups.Find(g => g.Id == id);
    public bool Contains(Connection connection) => connections.Contains(connection);

    public void Add(Connection connection)
    {
        if (Contains(connection))
            throw new InvalidOperationException($"Connection {connection.Id} is already in the library.");
        connections.Add(connection);
        Persist(connection);
    }

    /// <summary>Saves a changed connection. Returns false (and saves nothing) when it is not in the library.</summary>
    public bool Update(Connection connection)
    {
        if (!Contains(connection))
            return false;
        Persist(connection);
        return true;
    }

    public void Remove(Connection connection)
    {
        connections.Remove(connection);
        store.DeleteConnection(connection.Id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void AddGroup(ConnectionGroup group)
    {
        if (group.ParentId is { } parent && FindGroup(parent) is null)
            throw new InvalidOperationException($"Parent group {parent} does not exist.");
        groups.Add(group);
        Persist(group);
    }

    public bool UpdateGroup(ConnectionGroup group)
    {
        if (!groups.Contains(group))
            return false;
        if (group.ParentId is not null && AncestorsOf(group.ParentId).Contains(group))
            throw new InvalidOperationException("A group cannot be moved into itself.");
        Persist(group);
        return true;
    }

    /// <summary>Removes a group; its subgroups and connections move up to its parent.</summary>
    public void RemoveGroup(ConnectionGroup group)
    {
        foreach (ConnectionGroup child in groups.Where(g => g.ParentId == group.Id).ToList())
        {
            child.ParentId = group.ParentId;
            store.SaveGroup(child);
        }

        foreach (Connection connection in connections.Where(c => c.GroupId == group.Id).ToList())
        {
            connection.GroupId = group.ParentId;
            store.SaveConnection(connection);
        }

        groups.Remove(group);
        store.DeleteGroup(group.Id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adds many groups and connections at once (import); existing ids are replaced.</summary>
    public void AddRange(IReadOnlyList<ConnectionGroup> newGroups, IReadOnlyList<Connection> newConnections)
    {
        store.SaveAll(newGroups, newConnections);
        Reload();
    }

    /// <summary>The group and its ancestors, nearest first. Empty for null or an unknown id.</summary>
    public IEnumerable<ConnectionGroup> AncestorsOf(string? groupId)
    {
        var seen = new HashSet<string>();
        for (ConnectionGroup? group = FindGroup(groupId); group is not null && seen.Add(group.Id); group = FindGroup(group.ParentId))
            yield return group;
    }

    public IEnumerable<ConnectionGroup> ChildrenOf(string? parentId) =>
        groups.Where(g => g.ParentId == parentId).OrderBy(g => g.SortOrder).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase);

    /// <summary>True when the connection is in the group or one of its subgroups.</summary>
    public bool IsInGroup(Connection connection, string groupId) => AncestorsOf(connection.GroupId).Any(g => g.Id == groupId);

    /// <summary>"Parent / Child", or "" for no group.</summary>
    public string PathOf(string? groupId) =>
        string.Join(PathSeparator, AncestorsOf(groupId).Reverse().Select(g => g.Name));

    /// <summary>All group paths, sorted (for pickers).</summary>
    public IEnumerable<string> GroupPaths() => groups.Select(g => PathOf(g.Id)).Order(StringComparer.CurrentCultureIgnoreCase);

    /// <summary>
    /// The group at <paramref name="path"/> ("A / B", "/" also accepted), created with any missing
    /// parents. Null for an empty path.
    /// </summary>
    public ConnectionGroup? EnsureGroupPath(string? path)
    {
        string[] names = (path ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        ConnectionGroup? current = null;
        foreach (string name in names)
        {
            ConnectionGroup? next = groups.Find(g => g.ParentId == current?.Id && string.Equals(g.Name, name, StringComparison.CurrentCultureIgnoreCase));
            if (next is null)
            {
                next = new ConnectionGroup { Name = name, ParentId = current?.Id, SortOrder = ChildrenOf(current?.Id).Count() };
                AddGroup(next);
            }

            current = next;
        }

        return current;
    }

    public string? GetSetting(string key) => store.GetSetting(key);
    public void SetSetting(string key, string? value) => store.SetSetting(key, value);

    private void Persist(Connection connection)
    {
        connection.UpdatedAt = DateTime.UtcNow;
        store.SaveConnection(connection);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Persist(ConnectionGroup group)
    {
        group.UpdatedAt = DateTime.UtcNow;
        store.SaveGroup(group);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
