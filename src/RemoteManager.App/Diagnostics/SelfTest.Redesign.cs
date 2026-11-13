using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Microsoft.Data.Sqlite;
using RemoteManager.App.Connections.Rdp;
using RemoteManager.App.Controls;
using RemoteManager.App.Resources;
using RemoteManager.App.Services;
using RemoteManager.App.Views;
using RemoteManager.Core.Library;
using RemoteManager.Core.Model;
using RemoteManager.Core.Status;
using RemoteManager.Core.Storage;

namespace RemoteManager.App.Diagnostics;

/// <summary>
/// What the redesign added to the overview: the sidebar of plain rows (counts, group tree, compact and floating
/// mode), the filter chips, the tile sizes, theme and signal color, answer times of the status checks, the
/// "Aktive Sitzungen" filter, window placement, the title bar with the search box, and the "zuletzt" labels.
/// Works on its own group and connections named "Selftest Redesign …" and puts every setting back.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>The settings these checks change through the UI; each is put back to its stored value (or removed again).</summary>
    private static readonly string[] RedesignSettings =
    [
        "appearance.theme", "appearance.accent", "appearance.tileSize",
        "overview.sidebar", "overview.view", "overview.details", "window.main",
    ];

    private static async Task RunRedesignChecksAsync(MainWindow window)
    {
        AppHost app = AppHost.Current;
        ConnectionLibrary library = app.Library;
        string databasePath = app.StoreReport.DatabasePath;
        Appearance look = app.Appearance;

        double width = window.Width, height = window.Height;
        WindowState state = window.WindowState;
        bool wasList = window.IsListView, hadDetails = window.IsDetailsVisible;
        string filterBefore = window.Filter;
        AppTheme themeBefore = look.Theme;
        AccentPreset accentBefore = look.Accent;
        TileSize sizeBefore = look.TileSize;
        Dictionary<string, string> settingsBefore = ReadAppSettings(databasePath);
        string? screenVariable = Environment.GetEnvironmentVariable(WindowPlacement.ScreenVariable);

        var groups = new List<ConnectionGroup>();
        var created = new List<ConnectionItem>();
        bool sidebarWasCompact = false;
        try
        {
            window.WindowState = WindowState.Normal;
            await ResizeAsync(window, 1320, 840);
            // The checks start from the expanded sidebar; a folded one is folded again at the end.
            sidebarWasCompact = window.SidebarMode == SidebarMode.Compact;
            if (sidebarWasCompact)
                window.ToggleSidebar();
            window.SetListView(false);
            window.SetDetailsVisible(true);
            window.ClearChipFilters();
            window.ShowFilter(SidebarEntry.AllTag);

            RunAgoChecks(window);
            RunLatencyChecks(window);
            RunHostGlyphChecks();
            await RunStatusRoundTripChecksAsync(window);

            ConnectionGroup group = library.EnsureGroupPath("Selftest Redesign")!;
            ConnectionGroup sub = library.EnsureGroupPath("Selftest Redesign / Selftest Unterbereich")!;
            groups.AddRange([sub, group]);
            ConnectionItem Add(string name, ConnectionType type, string host, ConnectionGroup into, bool favorite, TimeSpan? lastConnected, params string[] tags) =>
                window.Adopt(new Connection
                {
                    Name = name, Type = type, Host = host, GroupId = into.Id, IsFavorite = favorite, Tags = [.. tags],
                    SortOrder = library.ConnectionsIn(into.Id).Count(),
                    LastConnectedAt = lastConnected is { } ago ? DateTime.UtcNow - ago : null,
                    Description = "Self-test entry of the redesign checks",
                });
            // .invalid hosts never resolve: a real status check can only end as "unknown" and never brings an answer time.
            ConnectionItem rdp = Add("Selftest Redesign RDP", ConnectionType.Rdp, "rm-selftest-redesign-a.invalid", group, true, TimeSpan.FromSeconds(320), "selftest-redesign");
            ConnectionItem ssh = Add("Selftest Redesign SSH", ConnectionType.Ssh, "rm-selftest-redesign-b.invalid", group, false, TimeSpan.FromHours(3.2));
            ConnectionItem tool = Add("Selftest Redesign TeamViewer", ConnectionType.TeamViewer, "123 456 789", sub, false, null);
            created.AddRange([rdp, ssh, tool]);
            await Settle(400);

            await RunDetailLatencyChecksAsync(window, rdp, tool);
            await RunTitleBarChecksAsync(window, rdp);
            await RunSidebarChecksAsync(window, group, sub, rdp, ssh, tool, databasePath);
            await RunFilterChipChecksAsync(window, group, rdp, ssh, tool, databasePath);
            await RunTileSizeChecksAsync(window, rdp, databasePath);
            await RunAppearanceChecksAsync(window, rdp, ssh, tool, databasePath);
            await RunActiveSessionChecksAsync(window);
            RunPlacementChecks(window);
        }
        finally
        {
            Environment.SetEnvironmentVariable(WindowPlacement.ScreenVariable, screenVariable);
            window.SearchBox.Text = "";
            window.ClearChipFilters();
            look.SetTileSize(sizeBefore);
            look.SetAccent(accentBefore);
            look.SetTheme(themeBefore);
            foreach (ConnectionItem c in created.Where(c => window.Connections.Contains(c)))
                await window.RemoveConnectionAsync(c);
            foreach (ConnectionGroup g in groups.Where(g => library.FindGroup(g.Id) is not null))
                await window.RemoveGroupAsync(g);

            await ResizeAsync(window, 1320, 840);
            if ((window.SidebarMode == SidebarMode.Compact) != sidebarWasCompact)
                window.ToggleSidebar();
            window.Width = width;
            window.Height = height;
            window.WindowState = state;
            await Settle(400);
            window.SetListView(wasList);
            window.SetDetailsVisible(hadDetails);
            window.ShowFilter(filterBefore);

            // The calls above saved "system", "medium", "1", ... where nothing was stored before: back to the exact rows.
            foreach (string key in RedesignSettings)
            {
                string? before = settingsBefore.GetValueOrDefault(key);
                if (library.GetSetting(key) != before)
                    library.SetSetting(key, before);
            }
        }

        Dictionary<string, string> settingsAfter = ReadAppSettings(databasePath);
        string[] changed = [.. RedesignSettings.Where(k => settingsBefore.GetValueOrDefault(k) != settingsAfter.GetValueOrDefault(k))];
        Check($"Redesign checks remove their test data and put every setting back ({(changed.Length == 0 ? "settings as before" : "changed: " + string.Join(", ", changed))})",
            changed.Length == 0
            && !library.Groups.Any(g => g.Name.StartsWith("Selftest", StringComparison.Ordinal))
            && !library.Connections.Any(c => c.Name.StartsWith("Selftest Redesign", StringComparison.Ordinal))
            && !window.Connections.Any(c => c.Name.StartsWith("Selftest Redesign", StringComparison.Ordinal))
            && window.SessionWindows.Count == 0 && window.TypeFilter is null && !window.OnlineOnly
            && look.Theme == themeBefore && look.Accent == accentBefore && look.TileSize == sizeBefore
            && window.Filter == filterBefore && window.IsListView == wasList);
    }

    // --- "Zuletzt …" on a tile --------------------------------------------------------------------------------

    private static void RunAgoChecks(MainWindow window)
    {
        DateTime now = DateTime.UtcNow;
        string Ago(TimeSpan age) => ConnectionItem.DescribeAgo(age, now - age);
        DateTime old = now.AddDays(-20);
        string date = string.Format(CultureInfo.CurrentCulture, Strings.Ago_Date, old.ToLocalTime().ToString("d", CultureInfo.CurrentCulture));
        string[] labels =
        [
            Ago(TimeSpan.FromSeconds(30)), Ago(TimeSpan.FromSeconds(320)), Ago(TimeSpan.FromHours(3.2)),
            Ago(TimeSpan.FromHours(30)), Ago(TimeSpan.FromDays(4.1)), Ago(TimeSpan.FromDays(20)),
        ];
        Check($"Ages read like a person would say them ({string.Join(" | ", labels)})",
            labels.SequenceEqual(["gerade eben", "vor 5 min", "vor 3 Std.", "gestern", "vor 4 Tagen", date])
            && date.Contains(old.ToLocalTime().Year.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal));

        string[] edges =
        [
            Ago(TimeSpan.FromSeconds(59)), Ago(TimeSpan.FromSeconds(60)), Ago(TimeSpan.FromMinutes(59.9)), Ago(TimeSpan.FromMinutes(60)),
            Ago(TimeSpan.FromHours(23.9)), Ago(TimeSpan.FromHours(24)), Ago(TimeSpan.FromHours(47.9)), Ago(TimeSpan.FromHours(48)),
            Ago(TimeSpan.FromDays(13.9)),
        ];
        Check($"Ages switch units at a minute, an hour, a day, two days, and two weeks ({string.Join(" | ", edges)})",
            edges.SequenceEqual(["gerade eben", "vor 1 min", "vor 59 min", "vor 1 Std.", "vor 23 Std.", "gestern", "gestern", "vor 2 Tagen", "vor 13 Tagen"])
            && ConnectionItem.DescribeAgo(TimeSpan.FromDays(14), now.AddDays(-14)).StartsWith("am ", StringComparison.Ordinal));

        // The footer of a tile; the time may be stored as UTC or come as local time.
        ConnectionItem item = window.CreateItem(new Connection { Name = "Selftest Redesign Fußzeile", Host = "rm-selftest-redesign-footer.invalid" });
        string never = item.FooterLabel;
        item.Model.LastConnectedAt = DateTime.UtcNow.AddSeconds(-320);
        string minutes = item.FooterLabel;
        item.Model.LastConnectedAt = DateTime.Now.AddHours(-3.2);
        string hours = item.FooterLabel;
        item.Model.LastConnectedAt = DateTime.UtcNow.AddHours(-30);
        string yesterday = item.FooterLabel;
        Check($"Tile footer says when a connection was last used ({never} | {minutes} | {hours} | {yesterday})",
            never == "Noch nie verbunden" && minutes == "Zuletzt vor 5 min" && hours == "Zuletzt vor 3 Std." && yesterday == "Zuletzt gestern");
    }

    // --- Status and answer time ---------------------------------------------------------------------------------

    /// <summary>The item alone (it is not listed, so no real status result reaches it).</summary>
    private static void RunLatencyChecks(MainWindow window)
    {
        ConnectionItem item = window.CreateItem(new Connection { Name = "Selftest Redesign Antwortzeit", Host = "rm-selftest-redesign-latency.invalid" });
        int notified = 0;
        item.PropertyChanged += (_, _) => notified++;
        bool fresh = !item.HasLatency && item.LatencyLabel.Length == 0 && item.LatencyHistory.Count == 0 && item.StatusCheckedAt is null
            && item.Status == OnlineStatus.Unknown;

        item.SetStatus(OnlineStatus.Online, TimeSpan.FromMilliseconds(12));
        bool online = item.Status == OnlineStatus.Online && item.HasLatency && item.LatencyLabel == "12 ms" && item.Latency == TimeSpan.FromMilliseconds(12)
            && item.LatencyHistory.SequenceEqual(new double?[] { 12 }) && item.StatusCheckedAt is not null && item.StatusLabel == Strings.Status_Online && notified > 0;
        Check($"An online result with an answer time shows \"{item.LatencyLabel}\" and starts the history", fresh && online);

        item.SetStatus(OnlineStatus.Offline, null);
        bool offline = item.Status == OnlineStatus.Offline && !item.HasLatency && item.LatencyLabel.Length == 0 && item.Latency is null
            && item.LatencyHistory.SequenceEqual(new double?[] { 12, null });
        // A time that comes with "offline" or "unknown" is not an answer of the target.
        item.SetStatus(OnlineStatus.Unknown, TimeSpan.FromMilliseconds(30));
        bool ignored = !item.HasLatency && item.LatencyHistory.SequenceEqual(new double?[] { 12, null, null });
        Check("An offline or unknown result clears the answer time and leaves a gap in the history", offline && ignored);

        item.SetStatus(OnlineStatus.Online, TimeSpan.FromMilliseconds(0.2));
        string fastest = item.LatencyLabel;
        item.Status = OnlineStatus.Online; // the plain setter: a status without a measured time
        bool unmeasured = item.Status == OnlineStatus.Online && !item.HasLatency;
        for (int i = 0; i < ConnectionItem.LatencySamples + 16; i++)
            item.SetStatus(OnlineStatus.Online, TimeSpan.FromMilliseconds(100 + i));
        Check($"The history keeps the last {ConnectionItem.LatencySamples} checks; an answer below a millisecond reads \"{fastest}\"",
            fastest == "1 ms" && unmeasured && item.LatencyHistory.Count == ConnectionItem.LatencySamples
            && item.LatencyHistory[0] == 116 && item.LatencyHistory[^1] == 100 + ConnectionItem.LatencySamples + 15);
    }

    /// <summary>
    /// The whole way of an answer time: the app's status checker against a local port, the result on the item
    /// in the overview. Then the port closes: the next check has no answer.
    /// </summary>
    private static async Task RunStatusRoundTripChecksAsync(MainWindow window)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        ConnectionItem live = window.CreateItem(new Connection
        {
            Name = "Selftest Redesign Lauscher", Type = ConnectionType.Ssh, Host = "127.0.0.1", Port = port,
            Description = "Local listener for the status check",
        });
        window.ShowUnsaved(live, true);
        try
        {
            await AppHost.Current.Status.CheckAsync([live.Model]);
            await WaitFor(() => live.StatusCheckedAt is not null, TimeSpan.FromSeconds(3));
            await Settle(200);
            Control? tile = window.Tiles.ContainerFromItem(live);
            bool onTile = tile?.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsVisible && t.Text == live.LatencyLabel && live.LatencyLabel.Length > 0) == true;
            Check($"A status check against a listening port shows online with its answer time on the tile ({live.StatusLabel}, {live.LatencyLabel})",
                live.Status == OnlineStatus.Online && live.HasLatency && live.Latency < TimeSpan.FromSeconds(2)
                && live.LatencyHistory.Count > 0 && live.LatencyHistory[^1] is > 0 && onTile);

            listener.Stop();
            int checks = live.LatencyHistory.Count;
            await AppHost.Current.Status.CheckAsync([live.Model]);
            await WaitFor(() => live.LatencyHistory.Count > checks, TimeSpan.FromSeconds(3));
            await Settle(200);
            // Windows takes about two seconds to refuse a loopback port, which is the checker's timeout: offline or unknown.
            Check($"After the port closed the next check has no answer time and leaves a gap ({live.StatusLabel})",
                live.Status != OnlineStatus.Online && !live.HasLatency && live.LatencyHistory.Count > checks
                && live.LatencyHistory[^1] is null && live.LatencyHistory.Any(v => v is not null));
        }
        finally
        {
            listener.Stop();
            window.ShowUnsaved(live, false);
        }
    }

    /// <summary>The answer-time card of the detail panel: only for a connection with at least one measured answer.</summary>
    private static async Task RunDetailLatencyChecksAsync(MainWindow window, ConnectionItem rdp, ConnectionItem tool)
    {
        window.Select([rdp]);
        await Settle(400);
        ConnectionDetailPanel panel = window.DetailPanel;

        // From here on without a pause, so a status result of the periodic check cannot come in between.
        bool hiddenAtFirst = panel.Shown == rdp && panel.SingleView.IsEffectivelyVisible && !panel.LatencyCard.IsVisible;
        rdp.SetStatus(OnlineStatus.Offline, null);
        bool hiddenWithoutAnswer = !panel.LatencyCard.IsVisible && panel.StatusPillText.Text == Strings.Status_Offline;
        rdp.SetStatus(OnlineStatus.Online, TimeSpan.FromMilliseconds(12));
        bool shown = panel.LatencyCard.IsVisible && panel.LatencyText.Text == "12 ms" && panel.StatusPillText.Text == Strings.Status_Online
            && panel.LatencyLine.Values is { } line && line.SequenceEqual(rdp.LatencyHistory) && line[^1] == 12;
        rdp.SetStatus(OnlineStatus.Offline, null);
        bool noAnswer = panel.LatencyCard.IsVisible && panel.LatencyText.Text == Strings.Details_NoAnswer
            && panel.LatencyLine.Values is { } gap && gap[^1] is null;
        window.Select([tool]);
        bool hiddenForTool = panel.Shown == tool && !panel.LatencyCard.IsVisible;
        Check("Detail panel: the answer-time card shows only for a connection with a measured answer, and \"keine Antwort\" after a failed check",
            hiddenAtFirst && hiddenWithoutAnswer && shown && noAnswer && hiddenForTool);

        // A short history with a dropout, for the picture.
        ApplyRedesignStatuses(rdp, null, tool);
        window.Select([rdp]);
        await Settle(500);
        SaveRenderProbe(window, "redesign-detail-answer-time.png");
    }

    /// <summary>Made-up status results: the first connection answers (with a dropout in its history), the second refuses, the third is unknown.</summary>
    private static void ApplyRedesignStatuses(ConnectionItem online, ConnectionItem? offline, ConnectionItem? unknown)
    {
        int[] times = [14, 12, 19, 13, 0, 0, 16, 12, 11, 15, 12];
        foreach (int ms in times)
            online.SetStatus(ms > 0 ? OnlineStatus.Online : OnlineStatus.Unknown, ms > 0 ? TimeSpan.FromMilliseconds(ms) : null);
        offline?.SetStatus(OnlineStatus.Offline, null);
        unknown?.SetStatus(OnlineStatus.Unknown, null);
    }

    /// <summary>The lamp panel of a connection without a preview: the same picture for the same id, another one for another id.</summary>
    private static void RunHostGlyphChecks()
    {
        byte[] first = RenderGlyph("selftest-redesign-a"), again = RenderGlyph("selftest-redesign-a"), other = RenderGlyph("selftest-redesign-b");
        byte[] empty = RenderGlyph(null, lit: false);
        Check("The lamp panel of a connection is the same on every render and differs between connections",
            first.Length > 0 && first.SequenceEqual(again) && !first.SequenceEqual(other) && !first.SequenceEqual(empty));
    }

    private static byte[] RenderGlyph(string? seed, bool lit = true)
    {
        var size = new Size(220, 120);
        var glyph = new HostGlyph { Seed = seed, Brush = lit ? Brushes.Orange : Brushes.Transparent, Width = size.Width, Height = size.Height };
        glyph.Measure(size);
        glyph.Arrange(new Avalonia.Rect(size));
        using var bitmap = new RenderTargetBitmap(PixelSize.FromSize(size, 1), new Vector(96, 96));
        bitmap.Render(glyph);
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    // --- Title bar ------------------------------------------------------------------------------------------------

    private static async Task RunTitleBarChecksAsync(MainWindow window, ConnectionItem rdp)
    {
        window.ShowFilter(SidebarEntry.AllTag);
        await Settle(300);
        TextBox search = window.SearchBox;
        double barHeight = window.TitleBar.Bounds.Height;
        Point? searchAt = search.TranslatePoint(new Point(0, 0), window);
        Point? contentRight = window.LauncherButton.TranslatePoint(new Point(window.LauncherButton.Bounds.Width, 0), window);
        bool inBar = search.IsEffectivelyVisible && search.GetVisualAncestors().Contains(window.TitleBarContent)
            && searchAt is { } p && p.Y >= 0 && p.Y + search.Bounds.Height <= barHeight + 0.5;
        // The three caption buttons need the right end of the bar.
        bool captionRoom = contentRight is { } r && r.X <= window.ClientSize.Width - 130;
        Check($"The window draws its own title bar with the search box in it (bar {barHeight:0} px, search at {searchAt?.Y:0}, content ends {window.ClientSize.Width - (contentRight?.X ?? 0):0} px before the edge)",
            window.IsExtendedIntoWindowDecorations && barHeight is >= 40 and <= 48 && inBar && captionRoom);

        int before = window.VisibleConnections.Count;
        window.Activate();
        window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F, KeyModifiers = KeyModifiers.Control, Source = window });
        await Settle(200);
        bool focused = search.IsFocused;
        search.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = rdp.Name, Source = search });
        await Settle(300);
        bool typed = search.Text == rdp.Name;
        bool narrowed = window.VisibleConnections.SequenceEqual([rdp]) && window.TileEntries.OfType<ConnectionItem>().SequenceEqual([rdp]);
        SaveRenderProbe(window, "redesign-search.png");
        search.Text = "";
        await Settle(300);
        Check($"Typing in the title bar search filters the overview ({before} -> 1 -> {window.VisibleConnections.Count}; Strg+F focused it: {focused})",
            before > 1 && typed && narrowed && focused && window.VisibleConnections.Count == before);
    }

    // --- Sidebar --------------------------------------------------------------------------------------------------

    private static async Task RunSidebarChecksAsync(MainWindow window, ConnectionGroup group, ConnectionGroup sub,
        ConnectionItem rdp, ConnectionItem ssh, ConnectionItem tool, string databasePath)
    {
        ConnectionLibrary library = AppHost.Current.Library;
        ConnectionLibrary Reopened() => new(new SqliteConnectionStore(databasePath));
        const string sidebarSetting = "overview.sidebar";
        ConnectionItem[] mine = [rdp, ssh, tool];

        window.ShowFilter(SidebarEntry.AllTag);
        await Settle(400);
        SidebarEntry? all = window.FindSidebarEntry(SidebarEntry.AllTag), favorites = window.FindSidebarEntry(SidebarEntry.FavoritesTag),
            recent = window.FindSidebarEntry(SidebarEntry.RecentTag), active = window.FindSidebarEntry(SidebarEntry.ActiveTag);
        int total = library.Connections.Count, starred = library.Connections.Count(c => c.IsFavorite);
        bool fixedEntries = window.SidebarEntries.Take(4).Select(e => e.Tag)
            .SequenceEqual([SidebarEntry.AllTag, SidebarEntry.FavoritesTag, SidebarEntry.RecentTag, SidebarEntry.ActiveTag]);
        bool counts = all?.Count == total && all.CountLabel == total.ToString(CultureInfo.CurrentCulture)
            && favorites?.Count == starred && favorites.CountLabel == starred.ToString(CultureInfo.CurrentCulture)
            && recent is { Count: null, CountLabel: "" } && active is { Count: 0, CountLabel: "", ShowLamp: false };
        bool rowShowsCount = SidebarControl(window, all)?.GetVisualDescendants().OfType<TextBlock>()
            .Any(t => t.Classes.Contains("navCount") && t.IsVisible && t.Text == all!.CountLabel) == true;
        Check($"Sidebar starts with all ({all?.CountLabel}), favorites ({favorites?.CountLabel}), recent, and active sessions, counted like the library ({total}, {starred} favorites)",
            fixedEntries && counts && rowShowsCount);

        // The group tree: a subgroup is listed below its parent while that is expanded.
        SidebarEntry? groupEntry = SidebarItem(window, group), subEntry = SidebarItem(window, sub);
        List<SidebarEntry> entries = [.. window.SidebarEntries];
        SidebarEntry[] labels = [.. entries.Where(e => e.IsHeader)];
        bool tree = groupEntry is { HasChildren: true, IsExpanded: true, Count: 3, Depth: 0, IsHeader: false }
            && subEntry is { HasChildren: false, Count: 1, Depth: 1 } && entries.IndexOf(subEntry) == entries.IndexOf(groupEntry) + 1
            && labels.Length == 2 && labels[0] is { Label: var groupsLabel, CanAdd: true } && groupsLabel == Strings.Nav_Groups
            && labels[1].Label == Strings.Nav_Tags && !labels[1].CanAdd
            && window.FindSidebarEntry(SidebarEntry.TagPrefix + "selftest-redesign") is { Count: 1 };

        Control? groupRow = SidebarControl(window, groupEntry);
        Button? chevron = groupRow?.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("navChevron"));
        bool chevronShown = chevron is { IsEffectivelyVisible: true }
            && SidebarControl(window, subEntry)?.GetVisualDescendants().OfType<Button>().Any(b => b.Classes.Contains("navChevron") && b.IsVisible) == false;
        Press(chevron);
        await Settle(400);
        GroupHeaderItem? header = window.TileEntries.OfType<GroupHeaderItem>().FirstOrDefault(h => h.Group == group);
        bool collapsed = SidebarItem(window, sub) is null && SidebarItem(window, group) is { IsExpanded: false, HasChildren: true, Count: 3 }
            && group.IsCollapsed && Reopened().FindGroup(group.Id)?.IsCollapsed == true
            && window.Filter == SidebarEntry.AllTag // the chevron folds, it does not select the row
            && header is { IsCollapsed: true } && !window.TileEntries.Contains(rdp);
        SaveRenderProbe(window, "redesign-sidebar-collapsed.png");
        if (header is not null)
            window.ToggleCollapsed(header);
        await Settle(400);
        bool expanded = SidebarItem(window, sub) is { Depth: 1 } && SidebarItem(window, group) is { IsExpanded: true }
            && !group.IsCollapsed && Reopened().FindGroup(group.Id)?.IsCollapsed == false && window.TileEntries.Contains(rdp);
        Check("Sidebar group tree: the subgroup shows only while its group is expanded (chevron in the sidebar, header in the overview; saved)",
            tree && chevronShown && collapsed && expanded);

        // Selecting a row is the filter.
        async Task<bool> Choose(SidebarEntry? entry)
        {
            if (entry is null)
                return false;
            window.Navigation.SelectedItem = entry;
            await Settle(300);
            return window.Filter == entry.Tag && window.Navigation.SelectedItem is SidebarEntry selected && selected.Tag == entry.Tag;
        }

        bool groupChosen = await Choose(SidebarItem(window, group)) && window.VisibleConnections.Count == 3 && mine.All(window.VisibleConnections.Contains)
            && window.PageTitle.Text == "Selftest Redesign";
        bool subChosen = await Choose(SidebarItem(window, sub)) && window.VisibleConnections.SequenceEqual([tool])
            && window.PageTitle.Text == "Selftest Redesign / Selftest Unterbereich";
        bool favoritesChosen = await Choose(window.FindSidebarEntry(SidebarEntry.FavoritesTag)) && window.VisibleConnections.Count == starred
            && window.VisibleConnections.All(c => c.IsFavorite) && window.VisibleConnections.Contains(rdp) && window.PageTitle.Text == Strings.Nav_Favorites;
        bool recentChosen = await Choose(window.FindSidebarEntry(SidebarEntry.RecentTag));
        List<ConnectionItem> recents = [.. window.VisibleConnections];
        recentChosen &= recents.All(c => c.Model.LastConnectedAt is not null) && recents.Contains(rdp) && recents.IndexOf(rdp) < recents.IndexOf(ssh) && !recents.Contains(tool)
            && recents.Select(c => c.Model.LastConnectedAt).SequenceEqual(recents.Select(c => c.Model.LastConnectedAt).OrderByDescending(t => t));
        bool tagChosen = await Choose(window.FindSidebarEntry(SidebarEntry.TagPrefix + "selftest-redesign")) && window.VisibleConnections.SequenceEqual([rdp])
            && window.PageTitle.Text == "#selftest-redesign";
        bool allChosen = await Choose(window.FindSidebarEntry(SidebarEntry.AllTag)) && window.VisibleConnections.Count == total;
        Check($"Selecting a sidebar row filters the overview (group {groupChosen}, subgroup {subChosen}, favorites {favoritesChosen}, recent {recentChosen}, tag {tagChosen}, all {allChosen})",
            groupChosen && subChosen && favoritesChosen && recentChosen && tagChosen && allChosen);

        await Choose(SidebarItem(window, group));
        SidebarEntry? label = window.SidebarEntries.FirstOrDefault(e => e.IsHeader);
        bool labelRow = SidebarControl(window, label)?.Classes.Contains("navHeader") == true;
        window.Navigation.SelectedItem = label;
        await Settle(300);
        Check("A section label of the sidebar is not selectable: the filter and its row stay",
            label is not null && labelRow && window.Filter == SidebarEntry.GroupPrefix + group.Id
            && window.Navigation.SelectedItem is SidebarEntry { IsHeader: false } kept && kept.Tag == window.Filter && window.VisibleConnections.Count == 3);

        // Wide window: the menu button folds the sidebar to icons and back; the choice is saved.
        string? savedBefore = library.GetSetting(sidebarSetting);
        double contentLeft = ContentLeft(window);
        bool wide = window.IsWideLayout && window.SidebarMode == SidebarMode.Expanded && !window.IsSidebarFloating
            && Near(window.SidebarSlot.Bounds.Width, 240) && savedBefore is null;
        window.ToggleSidebar();
        await Settle(500);
        List<SidebarEntry> compactEntries = [.. window.SidebarEntries];
        bool compact = window.SidebarMode == SidebarMode.Compact && !window.IsSidebarFloating
            && library.GetSetting(sidebarSetting) == "compact" && Reopened().GetSetting(sidebarSetting) == "compact"
            && window.SidebarSlot.Bounds.Width < 80 && Near(window.Sidebar.Bounds.Width, window.SidebarSlot.Bounds.Width)
            && ContentLeft(window) < contentLeft - 100 && window.Navigation.Classes.Contains("compact");
        // Icons only: no labels, no tags, top-level groups with their name as a tooltip; the filter stays.
        bool compactRows = !compactEntries.Any(e => e.IsHeader || e.TagName is not null) && SidebarItem(window, sub) is null
            && SidebarItem(window, group) is { ShowTip: true, Tip: "Selftest Redesign", HasChildren: false }
            && compactEntries.Take(4).All(e => e.ShowTip && e.Tip == e.Label) && window.Filter == SidebarEntry.GroupPrefix + group.Id
            && SidebarControl(window, SidebarItem(window, group))?.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Classes.Contains("navLabel") && t.IsVisible) == false;
        SaveRenderProbe(window, "redesign-sidebar-compact.png");
        Press(window.SidebarToggle);
        await Settle(500);
        bool back = window.SidebarMode == SidebarMode.Expanded && library.GetSetting(sidebarSetting) is null && Reopened().GetSetting(sidebarSetting) is null
            && Near(window.SidebarSlot.Bounds.Width, 240) && Near(ContentLeft(window), contentLeft) && window.SidebarEntries.Any(e => e.IsHeader)
            && SidebarItem(window, sub) is not null;
        Check($"Wide window: the sidebar folds to icons and back, and the choice is saved (expanded {wide}, compact {compact}, icon rows {compactRows}, back {back})",
            wide && compact && compactRows && back);

        // Narrow window: icons only; the menu button opens the sidebar over the content until an entry is chosen.
        await ResizeAsync(window, 1024, 640);
        double narrowContentLeft = ContentLeft(window);
        bool narrow = !window.IsWideLayout && window.SidebarMode == SidebarMode.Compact && !window.IsSidebarFloating && !window.SidebarScrim.IsVisible
            && SidebarItem(window, sub) is null;
        window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.B, KeyModifiers = KeyModifiers.Control, Source = window });
        await Settle(500);
        bool floating = window.IsSidebarFloating && window.SidebarMode == SidebarMode.Compact && window.Sidebar.Classes.Contains("floating")
            && window.SidebarScrim.IsEffectivelyVisible && Near(window.Sidebar.Bounds.Width, 240) && window.SidebarSlot.Bounds.Width < 80
            && Near(ContentLeft(window), narrowContentLeft) // over the content, not beside it
            && window.SidebarEntries.Any(e => e.IsHeader) && SidebarItem(window, sub) is not null
            && library.GetSetting(sidebarSetting) is null; // a floating sidebar is for the moment
        SaveRenderProbe(window, "redesign-sidebar-floating.png");
        // A subgroup: listed while the sidebar is open, gone from the icons afterwards, and still the filter.
        window.Navigation.SelectedItem = SidebarItem(window, sub);
        await Settle(500);
        bool closedByChoice = !window.IsSidebarFloating && !window.SidebarScrim.IsVisible && window.Sidebar.Bounds.Width < 80
            && window.Filter == SidebarEntry.GroupPrefix + sub.Id && window.VisibleConnections.SequenceEqual([tool])
            && !window.Sidebar.Classes.Contains("floating") && SidebarItem(window, sub) is null;

        window.ToggleSidebar();
        await Settle(400);
        bool reopened = window.IsSidebarFloating;
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        var onContent = new Point(window.ClientSize.Width - 80, window.ClientSize.Height - 80);
        window.SidebarScrim.RaiseEvent(new PointerPressedEventArgs(window.SidebarScrim, pointer, window, onContent, 1,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
        await Settle(400);
        bool closedByClick = reopened && !window.IsSidebarFloating && !window.SidebarScrim.IsVisible && window.Filter == SidebarEntry.GroupPrefix + sub.Id;

        window.ToggleSidebar();
        await Settle(300);
        bool floatingAgain = window.IsSidebarFloating;
        await ResizeAsync(window, 1320, 840);
        bool closedByWidth = floatingAgain && !window.IsSidebarFloating && window.SidebarMode == SidebarMode.Expanded && !window.SidebarScrim.IsVisible;
        Check($"Narrow window: the sidebar opens over the content and closes on a choice, a click beside it, or a wide window (narrow {narrow}, floating {floating}, choice {closedByChoice}, click {closedByClick}, wide {closedByWidth})",
            narrow && floating && closedByChoice && closedByClick && closedByWidth);

        window.SetDetailsVisible(true);
        window.ShowFilter(SidebarEntry.AllTag);
        await Settle(300);
    }

    /// <summary>Where the content area starts, in window coordinates.</summary>
    private static double ContentLeft(MainWindow window) => window.ContentGrid.TranslatePoint(new Point(0, 0), window)?.X ?? -1;

    // --- Filter chips ---------------------------------------------------------------------------------------------

    private static async Task RunFilterChipChecksAsync(MainWindow window, ConnectionGroup group, ConnectionItem rdp, ConnectionItem ssh, ConnectionItem tool, string databasePath)
    {
        AppHost app = AppHost.Current;
        string Name(ConnectionType type) => app.Handlers[type].DisplayName;

        window.ClearChipFilters();
        window.ShowFilter(SidebarEntry.AllTag);
        await Settle(300);
        List<(string Label, string Count, bool Checked)> chips = ChipStates(window);
        ConnectionType[] present = [.. Enum.GetValues<ConnectionType>().Where(t => window.Connections.Any(c => c.Model.Type == t))];
        string[] expected = [Strings.Filter_AllTypes, .. present.Select(Name), Strings.Filter_OnlineOnly];
        bool everyType = chips.Select(c => c.Label).SequenceEqual(expected) && chips[0] is { Checked: true, Count: var allCount }
            && allCount == window.Connections.Count.ToString(CultureInfo.CurrentCulture)
            && present.All(t => chips.Any(c => c.Label == Name(t) && !c.Checked && c.Count == window.Connections.Count(i => i.Model.Type == t).ToString(CultureInfo.CurrentCulture)));
        Check($"Filter chips: \"Alle\", one per type that occurs, and \"Nur online\" ({string.Join(", ", chips.Select(c => $"{c.Label} {c.Count}"))})",
            everyType && expected.Length >= 5);

        // In the group of these checks: three connections of three types, in the order the types are declared.
        window.ShowFilter(SidebarEntry.GroupPrefix + group.Id);
        await Settle(300);
        chips = ChipStates(window);
        ConnectionType[] inGroup = [.. new[] { rdp, ssh, tool }.Select(c => c.Model.Type).Order()];
        bool scoped = chips.Select(c => (c.Label, c.Count)).SequenceEqual(
            [(Strings.Filter_AllTypes, "3"), .. inGroup.Select(t => (Name(t), "1")), (Strings.Filter_OnlineOnly, chips[^1].Count)]);

        window.SetTypeFilter(ConnectionType.Ssh);
        chips = ChipStates(window);
        bool byApi = window.TypeFilter == ConnectionType.Ssh && window.VisibleConnections.SequenceEqual([ssh])
            && chips.Single(c => c.Label == Name(ConnectionType.Ssh)).Checked && !chips[0].Checked && chips[0].Count == "3";
        Press(ChipOf(window, Name(ConnectionType.TeamViewer)));
        bool byClick = window.TypeFilter == ConnectionType.TeamViewer && window.VisibleConnections.SequenceEqual([tool])
            && ChipStates(window).Single(c => c.Label == Name(ConnectionType.TeamViewer)).Checked;
        Press(ChipOf(window, Name(ConnectionType.TeamViewer)));
        bool clickedOff = window.TypeFilter is null && window.VisibleConnections.Count == 3;
        window.SetTypeFilter(ConnectionType.Rdp);
        Press(ChipOf(window, Strings.Filter_AllTypes));
        bool allAgain = window.TypeFilter is null && window.VisibleConnections.Count == 3 && ChipStates(window)[0].Checked;
        Check($"A type chip narrows the overview to that type; clicking it again or \"Alle\" shows every type (scope {scoped}, set {byApi}, click {byClick}, off {clickedOff}, all {allAgain})",
            scoped && byApi && byClick && clickedOff && allAgain);

        // "Nur online" with made-up results; no pause before the checks, so no real result comes in between.
        Dictionary<string, string> settingsBefore = ReadAppSettings(databasePath);
        ApplyRedesignStatuses(rdp, ssh, tool);
        window.SetOnlineOnly(true);
        chips = ChipStates(window);
        bool onlineOnly = window.OnlineOnly && window.VisibleConnections.SequenceEqual([rdp]) && chips[^1] is { Checked: true, Count: "1" } && chips[0].Count == "3";
        Press(ChipOf(window, Strings.Filter_OnlineOnly));
        bool onlineOff = !window.OnlineOnly && window.VisibleConnections.Count == 3;
        bool health = window.Health is { Online: 1, Offline: 1, Unknown: 1 };
        Check("\"Nur online\" shows only the connections that answer, and the health bar counts online, offline, and unknown", onlineOnly && onlineOff && health);

        // TeamViewer and online: the group has connections, but none passes.
        window.SetTypeFilter(ConnectionType.TeamViewer);
        window.SetOnlineOnly(true);
        bool impossible = window.VisibleConnections.Count == 0 && window.CurrentEmptyKind == MainWindow.EmptyKind.Filtered
            && window.EmptyState.IsVisible && window.EmptyTitle.Text == Strings.Empty_Filtered_Title
            && window.EmptyPrimaryButton is { IsVisible: true, Content: string clear } && clear == Strings.Empty_ClearFilters
            && ChipStates(window).Any(c => c.Label == Name(ConnectionType.TeamViewer) && c.Checked);
        // Chips are for the moment: nothing of them reaches the settings table.
        bool notSaved = SameSettings(settingsBefore, ReadAppSettings(databasePath));
        await Settle(400);
        SaveRenderProbe(window, "redesign-empty-filtered.png");
        Press(window.EmptyPrimaryButton);
        await Settle(300);
        Check("Filters that leave nothing say so (\"Kein Treffer mit diesen Filtern\"), and \"Filter zurücksetzen\" brings the connections back",
            impossible && window.TypeFilter is null && !window.OnlineOnly && window.VisibleConnections.Count == 3
            && window.CurrentEmptyKind == MainWindow.EmptyKind.None && !window.EmptyState.IsVisible);
        Check("Chip filters are not saved (the settings table is unchanged)", notSaved);

        // A selected type stays as a chip where it does not occur, so it can be switched off there.
        window.SetTypeFilter(ConnectionType.TeamViewer);
        window.SearchBox.Text = rdp.Name;
        await Settle(300); // the text box reports a changed text with the next dispatcher round
        chips = ChipStates(window);
        bool staysAtZero = chips.Any(c => c.Label == Name(ConnectionType.TeamViewer) && c is { Checked: true, Count: "0" })
            && window.CurrentEmptyKind == MainWindow.EmptyKind.Filtered;
        window.SearchBox.Text = "";
        await Settle(200);
        window.ClearChipFilters();
        window.ShowFilter(SidebarEntry.AllTag);
        await Settle(300);
        Check("A selected type keeps its chip where no connection has it", staysAtZero && ChipStates(window).All(c => c.Checked == (c.Label == Strings.Filter_AllTypes)));
    }

    private static List<(string Label, string Count, bool Checked)> ChipStates(MainWindow window) =>
    [
        .. window.FilterChips.Select(chip =>
        {
            string[] texts = [.. (chip.Content as Panel)?.Children.OfType<TextBlock>().Select(t => t.Text ?? "") ?? []];
            return (texts.ElementAtOrDefault(0) ?? "", texts.ElementAtOrDefault(1) ?? "", chip.IsChecked == true);
        }),
    ];

    private static Button? ChipOf(MainWindow window, string label) =>
        window.FilterChips.FirstOrDefault(chip => (chip.Content as Panel)?.Children.OfType<TextBlock>().FirstOrDefault()?.Text == label);

    // --- Tile size ------------------------------------------------------------------------------------------------

    private static async Task RunTileSizeChecksAsync(MainWindow window, ConnectionItem rdp, string databasePath)
    {
        ConnectionLibrary library = AppHost.Current.Library;
        const string setting = "appearance.tileSize";
        window.SetListView(false);
        window.ShowFilter(SidebarEntry.AllTag);
        await Settle(300);

        var seen = new Dictionary<TileSize, (double Min, double Max, double Tile, double Height, bool Preview)>();
        bool applied = true, saved = true;
        foreach (TileSize size in new[] { TileSize.Small, TileSize.Large, TileSize.Medium })
        {
            // Once through the switch in the header, otherwise through the same call the menu makes.
            if (size == TileSize.Large)
                Press(window.SizeLargeToggle);
            else
                window.SetTileSize(size);
            await Settle(600);

            if (window.TilePanel is not { } panel || window.Tiles.ContainerFromItem(rdp) is not { } tile)
            {
                applied = false;
                continue;
            }

            // The panel gives every tile the same slot; the tile's margin is part of it.
            double slot = tile.Bounds.Width + tile.Margin.Left + tile.Margin.Right;
            bool preview = tile.GetVisualDescendants().OfType<Grid>().Any(g => g.Classes.Contains("tilePreview") && g.IsVisible);
            seen[size] = (panel.MinTileWidth, panel.MaxTileWidth, panel.TileWidth, tile.Bounds.Height, preview);
            applied &= window.TileSize == size && Near(slot, panel.TileWidth)
                && panel.TileWidth >= panel.MinTileWidth - 0.5 && panel.TileWidth <= panel.MaxTileWidth + 0.5
                && panel.Bounds.Width <= window.Tiles.Bounds.Width + 0.5
                && window.Tiles.Classes.Contains("small") == (size == TileSize.Small) && window.Tiles.Classes.Contains("large") == (size == TileSize.Large)
                && window.SizeSmallToggle.IsChecked == (size == TileSize.Small) && window.SizeMediumToggle.IsChecked == (size == TileSize.Medium)
                && window.SizeLargeToggle.IsChecked == (size == TileSize.Large);
            saved &= library.GetSetting(setting) == size.ToString().ToLowerInvariant()
                && new Appearance(new ConnectionLibrary(new SqliteConnectionStore(databasePath))).TileSize == size;
            if (size != TileSize.Medium)
                SaveRenderProbe(window, $"redesign-tiles-{size.ToString().ToLowerInvariant()}.png");
        }

        bool ordered = seen.Count == 3
            && seen[TileSize.Small].Min < seen[TileSize.Medium].Min && seen[TileSize.Medium].Min < seen[TileSize.Large].Min
            && seen[TileSize.Small].Max < seen[TileSize.Medium].Max && seen[TileSize.Medium].Max < seen[TileSize.Large].Max
            && seen[TileSize.Small].Tile <= seen[TileSize.Medium].Tile && seen[TileSize.Medium].Tile <= seen[TileSize.Large].Tile
            && seen[TileSize.Small].Height < seen[TileSize.Medium].Height && seen[TileSize.Medium].Height < seen[TileSize.Large].Height
            && !seen[TileSize.Small].Preview && seen[TileSize.Medium].Preview && seen[TileSize.Large].Preview;
        string sizes = string.Join(", ", seen.OrderBy(s => s.Key).Select(s => $"{s.Key} {s.Value.Tile:0} x {s.Value.Height:0}"));
        Check($"Tile size S, M, L changes the tile width and height; small tiles have no picture ({sizes})", applied && ordered);
        Check("The tile size is saved and comes back after a restart", saved);

        // The switch belongs to the tiles: off in the list, on again in the tiles, also after a size change in between.
        window.SetListView(true);
        await Settle(300);
        bool offInList = !window.SizeSwitch.IsEnabled;
        window.SetTileSize(TileSize.Small);
        window.SetListView(false);
        await Settle(300);
        bool onInTiles = window.SizeSwitch.IsEnabled && window.Tiles.Classes.Contains("small");
        window.SetTileSize(TileSize.Medium);
        await Settle(400);
        Check($"The S/M/L switch is enabled in the tile view only (list: {(offInList ? "off" : "on")}, back in the tiles: {(onInTiles ? "on" : "off")})",
            offInList && onInTiles && window.TileSize == TileSize.Medium);
    }

    // --- Theme and signal color -----------------------------------------------------------------------------------

    private static async Task RunAppearanceChecksAsync(MainWindow window, ConnectionItem rdp, ConnectionItem ssh, ConnectionItem tool, string databasePath)
    {
        Application application = Application.Current!;
        AppHost app = AppHost.Current;
        Appearance look = app.Appearance;
        Appearance Restarted() => new(new ConnectionLibrary(new SqliteConnectionStore(databasePath)));
        window.SetListView(false);
        window.ShowFilter(SidebarEntry.AllTag);

        var panelLuminance = new Dictionary<AppTheme, double>();
        var onlineColor = new Dictionary<AppTheme, Color?>();
        bool themes = true, accents = true, lamps = true, buttons = true;
        foreach (AppTheme theme in new[] { AppTheme.Dark, AppTheme.Light })
        {
            bool dark = theme == AppTheme.Dark;
            ThemeVariant variant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            look.SetTheme(theme);
            await Settle(300);
            // No status result since the switch: the lamp on the tile was repainted by the theme change alone.
            Ellipse? tileLamp = window.Tiles.ContainerFromItem(rdp)?.GetVisualDescendants().OfType<Ellipse>()
                .FirstOrDefault(e => e.Classes.Contains("lamp") && e.IsEffectivelyVisible);
            string lampKey = rdp.Status switch { OnlineStatus.Online => "RmOnlineBrush", OnlineStatus.Offline => "RmOfflineBrush", _ => "RmUnknownBrush" };
            lamps &= tileLamp?.Fill is ISolidColorBrush shown && shown.Color == ResourceColor(lampKey);
            onlineColor[theme] = ResourceColor("RmOnlineBrush");
            themes &= application.ActualThemeVariant == variant && window.ActualThemeVariant == variant
                && app.Library.GetSetting("appearance.theme") == (dark ? "dark" : "light") && Restarted().Theme == theme;
            panelLuminance[theme] = ResourceColor("RmPanelBrush") is { } surface ? RelativeLuminanceOf(surface) : -1;

            var ratios = new List<string>();
            bool readable = true;
            foreach (AccentPreset preset in Appearance.Accents)
            {
                look.SetAccent(preset);
                Color? fill = ResourceColor("RmAccentBrush"), ink = ResourceColor("RmOnAccentBrush");
                if (fill is not { } accent || ink is not { } onAccent)
                {
                    accents = readable = false;
                    continue;
                }

                // A fixed preset is exactly its color of the theme; "windows" is whatever the system says.
                accents &= (preset.IsSystem || accent == (dark ? preset.Dark : preset.Light))
                    && ResourceColor("AccentButtonBackground") == accent && ResourceColor("AccentButtonForeground") == onAccent
                    && ResourceColor("AccentButtonForegroundPointerOver") == onAccent && ResourceColor("RmAccentTextBrush") is not null
                    && app.Library.GetSetting("appearance.accent") == preset.Id && Restarted().Accent == preset;
                double ratio = ContrastRatio(accent, onAccent);
                readable &= ratio >= 4.5;
                ratios.Add($"{preset.Id} {accent} {ratio:0.0}");
            }

            Check($"Text on the signal color is readable for every color in the {(dark ? "dark" : "light")} theme, contrast at least 4.5 ({string.Join(", ", ratios)})",
                readable && ratios.Count == Appearance.Accents.Count);

            // The picture of the theme: the default amber, made-up statuses, one connection selected.
            look.SetAccent(Appearance.Accents[0]);
            ApplyRedesignStatuses(rdp, ssh, tool);
            window.Select([rdp]);
            window.Tiles.ScrollIntoView(rdp);
            await Settle(700);
            // The button itself, not only the resources: "Neue Verbindung" is filled with the signal color.
            ContentPresenter? face = window.AddButton.GetVisualDescendants().OfType<ContentPresenter>().FirstOrDefault();
            bool filled = face?.Background is ISolidColorBrush background && background.Color == ResourceColor("RmAccentBrush")
                && face.Foreground is ISolidColorBrush foreground && foreground.Color == ResourceColor("RmOnAccentBrush");
            if (!filled)
                Log($"  accent button in the {theme} theme: background {(face?.Background as ISolidColorBrush)?.Color}, text {(face?.Foreground as ISolidColorBrush)?.Color}, signal color {ResourceColor("RmAccentBrush")}");
            buttons &= filled;
            SaveRenderProbe(window, dark ? "redesign-dark.png" : "redesign-light.png");
            ApplyRedesignStatuses(rdp, ssh, tool);
            window.SetListView(true);
            await Settle(500);
            SaveRenderProbe(window, dark ? "redesign-dark-list.png" : "redesign-light-list.png");
            window.SetListView(false);
        }

        // The Windows accent can be any color: sweep the color cube, every fill must get readable text.
        double worst = double.MaxValue;
        Color worstFill = default;
        for (int r = 0; r <= 255; r += 15)
        {
            for (int g = 0; g <= 255; g += 15)
            {
                for (int b = 0; b <= 255; b += 15)
                {
                    Color fill = Color.FromRgb((byte)r, (byte)g, (byte)b);
                    double ratio = ContrastRatio(fill, Appearance.InkOn(fill));
                    if (ratio < worst)
                        (worst, worstFill) = (ratio, fill);
                }
            }
        }

        Check($"Any signal color gets text with a contrast of at least 4.5 (lowest {worst:0.00} on {worstFill})", worst >= 4.5);

        // "System" follows Windows.
        look.SetTheme(AppTheme.System);
        await Settle(300);
        ThemeVariant os = application.PlatformSettings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        bool system = application.ActualThemeVariant == os && app.Library.GetSetting("appearance.theme") == "system";

        Check($"Light and dark theme switch the whole app and are saved; \"System\" follows Windows ({os})",
            themes && system && panelLuminance[AppTheme.Dark] is >= 0 and < 0.2 && panelLuminance[AppTheme.Light] > 0.6);
        Check("The signal color sets the accent brushes and the accent button, and is saved", accents && buttons);
        Check($"Status lamps take the colors of the theme that is shown (online {onlineColor.GetValueOrDefault(AppTheme.Dark)} dark, {onlineColor.GetValueOrDefault(AppTheme.Light)} light)",
            lamps && onlineColor.GetValueOrDefault(AppTheme.Dark) is { } onDark && onlineColor.GetValueOrDefault(AppTheme.Light) is { } onLight && onDark != onLight);
    }

    private static Color? ResourceColor(string key) =>
        Application.Current is { } application && application.TryGetResource(key, application.ActualThemeVariant, out object? value)
        && value is ISolidColorBrush brush
            ? brush.Color
            : null;

    /// <summary>WCAG 2 relative luminance of an opaque color.</summary>
    private static double RelativeLuminanceOf(Color color)
    {
        static double Linear(byte channel)
        {
            double value = channel / 255.0;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Linear(color.R)) + (0.7152 * Linear(color.G)) + (0.0722 * Linear(color.B));
    }

    /// <summary>WCAG 2 contrast ratio of two opaque colors (1 to 21); normal text needs 4.5.</summary>
    private static double ContrastRatio(Color a, Color b)
    {
        double first = RelativeLuminanceOf(a), second = RelativeLuminanceOf(b);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }

    // --- Active sessions and where windows open --------------------------------------------------------------------

    private static async Task RunActiveSessionChecksAsync(MainWindow window)
    {
        // Not saved, like "Selftest A" below: the RDP client refuses loopback, so nothing ever connects.
        ConnectionItem item = window.CreateItem(new Connection
        {
            Id = TestId("redesign-session"), Name = "Selftest Redesign Sitzung", Host = "127.0.0.1", UserName = "selftest",
            Description = "Loopback: refused by the RDP client",
        });
        window.ShowUnsaved(item, true);
        SessionWindow? session = null;
        try
        {
            window.ShowFilter(SidebarEntry.ActiveTag);
            await Settle(400);
            bool noneBefore = window.FindSidebarEntry(SidebarEntry.ActiveTag) is { Count: 0, CountLabel: "", ShowLamp: false }
                && window.VisibleConnections.Count == 0 && window.CurrentEmptyKind == MainWindow.EmptyKind.NoSessions && !item.IsSessionOpen
                && window.PageTitle.Text == Strings.Nav_Active;
            SaveRenderProbe(window, "redesign-active-empty.png");

            // Open it the way a user does on a tile: the round connect button.
            window.ShowFilter(SidebarEntry.AllTag);
            window.Tiles.ScrollIntoView(item);
            await Settle(400);
            Press(window.Tiles.ContainerFromItem(item)?.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("tileConnect")));
            session = window.SessionWindows.OfType<SessionWindow>().FirstOrDefault(s => s.Connection == item);
            bool ready = session is not null && await WaitFor(() => session.Rdp.IsClientReady, TimeSpan.FromSeconds(20));
            await Settle(300);

            SidebarEntry? active = window.FindSidebarEntry(SidebarEntry.ActiveTag);
            bool lampOnRow = SidebarControl(window, active)?.GetVisualDescendants().OfType<Ellipse>().Any(e => e.IsVisible) == true;
            bool opened = ready && item.IsSessionOpen && item.ShowSessionBadge && active is { Count: 1, CountLabel: "1", ShowLamp: true } && lampOnRow
                && window.Tiles.ContainerFromItem(item)?.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == Strings.Session_OpenBadge) == true;
            window.ShowFilter(SidebarEntry.ActiveTag);
            await Settle(400);
            bool listed = window.VisibleConnections.SequenceEqual([item]) && window.CurrentEmptyKind == MainWindow.EmptyKind.None;
            SaveRenderProbe(window, "redesign-active-session.png");
            bool reused = session is not null && window.OpenSession(item) == session && window.SessionWindows.Count(w => w == session) == 1
                && window.FindSidebarEntry(SidebarEntry.ActiveTag) is { Count: 1 };
            Check($"An open session window marks its connection, counts in \"Aktive Sitzungen\" with a lamp, and that filter lists it (before {noneBefore}, opened {opened}, listed {listed})",
                noneBefore && opened && listed && reused);

            CheckSessionScreen(window, session);

            session?.Close();
            await Settle(600);
            active = window.FindSidebarEntry(SidebarEntry.ActiveTag);
            Check("Closing the session window clears the mark, the count with its lamp, and the filter's list",
                session is not null && !item.IsSessionOpen && !item.ShowSessionBadge && active is { Count: 0, CountLabel: "", ShowLamp: false }
                && SidebarControl(window, active)?.GetVisualDescendants().OfType<Ellipse>().Any(e => e.IsVisible) == false
                && window.VisibleConnections.Count == 0 && window.CurrentEmptyKind == MainWindow.EmptyKind.NoSessions
                && !window.SessionWindows.Contains(session));
        }
        finally
        {
            if (session is not null && window.SessionWindows.Contains(session))
                session.Close();
            window.ShowFilter(SidebarEntry.AllTag);
            window.ShowUnsaved(item, false);
            await Settle(300);
        }
    }

    /// <summary>Session windows open where the overview is, not always on the primary monitor.</summary>
    private static void CheckSessionScreen(MainWindow window, SessionWindow? session)
    {
        Screen? overview = window.Screens.ScreenFromWindow(window);
        if (window.Screens.All.Count < 2 || overview is null || overview.IsPrimary)
        {
            Log("SKIP session window screen (one screen, or the overview is on the primary one: nothing to tell apart).");
            return;
        }

        Screen? opened = session?.Screens.ScreenFromWindow(session);
        Check($"A session window opens on the screen of the overview ({DescribeScreen(overview)}; session on {DescribeScreen(opened)})",
            opened is not null && opened.Bounds == overview.Bounds && session is not null && overview.Bounds.Contains(session.Position));
    }

    private static string DescribeScreen(Screen? screen) =>
        screen is null ? "no screen" : $"{screen.Bounds.Width} x {screen.Bounds.Height} at {screen.Bounds.X},{screen.Bounds.Y}{(screen.IsPrimary ? ", primary" : "")}";

    /// <summary>
    /// REMOTE_MANAGER_SCREEN and the remembered place of the overview. The windows made here are never shown;
    /// the variable is changed for a moment only (no pause in between) and put back by the caller as well.
    /// </summary>
    private static void RunPlacementChecks(MainWindow window)
    {
        ConnectionLibrary library = AppHost.Current.Library;
        const string placeSetting = "window.main";
        IReadOnlyList<Screen> screens = window.Screens.All;
        string? variable = Environment.GetEnvironmentVariable(WindowPlacement.ScreenVariable);
        string? savedPlace = library.GetSetting(placeSetting);
        Screen? current = window.Screens.ScreenFromWindow(window);

        if (screens.Count < 2 || string.IsNullOrEmpty(variable))
        {
            Log($"SKIP forced screen ({(screens.Count < 2 ? "one screen" : WindowPlacement.ScreenVariable + " not set")}): the overview on the screen the variable names.");
        }
        else
        {
            Screen? forced = WindowPlacement.ForcedScreen(window.Screens);
            Check($"{WindowPlacement.ScreenVariable}={variable} names a screen that is not the primary one, and the overview is on it ({DescribeScreen(forced)}; overview on {DescribeScreen(current)})",
                forced is { IsPrimary: false } && current is not null && current.Bounds == forced.Bounds && forced.Bounds.Contains(window.Position));
        }

        try
        {
            Screen? Forced(string? value)
            {
                Environment.SetEnvironmentVariable(WindowPlacement.ScreenVariable, value);
                return WindowPlacement.ForcedScreen(window.Screens);
            }

            Screen? largestOther = screens.Where(s => !s.IsPrimary).OrderByDescending(s => (long)s.Bounds.Width * s.Bounds.Height).FirstOrDefault();
            bool byNumber = Enumerable.Range(1, screens.Count).All(n => Forced(n.ToString(CultureInfo.InvariantCulture))?.Bounds == screens[n - 1].Bounds);
            bool secondary = Forced("secondary")?.Bounds == largestOther?.Bounds && Forced("SECONDARY")?.Bounds == largestOther?.Bounds;
            bool nothing = Forced(null) is null && Forced("") is null && Forced("0") is null
                && Forced((screens.Count + 1).ToString(CultureInfo.InvariantCulture)) is null && Forced("rm-selftest-no-such-display") is null;
            Check($"{WindowPlacement.ScreenVariable} takes a screen number or \"secondary\" and ignores anything else ({screens.Count} screens)",
                byNumber && secondary && nothing);

            // The remembered place, without the variable: saved from one window, restored into a new one.
            Environment.SetEnvironmentVariable(WindowPlacement.ScreenVariable, null);
            Screen target = current ?? screens[0];
            var place = new PixelPoint(target.WorkingArea.X + 120, target.WorkingArea.Y + 90);
            var source = new Window { Width = 1000, Height = 640, Position = place };
            WindowPlacement.SaveMain(source, library);
            string? normal = library.GetSetting(placeSetting);
            // Maximized: the size from before stays, so "restore" gives a sensible window.
            source.Width = 2000;
            source.WindowState = WindowState.Maximized;
            WindowPlacement.SaveMain(source, library);
            string? maximized = library.GetSetting(placeSetting);
            bool saved = normal == $"{place.X},{place.Y},1000,640,0" && maximized == $"{place.X},{place.Y},1000,640,1";

            var restored = new Window { Width = 1320, Height = 840 };
            WindowPlacement.RestoreMain(restored, library);
            bool comesBack = restored.Position == place && restored.Width == 1000 && restored.Height == 640
                && restored.WindowState == WindowState.Maximized && restored.WindowStartupLocation == WindowStartupLocation.Manual;

            // A place on a monitor that is gone (docking station) is not used.
            library.SetSetting(placeSetting, "-32000,-32000,1000,640,0");
            var elsewhere = new Window { Width = 1320, Height = 840 };
            WindowStartupLocation startup = elsewhere.WindowStartupLocation;
            WindowPlacement.RestoreMain(elsewhere, library);
            bool ignored = elsewhere.Width == 1320 && elsewhere.Height == 840 && elsewhere.WindowStartupLocation == startup
                && elsewhere.Position.X > -30000;
            library.SetSetting(placeSetting, "nonsense");
            WindowPlacement.RestoreMain(elsewhere, library);
            ignored &= elsewhere.Width == 1320 && elsewhere.WindowStartupLocation == startup;
            Check($"The overview remembers its place and size, also maximized, and ignores a place on a monitor that is gone (saved {saved} \"{maximized}\", restored {comesBack} at {restored.Position}, ignored {ignored})",
                saved && comesBack && ignored);

            // The variable wins over a remembered place.
            if (largestOther is not null)
            {
                library.SetSetting(placeSetting, normal);
                Environment.SetEnvironmentVariable(WindowPlacement.ScreenVariable, "secondary");
                var forcedWindow = new Window { Width = 900, Height = 600 };
                WindowPlacement.RestoreMain(forcedWindow, library);
                Check("The variable wins over the remembered place: the window is centered on the named screen",
                    largestOther.WorkingArea.Contains(forcedWindow.Position) && forcedWindow.Width == 900
                    && largestOther.WorkingArea.Contains(forcedWindow.Position + new PixelPoint((int)(900 * largestOther.Scaling) - 1, (int)(600 * largestOther.Scaling) - 1)));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(WindowPlacement.ScreenVariable, variable);
            library.SetSetting(placeSetting, savedPlace);
        }
    }

    // --- Helpers --------------------------------------------------------------------------------------------------

    private static void Press(Button? button) => button?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static bool Near(double value, double expected) => Math.Abs(value - expected) < 1;

    /// <summary>Every row of app_settings, read from the file (what a restart would find).</summary>
    private static Dictionary<string, string> ReadAppSettings(string databasePath)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        using var db = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        db.Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT key, value FROM app_settings";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
            settings[reader.GetString(0)] = reader.IsDBNull(1) ? "" : reader.GetString(1);
        return settings;
    }

    private static bool SameSettings(Dictionary<string, string> before, Dictionary<string, string> after) =>
        before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out string? value) && value == pair.Value);
}
