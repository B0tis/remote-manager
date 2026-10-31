using RemoteManager.Core.Library;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;

namespace RemoteManager.Core.RemoteTools;

/// <summary>Counts of one sync, for the summary in the UI.</summary>
public sealed record DeviceSyncReport(int Added, int Updated, int Unchanged, int MarkedRemoved);

/// <summary>
/// Merges the TeamViewer device list into the library. A device is matched by its device id, else by
/// the remote ID of a TeamViewer connection (a manually created one is adopted). The sync only changes
/// alias, the name while it still equals the old alias, and the sync markers; notes, tags, description,
/// group, favorite, and password stay local. New devices go to "&lt;newGroupRoot&gt; / &lt;TeamViewer group&gt;".
/// Devices no longer listed are marked <see cref="RemoteToolSettings.RemovedAtSource"/>, never deleted.
/// </summary>
public static class TeamViewerSync
{
    public static DeviceSyncReport Apply(ConnectionLibrary library, IReadOnlyList<TeamViewerDevice> devices,
        IReadOnlyList<TeamViewerGroup> groups, string newGroupRoot, DateTime now)
    {
        Dictionary<string, string> groupNames = groups.GroupBy(g => g.Id).ToDictionary(g => g.Key, g => g.First().Name);
        List<Connection> teamViewer = [.. library.Connections.Where(c => c.Type == ConnectionType.TeamViewer)];
        var seen = new HashSet<Connection>();
        int added = 0, updated = 0, unchanged = 0, removed = 0;

        foreach (TeamViewerDevice device in devices)
        {
            string remoteId = RemoteToolCommandLine.NormalizeId(device.RemoteId);
            Connection? match =
                (device.DeviceId.Length > 0 ? teamViewer.Find(c => !seen.Contains(c) && c.SettingsAs<RemoteToolSettings>().ExternalId == device.DeviceId) : null)
                ?? teamViewer.Find(c => !seen.Contains(c) && RemoteToolCommandLine.NormalizeId(c.Host) == remoteId);

            if (match is null)
            {
                string path = groupNames.TryGetValue(device.GroupId, out string? groupName) && groupName.Length > 0
                    ? $"{newGroupRoot}{ConnectionLibrary.PathSeparator}{groupName.Replace('/', '-')}"
                    : newGroupRoot;
                var connection = new Connection
                {
                    Name = device.Alias.Length > 0 ? device.Alias : remoteId,
                    Type = ConnectionType.TeamViewer,
                    Host = remoteId,
                    Description = device.Description,
                    GroupId = library.EnsureGroupPath(path)?.Id,
                };
                connection.Settings = new RemoteToolSettings
                {
                    Alias = device.Alias, Source = RemoteToolSource.TeamViewerApi, ExternalId = device.DeviceId, LastSyncedAt = now,
                };
                library.Add(connection);
                seen.Add(connection);
                added++;
                continue;
            }

            seen.Add(match);
            var old = match.SettingsAs<RemoteToolSettings>();
            bool changed = old.Alias != device.Alias || old.RemovedAtSource || old.Source != RemoteToolSource.TeamViewerApi
                || old.ExternalId != device.DeviceId || RemoteToolCommandLine.NormalizeId(match.Host) != remoteId;
            if (device.Alias.Length > 0 && match.Name != device.Alias && (match.Name == old.Alias || match.Name.Length == 0))
            {
                match.Name = device.Alias;
                changed = true;
            }

            // Same ID typed with blanks ("123 456 789") stays as the user wrote it.
            if (RemoteToolCommandLine.NormalizeId(match.Host) != remoteId)
                match.Host = remoteId;
            match.Settings = old with
            {
                Alias = device.Alias, Source = RemoteToolSource.TeamViewerApi, ExternalId = device.DeviceId,
                LastSyncedAt = now, RemovedAtSource = false,
            };
            if (changed)
            {
                match.UpdatedAt = now;
                updated++;
            }
            else
            {
                unchanged++;
            }

            library.Update(match);
        }

        foreach (Connection gone in teamViewer.Where(c => !seen.Contains(c)))
        {
            var settings = gone.SettingsAs<RemoteToolSettings>();
            if (settings.Source != RemoteToolSource.TeamViewerApi || settings.RemovedAtSource)
                continue;
            gone.Settings = settings with { RemovedAtSource = true, LastSyncedAt = now };
            gone.UpdatedAt = now;
            library.Update(gone);
            removed++;
        }

        return new DeviceSyncReport(added, updated, unchanged, removed);
    }
}
