using System.Diagnostics;
using RemoteManager.App.Resources;
using RemoteManager.Core.RemoteTools;

namespace RemoteManager.App.Connections.RemoteTools;

/// <summary>
/// The TeamViewer Web API connection of the app: script token in the default credential source (Windows
/// Credential Manager, never in the database), online-state cache for the status probe, and the device sync.
/// </summary>
public static class TeamViewerAccount
{
    private static readonly HttpClient DefaultHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Online states by TeamViewer ID; filled by every device fetch.</summary>
    public static RemoteToolStatusCache StatusCache { get; } = new();

    /// <summary>Probe of TeamViewer connections: cached API state, refreshed at most every 90 s while a token is stored.</summary>
    public static CachedRemoteStatusProbe StatusProbe { get; } = new(StatusCache, FetchForStatusAsync, TimeSpan.FromSeconds(90));

    /// <summary>Credential key of the token. The self-test points it at a "selftest-" key.</summary>
    internal static string TokenKey { get; set; } = "teamviewer-api-token";

    /// <summary>The self-test replaces the HTTP client with a fake server.</summary>
    internal static TeamViewerApiClient Client { get; set; } = new(DefaultHttp);

    public static async Task<bool> HasTokenAsync() =>
        await AppHost.Current.Credentials.DefaultSource.ExistsAsync(TokenKey).ConfigureAwait(false);

    public static async Task SaveTokenAsync(string token) =>
        await AppHost.Current.Credentials.DefaultSource.WriteAsync(TokenKey, token.Trim()).ConfigureAwait(false);

    public static async Task DeleteTokenAsync()
    {
        await AppHost.Current.Credentials.DefaultSource.DeleteAsync(TokenKey).ConfigureAwait(false);
        StatusCache.Replace([], DateTime.UtcNow);
    }

    /// <summary>
    /// Fetches devices and groups and merges them into the library. Call on the UI thread (the library
    /// lives there); the network part runs on the thread pool. Throws <see cref="TeamViewerApiException"/>.
    /// </summary>
    public static async Task<DeviceSyncReport> SyncAsync(CancellationToken cancellationToken = default)
    {
        string token = await ReadTokenAsync().ConfigureAwait(true)
            ?? throw new TeamViewerApiException(TeamViewerApiError.Unauthorized, "No token stored");
        (IReadOnlyList<TeamViewerDevice> devices, IReadOnlyList<TeamViewerGroup> groups) = await Task.Run(async () =>
        {
            IReadOnlyList<TeamViewerDevice> d = await Client.GetDevicesAsync(token, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<TeamViewerGroup> g = await Client.GetGroupsAsync(token, cancellationToken).ConfigureAwait(false);
            return (d, g);
        }, cancellationToken).ConfigureAwait(true);

        StatusCache.Replace(devices.Select(d => (d.RemoteId, d.Online)), DateTime.UtcNow);
        DeviceSyncReport report = TeamViewerSync.Apply(AppHost.Current.Library, devices, groups, Strings.RemoteTool_Sync_NewGroup, DateTime.UtcNow);
        Trace.WriteLine($"TeamViewer sync: {devices.Count} devices, added {report.Added}, updated {report.Updated}, unchanged {report.Unchanged}, marked removed {report.MarkedRemoved}");
        return report;
    }

    /// <summary>German message for a failed sync.</summary>
    public static string Describe(TeamViewerApiException ex) => ex.Error switch
    {
        TeamViewerApiError.Unauthorized => Strings.RemoteTool_Sync_Error_Unauthorized,
        TeamViewerApiError.Forbidden => Strings.RemoteTool_Sync_Error_Forbidden,
        _ => string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.RemoteTool_Sync_Error_Other, ex.Message),
    };

    private static async Task<string?> ReadTokenAsync() =>
        await AppHost.Current.Credentials.DefaultSource.ReadAsync(TokenKey).ConfigureAwait(false);

    /// <summary>Background refresh for the status probe: only the cache, the library is left alone.</summary>
    private static async Task FetchForStatusAsync(CancellationToken cancellationToken)
    {
        if (await ReadTokenAsync().ConfigureAwait(false) is not { Length: > 0 } token)
            return;
        try
        {
            IReadOnlyList<TeamViewerDevice> devices = await Client.GetDevicesAsync(token, cancellationToken).ConfigureAwait(false);
            StatusCache.Replace(devices.Select(d => (d.RemoteId, d.Online)), DateTime.UtcNow);
        }
        catch (TeamViewerApiException ex)
        {
            Trace.WriteLine($"TeamViewer status refresh failed: {ex.Error} {ex.Message}");
        }
    }
}
