using System.Globalization;
using RemoteManager.Core.Credentials;
using RemoteManager.Core.Library;
using RemoteManager.Core.Model;
using RemoteManager.Core.Resources;

namespace RemoteManager.Core.ImportExport;

/// <summary>What an import changed, for the summary dialog.</summary>
public sealed record ImportSummary(
    int AddedConnections, int ReplacedConnections, int SkippedDuplicates, int AddedGroups, int StoredPasswords,
    IReadOnlyList<string> Warnings, IReadOnlyList<Connection> Connections);

/// <summary>
/// Merges an <see cref="ImportResult"/> into the library. Groups with the same name under the same
/// parent are reused, so importing a file twice does not double the tree; a connection whose id already
/// exists (backup restore) is replaced; a connection equal to an existing one in the same group (type,
/// name, host, port) is skipped. Passwords go to the credential service's default source (the
/// Credential Manager), never into the library.
/// </summary>
public sealed class ImportService(ConnectionLibrary library, CredentialService credentials)
{
    /// <param name="targetGroup">Group path to import under ("Import / RDCMan"), or null for the top level.</param>
    /// <remarks>
    /// Changes go through <see cref="ConnectionLibrary.Add"/>/<see cref="ConnectionLibrary.Update"/> one by one, so
    /// objects the UI already holds stay the library's (a replaced connection is updated in place). Call it on
    /// the thread that owns the library; it does not switch threads.
    /// </remarks>
    public async Task<ImportSummary> ApplyAsync(ImportResult result, string? targetGroup = null, CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>(result.Warnings);
        ConnectionGroup? target = library.EnsureGroupPath(targetGroup);

        // Map imported group ids onto library groups: same id (backup), or same name under the same parent.
        var groupIds = new Dictionary<string, string>();
        var pending = new List<ConnectionGroup>(result.Groups);
        int addedGroups = 0, passwords = 0;
        while (pending.Count > 0)
        {
            // Parents first: a group is ready when its parent is mapped or not part of the import.
            ConnectionGroup? group = pending.FirstOrDefault(g => g.ParentId is null || groupIds.ContainsKey(g.ParentId) || pending.All(p => p.Id != g.ParentId));
            if (group is null)
                break;
            pending.Remove(group);

            group.ParentId = group.ParentId is { } p && groupIds.TryGetValue(p, out string? mappedParent) ? mappedParent
                : group.ParentId is not null && library.FindGroup(group.ParentId) is not null ? group.ParentId
                : target?.Id;
            ConnectionGroup? existing = library.FindGroup(group.Id);
            if (existing is not null)
            {
                // Backup restore: the stored group takes the backup's values.
                CopyInto(existing, group);
                library.UpdateGroup(existing);
            }
            else if (library.Groups.FirstOrDefault(g => g.ParentId == group.ParentId && string.Equals(g.Name, group.Name, StringComparison.CurrentCultureIgnoreCase)) is { } sameName)
            {
                existing = sameName;
                // An existing group keeps its logon; an imported logon only fills an empty one.
                if (existing.UserName.Length == 0 && existing.Credential is null && group.UserName.Length > 0)
                {
                    (existing.UserName, existing.Domain) = (group.UserName, group.Domain);
                    library.UpdateGroup(existing);
                }
                else if (result.GroupSecrets.Remove(group.Id))
                {
                    warnings.Add(string.Format(CultureInfo.CurrentCulture, ImportStrings.Import_GroupLogonKept, existing.Name));
                }
            }
            else
            {
                existing = group;
                group.SortOrder = library.ChildrenOf(group.ParentId).Count();
                library.AddGroup(group);
                addedGroups++;
            }

            groupIds[group.Id] = existing.Id;
            if (result.GroupSecrets.TryGetValue(group.Id, out string? groupSecret)
                && await StoreAsync(() => credentials.SaveGroupSecretAsync(existing, groupSecret, cancellationToken), existing.Name, warnings))
            {
                passwords++;
                library.UpdateGroup(existing);
            }
        }

        foreach (ConnectionGroup orphan in pending)
            warnings.Add(string.Format(CultureInfo.CurrentCulture, ImportStrings.Import_GroupCycle, orphan.Name));

        int added = 0, replaced = 0, skipped = 0;
        var imported = new List<Connection>();
        foreach (Connection connection in result.Connections)
        {
            connection.GroupId = connection.GroupId is { } g && groupIds.TryGetValue(g, out string? mapped) ? mapped
                : connection.GroupId is not null && library.FindGroup(connection.GroupId) is not null ? connection.GroupId
                : target?.Id;
            connection.UpdatedAt = DateTime.UtcNow;

            Connection? existing = library.FindConnection(connection.Id);
            if (existing is null && library.Connections.Any(c => IsSame(c, connection)))
            {
                skipped++;
                continue;
            }

            if (result.Secrets.TryGetValue(connection.Id, out string? secret) && secret.Length > 0
                && await StoreAsync(() => credentials.SaveOwnSecretAsync(connection, secret, cancellationToken), connection.Name, warnings))
            {
                passwords++;
            }

            if (existing is not null)
            {
                CopyInto(existing, connection);
                library.Update(existing);
                imported.Add(existing);
                replaced++;
            }
            else
            {
                library.Add(connection);
                imported.Add(connection);
                added++;
            }
        }

        result.Secrets.Clear();
        result.GroupSecrets.Clear();
        return new ImportSummary(added, replaced, skipped, addedGroups, passwords, warnings, imported);
    }

    private static void CopyInto(ConnectionGroup target, ConnectionGroup source)
    {
        target.Name = source.Name;
        target.ParentId = source.ParentId;
        target.Description = source.Description;
        target.SortOrder = source.SortOrder;
        target.IsCollapsed = source.IsCollapsed;
        target.UserName = source.UserName;
        target.Domain = source.Domain;
        target.Credential = source.Credential;
        target.UpdatedAt = DateTime.UtcNow;
    }

    private static void CopyInto(Connection target, Connection source)
    {
        target.Name = source.Name;
        target.Type = source.Type;
        target.Settings = source.Settings;
        target.Host = source.Host;
        target.Port = source.Port;
        target.Description = source.Description;
        target.Notes = source.Notes;
        target.Tags = [.. source.Tags];
        target.IsFavorite = source.IsFavorite;
        target.GroupId = source.GroupId;
        target.SortOrder = source.SortOrder;
        target.LastConnectedAt = source.LastConnectedAt;
        target.PreviewPath = source.PreviewPath;
        target.UserName = source.UserName;
        target.Domain = source.Domain;
        target.Credential = source.Credential;
        target.UpdatedAt = source.UpdatedAt;
    }

    private static bool IsSame(Connection a, Connection b) =>
        a.Type == b.Type && a.GroupId == b.GroupId && a.EffectivePort == b.EffectivePort
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);

    private static async Task<bool> StoreAsync(Func<ValueTask> write, string label, List<string> warnings)
    {
        try
        {
            await write();
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            warnings.Add(string.Format(CultureInfo.CurrentCulture, ImportStrings.Import_PasswordNotStored, label, ex.Message));
            return false;
        }
    }
}
