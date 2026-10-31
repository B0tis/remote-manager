using System.Diagnostics;
using System.Text;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Resources;
using RemoteManager.App.Sync;
using RemoteManager.App.Views;
using RemoteManager.Core.Credentials;
using RemoteManager.Core.Library;
using RemoteManager.Core.Model;
using RemoteManager.Core.Storage;
using RemoteManager.Core.Sync;

namespace RemoteManager.App.Diagnostics;

/// <summary>
/// Device sync: PC A and PC B are two data folders with their own database and sync service, sharing one
/// sync folder (all under %TEMP%, removed afterwards). Their passphrases go to per-run Credential Manager
/// keys; the real "RemoteManager:sync-passphrase" is never touched.
/// </summary>
internal static partial class SelfTest
{
    private static async Task RunSyncChecksAsync(MainWindow window)
    {
        string root = Path.Combine(Path.GetTempPath(), TestId("sync"));
        string syncFolder = Path.Combine(root, "sync");
        Directory.CreateDirectory(syncFolder);
        string passphrase = $"Selftest-{Guid.NewGuid():N}";
        var services = new List<SyncService>();
        try
        {
            (SyncService a, ConnectionLibrary libraryA) = await OpenSyncPcAsync(Path.Combine(root, "pc-a"), "a", services);
            (SyncService b, ConnectionLibrary libraryB) = await OpenSyncPcAsync(Path.Combine(root, "pc-b"), "b", services);

            // --- A change on A shows up on B --------------------------------------------------------------
            var group = new ConnectionGroup { Name = "Selftest Kunde Sync" };
            libraryA.AddGroup(group);
            var dc = new Connection
            {
                Name = "selftest-sync-dc01", Host = "sync-dc01.selftest.invalid", Notes = "Sync-Notiz äöü", Tags = ["sync-tag"],
                GroupId = group.Id, Credential = new CredentialRef(WindowsCredentialSource.SourceId, TestId("sync-ref")),
            };
            libraryA.Add(dc);
            a.Configure(true, syncFolder, passphrase);
            var watch = Stopwatch.StartNew();
            SyncResult? first = await a.SyncNowAsync();
            Log($"  first sync on A: {watch.ElapsedMilliseconds} ms (Argon2id key derivation included), pushed {first?.Pushed}");
            Check("Sync: the passphrase is stored in the Credential Manager (per-run key)", a.HasPassphrase && a.ReadPassphrase() == passphrase);

            b.Configure(true, syncFolder, passphrase);
            SyncResult? pulled = await b.SyncNowAsync();
            Connection? onB = libraryB.FindConnection(dc.Id);
            Check($"Sync: a connection added on PC A appears on PC B (pulled {pulled?.Pulled})",
                onB is { Name: "selftest-sync-dc01", Host: "sync-dc01.selftest.invalid", Notes: "Sync-Notiz äöü" }
                && onB.Tags.SequenceEqual(["sync-tag"]) && libraryB.FindGroup(onB.GroupId)?.Name == group.Name
                && onB.Credential == dc.Credential);

            // --- Sync on change (debounced) ---------------------------------------------------------------
            a.Start();
            dc.Description = "per Änderung synchronisiert";
            libraryA.Update(dc);
            DateTime? before = a.LastSync;
            bool synced = await WaitFor(() => a.LastSync != before && !a.IsRunning, TimeSpan.FromSeconds(20));
            await b.SyncNowAsync();
            Check("Sync: an edit is sent a few seconds later without pressing anything", synced && onB?.Description == dc.Description);

            // --- No plaintext in the sync folder ----------------------------------------------------------
            string[] needles = [dc.Name, dc.Host, "Sync-Notiz", "sync-tag", group.Name, dc.Id, TestId("sync-ref"), passphrase];
            List<string> leaks = [.. Directory.GetFiles(syncFolder).SelectMany(file =>
            {
                byte[] bytes = File.ReadAllBytes(file);
                return needles.Where(n => bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(n)) >= 0 || bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(n)) >= 0)
                    .Select(n => $"{Path.GetFileName(file)}: {n}");
            })];
            Check($"Sync: the sync folder holds no plaintext name, host, note, id, or passphrase ({Directory.GetFiles(syncFolder).Length} file(s), {needles.Length} needles)",
                leaks.Count == 0);
            foreach (string leak in leaks)
                Log($"  plaintext found: {leak}");

