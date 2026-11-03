using System.Text;
using RemoteManager.Core.Library;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.Storage;
using RemoteManager.Core.Sync;

namespace RemoteManager.Core.Tests;

/// <summary>Two (or three) simulated PCs, each with its own data folder, sharing one sync folder.</summary>
public sealed class SyncTests : IDisposable
{
    private const string Passphrase = "Korrektes Pferd Batterie Heftklammer";
    private readonly TempFolder root = new();
    private readonly string syncFolder;

    public SyncTests()
    {
        syncFolder = root.File("sync");
        Directory.CreateDirectory(syncFolder);
    }

    public void Dispose() => root.Dispose();

    private string SyncFile => Path.Combine(syncFolder, FolderSyncTransport.FileName);

    [Fact]
    public async Task ChangeOnOnePcAppearsOnTheOther()
    {
        Pc a = await PcAsync("A"), b = await PcAsync("B");
        ConnectionGroup group = new() { Name = "Kunde A" };
        a.Library.AddGroup(group);
        a.Library.Add(new Connection { Name = "dc01", Host = "10.0.0.10", GroupId = group.Id, Tags = ["prod"], Notes = "Wartung", SortOrder = 4,
            Credential = new CredentialRef("wincred", "dc01-ref"), Settings = new RdpSettings { Display = RdpDisplayMode.SmartSizing } });

        await a.SyncAsync();
        SyncResult pulled = await b.SyncAsync();

        Connection dc = Assert.Single(b.Library.Connections);
        Assert.Equal(1 + 1, pulled.Pulled);
        Assert.Equal(("dc01", "10.0.0.10", "Wartung", 4), (dc.Name, dc.Host, dc.Notes, dc.SortOrder));
        Assert.Equal(["prod"], dc.Tags);
        Assert.Equal("Kunde A", b.Library.FindGroup(dc.GroupId)!.Name);
        Assert.Equal(new CredentialRef("wincred", "dc01-ref"), dc.Credential); // the reference syncs, never the secret
        Assert.Equal(RdpDisplayMode.SmartSizing, dc.SettingsAs<RdpSettings>().Display);

        // And it survives a restart of B (it was saved, not only shown).
        Assert.Equal("dc01", Assert.Single(new SqliteConnectionStore(b.DatabasePath).LoadConnections()).Name);

        // A second round changes nothing and does not rewrite the file.
        Assert.False((await a.SyncAsync()).Wrote);
    }

    [Fact]
    public async Task EditsToDifferentFieldsMergeWithoutConflict()
    {
        (Pc a, Pc b, Connection onA, Connection onB) = await SharedConnectionAsync();
        onA.Host = "10.0.0.99";
        a.Library.Update(onA);
        onB.Description = "Neuer Text";
        b.Library.Update(onB);

        await a.SyncAsync();
        SyncResult result = await b.SyncAsync();
        await a.SyncAsync();

        Assert.Equal(0, result.OpenConflicts);
        foreach (Connection c in new[] { onA, onB })
            Assert.Equal(("10.0.0.99", "Neuer Text"), (c.Host, c.Description));
    }

