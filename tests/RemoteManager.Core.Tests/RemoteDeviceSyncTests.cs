using System.Net;
using System.Text;
using RemoteManager.Core.ImportExport;
using RemoteManager.Core.Library;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.RemoteTools;
using RemoteManager.Core.Status;
using RemoteManager.Core.Storage;

namespace RemoteManager.Core.Tests;

/// <summary>Answers requests from a lambda and remembers them.</summary>
internal sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public sealed class TeamViewerApiClientTests
{
    private const string Devices = """
        {"devices":[
          {"remotecontrol_id":"r123456789","device_id":"d1","alias":"Buchhaltung-PC","groupid":"g1","online_state":"Online","description":"Frau M."},
          {"remotecontrol_id":"r987654321","device_id":"d2","alias":"Lager","groupid":"g2","online_state":"Offline"},
          {"remotecontrol_id":"r555","device_id":"d3","alias":"Neu","online_state":"weird"},
          {"device_id":"d4","alias":"no id"}
        ]}
        """;

    [Fact]
    public async Task ParsesDevicesWithBearerToken()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(Devices));
        var client = new TeamViewerApiClient(new HttpClient(handler));

        IReadOnlyList<TeamViewerDevice> devices = await client.GetDevicesAsync("tok-123", TestContext.Current.CancellationToken);

        Assert.Equal(3, devices.Count);
        Assert.Equal(new TeamViewerDevice("d1", "123456789", "Buchhaltung-PC", "Frau M.", "g1", OnlineStatus.Online), devices[0]);
        Assert.Equal(OnlineStatus.Offline, devices[1].Online);
        Assert.Equal(OnlineStatus.Unknown, devices[2].Online);
        HttpRequestMessage request = Assert.Single(handler.Requests);
        Assert.Equal("https://webapi.teamviewer.com/api/v1/devices", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("tok-123", request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task ParsesGroups()
    {
        var client = new TeamViewerApiClient(new HttpClient(new FakeHttpHandler(_ => FakeHttpHandler.Json("""{"groups":[{"id":"g1","name":"Kunde A"},{"name":"x"}]}"""))));
        Assert.Equal([new TeamViewerGroup("g1", "Kunde A")], await client.GetGroupsAsync("t", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, TeamViewerApiError.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, TeamViewerApiError.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError, TeamViewerApiError.Http)]
    public async Task MapsHttpErrors(HttpStatusCode status, TeamViewerApiError error)
    {
        var client = new TeamViewerApiClient(new HttpClient(new FakeHttpHandler(_ => FakeHttpHandler.Json("{}", status))));
        var ex = await Assert.ThrowsAsync<TeamViewerApiException>(() => client.GetDevicesAsync("secret-token", TestContext.Current.CancellationToken));
        Assert.Equal(error, ex.Error);
        Assert.DoesNotContain("secret-token", ex.Message);
    }

    [Fact]
    public async Task BadJsonIsAFormatError()
    {
        var client = new TeamViewerApiClient(new HttpClient(new FakeHttpHandler(_ => FakeHttpHandler.Json("<html>"))));
        var ex = await Assert.ThrowsAsync<TeamViewerApiException>(() => client.GetDevicesAsync("t", TestContext.Current.CancellationToken));
        Assert.Equal(TeamViewerApiError.Format, ex.Error);
    }

    [Theory]
    [InlineData("r123", "123")]
    [InlineData("123", "123")]
    [InlineData("rabc", "rabc")]
    public void StripPrefix(string input, string expected) => Assert.Equal(expected, TeamViewerApiClient.StripPrefix(input));
}

public sealed class TeamViewerSyncTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 30, 10, 0, 0, DateTimeKind.Utc);
    private static readonly TeamViewerGroup[] Groups = [new("g1", "Kunde A")];

    private readonly TempFolder folder = new();
    private readonly ConnectionLibrary library;

    public TeamViewerSyncTests() => library = new ConnectionLibrary(new SqliteConnectionStore(folder.File("sync.db")));

    public void Dispose() => folder.Dispose();

    [Fact]
    public void NewDevicesLandInTheNewGroupWithAlias()
    {
        DeviceSyncReport report = TeamViewerSync.Apply(library, [Device("d1", "123456789", "Buchhaltung-PC", "g1"), Device("d2", "222", "Ohne Gruppe", "")], Groups, "Neu aus TeamViewer", Now);

        Assert.Equal(new DeviceSyncReport(2, 0, 0, 0), report);
        Connection pc = library.Connections.Single(c => c.Host == "123456789");
        Assert.Equal("Buchhaltung-PC", pc.Name);
        Assert.Equal(ConnectionType.TeamViewer, pc.Type);
        Assert.Equal("Neu aus TeamViewer / Kunde A", library.PathOf(pc.GroupId));
        Assert.Equal("Neu aus TeamViewer", library.PathOf(library.Connections.Single(c => c.Host == "222").GroupId));
        var settings = pc.SettingsAs<RemoteToolSettings>();
        Assert.Equal(("Buchhaltung-PC", RemoteToolSource.TeamViewerApi, "d1", Now), (settings.Alias, settings.Source, settings.ExternalId, settings.LastSyncedAt!.Value));
    }

    [Fact]
    public void ResyncChangesAliasButKeepsLocalNotesTagsAndGroup()
    {
        TeamViewerSync.Apply(library, [Device("d1", "123456789", "Alt", "g1")], Groups, "Neu", Now);
        Connection pc = library.Connections.Single();
        ConnectionGroup mine = library.EnsureGroupPath("Meine / Ordnung")!;
        pc.Notes = "Schlüssel beim Hausmeister";
        pc.Tags = ["kunde-a"];
        pc.Description = "lokal";
        pc.GroupId = mine.Id;
        pc.IsFavorite = true;
        library.Update(pc);

        DeviceSyncReport report = TeamViewerSync.Apply(library, [Device("d1", "123456789", "Neu-Alias", "g1", "von TeamViewer")], Groups, "Neu", Now.AddHours(1));

        Assert.Equal(new DeviceSyncReport(0, 1, 0, 0), report);
        // Read back from the database, not only the object in memory.
        Connection stored = new ConnectionLibrary(library.Store).Connections.Single();
        Assert.Equal("Neu-Alias", stored.Name);
        Assert.Equal("Neu-Alias", stored.SettingsAs<RemoteToolSettings>().Alias);
        Assert.Equal("Schlüssel beim Hausmeister", stored.Notes);
        Assert.Equal(["kunde-a"], stored.Tags);
        Assert.Equal("lokal", stored.Description);
        Assert.Equal(mine.Id, stored.GroupId);
        Assert.True(stored.IsFavorite);
    }

    [Fact]
    public void RenamedConnectionKeepsItsName()
    {
        TeamViewerSync.Apply(library, [Device("d1", "1", "Alt", "")], [], "Neu", Now);
        Connection pc = library.Connections.Single();
        pc.Name = "Mein Name";
        library.Update(pc);

        TeamViewerSync.Apply(library, [Device("d1", "1", "Anders", "")], [], "Neu", Now);

        Assert.Equal("Mein Name", pc.Name);
        Assert.Equal("Anders", pc.SettingsAs<RemoteToolSettings>().Alias);
    }

    [Fact]
    public void UnchangedDevicesAreCounted()
    {
        TeamViewerSync.Apply(library, [Device("d1", "1", "A", "")], [], "Neu", Now);
        Assert.Equal(new DeviceSyncReport(0, 0, 1, 0), TeamViewerSync.Apply(library, [Device("d1", "1", "A", "")], [], "Neu", Now));
    }

    [Fact]
    public void ManualConnectionIsAdoptedByRemoteId()
    {
        var manual = new Connection { Name = "Kasse", Type = ConnectionType.TeamViewer, Host = "123 456 789", Notes = "n" };
        library.Add(manual);

        DeviceSyncReport report = TeamViewerSync.Apply(library, [Device("d1", "123456789", "Kasse-TV", "g1")], Groups, "Neu", Now);

        Assert.Equal(new DeviceSyncReport(0, 1, 0, 0), report);
        Assert.Single(library.Connections);
        Assert.Equal("Kasse", manual.Name); // the user's own name stays
        Assert.Equal("n", manual.Notes);
        Assert.Equal("d1", manual.SettingsAs<RemoteToolSettings>().ExternalId);
        Assert.Equal("123 456 789", manual.Host);
    }

    [Fact]
    public void DevicesGoneFromTheAccountAreMarkedNotDeleted()
    {
        TeamViewerSync.Apply(library, [Device("d1", "1", "A", ""), Device("d2", "2", "B", "")], [], "Neu", Now);
        library.Add(new Connection { Name = "Manuell", Type = ConnectionType.TeamViewer, Host = "3" });

        DeviceSyncReport report = TeamViewerSync.Apply(library, [Device("d1", "1", "A", "")], [], "Neu", Now);

        Assert.Equal(1, report.MarkedRemoved);
        Assert.Equal(3, library.Connections.Count);
        Assert.True(library.Connections.Single(c => c.Host == "2").SettingsAs<RemoteToolSettings>().RemovedAtSource);
        Assert.False(library.Connections.Single(c => c.Host == "3").SettingsAs<RemoteToolSettings>().RemovedAtSource);

        // Back in the account: the mark goes away.
        TeamViewerSync.Apply(library, [Device("d1", "1", "A", ""), Device("d2", "2", "B", "")], [], "Neu", Now);
        Assert.False(library.Connections.Single(c => c.Host == "2").SettingsAs<RemoteToolSettings>().RemovedAtSource);
    }

    [Fact]
    public void OtherTypesAreNotTouched()
    {
        library.Add(new Connection { Name = "AD", Type = ConnectionType.AnyDesk, Host = "1" });
        TeamViewerSync.Apply(library, [Device("d1", "1", "TV", "")], [], "Neu", Now);
        Assert.Equal(2, library.Connections.Count);
        Assert.Equal("AD", library.Connections.Single(c => c.Type == ConnectionType.AnyDesk).Name);
    }

    private static TeamViewerDevice Device(string deviceId, string remoteId, string alias, string group, string description = "") =>
        new(deviceId, remoteId, alias, description, group, OnlineStatus.Online);
}

