using RemoteManager.Core.Model;

namespace RemoteManager.Core.Storage;

/// <summary>Persistence of groups and connections. <see cref="SqliteConnectionStore"/> is the only implementation.</summary>
public interface IConnectionStore
{
    IReadOnlyList<ConnectionGroup> LoadGroups();
    IReadOnlyList<Connection> LoadConnections();

    void SaveGroup(ConnectionGroup group);
    void DeleteGroup(string groupId);

    void SaveConnection(Connection connection);
    void DeleteConnection(string connectionId);

    /// <summary>Writes many groups and connections in one transaction (import, migration).</summary>
    void SaveAll(IEnumerable<ConnectionGroup> groups, IEnumerable<Connection> connections);

    string? GetSetting(string key);
    void SetSetting(string key, string? value);
}