            // --- Concurrent edit of the same field: a visible conflict, resolved, both converge ------------
            a.Dispose(); // no automatic syncs on A while the conflict is set up
            dc.Name = "selftest-sync-von-A";
            libraryA.Update(dc);
            onB!.Name = "selftest-sync-von-B";
            libraryB.Update(onB);
            await a.SyncNowAsync();
            SyncResult? conflicted = await b.SyncNowAsync();
            Check($"Sync: the same field edited on A and B gives a conflict on B (new {conflicted?.NewConflicts}), nothing overwritten",
                conflicted is { NewConflicts: 1 } && b.OpenConflicts == 1 && onB.Name == "selftest-sync-von-B" && dc.Name == "selftest-sync-von-A");

            Task showing = SyncConflictsWindow.ShowAsync(window, b);
            await Settle(900);
            SyncConflictsWindow? conflicts = SyncConflictsWindow.Current;
            Check("Sync: the conflict window lists the conflict", conflicts?.Count == 1);
            if (conflicts is not null)
            {
                SaveRenderProbe(conflicts, "sync-conflicts.png");
                await conflicts.ResolveSelectedAsync(SyncResolution.KeepMine); // "Diese behalten" on B
                await Settle(400);
                SaveRenderProbe(conflicts, "sync-conflicts-resolved.png");
                conflicts.Close();
            }

            await showing;
            await a.SyncNowAsync();
            Check($"Sync: after \"Diese behalten\" on B both PCs have B's name ({dc.Name} / {onB.Name})",
                b.OpenConflicts == 0 && dc.Name == "selftest-sync-von-B" && onB.Name == "selftest-sync-von-B");

            // --- A third PC with the wrong passphrase gets a clear error, and the file stays untouched -----
            (SyncService c, ConnectionLibrary libraryC) = await OpenSyncPcAsync(Path.Combine(root, "pc-c"), "c", services);
            libraryC.Add(new Connection { Name = "selftest-sync-c", Host = "c.selftest.invalid" });
            byte[] fileBefore = await File.ReadAllBytesAsync(Path.Combine(syncFolder, FolderSyncTransport.FileName));
            Task dialog = SyncDialog.ShowAsync(c);
            await Settle(900);
            SyncDialog? content = SyncDialog.OpenContent;
            bool rejected = false;
            if (content is not null)
            {
                content.Fill(true, syncFolder, "Falsche-Passphrase-123");
                rejected = !await content.SaveAndSyncAsync(waitForSync: true);
                await Settle(300);
                SaveRenderProbe(window, "sync-dialog-wrong-passphrase.png");
                Log($"  dialog message: {content.Result.Message}");
            }

            byte[] fileAfter = await File.ReadAllBytesAsync(Path.Combine(syncFolder, FolderSyncTransport.FileName));
            Check("Sync: a wrong passphrase is refused with a clear message, nothing saved or written",
                rejected && content!.Result.Severity == FAInfoBarSeverity.Error && content.Result.Message == Strings.Sync_Error_WrongPassphrase
                && !c.IsEnabled && !c.HasPassphrase
                && fileBefore.AsSpan().SequenceEqual(fileAfter));

            // The right passphrase joins, replacing C's own connections with the synced ones.
            bool joined = false;
            if (content is not null)
            {
                content.Fill(true, syncFolder, passphrase, replaceLocal: true);
                joined = await content.SaveAndSyncAsync(waitForSync: true);
                await Settle(400);
                SaveRenderProbe(window, "sync-dialog.png");
            }

            SyncDialog.OpenDialog?.Hide();
            await dialog;
            Check("Sync: a third PC joins with the same passphrase and gets the synced connections",
                joined && libraryC.FindConnection(dc.Id)?.Name == "selftest-sync-von-B" && libraryC.Connections.Count == 1);
        }
        catch (Exception ex)
        {
            Fail($"Sync checks threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            foreach (SyncService service in services)
            {
                service.Dispose();
                service.DeletePassphrase();
            }

            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException ex)
            {
                Log($"  could not remove {root}: {ex.Message}");
            }
        }
    }

    /// <summary>A simulated PC: its own data folder and database, its sync service with a per-run passphrase key.</summary>
    private static async Task<(SyncService Service, ConnectionLibrary Library)> OpenSyncPcAsync(string folder, string name, List<SyncService> services)
    {
        (ConnectionLibrary library, DataStoreReport report) = await DataStore.OpenAsync(folder, new WindowsCredentialSource(), seedSamples: false);
        var service = new SyncService(library, report.DatabasePath, TestId("sync-passphrase-" + name));
        services.Add(service);
        return (service, library);
    }
}
