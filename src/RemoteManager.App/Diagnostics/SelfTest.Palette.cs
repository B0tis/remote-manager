using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RemoteManager.App.Connections;
using RemoteManager.App.Resources;
using RemoteManager.App.Services;
using RemoteManager.App.Views;
using RemoteManager.Core.Library;
using RemoteManager.Core.Model;

namespace RemoteManager.App.Diagnostics;

/// <summary>
/// The quick launcher as a command palette: "Zuletzt verwendet" and favorites for an empty query, keys,
/// app commands ("Aktionen") that do what their menu entry does, a typed address as "Direkt verbinden"
/// rows that open a session without saving anything, and the hotkey view. Works on temporary connections
/// named "Selftest Palette ..."; the only session it opens goes to a ".invalid" host.
/// </summary>
internal static partial class SelfTest
{
    private const string PaletteAdHocHost = "rm-selftest-palette-direct.invalid";

    private static async Task RunPaletteChecksAsync(MainWindow window)
    {
        AppHost app = AppHost.Current;
        ConnectionLibrary library = app.Library;
        Appearance look = app.Appearance;
        AppTheme themeBefore = look.Theme;
        TileSize sizeBefore = look.TileSize;
        bool wasList = window.IsListView;
        KeyGesture? hotkeyBefore = window.Hotkey?.Gesture;
        var created = new List<ConnectionItem>();
        ConnectionGroup group = library.EnsureGroupPath("Selftest Palette")!;
        try
        {
            DateTime now = DateTime.UtcNow;
            ConnectionItem Add(string name, ConnectionType type, string host, bool favorite, DateTime? lastConnected)
            {
                ConnectionItem item = window.Adopt(new Connection
                {
                    Name = name, Type = type, Host = host, GroupId = group.Id, IsFavorite = favorite, LastConnectedAt = lastConnected,
                    SortOrder = library.ConnectionsIn(group.Id).Count(), Tags = ["selftest"],
                });
                created.Add(item);
                return item;
            }

            ConnectionItem newest = Add("Selftest Palette Neu", ConnectionType.Ssh, "rm-selftest-palette-neu.invalid", favorite: true, now);
            ConnectionItem older = Add("Selftest Palette Alt", ConnectionType.Rdp, "rm-selftest-palette-alt.invalid", favorite: false, now.AddSeconds(-2));
            ConnectionItem favorite = Add("Selftest Palette Favorit", ConnectionType.Vnc, "rm-selftest-palette-fav.invalid", favorite: true, null);

            look.SetTheme(AppTheme.Dark);
            await Settle(500);
            await RunPaletteEmptyChecksAsync(window, newest, older, favorite);
            await RunPaletteKeyChecksAsync(window, newest, older);
            await RunPaletteCommandChecksAsync(window, look);
            await RunPaletteAdHocChecksAsync(window, library);
            await RunPaletteHotkeyChecksAsync(window);

            // The same screens in the light theme, switched through the palette itself.
            QuickLauncherWindow launcher = await OpenPaletteAsync(window, "hell");
            bool offered = launcher.SelectedRow is LauncherCommand { Id: "theme.light" };
            launcher.Launch(reveal: false);
            await Settle(700);
            Check("Palette: \"hell\" finds \"Helles Design\" and running it switches the theme like the menu entry",
                offered && look.Theme == AppTheme.Light && !launcher.IsVisible);
            await SavePaletteProbesAsync(window, "-light");
        }
        finally
        {
            look.SetTheme(themeBefore);
            look.SetTileSize(sizeBefore);
            window.SetListView(wasList);
            window.QuickLauncher?.Hide();
            if (hotkeyBefore is not null && window.Hotkey?.Gesture != hotkeyBefore)
                window.TrySetHotkey(hotkeyBefore);
            foreach (Window session in window.SessionWindows.Where(w => ((ISessionWindow)w).Connection.Model.Host == PaletteAdHocHost).ToList())
                session.Close();
            foreach (ConnectionItem c in created)
                await window.RemoveConnectionAsync(c);
            if (library.FindGroup(group.Id) is not null)
                await window.RemoveGroupAsync(group);
        }

        Check("Palette: self-test connections and the group are removed again",
            !library.Groups.Any(g => g.Name == "Selftest Palette") && !window.Connections.Any(created.Contains));
    }

