using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using RemoteManager.App.Connections;
using RemoteManager.App.Connections.RemoteTools;
using RemoteManager.App.Resources;
using RemoteManager.App.Services;
using RemoteManager.App.Views;
using RemoteManager.Core.Library;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.Storage;

namespace RemoteManager.App.Diagnostics;

/// <summary>
/// Overview UX: group sections, collapse (persisted), drag and drop (persisted order), sortable list
/// columns with multi-select, detail panel (start parameters, notes, history), group editor, quick
/// launcher (fuzzy search, Enter, global hotkey message), favorite star on tiles. Works on temporary
/// groups and connections named "Selftest …", removed again at the end.
/// </summary>
internal static partial class SelfTest
{
    private static async Task RunOverviewChecksAsync(MainWindow window)
    {
        AppHost app = AppHost.Current;
        ConnectionLibrary library = app.Library;
        string databasePath = app.StoreReport.DatabasePath;
        var groups = new List<ConnectionGroup>();
        var created = new List<ConnectionItem>();
        bool wasList = window.IsListView;
        OverviewColumn? sortBefore = window.SortColumn;
        bool sortDescendingBefore = window.SortDescending;
        KeyGesture? hotkeyBefore = window.Hotkey?.Gesture;
        try
        {
            ConnectionGroup customer = library.EnsureGroupPath("Selftest Kunde")!;
            ConnectionGroup site = library.EnsureGroupPath("Selftest Kunde / Selftest Standort")!;
            groups.AddRange([site, customer]);
            ConnectionItem Add(string name, ConnectionType type, string host, ConnectionGroup group, string[] tags) =>
                window.Adopt(new Connection
                {
                    Name = name, Type = type, Host = host, GroupId = group.Id, Tags = [.. tags],
                    SortOrder = library.ConnectionsIn(group.Id).Count(),
                    Description = "Self-test entry",
                });
            ConnectionItem alpha = Add("Selftest Alpha", ConnectionType.Rdp, "127.0.0.1", customer, ["selftest", "prod"]);
            ConnectionItem beta = Add("Selftest Beta", ConnectionType.Rdp, "rm-selftest-beta.invalid", customer, ["selftest"]);
            ConnectionItem gamma = Add("Selftest Gamma", ConnectionType.TeamViewer, "123 456 789", customer, ["selftest", "kasse"]);
            ConnectionItem delta = Add("Selftest Delta", ConnectionType.Rdp, "rm-selftest-delta.invalid", site, ["selftest"]);
            created.AddRange([alpha, beta, gamma, delta]);
            alpha.Model.Settings = alpha.Model.SettingsAs<RdpSettings>() with { Redirections = new RdpRedirections { Drives = true } };
            window.Save(alpha);


            await RunSectionChecksAsync(window, customer, site, [alpha, beta, gamma], delta, databasePath);
            RunDragDropChecks(window, customer, site, alpha, beta, gamma, delta, databasePath);
            await RunListChecksAsync(window, alpha, beta, gamma);
            await RunDetailChecksAsync(window, customer, alpha, databasePath);
            await RunGroupEditorChecksAsync(window, customer);
            await RunLauncherChecksAsync(window, beta, gamma);
            await RunTileStarCheckAsync(window, gamma);
        }
        finally
        {
            window.SetSort(sortBefore, sortDescendingBefore);
            window.SetListView(wasList);
            if (hotkeyBefore is not null && window.Hotkey?.Gesture != hotkeyBefore)
                window.TrySetHotkey(hotkeyBefore);
            foreach (ConnectionItem c in created)
                await window.RemoveConnectionAsync(c);
            foreach (ConnectionGroup g in groups.Where(g => library.FindGroup(g.Id) is not null))
                await window.RemoveGroupAsync(g);
        }


        Check("Self-test groups and connections are removed again",
            !library.Groups.Any(g => g.Name.StartsWith("Selftest", StringComparison.Ordinal))
            && !window.Connections.Any(c => created.Contains(c)));
    }

