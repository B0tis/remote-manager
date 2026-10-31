using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RemoteManager.Core.Credentials;
using RemoteManager.Core.Extensibility;
using RemoteManager.Core.ImportExport;
using RemoteManager.Core.Model;
using RemoteManager.Core.Status;

namespace RemoteManager.Core.Tests;

public sealed class StatusCheckerTests
{
    [Fact]
    public async Task ListeningPortIsOnline()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Assert.Equal(OnlineStatus.Online, await Probe(new Connection { Host = "127.0.0.1", Port = port }));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task RefusedPortIsOffline()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        Assert.Equal(OnlineStatus.Offline, await Probe(new Connection { Host = "127.0.0.1", Port = port }));
    }

    [Fact]
    public async Task UnresolvableHostIsUnknown()
    {
        Assert.Equal(OnlineStatus.Unknown, await Probe(new Connection { Host = "rm-test-host.invalid" }));
    }

    [Fact]
    public async Task HangingProbeTimesOutAsUnknown()
    {
        var checker = new StatusChecker(_ => new HangingProbe(), TimeSpan.FromMilliseconds(200));
        var results = new ConcurrentBag<OnlineStatus>();
        checker.StatusChanged += (_, e) => results.Add(e.Status);

        var watch = Stopwatch.StartNew();
        await checker.CheckAsync([new Connection { Host = "x" }, new Connection { Host = "y" }], TestContext.Current.CancellationToken);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
        Assert.Equal([OnlineStatus.Unknown, OnlineStatus.Unknown], results);
    }

    [Fact]
    public async Task TypeWithoutProbeIsUnknown()
    {
        var checker = new StatusChecker(_ => null);
        OnlineStatus? result = null;
        checker.StatusChanged += (_, e) => result = e.Status;

        await checker.CheckAsync([new Connection { Type = ConnectionType.TeamViewer, Host = "123" }], TestContext.Current.CancellationToken);

        Assert.Equal(OnlineStatus.Unknown, result);
    }

    [Fact]
    public async Task CheckDoesNotRunOnTheCallingThread()
    {
        int? probeThread = null;
        var checker = new StatusChecker(_ => new DelegateProbe(() => probeThread = Environment.CurrentManagedThreadId));

        // Call from a dedicated (non-pool) thread, like the UI thread: the test's own thread is a pool
        // thread, which the checker's Task.Run may legitimately reuse.
        Task check = Task.CompletedTask;
        var caller = new Thread(() => check = checker.CheckAsync([new Connection { Host = "x" }], TestContext.Current.CancellationToken));
        caller.Start();
        caller.Join();
        await check;

        Assert.NotNull(probeThread);
        Assert.NotEqual(caller.ManagedThreadId, probeThread);
    }

    [Fact]
    public void PluginCatalogFindsTheBuiltInExtensions()
    {
        Assert.Contains(PluginCatalog.Create<IImporter>(PluginCatalog.CoreAssembly), i => i is LegacyJsonImporter);
        Assert.Contains(PluginCatalog.Create<ICredentialSource>(PluginCatalog.CoreAssembly), s => s.Id == WindowsCredentialSource.SourceId);
    }

    private static async Task<OnlineStatus> Probe(Connection connection)
    {
        var checker = new StatusChecker(_ => TcpPortProbe.Instance, TimeSpan.FromSeconds(5));
        OnlineStatus? result = null;
        checker.StatusChanged += (_, e) => result = e.Status;
        await checker.CheckAsync([connection], TestContext.Current.CancellationToken);
        return result!.Value;
    }

    private sealed class HangingProbe : IStatusProbe
    {
        // Ignores cancellation on purpose: the checker must still give up after its timeout.
        public Task<OnlineStatus> ProbeAsync(Connection connection, CancellationToken cancellationToken) =>
            new TaskCompletionSource<OnlineStatus>().Task;
    }

    private sealed class DelegateProbe(Action onProbe) : IStatusProbe
    {
        public Task<OnlineStatus> ProbeAsync(Connection connection, CancellationToken cancellationToken)
        {
            onProbe();
            return Task.FromResult(OnlineStatus.Online);
        }
    }
}
