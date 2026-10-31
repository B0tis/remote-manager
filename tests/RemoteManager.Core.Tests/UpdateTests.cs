using RemoteManager.Core.Model;
using RemoteManager.Core.Storage;
using RemoteManager.Core.Updates;

namespace RemoteManager.Core.Tests;

public sealed class UpdateTests : IDisposable
{
    private readonly TempFolder folder = new();

    public void Dispose() => folder.Dispose();

    [Fact]
    public async Task BackupBeforeUpdateHoldsEveryConnectionAndOpensInTheNextVersion()
    {
        (var library, DataStoreReport report) = await DataStore.OpenAsync(folder.Path, new MemoryCredentialSource("wincred"), cancellationToken: TestContext.Current.CancellationToken);
        var group = new ConnectionGroup { Name = "Kunde B" };
        library.AddGroup(group);
        library.Add(new Connection { Name = "neu-vor-update", Host = "10.1.2.3", GroupId = group.Id, Tags = ["wichtig"], Notes = "bleibt" });
        string[] before = [.. library.Connections.Select(c => c.Name).Order()];

        // The app still has the store; the backup must not need it closed.
        string backup = DatabaseBackup.Create(report.DatabasePath, folder.File("backups"), "0.4.0");

        Assert.Contains("v0.4.0", Path.GetFileName(backup), StringComparison.Ordinal);
        string restored = folder.File("restored");
        Directory.CreateDirectory(restored);
        File.Copy(backup, Path.Combine(restored, DataStore.DatabaseFileName));
        (var reopened, _) = await DataStore.OpenAsync(restored, new MemoryCredentialSource("wincred"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(before, reopened.Connections.Select(c => c.Name).Order());
        Connection added = reopened.Connections.Single(c => c.Name == "neu-vor-update");
        Assert.Equal(["wichtig"], added.Tags);
        Assert.Equal("bleibt", added.Notes);
        Assert.Equal("Kunde B", reopened.Groups.Single(g => g.Id == added.GroupId).Name);
    }

    [Fact]
    public async Task OnlyTheLastThreeBackupsAreKept()
    {
        (_, DataStoreReport report) = await DataStore.OpenAsync(folder.Path, new MemoryCredentialSource("wincred"), cancellationToken: TestContext.Current.CancellationToken);
        string backups = folder.File("backups");
        var start = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        string[] created = [.. Enumerable.Range(0, 5).Select(i => DatabaseBackup.Create(report.DatabasePath, backups, $"0.4.{i}", now: start.AddDays(i)))];

        Assert.Equal(created.Skip(2).Reverse(), DatabaseBackup.List(backups));
        // Files that are not backups stay.
        File.WriteAllText(Path.Combine(backups, "notiz.txt"), "x");
        DatabaseBackup.Create(report.DatabasePath, backups, "0.4.5", now: start.AddDays(6));
        Assert.Equal(3, DatabaseBackup.List(backups).Count);
        Assert.True(File.Exists(Path.Combine(backups, "notiz.txt")));
    }

    [Theory]
    [InlineData("", UpdateSourceKind.GitHub, UpdateSource.DefaultRepository)]
    [InlineData("https://github.com/B0tis/remote-manager-releases/", UpdateSourceKind.GitHub, "https://github.com/B0tis/remote-manager-releases")]
    [InlineData("https://updates.example.org/remote-manager", UpdateSourceKind.Web, "https://updates.example.org/remote-manager")]
    [InlineData("https://github.com/B0tis/remote-manager/releases", UpdateSourceKind.Web, "https://github.com/B0tis/remote-manager/releases")]
    [InlineData(@"\\fileserver\software\remote-manager", UpdateSourceKind.Folder, @"\\fileserver\software\remote-manager")]
    [InlineData(@"""C:\Releases\Remote Manager""", UpdateSourceKind.Folder, @"C:\Releases\Remote Manager")]
    [InlineData("file:///C:/Releases", UpdateSourceKind.Folder, @"C:\Releases")]
    public void SourceIsReadFromOneString(string input, UpdateSourceKind kind, string location)
    {
        UpdateSource source = UpdateSource.Parse(input)!;
        Assert.Equal(kind, source.Kind);
        Assert.Equal(location, source.Location);
    }

    [Theory]
    [InlineData("releases")]
    [InlineData(@"..\releases")]
    [InlineData("ftp://example.org/releases")]
    public void RelativePathsAndOtherSchemesAreRejected(string input) => Assert.Null(UpdateSource.Parse(input));

    [Fact]
    public void StartupCheckRunsAtMostOncePerInterval()
    {
        var now = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(UpdateSchedule.IsStartupCheckDue(null, now));
        Assert.True(UpdateSchedule.IsStartupCheckDue("kaputt", now));
        Assert.False(UpdateSchedule.IsStartupCheckDue(UpdateSchedule.Format(now.AddHours(-1)), now));
        Assert.True(UpdateSchedule.IsStartupCheckDue(UpdateSchedule.Format(now - UpdateSchedule.Interval), now));
        // A clock that was set back must not stop the checks for good.
        Assert.True(UpdateSchedule.IsStartupCheckDue(UpdateSchedule.Format(now.AddDays(3)), now));
    }
}