    private static async Task RunSectionChecksAsync(MainWindow window, ConnectionGroup customer, ConnectionGroup site,
        ConnectionItem[] direct, ConnectionItem nested, string databasePath)
    {
        window.SetListView(false);
        window.Select([direct[0]]);
        await Settle(600);
        List<object> entries = [.. window.TileEntries];
        int header = entries.FindIndex(e => e is GroupHeaderItem { Group: var g } && g == customer);
        int subHeader = entries.FindIndex(e => e is GroupHeaderItem { Group: var g } && g == site);
        Check($"Tiles show group sections: header with count, own connections, then the nested group ({string.Join(", ", entries.Skip(Math.Max(0, header)).Take(6).Select(Label))})",
            header >= 0 && entries[header] is GroupHeaderItem { CountLabel: "4", Depth: 0 }
            && entries.Skip(header + 1).Take(3).SequenceEqual(direct)
            && subHeader == header + 4 && entries[subHeader] is GroupHeaderItem { Depth: 1 } && entries[subHeader + 1] == nested);
        Check("Group headers are not selectable; the detail panel shows the selected tile",
            window.Tiles.SelectedItems?.OfType<GroupHeaderItem>().Any() == false && window.DetailPanel.Shown == direct[0]);
        SaveRenderProbe(window, "overview-groups.png");

        window.ToggleCollapsed((GroupHeaderItem)entries[header]);
        await Settle(400);
        entries = [.. window.TileEntries];
        bool collapsed = entries.OfType<GroupHeaderItem>().FirstOrDefault(h => h.Group == customer) is { IsCollapsed: true, Summary.Length: > 0 }
            && !entries.Contains(direct[0]) && !entries.Contains(nested) && !entries.OfType<GroupHeaderItem>().Any(h => h.Group == site);
        bool persisted = new ConnectionLibrary(new SqliteConnectionStore(databasePath)).FindGroup(customer.Id)?.IsCollapsed == true;
        SaveRenderProbe(window, "overview-collapsed.png");
        Check("Collapsing a group hides its connections and subgroups, shows an online summary, and is saved", collapsed && persisted);

        window.ToggleCollapsed(entries.OfType<GroupHeaderItem>().First(h => h.Group == customer));
        await Settle(300);
        Check("Expanding shows the connections again and is saved",
            window.TileEntries.Contains(direct[0]) && new ConnectionLibrary(new SqliteConnectionStore(databasePath)).FindGroup(customer.Id)?.IsCollapsed == false);
    }

    private static void RunDragDropChecks(MainWindow window, ConnectionGroup customer, ConnectionGroup site,
        ConnectionItem alpha, ConnectionItem beta, ConnectionItem gamma, ConnectionItem delta, string databasePath)
    {
        ConnectionLibrary Reopened() => new(new SqliteConnectionStore(databasePath));
        GroupHeaderItem HeaderOf(ConnectionGroup? group) => window.TileEntries.OfType<GroupHeaderItem>().First(h => h.Group == group);

        bool reordered = window.Drop(new OverviewDrag { Connections = [gamma] }, alpha, DropPosition.Before);
        Check("Drag and drop: a tile dropped before another reorders the group, saved",
            reordered && Reopened().ConnectionsIn(customer.Id).Select(c => c.Name).SequenceEqual(["Selftest Gamma", "Selftest Alpha", "Selftest Beta"]));

        bool moved = window.Drop(new OverviewDrag { Connections = [alpha, beta] }, HeaderOf(site), DropPosition.Into);
        Check("Drag and drop: two selected connections dropped on a group header move into it, saved",
            moved && Reopened().ConnectionsIn(site.Id).Select(c => c.Name).SequenceEqual(["Selftest Delta", "Selftest Alpha", "Selftest Beta"])
            && alpha.GroupPath == "Selftest Kunde / Selftest Standort");

        bool intoOwnChild = window.Drop(new OverviewDrag { Group = customer }, HeaderOf(site), DropPosition.Into, apply: false);
        bool toTop = window.Drop(new OverviewDrag { Group = site }, new NavigationDropTarget(null), DropPosition.Into);
        bool topLevel = Reopened().FindGroup(site.Id)?.ParentId is null;
        bool nested = window.Drop(new OverviewDrag { Group = site }, HeaderOf(customer), DropPosition.Into);
        Check("Drag and drop: a group can move to the top level and back into another group, never into its own subgroup",
            !intoOwnChild && toTop && topLevel && nested && Reopened().PathOf(site.Id) == "Selftest Kunde / Selftest Standort");

        // Back to the starting layout for the next checks.
        window.Drop(new OverviewDrag { Connections = [alpha, beta] }, gamma, DropPosition.After);
        window.Drop(new OverviewDrag { Connections = [gamma] }, beta, DropPosition.After);
        Check("Drag and drop: drop after a tile puts it behind that tile",
            Reopened().ConnectionsIn(customer.Id).Select(c => c.Name).SequenceEqual(["Selftest Alpha", "Selftest Beta", "Selftest Gamma"])
            && delta.Model.GroupId == site.Id);
    }

