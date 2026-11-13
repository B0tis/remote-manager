using System.Globalization;
using Avalonia.Controls;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Connections;
using RemoteManager.App.Connections.RemoteTools;
using RemoteManager.App.Connections.Ssh;
using RemoteManager.App.Credentials;
using RemoteManager.App.Resources;
using RemoteManager.App.Services;
using RemoteManager.App.Sync;
using RemoteManager.App.Views;
using RemoteManager.Core.Credentials;
using RemoteManager.Core.Credentials.KeePass;
using RemoteManager.Core.Library;
using RemoteManager.Core.Model;
using RemoteManager.Core.Ssh;
using RemoteManager.Core.Sync;

namespace RemoteManager.App.Diagnostics;

/// <summary>
/// The small dialogs and windows: what a question promises (the verb on the primary button, Cancel as the default
/// of a destructive one, nothing changed after Cancel), the two pictures of the host key prompt, the sync conflict
/// window with a delete conflict, and a probe of every one of them in the dark and in the light theme
/// ("dialogs-*-dark.png", "dialogs-*-light.png"). Nothing here touches stored connections: the questions are
/// canceled, and the sync PCs are data folders under %TEMP%.
/// </summary>
internal static partial class SelfTest
{
    private static async Task RunDialogChecksAsync(MainWindow window)
    {
        Appearance look = AppHost.Current.Appearance;
        AppTheme themeBefore = look.Theme;
        string root = Path.Combine(Path.GetTempPath(), TestId("dialogs"));
        var services = new List<SyncService>();
        try
        {
            await RunQuestionChecksAsync(window);
            await RunHostKeyPictureChecksAsync(window);

            SyncService pc = await OpenConflictedPcAsync(root, services);
            (SyncService fresh, _) = await OpenSyncPcAsync(Path.Combine(root, "pc-new"), "dialogs-new", services);
            foreach (AppTheme theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                look.SetTheme(theme);
                await Settle(500);
                string suffix = theme == AppTheme.Dark ? "dark" : "light";
                await ProbeQuestionsAsync(window, suffix);
                await ProbeSettingsDialogsAsync(window, pc, fresh, suffix);
                await ProbeWindowsAsync(window, suffix);
                await ProbeConflictsAsync(window, pc, suffix);
            }

            await RunConflictResolutionChecksAsync(window, pc);
        }
        catch (Exception ex)
        {
            Fail($"Dialog checks threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            look.SetTheme(themeBefore);
            foreach (SyncService service in services)
            {
                service.Dispose();
                service.DeletePassphrase();
            }

            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch (IOException ex)
            {
                Log($"  could not remove {root}: {ex.Message}");
            }

            await Settle(400);
        }
    }

    /// <summary>Delete and move: the title names the object, the button the verb, and Cancel changes nothing.</summary>
    private static async Task RunQuestionChecksAsync(MainWindow window)
    {
        ConnectionItem one = window.Connections[0];
        int countBefore = window.Connections.Count;

        window.DeleteConnections([one]);
        await WaitFor(() => window.DeleteDialog is not null, TimeSpan.FromSeconds(5));
        await Settle(500);
        FAContentDialog? delete = window.DeleteDialog;
        Button? verb = DialogButton(delete, "PrimaryButton"), cancel = DialogButton(delete, "CloseButton");
        Check("Delete question: the title names the connection, the button says \"Löschen\"",
            delete?.Title is string title && title.Contains(one.Name, StringComparison.Ordinal) && delete.PrimaryButtonText == Strings.Action_Delete);
        Check("Delete question: Cancel is the default button, the verb is drawn as dangerous and not in the signal color",
            delete?.DefaultButton == FAContentDialogButton.Close && verb is not null && cancel is not null
            && verb.Classes.Contains("danger") && !verb.Classes.Contains("accent") && !cancel.Classes.Contains("accent"));
        delete?.Hide(FAContentDialogResult.None);
        await WaitFor(() => window.DeleteDialog is null, TimeSpan.FromSeconds(5));
        await Settle(300);
        Check("Delete question: Cancel keeps the connection", window.Connections.Count == countBefore && window.Connections.Contains(one));

        string? groupBefore = one.Model.GroupId;
        window.MoveToGroupAsync([one]);
        await WaitFor(() => window.MoveDialog is not null, TimeSpan.FromSeconds(5));
        await Settle(500);
        FAContentDialog? move = window.MoveDialog;
        ComboBox? target = (move?.Content as Control)?.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault();
        Check("Move question: a labelled target group, \"Verschieben\" is the default button",
            move is { DefaultButton: FAContentDialogButton.Primary } && move.PrimaryButtonText == Strings.Action_Move
            && move.Title is string moveTitle && moveTitle.Contains(one.Name, StringComparison.Ordinal)
            && target is { ItemCount: > 0, SelectedIndex: >= 0 });
        move?.Hide(FAContentDialogResult.None);
        await WaitFor(() => window.MoveDialog is null, TimeSpan.FromSeconds(5));
        await Settle(300);
        Check("Move question: Cancel leaves the connection in its group", one.Model.GroupId == groupBefore);
    }

    /// <summary>The first contact asks, a changed key alarms: two different pictures, and Cancel is the default in both.</summary>
    private static async Task RunHostKeyPictureChecksAsync(MainWindow window)
    {
        (SshHostKeyException unknown, SshHostKeyException changed) = HostKeyProblems();

        Task<bool> asking = HostKeyWindow.AskAsync(window, unknown);
        await WaitFor(() => HostKeyWindow.Current is { IsVisible: true }, TimeSpan.FromSeconds(5));
        await Settle(400);
        HostKeyWindow? prompt = HostKeyWindow.Current;
        bool question = prompt is not null && prompt.ShowsUnknownPicture && !prompt.ShowsChangedPicture && prompt.CancelIsDefault && prompt.AcceptIsRecommended;
        prompt?.Close();
        await asking;

        asking = HostKeyWindow.AskAsync(window, changed);
        await WaitFor(() => HostKeyWindow.Current is { IsVisible: true }, TimeSpan.FromSeconds(5));
        await Settle(400);
        prompt = HostKeyWindow.Current;
        bool alarm = prompt is not null && prompt.ShowsChangedPicture && !prompt.ShowsUnknownPicture && prompt.CancelIsDefault && !prompt.AcceptIsRecommended
            && prompt.ComparedFingerprints == (changed.Login.KnownHostKey, changed.Presented.Sha256Fingerprint);
        prompt?.Close();
        Check("Host key prompt: an unknown key asks, a changed key alarms with the known and the new fingerprint; Cancel is the default in both",
            question && alarm && !await asking);
    }

    private static (SshHostKeyException Unknown, SshHostKeyException Changed) HostKeyProblems()
    {
        var login = new SshLogin
        {
            ConnectionId = "selftest", Name = "web01", Host = "web01.example", Port = 22, UserName = "root",
            KnownHostKey = "SHA256:Xq3pN1v2YkV9b7c5mJ8wQe4rT6yU0iO2pA3sD5fG7hK",
        };
        var presented = new SshHostKey("ssh-ed25519", "SHA256:p8vR2kD0LmZ7tQ1sW4eY6uI9oP3aS5dF7gH1jK2lZxC", "aa:bb", 256);
        return (new SshHostKeyException(login with { KnownHostKey = "" }, presented, HostKeyStatus.Unknown),
            new SshHostKeyException(login, presented, HostKeyStatus.Changed));
    }

    /// <summary>The questions (in-window dialogs); every one is canceled.</summary>
    private static async Task ProbeQuestionsAsync(MainWindow window, string theme)
    {
        ConnectionItem one = window.Connections[0];

        window.DeleteConnections([one]);
        await ProbeQuestionAsync(window, () => window.DeleteDialog, $"dialogs-delete-{theme}.png");
        window.DeleteConnections([.. window.Connections.Take(8)]);
        await ProbeQuestionAsync(window, () => window.DeleteDialog, $"dialogs-delete-many-{theme}.png");
        window.MoveToGroupAsync([one]);
        await ProbeQuestionAsync(window, () => window.MoveDialog, $"dialogs-move-{theme}.png");

        if (window.Connections.SelectMany(c => c.Model.Tags).FirstOrDefault() is { } tag)
        {
            Task removing = window.RemoveTagAsync(tag);
            await ProbeQuestionAsync(window, () => window.TagRemoveDialog, $"dialogs-tag-remove-{theme}.png");
            await removing;
        }

        MissingToolDialog.Show(ConnectionType.RustDesk, window);
        await ProbeQuestionAsync(window, () => MissingToolDialog.Current, $"dialogs-tool-missing-{theme}.png");
        PlannedConnectionDialog.Show(one, window, Strings.Planned_Phase3);
        await ProbeQuestionAsync(window, () => PlannedConnectionDialog.Current, $"dialogs-planned-{theme}.png");
    }

    private static async Task ProbeQuestionAsync(MainWindow window, Func<FAContentDialog?> dialog, string name)
    {
        await WaitFor(() => dialog() is not null, TimeSpan.FromSeconds(5));
        await Settle(600);
        SaveRenderProbe(window, name);
        dialog()?.Hide(FAContentDialogResult.None);
        await WaitFor(() => dialog() is null, TimeSpan.FromSeconds(5));
        await Settle(250);
    }

    /// <summary>Sync (a PC that never synced, and one with open conflicts), remote tools, and about.</summary>
    private static async Task ProbeSettingsDialogsAsync(MainWindow window, SyncService pc, SyncService fresh, string theme)
    {
        Task first = SyncDialog.ShowAsync(fresh);
        await WaitFor(() => SyncDialog.OpenContent is not null, TimeSpan.FromSeconds(5));
        await Settle(700);
        if (theme == "dark")
        {
            Check("Sync dialog: a PC that never synced is asked what happens to its own connections, and shows no conflicts",
                SyncDialog.OpenContent is { ShowsJoinChoice: true, ShowsConflicts: false });
        }

        SaveRenderProbe(window, $"dialogs-sync-first-{theme}.png");
        SyncDialog.OpenDialog?.Hide();
        await first;
        await Settle(250);

        Task sync = SyncDialog.ShowAsync(pc);
        await WaitFor(() => SyncDialog.OpenContent is not null, TimeSpan.FromSeconds(5));
        await Settle(700);
        if (theme == "dark")
        {
            Check($"Sync dialog: open conflicts ({pc.OpenConflicts}) show in the state row with the way to decide them",
                SyncDialog.OpenContent is { ShowsConflicts: true });
        }

        SaveRenderProbe(window, $"dialogs-sync-{theme}.png");
        SyncDialog.OpenDialog?.Hide();
        await sync;
        await Settle(250);

        Task tools = RemoteToolsDialog.ShowAsync();
        await WaitFor(() => RemoteToolsDialog.OpenContent is not null, TimeSpan.FromSeconds(5));
        await Settle(700);
        SaveRenderProbe(window, $"dialogs-remote-tools-{theme}.png");

        // The three cards are taller than FluentAvalonia lets a dialog grow; a window with the room shows them all.
        double heightBefore = window.Height;
        window.Height = 1000;
        await Settle(600);
        if (theme == "dark")
        {
            ScrollViewer? scroll = RemoteToolsDialog.OpenDialog?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(s => s.Name == "ContentScrollViewer");
            Check($"Remote tools: on a tall window the three cards show without scrolling (content {scroll?.Extent.Height:0} in {scroll?.Viewport.Height:0})",
                scroll is not null && scroll.Extent.Height <= scroll.Viewport.Height + 0.5);
        }

        SaveRenderProbe(window, $"dialogs-remote-tools-tall-{theme}.png");
        RemoteToolsDialog.OpenDialog?.Hide();
        await tools;
        window.Height = heightBefore;
        await Settle(400);

        Task about = AboutView.ShowAsync(window);
        await WaitFor(() => AboutView.OpenContent is not null, TimeSpan.FromSeconds(5));
        await Settle(700);
        SaveRenderProbe(window, $"dialogs-about-{theme}.png");
        AboutView.OpenDialog?.Hide();
        await about;
        await Settle(250);
    }

    /// <summary>The standalone windows: host key (both pictures), master password, entry picker.</summary>
    private static async Task ProbeWindowsAsync(MainWindow window, string theme)
    {
        (SshHostKeyException unknown, SshHostKeyException changed) = HostKeyProblems();
        foreach ((SshHostKeyException problem, string name) in new[] { (unknown, "unknown"), (changed, "changed") })
        {
            Task<bool> asking = HostKeyWindow.AskAsync(window, problem);
            await WaitFor(() => HostKeyWindow.Current is { IsVisible: true }, TimeSpan.FromSeconds(5));
            await Settle(500);
            if (HostKeyWindow.Current is { } prompt)
            {
                SaveRenderProbe(prompt, $"dialogs-hostkey-{name}-{theme}.png");
                prompt.Close();
            }

            await asking;
        }

        // Invented paths: only the file names show, nothing is opened.
        var unlock = new MasterPasswordWindow(new MasterPasswordRequest(@"C:\Daten\Selftest\Kunden-Tresor.kdbx", @"C:\Daten\Selftest\Kunden-Tresor.keyx", true), TimeSpan.FromMinutes(10));
        unlock.Show(window);
        await Settle(500);
        SaveRenderProbe(unlock, $"dialogs-unlock-{theme}.png");
        unlock.Close();

        KdbxEntry Entry(string title, string user, params string[] groups) =>
            new(Guid.NewGuid().ToString("N"), groups, new Dictionary<string, string> { ["Title"] = title, ["UserName"] = user });
        var picker = new KeePassEntryPicker(@"C:\Daten\Selftest\Kunden-Tresor.kdbx",
        [
            Entry("Domänen-Admin", @"SELFTEST\administrator", "Kunden", "Selftest GmbH"),
            Entry("Firewall", "admin", "Kunden", "Selftest GmbH", "Netzwerk"),
            Entry("Hyper-V Host", "root", "Kunden", "Selftest GmbH"),
            Entry("VNC Werkstatt", "", "Kunden", "Selftest GmbH"),
            Entry("Notfallzugang", "notfall@selftest.invalid"),
        ]);
        picker.Show(window);
        await Settle(500);
        picker.Filter("firewall");
        await Settle(200);
        bool filtered = picker.Shown.Count == 1 && !picker.ShowsEmptyText;
        picker.Filter("gibt es nicht");
        await Settle(200);
        bool empty = picker.Shown.Count == 0 && picker.ShowsEmptyText;
        picker.Filter("");
        await Settle(200);
        if (theme == "dark")
            Check("Entry picker: the filter narrows the list, and no match says so", filtered && empty && picker.Shown.Count == 5);
        SaveRenderProbe(picker, $"dialogs-keepass-picker-{theme}.png");
        picker.Close();
        await Settle(250);
    }

    /// <summary>Every open conflict of <paramref name="pc"/> in the conflict window.</summary>
    private static async Task ProbeConflictsAsync(MainWindow window, SyncService pc, string theme)
    {
        Task showing = SyncConflictsWindow.ShowAsync(window, pc);
        await WaitFor(() => SyncConflictsWindow.Current is { IsVisible: true }, TimeSpan.FromSeconds(5));
        await Settle(600);
        if (SyncConflictsWindow.Current is { } conflicts)
        {
            for (int i = 0; i < conflicts.Count; i++)
            {
                conflicts.Select(i);
                await Settle(300);
                SaveRenderProbe(conflicts, $"dialogs-conflict-{i + 1}-{theme}.png");
            }

            conflicts.Close();
        }

        await showing;
        await Settle(250);
    }

    /// <summary>A delete conflict offers "restore" or "leave deleted"; after the last decision the window says so.</summary>
    private static async Task RunConflictResolutionChecksAsync(MainWindow window, SyncService pc)
    {
        int before = pc.OpenConflicts;
        Task showing = SyncConflictsWindow.ShowAsync(window, pc);
        await WaitFor(() => SyncConflictsWindow.Current is { IsVisible: true }, TimeSpan.FromSeconds(5));
        await Settle(500);
        SyncConflictsWindow? conflicts = SyncConflictsWindow.Current;
        bool listed = conflicts is not null && conflicts.Count == before && before == 3 && !conflicts.ShowsEmptyState;
        var answers = new List<string>();
        if (conflicts is not null)
        {
            for (int i = 0; i < conflicts.Count; i++)
            {
                conflicts.Select(i);
                await Settle(200);
                answers.Add($"{conflicts.KeepMineLabel}|{conflicts.KeepOtherLabel}|{conflicts.OffersKeepBoth}");
            }

            while (conflicts.Count > 0)
            {
                await conflicts.ResolveSelectedAsync(SyncResolution.KeepMine);
                await Settle(300);
            }

            SaveRenderProbe(conflicts, "dialogs-conflicts-resolved-light.png");
        }

        Check($"Conflict window: each kind of conflict names its two answers ({string.Join("; ", answers)})",
            listed && answers.Count == 3
            && answers.Contains($"{Strings.Sync_KeepMine}|{Strings.Sync_KeepOther}|True")
            && answers.Contains($"{Strings.Sync_KeepDeleted}|{Strings.Sync_Restore}|False")
            && answers.Contains($"{Strings.Sync_KeepExisting}|{Strings.Sync_DeleteHereToo}|False"));
        Check("Conflict window: after the last decision it shows that nothing is open", conflicts is { ShowsEmptyState: true, Count: 0 } && pc.OpenConflicts == 0);
        conflicts?.Close();
        await showing;
    }

    /// <summary>
    /// PC B of two simulated PCs, with three open conflicts against PC A: a connection edited on both (several
    /// fields), one deleted on B and edited on A, and one edited on B and deleted on A.
    /// </summary>
    private static async Task<SyncService> OpenConflictedPcAsync(string root, List<SyncService> services)
    {
        string syncFolder = Path.Combine(root, "sync");
        Directory.CreateDirectory(syncFolder);
        string passphrase = $"Selftest-{Guid.NewGuid():N}";
        (SyncService a, ConnectionLibrary libraryA) = await OpenSyncPcAsync(Path.Combine(root, "pc-a"), "dialogs-a", services);
        (SyncService b, ConnectionLibrary libraryB) = await OpenSyncPcAsync(Path.Combine(root, "pc-b"), "dialogs-b", services);

        var group = new ConnectionGroup { Name = "Selftest Kunde" };
        libraryA.AddGroup(group);
        var edited = new Connection { Name = "selftest-dc01", Host = "dc01.selftest.invalid", Description = "Domänencontroller", Tags = ["windows"], GroupId = group.Id, UserName = "administrator" };
        var deletedHere = new Connection { Name = "selftest-web01", Type = ConnectionType.Ssh, Host = "web01.selftest.invalid", Port = 22, Description = "Webserver", GroupId = group.Id, UserName = "root" };
        var deletedThere = new Connection { Name = "selftest-alt", Host = "alt.selftest.invalid", Description = "Altes System", GroupId = group.Id };
        foreach (Connection connection in new[] { edited, deletedHere, deletedThere })
            libraryA.Add(connection);
        a.Configure(true, syncFolder, passphrase);
        await a.SyncNowAsync();
        b.Configure(true, syncFolder, passphrase);
        await b.SyncNowAsync();

        // PC A.
        edited.Name = "selftest-dc01 (Hauptstandort)";
        edited.Host = "dc01.hq.selftest.invalid";
        edited.Description = "Domänencontroller, Hauptstandort";
        libraryA.Update(edited);
        deletedHere.Description = "Webserver, neu aufgesetzt";
        libraryA.Update(deletedHere);
        libraryA.Remove(deletedThere);

        // PC B, before it has seen any of that.
        Connection onB = libraryB.FindConnection(edited.Id)!;
        onB.Name = "selftest-dc01 (Altbau)";
        onB.Host = "dc01.altbau.selftest.invalid";
        onB.Description = "DC im Altbau";
        libraryB.Update(onB);
        libraryB.Remove(libraryB.FindConnection(deletedHere.Id)!);
        Connection stays = libraryB.FindConnection(deletedThere.Id)!;
        stays.Description = "Altes System, wird noch gebraucht";
        libraryB.Update(stays);

        await a.SyncNowAsync();
        SyncResult? result = await b.SyncNowAsync();
        Log(string.Format(CultureInfo.InvariantCulture, "  dialogs: PC B has {0} open conflicts (new {1})", b.OpenConflicts, result?.NewConflicts));
        return b;
    }

    /// <summary>A button of the dialog's own button row ("PrimaryButton", "CloseButton").</summary>
    private static Button? DialogButton(FAContentDialog? dialog, string name) =>
        dialog?.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == name && b.TemplatedParent == dialog);
}
