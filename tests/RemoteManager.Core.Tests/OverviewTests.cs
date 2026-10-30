using RemoteManager.Core.History;
using RemoteManager.Core.Library;
using RemoteManager.Core.Model;
using RemoteManager.Core.Search;
using RemoteManager.Core.Storage;

namespace RemoteManager.Core.Tests;

/// <summary>Ordering (drag and drop), group moves, connection history, and the launcher's fuzzy search.</summary>
public sealed class OverviewTests : IDisposable
{
    private readonly TempFolder folder = new();
    private readonly ConnectionLibrary library;

    public OverviewTests()
    {
        library = new ConnectionLibrary(new SqliteConnectionStore(folder.File("a.db")));
    }

    public void Dispose() => folder.Dispose();

    private Connection Add(string name, string? groupId = null)
    {
        var c = new Connection { Name = name, Host = name, GroupId = groupId, SortOrder = library.ConnectionsIn(groupId).Count() };
        library.Add(c);
        return c;
    }

    private ConnectionLibrary Reopen() => new(new SqliteConnectionStore(folder.File("a.db")));

    [Fact]
    public void MoveConnectionsReordersAndPersists()
    {
        ConnectionGroup group = library.EnsureGroupPath("A")!;
        Connection a = Add("a", group.Id), b = Add("b", group.Id), c = Add("c", group.Id);

        library.MoveConnections([c], group.Id, before: a);

        Assert.Equal(["c", "a", "b"], library.ConnectionsIn(group.Id).Select(x => x.Name));
        Assert.Equal(["c", "a", "b"], Reopen().ConnectionsIn(group.Id).Select(x => x.Name));
    }

    [Fact]
    public void MoveConnectionsIntoAnotherGroupAppendsInGivenOrder()
    {
        ConnectionGroup source = library.EnsureGroupPath("A")!, target = library.EnsureGroupPath("B")!;
        Connection a = Add("a", source.Id), b = Add("b", source.Id);
        Add("x", target.Id);

        library.MoveConnections([b, a], target.Id);

        Assert.Empty(library.ConnectionsIn(source.Id));
        Assert.Equal(["x", "b", "a"], Reopen().ConnectionsIn(target.Id).Select(x => x.Name));
    }

    [Fact]
    public void MoveConnectionsToUngrouped()
    {
        ConnectionGroup group = library.EnsureGroupPath("A")!;
        Connection a = Add("a", group.Id);

        library.MoveConnections([a], null);

        Assert.Null(Reopen().FindConnection(a.Id)!.GroupId);
    }

    [Fact]
    public void MoveGroupNestsAndReorders()
    {
        ConnectionGroup a = library.EnsureGroupPath("A")!, b = library.EnsureGroupPath("B")!, c = library.EnsureGroupPath("C")!;

        library.MoveGroup(c, null, before: a);
        Assert.Equal(["C", "A", "B"], Reopen().ChildrenOf(null).Select(g => g.Name));

        library.MoveGroup(b, a.Id);
        ConnectionLibrary reopened = Reopen();
        Assert.Equal("A / B", reopened.PathOf(b.Id));
        Assert.Equal(["C", "A"], reopened.ChildrenOf(null).Select(g => g.Name));
    }

    [Fact]
    public void AGroupCannotBeDroppedIntoItsSubgroup()
    {
        ConnectionGroup parent = library.EnsureGroupPath("A")!;
        ConnectionGroup child = library.EnsureGroupPath("A / B")!;

        Assert.False(library.CanMoveGroup(parent, child.Id));
        Assert.False(library.CanMoveGroup(parent, parent.Id));
        Assert.True(library.CanMoveGroup(child, null));
        Assert.Throws<InvalidOperationException>(() => library.MoveGroup(parent, child.Id));
        Assert.Equal(["A", "A / B"], library.DescendantsOf(null).Select(g => library.PathOf(g.Id)));
    }

    [Fact]
    public void CollapsedStatePersists()
    {
        ConnectionGroup group = library.EnsureGroupPath("A")!;
        group.IsCollapsed = true;
        library.UpdateGroup(group);

        Assert.True(Reopen().FindGroup(group.Id)!.IsCollapsed);
    }

