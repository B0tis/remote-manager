using System.Net;
using System.Net.Sockets;
using RemoteManager.Core.Model;
using RemoteManager.Core.Status;

namespace RemoteManager.Core.Tests;

/// <summary>
/// The answer time of a status check (<see cref="StatusChangedEventArgs.Latency"/>): only an online result of a
/// probe that measures it carries one, because only then the elapsed time says something about the target.
/// </summary>
public sealed class StatusLatencyTests
{
    [Fact]
    public async Task OnlineResultOfAMeasuringProbeCarriesTheAnswerTime()
    {
        TimeSpan wait = TimeSpan.FromMilliseconds(120);
        StatusChangedEventArgs result = await Check(new FixedProbe(OnlineStatus.Online, measures: true, wait));

        Assert.Equal(OnlineStatus.Online, result.Status);
        Assert.NotNull(result.Latency);
        // The time covers the probe itself (timers may fire a little early) and stays far below the timeout.
        Assert.InRange(result.Latency.Value, wait - TimeSpan.FromMilliseconds(40), TimeSpan.FromSeconds(4));
    }

    [Theory]
    [InlineData(OnlineStatus.Offline)]
    [InlineData(OnlineStatus.Unknown)]
    public async Task ResultWithoutAnAnswerCarriesNoAnswerTime(OnlineStatus status)
    {
        StatusChangedEventArgs result = await Check(new FixedProbe(status, measures: true));

        Assert.Equal(status, result.Status);
        Assert.Null(result.Latency);
    }

    [Fact]
    public async Task ProbeThatDoesNotMeasureCarriesNoAnswerTime()
    {
        StatusChangedEventArgs result = await Check(new FixedProbe(OnlineStatus.Online, measures: false));

        Assert.Equal(OnlineStatus.Online, result.Status);
        Assert.Null(result.Latency);
    }

    [Fact]
    public async Task ProbesDoNotMeasureUnlessTheySaySo()
    {
        // The interface default: a probe that reads a cache or asks a service must opt in explicitly.
        IStatusProbe plain = new PlainProbe();
        Assert.False(plain.MeasuresLatency);
        Assert.True(((IStatusProbe)TcpPortProbe.Instance).MeasuresLatency);

        StatusChangedEventArgs result = await Check(plain);

        Assert.Equal(OnlineStatus.Online, result.Status);
        Assert.Null(result.Latency);
    }

    [Fact]
    public async Task TimeoutAndFailingProbeCarryNoAnswerTime()
    {
        StatusChangedEventArgs timedOut = await Check(new FixedProbe(OnlineStatus.Online, measures: true, TimeSpan.FromSeconds(30)), TimeSpan.FromMilliseconds(200));
        Assert.Equal(OnlineStatus.Unknown, timedOut.Status);
        Assert.Null(timedOut.Latency);

        StatusChangedEventArgs failed = await Check(new ThrowingProbe());
        Assert.Equal(OnlineStatus.Unknown, failed.Status);
        Assert.Null(failed.Latency);
    }

    [Fact]
    public async Task TypeWithoutProbeCarriesNoAnswerTime()
    {
        var checker = new StatusChecker(_ => null);
        StatusChangedEventArgs? result = null;
        checker.StatusChanged += (_, e) => result = e;

        await checker.CheckAsync([new Connection { Type = ConnectionType.TeamViewer, Host = "123" }], TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(OnlineStatus.Unknown, result.Status);
        Assert.Null(result.Latency);
    }

    [Fact]
    public async Task EveryConnectionGetsItsOwnAnswerTime()
    {
        var slow = new Connection { Name = "slow", Host = "slow" };
        var fast = new Connection { Name = "fast", Host = "fast" };
        var down = new Connection { Name = "down", Host = "down" };
        var checker = new StatusChecker(c => c == down
            ? new FixedProbe(OnlineStatus.Offline, measures: true)
            : new FixedProbe(OnlineStatus.Online, measures: true, c == slow ? TimeSpan.FromMilliseconds(400) : TimeSpan.Zero));
        var results = new Dictionary<Connection, StatusChangedEventArgs>();
        checker.StatusChanged += (_, e) =>
        {
            lock (results)
                results[e.Connection] = e;
        };

        await checker.CheckAsync([slow, fast, down], TestContext.Current.CancellationToken);

        Assert.Equal(3, results.Count);
        Assert.NotNull(results[slow].Latency);
        Assert.NotNull(results[fast].Latency);
        Assert.Null(results[down].Latency);
        Assert.True(results[slow].Latency > results[fast].Latency, $"slow {results[slow].Latency}, fast {results[fast].Latency}");
    }

    [Fact]
    public async Task ListeningPortIsOnlineWithASmallAnswerTime()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            StatusChangedEventArgs result = await Check(TcpPortProbe.Instance, TimeSpan.FromSeconds(5), new Connection { Host = "127.0.0.1", Port = port });

            Assert.Equal(OnlineStatus.Online, result.Status);
            Assert.NotNull(result.Latency);
            // A loopback connect takes well under a millisecond; the bound only leaves room for a busy test machine.
            Assert.InRange(result.Latency.Value, TimeSpan.FromTicks(1), TimeSpan.FromSeconds(2));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task RefusedPortIsOfflineWithoutAnAnswerTime()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        StatusChangedEventArgs result = await Check(TcpPortProbe.Instance, TimeSpan.FromSeconds(5), new Connection { Host = "127.0.0.1", Port = port });

        Assert.Equal(OnlineStatus.Offline, result.Status);
        Assert.Null(result.Latency);
    }

    [Fact]
    public async Task UnresolvableHostIsUnknownWithoutAnAnswerTime()
    {
        StatusChangedEventArgs result = await Check(TcpPortProbe.Instance, TimeSpan.FromSeconds(5), new Connection { Host = "rm-test-latency.invalid" });

        Assert.Equal(OnlineStatus.Unknown, result.Status);
        Assert.Null(result.Latency);
    }

    private static async Task<StatusChangedEventArgs> Check(IStatusProbe probe, TimeSpan? timeout = null, Connection? connection = null)
    {
        connection ??= new Connection { Host = "x" };
        var checker = new StatusChecker(_ => probe, timeout ?? TimeSpan.FromSeconds(5));
        StatusChangedEventArgs? result = null;
        checker.StatusChanged += (_, e) => result = e;
        await checker.CheckAsync([connection], TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Same(connection, result.Connection);
        return result;
    }

    /// <summary>Answers with a fixed status after <paramref name="wait"/>.</summary>
    private sealed class FixedProbe(OnlineStatus status, bool measures, TimeSpan wait = default) : IStatusProbe
    {
        public bool MeasuresLatency => measures;

        public async Task<OnlineStatus> ProbeAsync(Connection connection, CancellationToken cancellationToken)
        {
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, cancellationToken);
            return status;
        }
    }

    /// <summary>Does not say whether it measures: the interface default applies.</summary>
    private sealed class PlainProbe : IStatusProbe
    {
        public Task<OnlineStatus> ProbeAsync(Connection connection, CancellationToken cancellationToken) =>
            Task.FromResult(OnlineStatus.Online);
    }

    private sealed class ThrowingProbe : IStatusProbe
    {
        public bool MeasuresLatency => true;

        public Task<OnlineStatus> ProbeAsync(Connection connection, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("probe failed");
    }
}
