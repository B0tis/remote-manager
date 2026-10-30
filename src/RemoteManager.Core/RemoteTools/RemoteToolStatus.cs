using System.Collections.Concurrent;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.Status;

namespace RemoteManager.Core.RemoteTools;

/// <summary>
/// Online states reported by a tool's API, by remote ID. Thread-safe: the sync writes it, the status
/// probes read it on the thread pool. In memory only; a status is never stored.
/// </summary>
public sealed class RemoteToolStatusCache
{
    private readonly ConcurrentDictionary<string, OnlineStatus> states = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When the cache was last filled, or null when never.</summary>
    public DateTime? UpdatedAt { get; private set; }

    public void Replace(IEnumerable<(string RemoteId, OnlineStatus Status)> entries, DateTime now)
    {
        states.Clear();
        foreach ((string id, OnlineStatus status) in entries)
            states[RemoteToolCommandLine.NormalizeId(id)] = status;
        UpdatedAt = now;
    }

    public OnlineStatus Get(string remoteId) =>
        states.TryGetValue(RemoteToolCommandLine.NormalizeId(remoteId), out OnlineStatus status) ? status : OnlineStatus.Unknown;
}

/// <summary>
/// Status of a remote-tool connection from <see cref="RemoteToolStatusCache"/>. When the cache is older
/// than <c>maxAge</c> it asks <c>refresh</c> once (concurrent probes share the call). The refresh keeps
/// running when a probe times out, so the next check has fresh data. Devices the source no longer lists
/// and connections never synced stay unknown.
/// </summary>
public sealed class CachedRemoteStatusProbe(RemoteToolStatusCache cache, Func<CancellationToken, Task>? refresh, TimeSpan maxAge, Func<DateTime>? clock = null) : IStatusProbe
{
    private readonly Func<DateTime> now = clock ?? (() => DateTime.UtcNow);
    private readonly Lock gate = new();
    private Task? refreshing;
    private DateTime? lastAttempt;

    public async Task<OnlineStatus> ProbeAsync(Connection connection, CancellationToken cancellationToken)
    {
        if (connection.Settings is RemoteToolSettings { RemovedAtSource: true })
            return OnlineStatus.Unknown;

        if (refresh is not null && (cache.UpdatedAt is not { } updated || now() - updated > maxAge))
        {
            Task pending;
            lock (gate)
            {
                // At most one call per maxAge, even when it fails: a bad token must not hammer the API.
                if ((refreshing is null || refreshing.IsCompleted) && (lastAttempt is not { } last || now() - last > maxAge))
                {
                    lastAttempt = now();
                    refreshing = Task.Run(() => refresh(CancellationToken.None), CancellationToken.None);
                }

                pending = refreshing ?? Task.CompletedTask;
            }

            try
            {
                await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Failed or slow refresh: answer from what is cached (possibly nothing).
            }
        }

        return cache.Get(connection.Host);
    }
}
