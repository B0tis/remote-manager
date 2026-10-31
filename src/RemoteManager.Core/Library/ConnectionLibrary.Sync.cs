using RemoteManager.Core.Model;

namespace RemoteManager.Core.Library;

/// <summary>Changes that come from another device (<see cref="Sync.SyncEngine"/>).</summary>
public sealed partial class ConnectionLibrary
{
    /// <summary>
    /// Saves changed and new groups and connections and removes deleted ones, in place: objects already in the
    /// library stay the same instances (open windows keep a live one). Afterwards references that point nowhere
    /// (a connection in a group deleted elsewhere) go to the top level and group cycles from parallel moves are
    /// broken. Returns how many records had to be repaired that way; they count as local changes.
    /// </summary>
    public int ApplyExternalChanges(
        IReadOnlyList<ConnectionGroup> savedGroups, IReadOnlyList<Connection> savedConnections,
        IReadOnlyCollection<string> removedGroupIds, IReadOnlyCollection<string> removedConnectionIds)
    {
        foreach (ConnectionGroup group in savedGroups.Where(g => !groups.Contains(g)))
            groups.Add(group);
        foreach (Connection connection in savedConnections.Where(c => !Contains(c)))
            connections.Add(connection);
        groups.RemoveAll(g => removedGroupIds.Contains(g.Id));
        connections.RemoveAll(c => removedConnectionIds.Contains(c.Id));

        var repairedGroups = new List<ConnectionGroup>();
        var repairedConnections = new List<Connection>();
        foreach (ConnectionGroup group in groups.Where(g => g.ParentId is not null && (FindGroup(g.ParentId) is null || IsInCycle(g))))
        {
            group.ParentId = null;
            repairedGroups.Add(group);
        }

        foreach (Connection connection in connections.Where(c => c.GroupId is not null && FindGroup(c.GroupId) is null))
        {
            connection.GroupId = null;
            repairedConnections.Add(connection);
        }

        store.SaveAll(savedGroups.Union(repairedGroups), savedConnections.Union(repairedConnections));
        foreach (string id in removedConnectionIds)
            store.DeleteConnection(id);
        foreach (string id in removedGroupIds)
            store.DeleteGroup(id);
        Changed?.Invoke(this, EventArgs.Empty);
        return repairedGroups.Count + repairedConnections.Count;
    }

    /// <summary>True when following the parents from <paramref name="group"/> comes back to it.</summary>
    private bool IsInCycle(ConnectionGroup group)
    {
        var seen = new HashSet<string>();
        for (ConnectionGroup? current = FindGroup(group.ParentId); current is not null; current = FindGroup(current.ParentId))
        {
            if (current == group)
                return true;
            if (!seen.Add(current.Id))
                return false; // a cycle further up; it is broken at one of its own members
        }

        return false;
    }
}