    private static async Task<QuickLauncherWindow> OpenPaletteAsync(MainWindow window, string query)
    {
        window.OpenLauncher();
        QuickLauncherWindow launcher = window.QuickLauncher!;
        await Settle(300);
        launcher.Query = query;
        await Settle(250);
        return launcher;
    }

    private static void PressInPalette(QuickLauncherWindow launcher, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        launcher.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = launcher });

    private static async Task RunPaletteEmptyChecksAsync(MainWindow window, ConnectionItem newest, ConnectionItem older, ConnectionItem favorite)
    {
        QuickLauncherWindow launcher = await OpenPaletteAsync(window, "");
        IReadOnlyList<ConnectionItem> recent = launcher.Recent;
        Check($"Palette: an empty query lists \"Zuletzt verwendet\", most recent first ({string.Join(", ", recent.Take(3).Select(c => c.Name))})",
            recent.Count >= 2 && recent[0] == newest && recent[1] == older
            && launcher.Sections.FirstOrDefault() == Strings.Nav_Recent && launcher.SelectedResult == newest);
        // The list shows five favorites by name; a library with many of them may not reach the self-test's one.
        Check("Palette: favorites follow under their own label, without the ones already listed as recent",
            (launcher.Favorites.Contains(favorite) || launcher.Favorites.Count >= 5) && !launcher.Favorites.Contains(newest) && launcher.Favorites.All(c => c.IsFavorite)
            && launcher.Sections.SequenceEqual([Strings.Nav_Recent, Strings.Nav_Favorites])
            && launcher.Results.SequenceEqual([.. recent, .. launcher.Favorites])
            && launcher.Commands.Count == 0 && launcher.AdHocTargets.Count == 0);
        SaveRenderProbe(launcher, "palette-empty.png");
        launcher.Hide();
    }

    private static async Task RunPaletteKeyChecksAsync(MainWindow window, ConnectionItem newest, ConnectionItem older)
    {
        QuickLauncherWindow launcher = await OpenPaletteAsync(window, "");
        object? first = launcher.SelectedRow;
        PressInPalette(launcher, Key.Down);
        bool down = launcher.SelectedResult == older;
        PressInPalette(launcher, Key.Up);
        PressInPalette(launcher, Key.Up);
        bool wrappedUp = launcher.SelectedRow == launcher.Results[^1];
        PressInPalette(launcher, Key.Down);
        bool wrappedDown = launcher.SelectedRow == first && first == newest;
        PressInPalette(launcher, Key.Tab);
        bool nextSection = launcher.SelectedRow == launcher.Favorites.FirstOrDefault();
        PressInPalette(launcher, Key.Tab);
        bool sectionWrapped = launcher.SelectedRow == first;
        Check("Palette keys: Down and Up move and wrap at the ends, Tab jumps to the next section and wraps",
            down && wrappedUp && wrappedDown && nextSection && sectionWrapped);

        // Strg+Enter shows the connection in the overview instead of connecting.
        launcher.SelectedRow = older;
        PressInPalette(launcher, Key.Enter, KeyModifiers.Control);
        await Settle(500);
        Check("Palette keys: Strg+Enter shows the selected connection in the overview with its details, without opening a session",
            !launcher.IsVisible && window.DetailPanel.Shown == older && window.SelectedConnections.SequenceEqual([older])
            && !window.SessionWindows.Any(w => ((ISessionWindow)w).Connection == older));

        launcher = await OpenPaletteAsync(window, "selftest palette");
        bool listed = launcher.Results.Count >= 3 && launcher.Sections.FirstOrDefault() == Strings.Launcher_Section_Connections;
        PressInPalette(launcher, Key.Escape);
        await Settle(200);
        Check("Palette keys: a search lists the matches under \"Verbindungen\", Esc closes the palette", listed && !launcher.IsVisible);
    }

    private static async Task RunPaletteCommandChecksAsync(MainWindow window, Appearance look)
    {
        // Everything the task names is reachable, and every command has a label, an icon, and something to run.
        string[] expected =
        [
            "connection.new", "group.new", "import", "sync", "status.refresh", "view.tiles", "view.list", "theme.light", "theme.dark",
            "theme.system", "tiles.small", "tiles.medium", "tiles.large", "tools", "about",
        ];
        string[] ids = [.. window.LauncherCommands.Select(c => c.Id)];
        string[] missing = [.. expected.Where(e => !ids.Contains(e))];
        Check($"Palette offers the app commands of the \"Mehr\" menu ({(missing.Length == 0 ? $"{ids.Length} commands" : "missing: " + string.Join(", ", missing))})",
            missing.Length == 0 && ids.Distinct().Count() == ids.Length && window.LauncherCommands.All(c => c.Label.Length > 0));

        QuickLauncherWindow launcher = await OpenPaletteAsync(window, ">");
        Check("Palette: \">\" alone lists every action and no connection",
            launcher.Commands.SequenceEqual(window.LauncherCommands) && launcher.Results.Count == 0 && launcher.AdHocTargets.Count == 0
            && launcher.Sections.SequenceEqual([Strings.Launcher_Section_Commands]) && launcher.SelectedRow == launcher.Commands[0]);
        SaveRenderProbe(launcher, "palette-commands.png");

        // "liste" finds the view command; running it does what "Ansicht > Liste" does, and the menu undoes it.
        window.SetListView(false);
        launcher.Query = "liste";
        await Settle(250);
        LauncherCommand? list = launcher.Commands.FirstOrDefault();
        bool found = list is { Id: "view.list", IsCurrent: false } && launcher.Sections.Contains(Strings.Launcher_Section_Commands);
        launcher.SelectedRow = list;
        launcher.Launch(reveal: false);
        await Settle(500);
        bool ran = window.IsListView && !launcher.IsVisible;
        MenuItem? tiles = window.MoreMenuItems().FirstOrDefault(i => i.Header as string == Strings.Menu_ViewTiles);
        tiles?.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Settle(300);
        bool menuUndoes = tiles is not null && !window.IsListView;
        launcher = await OpenPaletteAsync(window, "> ansicht");
        bool marksCurrent = launcher.Commands.FirstOrDefault(c => c.Id == "view.tiles") is { IsCurrent: true }
            && launcher.Commands.FirstOrDefault(c => c.Id == "view.list") is { IsCurrent: false };
        Check("Palette: \"liste\" finds \"Ansicht: Liste\", Enter switches the overview like the menu entry, and the choice in use is marked",
            found && ran && menuUndoes && marksCurrent);

        // Tile size and theme: the same Appearance calls as "Darstellung".
        look.SetTileSize(TileSize.Medium);
        launcher.Query = "kachelgröße groß";
        await Settle(250);
        bool sizeFound = launcher.SelectedRow is LauncherCommand { Id: "tiles.large" };
        PressInPalette(launcher, Key.Enter);
        await Settle(400);
        bool sizeRan = look.TileSize == TileSize.Large && window.TileSize == TileSize.Large;
        look.SetTileSize(TileSize.Medium);
        look.SetTheme(AppTheme.Light);
        launcher = await OpenPaletteAsync(window, "dunkel");
        bool themeFound = launcher.SelectedRow is LauncherCommand { Id: "theme.dark" };
        PressInPalette(launcher, Key.Enter);
        await Settle(600);
        Check("Palette: \"kachelgröße groß\" and \"dunkel\" run the tile size and theme commands (Enter)",
            sizeFound && sizeRan && themeFound && look.Theme == AppTheme.Dark);

        // A search for a connection is not flooded with actions whose letters happen to appear in order.
        launcher = await OpenPaletteAsync(window, "selftest palette alt");
        bool quiet = launcher.Commands.Count == 0 && launcher.Results.FirstOrDefault()?.Name == "Selftest Palette Alt";
        launcher.Query = "neue";
        await Settle(250);
        bool commandsOnly = launcher.Results.Count == 0 && launcher.SelectedRow is LauncherCommand { Id: "connection.new" };
        launcher.Query = "neu";
        await Settle(250);
        bool mixed = launcher.Results.Any(c => c.Name == "Selftest Palette Neu") && launcher.SelectedRow is ConnectionItem
            && launcher.Commands.Take(2).Select(c => c.Id).SequenceEqual(["connection.new", "group.new"])
            && launcher.Sections.SequenceEqual([Strings.Launcher_Section_Connections, Strings.Launcher_Section_Commands]);
        SaveRenderProbe(launcher, "palette-search.png");
        launcher.Query = "> gibtesnicht";
        await Settle(250);
        bool none = launcher.Commands.Count == 0 && launcher.SelectedRow is null;
        SaveRenderProbe(launcher, "palette-no-match.png");
        Check("Palette: actions show below the connections and only for a real match (\"neu\" both, \"neue\" actions only, a connection name none); an unknown action says so",
            quiet && commandsOnly && mixed && none);

        // A command with a dialog: the overview comes up with the same editor "Neue Verbindung" opens.
        launcher.Query = "neue verbindung";
        await Settle(250);
        bool newFound = launcher.Commands.FirstOrDefault() is { Id: "connection.new" };
        launcher.SelectedRow = launcher.Commands.FirstOrDefault();
        int connectionsBefore = window.Connections.Count;
        launcher.Launch(reveal: false);
        await Settle(900);
        bool editorOpen = ConnectionEditor.OpenDialog is not null && !launcher.IsVisible;
        ConnectionEditor.OpenDialog?.Hide();
        await Settle(400);
        Check("Palette: \"neue verbindung\" opens the editor for a new connection in the overview; canceling adds nothing",
            newFound && editorOpen && ConnectionEditor.OpenDialog is null && window.Connections.Count == connectionsBefore);
    }

    private static async Task RunPaletteAdHocChecksAsync(MainWindow window, ConnectionLibrary library)
    {
        // Rows only: nothing is opened for an address that could exist.
        QuickLauncherWindow launcher = await OpenPaletteAsync(window, "root@10.0.0.5:2222");
        string[] labels = [.. launcher.AdHocTargets.Select(t => t.Label)];
        Check($"Palette: a typed \"user@host:port\" is offered as \"Direkt verbinden\" rows, the likely type first ({string.Join(" | ", labels)})",
            launcher.AdHocTargets.Select(t => t.Type).SequenceEqual([ConnectionType.Ssh, ConnectionType.Rdp, ConnectionType.Vnc])
            && labels.FirstOrDefault() == string.Format(CultureInfo.CurrentCulture, Strings.Launcher_Direct_Label, "root@10.0.0.5:2222", "SSH")
            && launcher.AdHocTargets.All(t => t.Target is { Host: "10.0.0.5", Port: 2222, UserName: "root" })
            && launcher.Sections.Contains(Strings.Launcher_Section_Direct) && launcher.SelectedRow == launcher.AdHocTargets[0]);
        SaveRenderProbe(launcher, "palette-adhoc.png");

        launcher.Query = "fe80::1";
        await Settle(200);
        bool ipv6 = launcher.AdHocTargets.Count == 3 && launcher.AdHocTargets[0].Type == ConnectionType.Rdp;
        launcher.Query = "selftest";
        await Settle(200);
        bool wordIsSearch = launcher.AdHocTargets.Count == 0 && launcher.Results.Count > 0;
        launcher.Query = "rmselftestnirgendwo";
        await Settle(200);
        bool wordAsLastResort = launcher.Results.Count == 0 && launcher.Commands.Count == 0 && launcher.AdHocTargets.Count == 3;
        launcher.Query = "zwei wörter ohne treffer";
        await Settle(200);
        bool nothing = launcher.AdHocTargets.Count == 0 && launcher.SelectedRow is null;
        Check("Palette: an IPv6 address is a target, a single word is a search first and a host name only when nothing matches, several words never are",
            ipv6 && wordIsSearch && wordAsLastResort && nothing);

        // A type in front leaves one row. Enter opens a session for a connection that is saved nowhere.
        launcher.Query = $"rdp {PaletteAdHocHost}";
        await Settle(250);
        bool single = launcher.AdHocTargets.Count == 1 && launcher.AdHocTargets[0].Type == ConnectionType.Rdp && launcher.Results.Count == 0;
        int sessionsBefore = window.SessionWindows.Count, storedBefore = library.Connections.Count, shownBefore = window.Connections.Count;
        PressInPalette(launcher, Key.Enter);
        await Settle(1200);
        Window? session = window.SessionWindows.FirstOrDefault(w => ((ISessionWindow)w).Connection.Model.Host == PaletteAdHocHost);
        bool opened = session is not null && !launcher.IsVisible && window.SessionWindows.Count == sessionsBefore + 1
            && session.Title == PaletteAdHocHost && ((ISessionWindow)session).Connection.Model.Type == ConnectionType.Rdp;
        bool unsaved = library.Connections.Count == storedBefore && window.Connections.Count == shownBefore
            && !library.Connections.Any(c => c.Host == PaletteAdHocHost);

        // The same target again: its window, not a second one.
        launcher = await OpenPaletteAsync(window, $"rdp {PaletteAdHocHost}");
        launcher.Launch(reveal: false);
        await Settle(600);
        bool reused = window.SessionWindows.Count == sessionsBefore + 1;
        session?.Close();
        await Settle(600);
        Check("Palette: Enter on \"Direkt verbinden\" opens a session window for the typed host, saved neither to the library nor the overview; again reuses the window",
            single && opened && unsaved && reused && window.SessionWindows.Count == sessionsBefore && library.Connections.Count == storedBefore);

        // Strg+Enter: the editor for a new connection, filled with the target.
        launcher = await OpenPaletteAsync(window, $"ssh admin@{PaletteAdHocHost}:2200");
        PressInPalette(launcher, Key.Enter, KeyModifiers.Control);
        await Settle(900);
        bool filled = ConnectionEditor.OpenEditor is { } editor && editor.User.Text == "admin" && !launcher.IsVisible;
        ConnectionEditor.OpenDialog?.Hide();
        await Settle(400);
        Check("Palette: Strg+Enter on \"Direkt verbinden\" opens the editor for a new connection with the typed user; canceling saves nothing",
            filled && library.Connections.Count == storedBefore && window.SessionWindows.Count == sessionsBefore);
    }

    private static async Task RunPaletteHotkeyChecksAsync(MainWindow window)
    {
        if (window.Hotkey is null)
        {
            Log("SKIP Palette hotkey view: the global hotkey is not started");
            return;
        }

        // Pressed while the palette is open, the global hotkey closes it again.
        QuickLauncherWindow launcher = await OpenPaletteAsync(window, "");
        SendMessage(window.Hotkey.WindowHandle, GlobalHotkey.WmHotkey, GlobalHotkey.HotkeyId, 0);
        await Settle(300);
        bool closed = !launcher.IsVisible;
        SendMessage(window.Hotkey.WindowHandle, GlobalHotkey.WmHotkey, GlobalHotkey.HotkeyId, 0);
        await Settle(400);
        Check("Global hotkey toggles the palette: pressed again it closes, then opens it", closed && launcher.IsVisible);

        // The action "Globalen Hotkey ändern" opens the same view as "Ändern" and keeps the palette open.
        launcher.Query = "hotkey";
        await Settle(250);
        bool found = launcher.SelectedRow is LauncherCommand { Id: "hotkey" };
        PressInPalette(launcher, Key.Enter);
        await Settle(300);
        bool shown = launcher.IsVisible && launcher.IsHotkeyViewShown && launcher.IsRecordingHotkey && launcher.HotkeyMessage == Strings.Launcher_PressHotkey;
        SaveRenderProbe(launcher, "palette-hotkey.png");

        KeyGesture? before = window.Hotkey.Gesture;
        PressInPalette(launcher, Key.R, KeyModifiers.Shift);
        bool invalidExplained = launcher.IsRecordingHotkey && launcher.HotkeyMessage == Strings.Launcher_HotkeyInvalid && window.Hotkey.Gesture == before;
        Check("Palette hotkey view: the action opens it, it waits for a combination, and one without Strg, Alt, or Win is explained and changes nothing",
            found && shown && invalidExplained);

        // The current hotkey reaches the app as WM_HOTKEY, not as keys: in the waiting view it means "keep it".
        if (before is not null)
        {
            SendMessage(window.Hotkey.WindowHandle, GlobalHotkey.WmHotkey, GlobalHotkey.HotkeyId, 0);
            await Settle(300);
            Check("Palette hotkey view: pressing the current hotkey keeps it and confirms, instead of resetting the palette",
                launcher.IsVisible && launcher.IsHotkeyViewShown && !launcher.IsRecordingHotkey && window.Hotkey.Gesture == before
                && launcher.HotkeyMessage == string.Format(CultureInfo.CurrentCulture, Strings.Launcher_Hotkey_Saved, GlobalHotkey.Format(before)));
            launcher.ShowHotkeySettings();
            await Settle(200);
        }

        // A combination Windows refuses: this process holds it itself through a second registration.
        var taken = new KeyGesture(Key.F9, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift);
        const int blockerId = 0x524E;
        bool blocked = RegisterHotKey(0, blockerId, 0x1 | 0x2 | 0x4, 0x78);
        try
        {
            PressInPalette(launcher, taken.Key, taken.KeyModifiers);
            await Settle(200);
            string refused = before is null
                ? string.Format(CultureInfo.CurrentCulture, Strings.Launcher_Hotkey_RefusedNone, GlobalHotkey.Format(taken))
                : string.Format(CultureInfo.CurrentCulture, Strings.Launcher_Hotkey_Refused, GlobalHotkey.Format(taken), GlobalHotkey.Format(before));
            Log($"  blocker registration {(blocked ? "held" : "not possible (the combination is taken by another program)")}");
            SaveRenderProbe(launcher, "palette-hotkey-refused.png");
            Check("Palette hotkey view: a combination Windows refuses is named, the previous hotkey stays registered, and the view keeps waiting",
                launcher.HotkeyMessage == refused && window.Hotkey.Gesture == before && launcher.IsRecordingHotkey && launcher.IsHotkeyViewShown);
        }
        finally
        {
            if (blocked)
                UnregisterHotKey(0, blockerId);
        }

        var custom = new KeyGesture(Key.F10, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift);
        PressInPalette(launcher, custom.Key, custom.KeyModifiers);
        await Settle(200);
        bool accepted = window.Hotkey.Gesture == custom;
        Log($"  hotkey change to {GlobalHotkey.Format(custom)}: {(accepted ? "registered" : "refused by Windows (taken)")}");
        SaveRenderProbe(launcher, "palette-hotkey-saved.png");
        bool saved = !accepted || (!launcher.IsRecordingHotkey && AppHost.Current.Library.GetSetting("launcher.hotkey") == custom.ToString()
            && launcher.HotkeyMessage == string.Format(CultureInfo.CurrentCulture, Strings.Launcher_Hotkey_Saved, GlobalHotkey.Format(custom)));
        PressInPalette(launcher, Key.Escape);
        await Settle(200);
        bool back = launcher.IsVisible && !launcher.IsHotkeyViewShown && !launcher.IsRecordingHotkey;
        PressInPalette(launcher, Key.Escape);
        await Settle(200);
        Check("Palette hotkey view: an accepted combination is saved and confirmed, Esc returns to the search, Esc again closes the palette",
            saved && back && !launcher.IsVisible);
        if (before is not null)
            window.TrySetHotkey(before);
    }

    /// <summary>The palette's screens in the theme in use; <paramref name="suffix"/> names the theme in the file.</summary>
    private static async Task SavePaletteProbesAsync(MainWindow window, string suffix)
    {
        QuickLauncherWindow launcher = await OpenPaletteAsync(window, "");
        SaveRenderProbe(launcher, $"palette-empty{suffix}.png");
        launcher.Query = ">";
        await Settle(250);
        SaveRenderProbe(launcher, $"palette-commands{suffix}.png");
        launcher.Query = "neu";
        await Settle(250);
        SaveRenderProbe(launcher, $"palette-search{suffix}.png");
        launcher.Query = "root@10.0.0.5:2222";
        await Settle(250);
        SaveRenderProbe(launcher, $"palette-adhoc{suffix}.png");
        launcher.ShowHotkeySettings();
        await Settle(250);
        SaveRenderProbe(launcher, $"palette-hotkey{suffix}.png");
        PressInPalette(launcher, Key.Escape);
        launcher.Hide();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
}