    [Fact]
    public async Task SameFieldEditedOnBothShowsConflictAndKeepMineConverges()
    {
        (Pc a, Pc b, Connection onA, Connection onB) = await SharedConnectionAsync();
        onA.Name = "dc01-A";
        a.Library.Update(onA);
        onB.Name = "dc01-B";
        onB.Notes = "nur B";
        b.Library.Update(onB);

        await a.SyncAsync();
        SyncResult result = await b.SyncAsync();

        Assert.Equal(1, result.NewConflicts);
        SyncConflict conflict = Assert.Single(b.Engine.LoadConflicts().Values);
        Assert.Equal(["name"], conflict.Fields);
        Assert.Equal("dc01-B", SyncFieldMap.Display(conflict.Local!.Get("name")));
        Assert.Equal("dc01-A", SyncFieldMap.Display(conflict.Remote.Fields!.Get("name")));
        Assert.Equal("A", conflict.Remote.DeviceName);
        // Nothing was overwritten: each side still has its own name; B's other edit already went out.
        Assert.Equal("dc01-B", onB.Name);
        await a.SyncAsync();
        Assert.Equal(("dc01-A", "nur B"), (onA.Name, onA.Notes));

        // Later syncs keep the conflict until the user decides.
        await b.SyncAsync();
        Assert.Single(b.Engine.LoadConflicts());

        b.Engine.Resolve(conflict.Key, SyncResolution.KeepMine);
        await b.SyncAsync();
        await a.SyncAsync();
        Assert.Empty(b.Engine.LoadConflicts());
        Assert.Equal("dc01-B", onA.Name);
        Assert.Equal("dc01-B", onB.Name);
    }

    [Fact]
    public async Task KeepOtherAndKeepBothConverge()
    {
        (Pc a, Pc b, Connection onA, Connection onB) = await SharedConnectionAsync();
        onA.Host = "a.example";
        a.Library.Update(onA);
        onB.Host = "b.example";
        b.Library.Update(onB);
        await a.SyncAsync();
        await b.SyncAsync();
        b.Engine.Resolve(new SyncKey(SyncKey.Connection, onB.Id), SyncResolution.KeepOther);
        await b.SyncAsync();
        await a.SyncAsync();
        Assert.Equal(("a.example", "a.example"), (onA.Host, onB.Host));

        onA.Host = "a2.example";
        a.Library.Update(onA);
        onB.Host = "b2.example";
        b.Library.Update(onB);
        await a.SyncAsync();
        await b.SyncAsync();
        b.Engine.Resolve(new SyncKey(SyncKey.Connection, onB.Id), SyncResolution.KeepBoth, "dc01 (Kopie)");
        await b.SyncAsync();
        await a.SyncAsync();

        Assert.Equal("a2.example", onB.Host);
        foreach (Pc pc in new[] { a, b })
        {
            Connection copy = pc.Library.Connections.Single(c => c.Name == "dc01 (Kopie)");
            Assert.Equal("b2.example", copy.Host);
            Assert.Null(copy.Credential);
            Assert.Equal(2, pc.Library.Connections.Count);
        }
    }

    [Fact]
    public async Task EditAgainstDeleteIsAConflictInBothDirections()
    {
        (Pc a, Pc b, Connection onA, Connection onB) = await SharedConnectionAsync();
        a.Library.Remove(onA);
        onB.Notes = "noch gebraucht";
        b.Library.Update(onB);
        await a.SyncAsync();
        SyncResult result = await b.SyncAsync();

        SyncConflict conflict = Assert.Single(b.Engine.LoadConflicts().Values);
        Assert.True(conflict.IsDeleteConflict && conflict.Remote.IsDeleted);
        Assert.Equal(1, result.OpenConflicts);
        Assert.Contains(onB, b.Library.Connections); // not deleted silently

        b.Engine.Resolve(conflict.Key, SyncResolution.KeepMine);
        await b.SyncAsync();
        await a.SyncAsync();
        Assert.Equal("noch gebraucht", Assert.Single(a.Library.Connections).Notes);

        // The other way round: edited on A, deleted on B; B takes A's version.
        Connection back = a.Library.Connections[0];
        back.Notes = "A ändert";
        a.Library.Update(back);
        b.Library.Remove(onB);
        await a.SyncAsync();
        await b.SyncAsync();
        conflict = Assert.Single(b.Engine.LoadConflicts().Values);
        Assert.Null(conflict.Local);
        b.Engine.Resolve(conflict.Key, SyncResolution.KeepOther);
        await b.SyncAsync();
        Assert.Equal("A ändert", Assert.Single(b.Library.Connections).Notes);
    }