public sealed class CachedRemoteStatusProbeTests
{
    [Fact]
    public async Task ConcurrentProbesShareOneRefresh()
    {
        var cache = new RemoteToolStatusCache();
        int calls = 0;
        var gate = new TaskCompletionSource();
        var probe = new CachedRemoteStatusProbe(cache, async _ =>
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            cache.Replace([("123456789", OnlineStatus.Online), ("2", OnlineStatus.Offline)], DateTime.UtcNow);
        }, TimeSpan.FromMinutes(1));

        Task<OnlineStatus> a = probe.ProbeAsync(Tv("123 456 789"), TestContext.Current.CancellationToken);
        Task<OnlineStatus> b = probe.ProbeAsync(Tv("2"), TestContext.Current.CancellationToken);
        gate.SetResult();

        Assert.Equal(OnlineStatus.Online, await a);
        Assert.Equal(OnlineStatus.Offline, await b);
        Assert.Equal(OnlineStatus.Unknown, await probe.ProbeAsync(Tv("999"), TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RemovedDevicesAreUnknown()
    {
        var cache = new RemoteToolStatusCache();
        cache.Replace([("1", OnlineStatus.Online)], DateTime.UtcNow);
        var probe = new CachedRemoteStatusProbe(cache, null, TimeSpan.FromMinutes(1));
        Connection gone = Tv("1");
        gone.Settings = new RemoteToolSettings { RemovedAtSource = true };
        Assert.Equal(OnlineStatus.Unknown, await probe.ProbeAsync(gone, TestContext.Current.CancellationToken));
        Assert.Equal(OnlineStatus.Online, await probe.ProbeAsync(Tv("1"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FailingRefreshIsNotRetriedWithinMaxAge()
    {
        DateTime now = DateTime.UtcNow;
        int calls = 0;
        var probe = new CachedRemoteStatusProbe(new RemoteToolStatusCache(), _ =>
        {
            calls++;
            throw new InvalidOperationException("bad token");
        }, TimeSpan.FromSeconds(90), () => now);

        Assert.Equal(OnlineStatus.Unknown, await probe.ProbeAsync(Tv("1"), TestContext.Current.CancellationToken));
        Assert.Equal(OnlineStatus.Unknown, await probe.ProbeAsync(Tv("1"), TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);
        now = now.AddMinutes(2);
        await probe.ProbeAsync(Tv("1"), TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task SlowRefreshTimesOutButStillFillsTheCache()
    {
        var cache = new RemoteToolStatusCache();
        var gate = new TaskCompletionSource();
        var probe = new CachedRemoteStatusProbe(cache, async _ =>
        {
            await gate.Task;
            cache.Replace([("1", OnlineStatus.Online)], DateTime.UtcNow);
        }, TimeSpan.FromMinutes(1));

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        Assert.Equal(OnlineStatus.Unknown, await probe.ProbeAsync(Tv("1"), timeout.Token));
        gate.SetResult();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(OnlineStatus.Online, await probe.ProbeAsync(Tv("1"), TestContext.Current.CancellationToken));
    }

    private static Connection Tv(string host) => new() { Type = ConnectionType.TeamViewer, Host = host };
}

public sealed class AnyDeskCsvImporterTests : IDisposable
{
    private readonly TempFolder folder = new();

    public void Dispose() => folder.Dispose();

    [Fact]
    public void ReadsSemicolonExportWithHeader()
    {
        ImportResult result = AnyDeskCsvImporter.Parse("﻿ID;Alias;Computer Name;Comment;Online\r\n123 456 789;kasse2@firma;KASSE-02;Laden, Unattended;yes\r\n987654321;;LAGER;;no\r\n");

        Assert.Equal(2, result.Connections.Count);
        Connection kasse = result.Connections[0];
        Assert.Equal(("kasse2@firma", ConnectionType.AnyDesk, "123456789", "Laden, Unattended"), (kasse.Name, kasse.Type, kasse.Host, kasse.Description));
        var settings = kasse.SettingsAs<RemoteToolSettings>();
        Assert.Equal(("kasse2@firma", RemoteToolSource.AnyDeskCsv, "123456789"), (settings.Alias, settings.Source, settings.ExternalId));
        Assert.Equal("LAGER", result.Connections[1].Name);
        Assert.Empty(result.Secrets);
    }

    [Fact]
    public void ReadsQuotedCommaExport()
    {
        ImportResult result = AnyDeskCsvImporter.Parse("\"cid\",\"alias\",\"comment\"\n\"111\",\"a@ad\",\"Sagt \"\"Hallo\"\", ok\"\n");
        Connection c = Assert.Single(result.Connections);
        Assert.Equal("Sagt \"Hallo\", ok", c.Description);
    }

    [Fact]
    public void WithoutHeaderFirstColumnsAreIdAndAlias()
    {
        ImportResult result = AnyDeskCsvImporter.Parse("111;Eins\n222;Zwei\n");
        Assert.Equal(["Eins", "Zwei"], result.Connections.Select(c => c.Name));
    }

    [Fact]
    public void SkipsInvalidEmptyAndDuplicateRows()
    {
        ImportResult result = AnyDeskCsvImporter.Parse("ID;Alias\n111;A\n;\n111;B\n--plain;C\n");
        Assert.Single(result.Connections);
        Assert.Equal(3, result.Warnings.Count);
    }

    [Fact]
    public void RemoveKnownDropsExistingAnyDeskIds()
    {
        ImportResult result = AnyDeskCsvImporter.Parse("ID;Alias\n111;A\n222;B\n");
        Connection[] existing = [new() { Type = ConnectionType.AnyDesk, Host = "111" }, new() { Type = ConnectionType.TeamViewer, Host = "222" }];
        Assert.Equal(1, AnyDeskCsvImporter.RemoveKnown(existing, result));
        Assert.Equal("222", Assert.Single(result.Connections).Host);
    }

    [Fact]
    public async Task CanImportChecksTheHeader()
    {
        string anyDesk = folder.File("clients.csv");
        await File.WriteAllTextAsync(anyDesk, "ID;Alias\n1;a\n", TestContext.Current.CancellationToken);
        string other = folder.File("other.csv");
        await File.WriteAllTextAsync(other, "Host;Port\nx;1\n", TestContext.Current.CancellationToken);

        var importer = new AnyDeskCsvImporter();
        Assert.True(importer.CanImport(anyDesk));
        Assert.False(importer.CanImport(other));
        Assert.False(importer.CanImport(folder.File("missing.csv")));
        Assert.Single((await importer.ImportAsync(anyDesk, TestContext.Current.CancellationToken)).Connections);
    }
}
