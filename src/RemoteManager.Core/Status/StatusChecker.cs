using RemoteManager.Core.Model;

namespace RemoteManager.Core.Status;

public sealed class StatusChangedEventArgs(Connection connection, OnlineStatus status) : EventArgs
{
    public Connection Connection { get; } = connection;
    public OnlineStatus Status { get; } = status;
}

/// <summary>
/// Runs status probes off the UI thread: bounded parallelism and a hard timeout per check (a timeout
/// is <see cref="OnlineStatus.Unknown"/>). <see cref="StatusChanged"/> is raised on a thread-pool
/// thread; the UI marshals it to its own thread. The app decides when to check (start, timer, button).
/// </summary>
public sealed class StatusChecker : IDisposable
{
    private readonly Func<Connection, IStatusProbe?> probeFor;
    private readonly SemaphoreSlim slots;
    private readonly CancellationTokenSource lifetime = new();

    /// <param name="probeFor">The probe of a connection's type, or null when the type has none (status stays unknown).</param>
    public StatusChecker(Func<Connection, IStatusProbe?> probeFor, TimeSpan? timeout = null, int maxParallel = 16)
    {
        this.probeFor = probeFor;
        Timeout = timeout ?? TimeSpan.FromSeconds(2);
        slots = new SemaphoreSlim(maxParallel, maxParallel);
    }

    public TimeSpan Timeout { get; }

    public event EventHandler<StatusChangedEventArgs>? StatusChanged;

    /// <summary>Checks all connections in parallel and completes when every result is reported.</summary>
    public Task CheckAsync(IEnumerable<Connection> connections, CancellationToken cancellationToken = default)
    {
        Connection[] snapshot = [.. connections];
        // Task.Run: nothing of the probing (DNS included) may run on the caller's (UI) thread.
        return Task.Run(() => Task.WhenAll(snapshot.Select(c => CheckOneAsync(c, cancellationToken))), cancellationToken);
    }

    /// <summary>Stops reporting; checks in flight end quietly.</summary>
    public void Dispose() => lifetime.Cancel();

    private async Task CheckOneAsync(Connection connection, CancellationToken cancellationToken)
    {
        OnlineStatus status = OnlineStatus.Unknown;
        if (probeFor(connection) is { } probe)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            try
            {
                await slots.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                linked.CancelAfter(Timeout);
                Task<OnlineStatus> probing = probe.ProbeAsync(connection, linked.Token);
                // Belt and braces: a probe that ignores its token still cannot hold the check past the timeout.
                Task finished = await Task.WhenAny(probing, Task.Delay(Timeout + TimeSpan.FromMilliseconds(250), CancellationToken.None)).ConfigureAwait(false);
                status = finished == probing && probing.IsCompletedSuccessfully ? probing.Result : OnlineStatus.Unknown;
            }
            catch (Exception)
            {
                status = OnlineStatus.Unknown;
            }
            finally
            {
                slots.Release();
            }

            if (cancellationToken.IsCancellationRequested || lifetime.IsCancellationRequested)
                return;
        }

        StatusChanged?.Invoke(this, new StatusChangedEventArgs(connection, status));
    }
}