    [Fact]
    public async Task DeletesSyncAsTombstones()
    {
        (Pc a, Pc b, Connection onA, _) = await SharedConnectionAsync();
        ConnectionGroup group = new() { Name = "Weg" };
        a.Library.AddGroup(group);
        await a.SyncAsync();
        await b.SyncAsync();
        Assert.Single(b.Library.Groups);

        a.Library.Remove(onA);
        a.Library.RemoveGroup(group);
        await a.SyncAsync();
        await b.SyncAsync();

        Assert.Empty(b.Library.Connections);
        Assert.Empty(b.Library.Groups);
        // A third PC that joins later does not bring them back either.
        Pc c = await PcAsync("C");
        await c.SyncAsync();
        Assert.Empty(c.Library.Connections);
    }

    [Fact]
    public async Task DeletingAFolderWithItsContentSendsATombstoneForEachRecord()
    {
        Pc a = await PcAsync("A"), b = await PcAsync("B");
        ConnectionGroup top = a.Library.EnsureGroupPath("Kunde")!;
        ConnectionGroup site = a.Library.EnsureGroupPath("Kunde / Standort")!;
        var inTop = new Connection { Name = "fw", Host = "fw.local", GroupId = top.Id };
        var inSite = new Connection { Name = "dc01", Host = "dc01.local", GroupId = site.Id };
        a.Library.Add(inTop);
        a.Library.Add(inSite);
        a.Library.Add(new Connection { Name = "bleibt", Host = "x.local" });
        await a.SyncAsync();
        await b.SyncAsync();

        a.Library.RemoveGroup(top, GroupRemoval.DeleteContent);
        await a.SyncAsync();

        Dictionary<SyncKey, SyncRecord> bases = new SyncStateStore(a.DatabasePath).LoadBases();
        SyncKey[] deleted =
        [
            new(SyncKey.Group, top.Id), new(SyncKey.Group, site.Id),
            new(SyncKey.Connection, inTop.Id), new(SyncKey.Connection, inSite.Id),
        ];
        Assert.All(deleted, key => Assert.True(bases[key].IsDeleted));

        await b.SyncAsync();
        Assert.Empty(b.Library.Groups);
        Assert.Equal("bleibt", Assert.Single(b.Library.Connections).Name);
        Assert.Empty(b.Engine.LoadConflicts());
    }

    [Fact]
    public async Task DeletingANestedFolderMovesItsContentUpOnEveryPc()
    {
        Pc a = await PcAsync("A"), b = await PcAsync("B");
        ConnectionGroup top = a.Library.EnsureGroupPath("Kunde")!;
        ConnectionGroup site = a.Library.EnsureGroupPath("Kunde / Standort")!;
        ConnectionGroup room = a.Library.EnsureGroupPath("Kunde / Standort / Keller")!;
        a.Library.Add(new Connection { Name = "dc01", Host = "dc01.local", GroupId = site.Id });
        a.Library.Add(new Connection { Name = "nas", Host = "nas.local", GroupId = room.Id });
        await a.SyncAsync();
        await b.SyncAsync();

        a.Library.RemoveGroup(site, GroupRemoval.MoveContentUp);
        await a.SyncAsync();
        await b.SyncAsync();
        await a.SyncAsync();

        Assert.True(new SyncStateStore(a.DatabasePath).LoadBases()[new SyncKey(SyncKey.Group, site.Id)].IsDeleted);
        foreach (Pc pc in new[] { a, b })
        {
            Assert.Equal(["Kunde", "Kunde / Keller"], pc.Library.Groups.Select(g => pc.Library.PathOf(g.Id)).Order());
            Assert.Equal(top.Id, pc.Library.Connections.Single(c => c.Name == "dc01").GroupId);
            Assert.Equal(room.Id, pc.Library.Connections.Single(c => c.Name == "nas").GroupId);
            Assert.Empty(pc.Engine.LoadConflicts());
        }
    }