    private static async Task RunListChecksAsync(MainWindow window, ConnectionItem alpha, ConnectionItem beta, ConnectionItem gamma)
    {
        window.SetListView(true);
        window.SetSort(OverviewColumn.Name);
        await Settle(300);
        List<ConnectionItem> rows = [.. window.RowEntries.OfType<ConnectionItem>()];
        bool flatSorted = window.RowEntries.All(e => e is ConnectionItem)
            && rows.Select(r => r.Name).SequenceEqual(rows.Select(r => r.Name).Order(StringComparer.CurrentCultureIgnoreCase));
        window.SetSort(OverviewColumn.Name, descending: true);
        await Settle(200);
        List<string> descending = [.. window.RowEntries.OfType<ConnectionItem>().Select(r => r.Name)];
        Check("List columns sort ascending and descending (flat list while sorted)",
            flatSorted && descending.SequenceEqual(rows.Select(r => r.Name).Reverse()) && AppHost.Current.Library.GetSetting("overview.sort") == "Name:desc");


        window.SetSort(OverviewColumn.Type);
        window.Select([alpha, gamma]);
        await Settle(400);
        SaveRenderProbe(window, "overview-list-sorted-multiselect.png");
        Check("Multi-select: two rows selected, the detail panel offers the bulk actions",
            window.SelectedConnections.Count == 2 && window.DetailPanel.IsMultiple);

        window.SetFavorite(window.SelectedConnections, true);
        bool bulkFavorite = alpha.IsFavorite && gamma.IsFavorite && !beta.IsFavorite;
        window.SetFavorite([alpha, gamma], false);
        Check("Bulk favorite applies to every selected connection", bulkFavorite && !alpha.IsFavorite);


        window.SetSort(null);
        await Settle(300);
        Check("Clearing the sort brings back the group sections in the list", window.RowEntries.OfType<GroupHeaderItem>().Any());
        window.SetListView(false);
    }

    private static async Task RunDetailChecksAsync(MainWindow window, ConnectionGroup customer, ConnectionItem alpha, string databasePath)
    {
        window.SetDetailsVisible(true);
        window.Select([alpha]);
        await Settle(400);
        ConnectionDetailPanel panel = window.DetailPanel;
        Check($"Detail panel shows the connection and its start parameters ({string.Join(" | ", panel.ParameterLines)})",
            panel.NameShown == alpha.Name && panel.ParameterLines.Contains(Strings.StartParams_Rdp_NlaOn)
            && panel.ParameterLines.Any(l => l.Contains(Strings.StartParams_Rdp_Drives, StringComparison.Ordinal)));

        panel.Notes.Text = "Wartungsfenster Sa 20 Uhr, vorher Kunde anrufen.";
        window.Select([]);
        bool notesSaved = new ConnectionLibrary(new SqliteConnectionStore(databasePath)).FindConnection(alpha.Id)?.Notes == "Wartungsfenster Sa 20 Uhr, vorher Kunde anrufen.";
        window.MarkConnected(alpha);
        window.Select([alpha]);
        await Settle(300);
        Check($"Notes are saved from the panel; a logon is recorded in the history ({string.Join(" | ", panel.HistoryLines)})",
            notesSaved && panel.HistoryLines.Count == 1 && AppHost.Current.History.Recent(alpha.Id).Count == 1);

        customer.UserName = "selftest-admin";
        customer.Domain = "CORP";
        AppHost.Current.Library.UpdateGroup(customer);
        panel.Refresh();
        await Settle(300);
        SaveRenderProbe(window, "detail-panel.png");
        Check("Detail panel shows the inherited group logon", alpha.Credentials is { IsInherited: true, UserName: "selftest-admin" });
    }