    [Fact]
    public void SiblingNamesAreUniqueCaseInsensitive()
    {
        ConnectionGroup a = library.EnsureGroupPath("Kunde A")!;
        library.EnsureGroupPath("Kunde A / Standort");

        Assert.True(library.HasSiblingNamed(null, " kunde a "));
        Assert.False(library.HasSiblingNamed(null, "Kunde A", except: a));
        Assert.False(library.HasSiblingNamed(null, "Standort"));
    }

    [Fact]
    public void HistoryRecordsEndsAndCascades()
    {
        Connection a = Add("a"), b = Add("b");
        var history = new ConnectionHistory(folder.File("a.db"));
        DateTime now = DateTime.UtcNow;
        long first = history.Start(a.Id, HistoryResult.Connected, now.AddHours(-2));
        history.End(first, now.AddHours(-1));
        history.Start(a.Id, HistoryResult.Launched, now);
        history.Start(b.Id, HistoryResult.Failed, now);

        IReadOnlyList<HistoryEntry> entries = history.Recent(a.Id);
        Assert.Equal([HistoryResult.Launched, HistoryResult.Connected], entries.Select(e => e.Result));
        Assert.InRange(entries[1].Duration!.Value.TotalMinutes, 59.9, 60.1);
        Assert.Null(entries[0].EndedAt);

        IReadOnlyDictionary<string, UsageStats> usage = history.Usage(now.AddDays(-1));
        Assert.Equal(2, usage[a.Id].Count);
        Assert.False(usage.ContainsKey(b.Id)); // failed attempts do not count

        library.Remove(a);
        Assert.Empty(history.Recent(a.Id));
    }

    [Theory]
    [InlineData("dc01", "dc01", true)]
    [InlineData("dc1", "dc01", true)]
    [InlineData("kda", "Kunde A", true)]
    [InlineData("xyz", "dc01", false)]
    [InlineData("10.0", "10.0.0.10", true)]
    public void FuzzyScoreMatches(string query, string text, bool matches) =>
        Assert.Equal(matches, FuzzySearch.Score(query, text) is not null);

    [Fact]
    public void FuzzyScorePrefersBetterHits()
    {
        int exact = FuzzySearch.Score("dc01", "dc01")!.Value;
        int prefix = FuzzySearch.Score("dc", "dc01")!.Value;
        int inner = FuzzySearch.Score("01", "dc01")!.Value;
        int subsequence = FuzzySearch.Score("dc1", "dc01")!.Value;

        Assert.True(exact > prefix && prefix > inner && inner > subsequence);
    }

    [Fact]
    public void RankNeedsEveryWordAndUsesUsageForTies()
    {
        (string Name, string Group, double Usage)[] items =
        [
            ("dc01", "Kunde A", 0),
            ("dc02", "Kunde B", 50),
            ("file01", "Kunde A", 0),
        ];
        IEnumerable<SearchField> Fields((string Name, string Group, double Usage) i) => [new(i.Name, 3), new(i.Group, 1)];

        Assert.Equal(["dc01"], FuzzySearch.Rank("dc kunde a", items, Fields).Select(i => i.Name));
        Assert.Equal(["dc02", "dc01"], FuzzySearch.Rank("dc", items, Fields, i => i.Usage).Select(i => i.Name));
        Assert.Equal(3, FuzzySearch.Rank("", items, Fields).Count);
        Assert.Equal("dc02", FuzzySearch.Rank("", items, Fields, i => i.Usage)[0].Name);
    }

    [Fact]
    public void UsageBonusFavorsRecentAndFrequent()
    {
        DateTime now = DateTime.UtcNow;
        double recent = FuzzySearch.UsageBonus(new UsageStats(1, now.AddHours(-1)), now);
        double old = FuzzySearch.UsageBonus(new UsageStats(1, now.AddDays(-60)), now);
        double frequent = FuzzySearch.UsageBonus(new UsageStats(10, now.AddDays(-60)), now);

        Assert.True(recent > old && frequent > old);
        Assert.Equal(0, FuzzySearch.UsageBonus(null, now));
    }
}