    [Fact]
    public async Task ConnectionAddedToAGroupDeletedElsewhereMovesToTheTop()
    {
        Pc a = await PcAsync("A"), b = await PcAsync("B");
        ConnectionGroup group = new() { Name = "Alt" };
        a.Library.AddGroup(group);
        await a.SyncAsync();
        await b.SyncAsync();

        a.Library.RemoveGroup(a.Library.Groups[0]);
        b.Library.Add(new Connection { Name = "neu", Host = "neu.local", GroupId = group.Id });
        await a.SyncAsync();
        await b.SyncAsync();
        await a.SyncAsync();

        foreach (Pc pc in new[] { a, b })
        {
            Assert.Empty(pc.Library.Groups);
            Assert.Null(Assert.Single(pc.Library.Connections).GroupId);
        }
    }

    [Fact]
    public async Task SyncFileHoldsNoPlaintext()
    {
        Pc a = await PcAsync("A");
        ConnectionGroup group = new() { Name = "Geheimkunde-GmbH", UserName = "domadmin" };
        a.Library.AddGroup(group);
        var connection = new Connection
        {
            Name = "srv-buchhaltung", Host = "buchhaltung.intern.example", Description = "Lohnabrechnung", Notes = "Schlüssel im Tresor",
            Tags = ["finanzen"], GroupId = group.Id, UserName = "lohn-admin", Credential = new CredentialRef("wincred", "secret-ref-key"),
        };
        a.Library.Add(connection);
        await a.SyncAsync();

        byte[] file = await File.ReadAllBytesAsync(SyncFile, TestContext.Current.CancellationToken);
        string[] needles = [group.Name, "domadmin", connection.Name, connection.Host, "Lohnabrechnung", "Tresor", "finanzen", "lohn-admin",
            "secret-ref-key", "wincred", connection.Id, group.Id, Passphrase, "\"name\"", "connection"];
        foreach (string needle in needles)
        {
            Assert.False(Contains(file, Encoding.UTF8.GetBytes(needle)), $"UTF-8 '{needle}' in the sync file");
            Assert.False(Contains(file, Encoding.Unicode.GetBytes(needle)), $"UTF-16 '{needle}' in the sync file");
        }
    }