    private static async Task RunGroupEditorChecksAsync(MainWindow window, ConnectionGroup customer)
    {
        Task<(bool Saved, string? ParentId)> editing = GroupEditor.ShowAsync(customer, isNew: false);
        await Settle(800);
        GroupEditor editor = GroupEditor.OpenEditor!;
        SaveRenderProbe(window, "group-editor.png");
        bool loaded = editor.NameField.Text == customer.Name && editor.UserField.Text == "selftest-admin" && editor.SelectedParentId is null;
        editor.NameField.Text = "Selftest/Kunde";
        bool slashRejected = !await editor.TryCommitAsync(customer);
        editor.NameField.Text = "Selftest Kunde Neu";
        bool committed = await editor.TryCommitAsync(customer);
        GroupEditor.OpenDialog?.Hide();
        await editing;
        AppHost.Current.Library.UpdateGroup(customer);
        Check("Group editor loads name and logon, rejects \"/\", renames the group",
            loaded && slashRejected && committed && customer.Name == "Selftest Kunde Neu"
            && AppHost.Current.Library.PathOf(customer.Id) == "Selftest Kunde Neu");
    }

    private static async Task RunLauncherChecksAsync(MainWindow window, ConnectionItem beta, ConnectionItem gamma)
    {
        Log($"Launcher hotkey: {(window.Hotkey?.Gesture is { } g ? GlobalHotkey.Format(g) : "not registered (taken?)")}");
        window.OpenLauncher();
        QuickLauncherWindow launcher = window.QuickLauncher!;
        await Settle(400);
        launcher.Query = "selftest beta";
        await Settle(200);
        bool exact = launcher.Results.FirstOrDefault() == beta;
        launcher.Query = "stgam";
        await Settle(200);
        bool fuzzy = launcher.Results.FirstOrDefault() == gamma && launcher.SelectedResult == gamma;
        launcher.Query = "selftest kasse";
        await Settle(300);
        SaveRenderProbe(launcher, "quick-launcher.png");
        bool byTag = launcher.Results.FirstOrDefault() == gamma;
        Check("Quick launcher: exact, fuzzy (\"stgam\"), and tag search find the connection first", exact && fuzzy && byTag);


        // The first hit is a TeamViewer connection: with the tool "not installed" (so a real TeamViewer never
        // starts) its handler answers with the missing-tool dialog on the overview.
        RemoteToolLauncher.LocatorOverride = FakeLocator(null);
        try
        {
            launcher.Launch(reveal: false);
            await Settle(800);
        }
        finally
        {
            RemoteToolLauncher.LocatorOverride = null;
        }

        bool launched = !launcher.IsVisible && MissingToolDialog.Current is not null;
        MissingToolDialog.Current?.Hide();
        await Settle(300);
        Check("Quick launcher: Enter connects the first hit and closes the launcher (the handler's dialog shows on the overview)", launched);

        // The global hotkey arrives as WM_HOTKEY on the main window.
        SendMessage(window.Hotkey!.WindowHandle, GlobalHotkey.WmHotkey, GlobalHotkey.HotkeyId, 0);
        await Settle(500);
        bool opened = launcher.IsVisible && launcher.Query.Length == 0;
        launcher.Hide();
        Check("Global hotkey message opens the launcher with an empty search", opened);

        var custom = new KeyGesture(Key.F11, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift);
        bool changed = window.TrySetHotkey(custom);
        Log($"Hotkey change to {GlobalHotkey.Format(custom)}: {(changed ? "registered" : "refused by Windows (taken)")}");
        Check("Hotkey change is saved when Windows accepts it, and invalid combinations are rejected",
            (!changed || AppHost.Current.Library.GetSetting("launcher.hotkey") == custom.ToString())
            && !GlobalHotkey.IsValid(new KeyGesture(Key.R, KeyModifiers.Shift)) && GlobalHotkey.Parse("nonsense") == GlobalHotkey.Default);
    }

    private static async Task RunTileStarCheckAsync(MainWindow window, ConnectionItem gamma)
    {
        window.SetListView(false);
        window.Tiles.ScrollIntoView(gamma);
        await Settle(400);
        Button? star = window.Tiles.ContainerFromItem(gamma)?.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("star"));
        bool before = gamma.IsFavorite;
        star?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle(200);
        bool toggled = star is not null && gamma.IsFavorite != before;
        SaveRenderProbe(window, "overview-tiles-favorite.png");
        window.SetFavorite([gamma], before);
        Check("The star on a tile toggles the favorite", toggled && gamma.IsFavorite == before);
    }

    private static string Label(object entry) => entry switch
    {
        GroupHeaderItem h => $"[{h.Title} {h.CountLabel}]",
        ConnectionItem c => c.Name,
        _ => "?",
    };

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
}
