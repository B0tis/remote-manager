using RemoteManager.Core.Model;

namespace RemoteManager.Core.Library;

/// <summary>Order and placement of groups and connections (drag and drop, group management).</summary>
public sealed partial class ConnectionLibrary
{
    /// <summary>Connections directly in the group (null: ungrouped), in their saved order.</summary>
    public IEnumerable<Connection> ConnectionsIn(string? groupId) =>
        connections.Where(c => c.GroupId == groupId).OrderBy(c => c.SortOrder).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase);

    /// <summary>All groups below <paramref name="groupId"/>, depth-first in their saved order.</summary>
    public IEnumerable<ConnectionGroup> DescendantsOf(string? groupId)
    {
        foreach (ConnectionGroup child in ChildrenOf(groupId).ToList())
        {
            yield return child;
            foreach (ConnectionGroup grandchild in DescendantsOf(child.Id))
                yield return grandchild;
        }
    }

    /// <summary>True when <paramref name="group"/> may be placed under <paramref name="parentId"/> (not itself, not one of its subgroups).</summary>
    public bool CanMoveGroup(ConnectionGroup group, string? parentId) =>
        parentId is null || !AncestorsOf(parentId).Contains(group);

    /// <summary>
    /// Puts <paramref name="moving"/> into <paramref name="groupId"/> (null: ungrouped), in their given
    /// order, before <paramref name="before"/> or at the end. The group's order is renumbered and saved.
    /// </summary>
    public void MoveConnections(IReadOnlyList<Connection> moving, string? groupId, Connection? before = null)
    {
        if (groupId is not null && FindGroup(groupId) is null)
            throw new InvalidOperationException($"Group {groupId} does not exist.");
        if (moving.Any(c => !Contains(c)))
            throw new InvalidOperationException("Only connections in the library can be moved.");

        List<Connection> order = [.. ConnectionsIn(groupId).Where(c => !moving.Contains(c))];
        int index = before is not null && order.IndexOf(before) is var at and >= 0 ? at : order.Count;
        order.InsertRange(index, moving);
        for (int i = 0; i < order.Count; i++)
        {
            Connection c = order[i];
            if (c.SortOrder == i && c.GroupId == groupId)
                continue;
            c.SortOrder = i;
            c.GroupId = groupId;
            c.UpdatedAt = DateTime.UtcNow;
            store.SaveConnection(c);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Puts <paramref name="group"/> under <paramref name="parentId"/> (null: top level), before
    /// <paramref name="before"/> or at the end, and renumbers its new siblings.
    /// </summary>
    public void MoveGroup(ConnectionGroup group, string? parentId, ConnectionGroup? before = null)
    {
        if (!groups.Contains(group))
            throw new InvalidOperationException($"Group {group.Id} is not in the library.");
        if (parentId is not null && FindGroup(parentId) is null)
            throw new InvalidOperationException($"Group {parentId} does not exist.");
        if (!CanMoveGroup(group, parentId))
            throw new InvalidOperationException("A group cannot be moved into itself.");

        List<ConnectionGroup> order = [.. ChildrenOf(parentId).Where(g => g != group)];
        int index = before is not null && order.IndexOf(before) is var at and >= 0 ? at : order.Count;
        order.Insert(index, group);
        for (int i = 0; i < order.Count; i++)
        {
            ConnectionGroup g = order[i];
            if (g.SortOrder == i && g.ParentId == parentId)
                continue;
            g.SortOrder = i;
            g.ParentId = parentId;
            g.UpdatedAt = DateTime.UtcNow;
            store.SaveGroup(g);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>True when another group under the same parent already has <paramref name="name"/> (paths must stay unique).</summary>
    public bool HasSiblingNamed(string? parentId, string name, ConnectionGroup? except = null) =>
        groups.Any(g => g != except && g.ParentId == parentId && string.Equals(g.Name, name.Trim(), StringComparison.CurrentCultureIgnoreCase));
}