    [Fact]
    public async Task WrongPassphraseIsAClearErrorAndLeavesTheFileAlone()
    {
        Pc a = await PcAsync("A");
        a.Library.Add(new Connection { Name = "x", Host = "x.local" });
        await a.SyncAsync();
        byte[] before = await File.ReadAllBytesAsync(SyncFile, TestContext.Current.CancellationToken);

        Pc b = await PcAsync("B");
        b.Library.Add(new Connection { Name = "y", Host = "y.local" });
        var ex = await Assert.ThrowsAsync<SyncException>(() => b.Engine.SyncAsync("falsche Passphrase", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(SyncError.WrongPassphrase, ex.Error);
        Assert.Equal(before, await File.ReadAllBytesAsync(SyncFile, TestContext.Current.CancellationToken));
        Assert.Single(b.Library.Connections);
        Assert.False(b.Engine.HasSynced);
    }

    [Fact]
    public async Task IncompleteFileIsNeverOverwritten()
    {
        Pc a = await PcAsync("A");
        a.Library.Add(new Connection { Name = "x", Host = "x.local" });
        await a.SyncAsync();
        byte[] full = await File.ReadAllBytesAsync(SyncFile, TestContext.Current.CancellationToken);
        byte[] half = full[..(full.Length / 2)];
        await File.WriteAllBytesAsync(SyncFile, half, TestContext.Current.CancellationToken);

        Pc b = await PcAsync("B");
        var ex = await Assert.ThrowsAsync<SyncException>(() => b.SyncAsync());

        Assert.Equal(SyncError.Damaged, ex.Error);
        Assert.Equal(half, await File.ReadAllBytesAsync(SyncFile, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LockedFileIsRetried()
    {
        Pc a = await PcAsync("A");
        a.Library.Add(new Connection { Name = "x", Host = "x.local" });
        await a.SyncAsync();
        a.Library.Add(new Connection { Name = "y", Host = "y.local" });

        Task<SyncResult> syncing;
        using (new FileStream(SyncFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            syncing = a.SyncAsync();
            await Task.Delay(700, TestContext.Current.CancellationToken);
        }

        Assert.True((await syncing).Wrote);
        Pc b = await PcAsync("B");
        await b.SyncAsync();
        Assert.Equal(2, b.Library.Connections.Count);
        Assert.Empty(Directory.GetFiles(syncFolder, "*.tmp"));
    }

    [Fact]
    public async Task SyncthingConflictCopyIsMergedAndRemoved()
    {
        (Pc a, Pc b, Connection onA, Connection onB) = await SharedConnectionAsync();
        byte[] original = await File.ReadAllBytesAsync(SyncFile, TestContext.Current.CancellationToken);

        // A and B both write while their sync client is offline: Syncthing keeps A's file as a conflict copy.
        onA.Name = "von A";
        a.Library.Update(onA);
        await a.SyncAsync();
        byte[] fromA = await File.ReadAllBytesAsync(SyncFile, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(SyncFile, original, TestContext.Current.CancellationToken);
        onB.Description = "von B";
        b.Library.Update(onB);
        a.Library.Add(new Connection { Name = "nur auf A", Host = "a.local" });
        await b.SyncAsync();
        string copy = Path.Combine(syncFolder, "remote-manager.sync-conflict-20261031-120000-ABCDEFG.sync");
        await File.WriteAllBytesAsync(copy, fromA, TestContext.Current.CancellationToken);

        SyncResult result = await b.SyncAsync();

        Assert.Equal(1, result.MergedCopies);
        Assert.Equal(0, result.OpenConflicts);
        Assert.Equal(("von A", "von B"), (onB.Name, onB.Description));
        Assert.False(File.Exists(copy));
        await a.SyncAsync();
        Assert.Equal(("von A", "von B"), (onA.Name, onA.Description));
        Assert.Equal(2, a.Library.Connections.Count);
        await b.SyncAsync();
        Assert.Equal(2, b.Library.Connections.Count);
    }

    [Fact]
    public async Task ConflictCopyWithTheSameFieldShowsAConflict()
    {
        (Pc a, Pc b, Connection onA, Connection onB) = await SharedConnectionAsync();
        byte[] original = await File.ReadAllBytesAsync(SyncFile, TestContext.Current.CancellationToken);
        onA.Host = "a.example";
        a.Library.Update(onA);
        await a.SyncAsync();
        byte[] fromA = await File.ReadAllBytesAsync(SyncFile, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(SyncFile, original, TestContext.Current.CancellationToken);
        onB.Host = "b.example";
        b.Library.Update(onB);
        await b.SyncAsync();
        await File.WriteAllBytesAsync(Path.Combine(syncFolder, "remote-manager-PC-A.sync"), fromA, TestContext.Current.CancellationToken); // OneDrive style

        SyncResult result = await b.SyncAsync();

        Assert.Equal(1, result.OpenConflicts);
        Assert.Equal("b.example", onB.Host);
        Assert.Equal(["host"], Assert.Single(b.Engine.LoadConflicts().Values).Fields);
    }

    [Fact]
    public async Task FileRolledBackToAnOlderVersionDoesNotRevertNewerEdits()
    {
        (Pc a, Pc b, Connection onA, Connection onB) = await SharedConnectionAsync();
        byte[] old = await File.ReadAllBytesAsync(SyncFile, TestContext.Current.CancellationToken);
        onA.Host = "neu.example";
        a.Library.Update(onA);
        await a.SyncAsync();
        await b.SyncAsync();

        await File.WriteAllBytesAsync(SyncFile, old, TestContext.Current.CancellationToken); // e.g. a restored OneDrive version
        await b.SyncAsync();
        await a.SyncAsync();

        Assert.Equal(("neu.example", "neu.example"), (onA.Host, onB.Host));
        Assert.Empty(b.Engine.LoadConflicts());
    }

    [Fact]
    public async Task SecondPcCanReplaceItsOwnConnectionsWhenJoining()
    {
        Pc a = await PcAsync("A");
        a.Library.Add(new Connection { Name = "gemeinsam", Host = "shared.local" });
        await a.SyncAsync();
        Pc b = await PcAsync("B");
        b.Library.Add(new Connection { Name = "Beispiel", Host = "sample.local" });

        await b.Engine.SyncAsync(Passphrase, SyncJoin.ReplaceLocal, TestContext.Current.CancellationToken);

        Assert.Equal("gemeinsam", Assert.Single(b.Library.Connections).Name);
        await a.SyncAsync();
        Assert.Equal("gemeinsam", Assert.Single(a.Library.Connections).Name);
    }

    [Fact]
    public async Task HistoryPreviewAndCollapsedStateStayLocal()
    {
        (Pc a, Pc b, Connection onA, Connection onB) = await SharedConnectionAsync();
        onA.LastConnectedAt = DateTime.UtcNow;
        onA.PreviewPath = "x.png";
        a.Library.Update(onA);
        ConnectionGroup group = new() { Name = "G", IsCollapsed = true };
        a.Library.AddGroup(group);

        await a.SyncAsync();
        SyncResult result = await b.SyncAsync();

        Assert.Equal(1, result.Pulled); // only the new group
        Assert.Null(onB.LastConnectedAt);
        Assert.Null(onB.PreviewPath);
        Assert.False(b.Library.FindGroup(group.Id)!.IsCollapsed);
    }

    [Fact]
    public async Task ParallelGroupMovesDoNotCreateACycle()
    {
        Pc a = await PcAsync("A"), b = await PcAsync("B");
        ConnectionGroup g1 = new() { Name = "G1" }, g2 = new() { Name = "G2" };
        a.Library.AddGroup(g1);
        a.Library.AddGroup(g2);
        await a.SyncAsync();
        await b.SyncAsync();

        a.Library.MoveGroup(g1, g2.Id);
        b.Library.MoveGroup(b.Library.FindGroup(g2.Id)!, g1.Id);
        await a.SyncAsync();
        await b.SyncAsync();
        await a.SyncAsync();

        foreach (Pc pc in new[] { a, b })
            Assert.All(pc.Library.Groups, g => Assert.DoesNotContain(g, pc.Library.AncestorsOf(g.ParentId)));
    }

    private async Task<(Pc A, Pc B, Connection OnA, Connection OnB)> SharedConnectionAsync()
    {
        Pc a = await PcAsync("A"), b = await PcAsync("B");
        a.Library.Add(new Connection { Name = "dc01", Host = "10.0.0.10", Description = "DC" });
        await a.SyncAsync();
        await b.SyncAsync();
        return (a, b, a.Library.Connections[0], b.Library.Connections[0]);
    }

    private async Task<Pc> PcAsync(string name)
    {
        (ConnectionLibrary library, DataStoreReport report) = await DataStore.OpenAsync(root.File("pc-" + name), new MemoryCredentialSource("wincred"),
            seedSamples: false, TestContext.Current.CancellationToken);
        var engine = new SyncEngine(library, new SyncStateStore(report.DatabasePath), new FolderSyncTransport(syncFolder), new SyncDevice(Guid.NewGuid().ToString("N"), name));
        return new Pc(library, engine, report.DatabasePath);
    }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    private sealed record Pc(ConnectionLibrary Library, SyncEngine Engine, string DatabasePath)
    {
        public Task<SyncResult> SyncAsync() => Engine.SyncAsync(Passphrase, cancellationToken: TestContext.Current.CancellationToken);
    }
}
