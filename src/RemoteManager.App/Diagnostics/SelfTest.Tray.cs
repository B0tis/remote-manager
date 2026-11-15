using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Connections;
using RemoteManager.App.Connections.RemoteTools;
using RemoteManager.App.Resources;
using RemoteManager.App.Services;
using RemoteManager.App.Views;
using RemoteManager.Core.Model;

namespace RemoteManager.App.Diagnostics;

/// <summary>
/// Notification-area icon, "close to tray", and the shortcut overview. The icon's menu is checked through its
/// <see cref="NativeMenu"/> (what Windows shows is built from it when it opens) and rendered for review; closing
/// with the setting on must only hide the overview, with the session, the hotkey, and the way back intact. The
/// path that really ends the app is checked as a decision only: ending the process would end the test. Works on
/// connections named "Selftest Tray …", removed again at the end.
/// </summary>
internal static partial class SelfTest
{
    private static async Task RunTrayChecksAsync(MainWindow window)
    {
        Log("--- Tray, close to tray, shortcuts ---");
        AppHost app = AppHost.Current;
        Appearance look = app.Appearance;
        AppTheme themeBefore = look.Theme;
        bool closeToTrayBefore = window.CloseToTray;
        // The checks need the defaults: setting off, one-time hint not yet shown. Both are put back afterwards.
        string? hintBefore = app.Library.GetSetting(MainWindow.TrayHintSetting);
        string? placeBefore = app.Library.GetSetting(WindowPlacement.MainSetting);
        window.SetCloseToTray(false);
        app.Library.SetSetting(MainWindow.CloseToTraySetting, null);
        app.Library.SetSetting(MainWindow.TrayHintSetting, null);
        List<ConnectionItem> starredBefore = [.. window.Connections.Where(c => c.IsFavorite)];
        var created = new List<ConnectionItem>();
        try
        {
            ConnectionItem Add(string name, ConnectionType type, string host) =>
                window.Adopt(new Connection { Name = name, Type = type, Host = host, Description = "Self-test entry" });
            // The underscore checks that a name is not read as an access key.
            ConnectionItem first = Add("Selftest Tray srv_01", ConnectionType.Rdp, "127.0.0.1");
            ConnectionItem tool = Add("Selftest Tray Kasse", ConnectionType.TeamViewer, "123 456 789");
            created.AddRange([first, tool]);
            for (int i = 0; i < MainWindow.TrayRecentLimit; i++)
                created.Add(Add($"Selftest Tray alt {i + 1}", ConnectionType.Ssh, $"rm-selftest-tray-{i}.invalid"));
            await Settle(300);

            await RunTrayMenuChecksAsync(window, first, created, starredBefore);
            look.SetTheme(AppTheme.Dark);
            await Settle(500);
            await SaveTrayMenuProbesAsync(window, "tray-menu");
            look.SetTheme(AppTheme.Light);
            await Settle(500);
            await SaveTrayMenuProbesAsync(window, "tray-menu-light");
            look.SetTheme(AppTheme.Dark);
            await Settle(300);

            await RunCloseToTrayChecksAsync(window, first, tool);
            await RunShortcutChecksAsync(window);
        }
        finally
        {
            look.SetTheme(themeBefore);
            window.SetCloseToTray(closeToTrayBefore);
            if (!closeToTrayBefore)
                app.Library.SetSetting(MainWindow.CloseToTraySetting, null);
            app.Library.SetSetting(MainWindow.TrayHintSetting, hintBefore);
            app.Library.SetSetting(WindowPlacement.MainSetting, placeBefore);
            if (!window.IsVisible)
                window.ShowFromTray();
            window.TrayHint?.Close();
            ShortcutsView.OpenDialog?.Hide();
            MissingToolDialog.Current?.Hide();
            foreach (Window session in window.SessionWindows.Where(w => created.Contains(((ISessionWindow)w).Connection)).ToList())
                session.Close();
            foreach (ConnectionItem c in created)
                await window.RemoveConnectionAsync(c);
            window.SetFavorite([.. starredBefore.Where(c => window.Connections.Contains(c))], true);
            await Settle(300);
        }
    }

    private static async Task RunTrayMenuChecksAsync(MainWindow window, ConnectionItem first, List<ConnectionItem> created, List<ConnectionItem> starredBefore)
    {
        TrayIcon? icon = window.Tray;
        Check("The notification-area icon is registered with the app icon and the tooltip \"Remote-Manager\"",
            icon is { IsVisible: true, ToolTipText: "Remote-Manager", Icon: not null }
            && Application.Current is { } application && TrayIcon.GetIcons(application)?.Contains(icon) == true);
        if (window.TrayMenu is not { } menu)
        {
            Fail("The tray icon has no menu");
            return;
        }

        // A separator is a NativeMenuItem too (header "-"); null stands for it here.
        string?[] headers = [.. menu.Items.Select(i => i is NativeMenuItemSeparator ? null : (i as NativeMenuItem)?.Header)];
        string?[] expected =
        [
            Strings.Tray_Open, Strings.Menu_Launcher, null, Strings.Nav_Favorites, Strings.Nav_Recent, null, Strings.Tray_Quit,
        ];
        Check($"Tray menu: open, launcher, favorites, recent, quit ({string.Join(" | ", headers.Select(h => h ?? "-"))})",
            headers.SequenceEqual(expected)
            && menu.Items.OfType<NativeMenuItem>().All(i => i is NativeMenuItemSeparator || i.Menu is not null || i.HasClickHandlers));

        NativeMenuItem favorites = menu.Items.OfType<NativeMenuItem>().First(i => i.Header == Strings.Nav_Favorites);
        NativeMenuItem recent = menu.Items.OfType<NativeMenuItem>().First(i => i.Header == Strings.Nav_Recent);
        static List<string> Ids(NativeMenuItem parent) => [.. parent.Menu!.Items.OfType<NativeMenuItem>().Select(i => i.CommandParameter).OfType<string>()];
        static NativeMenuItem? Entry(NativeMenuItem parent, ConnectionItem c) =>
            parent.Menu!.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.CommandParameter as string == c.Id);

        // Without favorites the submenu is disabled; a star brings it back with that connection in it.
        window.SetFavorite(starredBefore, false);
        await Settle(300);
        bool disabled = !favorites.IsEnabled && Ids(favorites).Count == 0;
        window.SetFavorite([first], true);
        await Settle(300);
        NativeMenuItem? starred = Entry(favorites, first);
        bool followed = favorites.IsEnabled && Ids(favorites).SequenceEqual([first.Id]);
        // "__" is how a menu shows one underscore.
        bool named = starred is { Icon: not null } && starred.Header == "Selftest Tray srv__01"
            && starred.ToolTip == $"RDP, 127.0.0.1\n{first.StatusLabel}";
        window.SetFavorite([first], false);
        await Settle(300);
        bool removed = Entry(favorites, first) is null && !favorites.IsEnabled;
        window.SetFavorite(starredBefore, true);
        window.SetFavorite([first], true);
        await Settle(300);
        Check($"\"Favoriten\" follows the star: disabled without favorites, the connection with name, lamp, and tooltip when starred, gone when unstarred ({starred?.Header}, {starred?.ToolTip?.Replace('\n', '/')}; disabled {disabled}, followed {followed}, named {named}, removed {removed})",
            disabled && followed && named && removed && Ids(favorites).Contains(first.Id) && Ids(favorites).Count == starredBefore.Count + 1);

        // "Zuletzt verwendet": the five connected last, newest first. MarkConnected is what a session calls on logon.
        DateTime now = DateTime.UtcNow;
        List<ConnectionItem> older = [.. created.Skip(2)];
        for (int i = 0; i < older.Count; i++)
        {
            older[i].Model.LastConnectedAt = now.AddMinutes(-10 - i);
            window.Save(older[i]);
        }

        await Settle(300);
        bool five = Ids(recent).SequenceEqual(older.Select(c => c.Id));
        window.MarkConnected(first);
        await Settle(300);
        List<string> afterUse = Ids(recent);
        Check($"\"Zuletzt verwendet\" lists the five connections used last and follows a new logon ({afterUse.Count} entries)",
            five && recent.IsEnabled && afterUse.Count == MainWindow.TrayRecentLimit && afterUse[0] == first.Id
            && afterUse.Skip(1).SequenceEqual(older.Take(MainWindow.TrayRecentLimit - 1).Select(c => c.Id)));

        // A click on an entry connects like the launcher: the session window opens (no stored password: its logon card).
        if (Entry(recent, first) is INativeMenuItemExporterEventsImplBridge click)
            click.RaiseClicked();
        bool opened = await WaitFor(() => window.SessionWindows.Any(w => ((ISessionWindow)w).Connection == first), TimeSpan.FromSeconds(5));
        Check("A click on a tray entry opens the connection's session window", opened);
    }

    /// <summary>
    /// The menu as Windows shows it: the tray implementation builds a managed menu from the NativeMenu when the icon
    /// is right-clicked. That presenter is internal, so it is created by name here; in a window of its own on the
    /// overview's screen instead of at the mouse pointer.
    /// </summary>
    private static async Task SaveTrayMenuProbesAsync(MainWindow window, string name)
    {
        var presenterType = Type.GetType("Avalonia.Win32.TrayIconImpl+TrayIconMenuFlyoutPresenter, Avalonia.Win32");
        if (window.TrayMenu is not { } menu || presenterType is null
            || Activator.CreateInstance(presenterType, nonPublic: true) is not MenuFlyoutPresenter presenter)
        {
            Log("SKIP tray menu probe: the tray menu presenter of this Avalonia version was not found.");
            return;
        }

        presenter.ItemsSource = menu.Items;
        var host = new Window
        {
            Content = presenter, SizeToContent = SizeToContent.WidthAndHeight, WindowDecorations = WindowDecorations.None,
            ShowInTaskbar = false, ShowActivated = false, CanResize = false, Title = "Selftest tray menu",
        };
        WindowPlacement.OpenOnScreenOf(host, window);
        host.Show();
        try
        {
            await Settle(600);
            SaveControlProbe(presenter, window, $"{name}.png");
            if (presenter.GetVisualDescendants().OfType<MenuItem>().FirstOrDefault(m => m.Header as string == Strings.Nav_Favorites) is { } favorites)
            {
                favorites.Open();
                await Settle(600);
                if (favorites.GetVisualDescendants().OfType<Popup>().FirstOrDefault()?.Child is { } submenu)
                {
                    SaveControlProbe(submenu, window, $"{name}-favorites.png");
                }
                favorites.Close();
            }
        }
        finally
        {
            host.Close();
            await Settle(200);
        }
    }

    private static async Task RunCloseToTrayChecksAsync(MainWindow window, ConnectionItem first, ConnectionItem tool)
    {
        const WindowCloseReason User = WindowCloseReason.WindowClosing;
        Check("Close to tray is off by default and decides per close: only a plain close with the setting on hides; \"Beenden\", an app shutdown, and a Windows shutdown end the app",
            AppHost.Current.Library.GetSetting(MainWindow.CloseToTraySetting) is null && !window.CloseToTray
            && MainWindow.HidesOnClose(closeToTray: true, exitRequested: false, hasTrayIcon: true, User)
            && !MainWindow.HidesOnClose(closeToTray: false, exitRequested: false, hasTrayIcon: true, User)
            && !MainWindow.HidesOnClose(closeToTray: true, exitRequested: true, hasTrayIcon: true, User)
            && !MainWindow.HidesOnClose(closeToTray: true, exitRequested: false, hasTrayIcon: false, User)
            && !MainWindow.HidesOnClose(closeToTray: true, exitRequested: false, hasTrayIcon: true, WindowCloseReason.ApplicationShutdown)
            && !MainWindow.HidesOnClose(closeToTray: true, exitRequested: false, hasTrayIcon: true, WindowCloseReason.OSShutdown));

        // The setting is a checkable entry of the "Mehr" menu.
        MenuItem? entry = window.MoreMenuItems().FirstOrDefault(i => i.Header as string == Strings.Tray_CloseToTray);
        entry?.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Settle(400);
        SaveRenderProbe(window, "tray-setting-toast.png");
        Check("\"Beim Schließen im Infobereich weiterlaufen\" in the \"Mehr\" menu turns the setting on and says how to quit",
            entry is { ToggleType: MenuItemToggleType.CheckBox, IsChecked: true } && window.CloseToTray
            && AppHost.Current.Library.GetSetting(MainWindow.CloseToTraySetting) == "1"
            && window.ToastBar.IsOpen && window.ToastBar.Message == Strings.Tray_Toast_On);
        if (window.More.Flyout is MenuFlyout more && window.MoreMenuItems().FirstOrDefault(i => i.Header as string == Strings.Menu_View) is { } view)
        {
            window.Activate();
            more.ShowAt(window.More);
            await Settle(500);
            if (view.FindAncestorOfType<MenuFlyoutPresenter>() is { } presenter)
                SaveControlProbe(presenter, window, "menu-more-tray.png");
            more.Hide();
            await Settle(300);
        }

        if (!MainWindow.HidesOnClose(window.CloseToTray, exitRequested: false, window.Tray is not null, User))
        {
            Fail("Close to tray is not active; the close checks are skipped (closing would end the self-test)");
            return;
        }

        // Closing hides the overview: the window, the session, and the hotkey stay.
        nint handle = HandleOf(window);
        PixelPoint position = window.Position;
        Size size = window.ClientSize;
        Window? session = window.SessionWindows.FirstOrDefault(w => ((ISessionWindow)w).Connection == first);
        GlobalHotkey? hotkey = window.Hotkey;
        window.Close();
        await Settle(700);
        bool hidden = !window.IsVisible && !IsWindowVisible(handle) && IsWindow(handle);
        bool sessionKept = session is not null && window.SessionWindows.Contains(session) && session.IsVisible && IsWindowVisible(HandleOf(session));
        Check("With close to tray on, closing the overview hides it; the session window stays open", hidden && sessionKept);

        TrayHintWindow? hint = window.TrayHint;
        bool hinted = hint is { IsVisible: true, IsActive: false };
        if (hint is not null)
        {
            SaveRenderProbe(hint, "tray-hint.png");
            AppHost.Current.Appearance.SetTheme(AppTheme.Light);
            await Settle(500);
            SaveRenderProbe(hint, "tray-hint-light.png");
            AppHost.Current.Appearance.SetTheme(AppTheme.Dark);
            Screen? screen = window.Screens.ScreenFromWindow(window);
            Log($"  tray hint at {hint.Position}, size {hint.ClientSize}, screen {screen?.WorkingArea}");
            hinted &= screen is not null && screen.WorkingArea.Contains(hint.Position);
        }

        Check("The first time, a note in the corner of the overview's screen says where the app went (it takes no focus)", hinted);

        SendMessage(hotkey!.WindowHandle, GlobalHotkey.WmHotkey, GlobalHotkey.HotkeyId, 0);
        await Settle(500);
        bool launcherOpened = window.QuickLauncher is { IsVisible: true } && !window.IsVisible;
        window.QuickLauncher?.Hide();
        Check("The global hotkey still opens the launcher while the overview is hidden", launcherOpened);

        // A handler that asks something on the overview brings it back (the tool is "not installed", so nothing starts).
        RemoteToolLauncher.LocatorOverride = FakeLocator(null);
        try
        {
            window.ConnectFromTray(tool.Id);
            await Settle(800);
        }
        finally
        {
            RemoteToolLauncher.LocatorOverride = null;
        }

        bool cameBack = window.IsVisible && MissingToolDialog.Current is not null && window.TrayHint is null;
        MissingToolDialog.Current?.Hide();
        await Settle(300);
        Check($"Connecting from the tray brings the overview back when the handler shows a dialog there; same place, same size, no second start (visible {window.IsVisible}, note open {window.TrayHint is not null}, {window.Position}, {window.ClientSize})",
            cameBack && window.Position == position && window.ClientSize == size && window.Hotkey == hotkey && HandleOf(window) == handle);

        // Second close: hidden again, without the note; the icon brings it back.
        window.Close();
        await Settle(500);
        bool hiddenAgain = !window.IsVisible && window.TrayHint is null;
        window.ShowFromTray();
        await Settle(600);
        Check("A later close hides without the note, and the icon brings the overview back", hiddenAgain && window.IsVisible && IsWindowVisible(handle));

        // A failed connection must not go unseen, and the overview must not take the keyboard from a session:
        // the corner note carries a warning or an error, and nothing else.
        window.Close();
        await Settle(400);
        window.ShowToast("Selftest Tray", "Gespeichert.", FAInfoBarSeverity.Success);
        await Settle(400);
        bool quiet = window.TrayHint is null && !window.IsVisible;
        const string Problem = "TeamViewer konnte nicht gestartet werden: Das System kann die angegebene Datei nicht finden.";
        window.ShowToast("Selftest Tray Kasse", Problem, FAInfoBarSeverity.Error);
        await Settle(600);
        TrayHintWindow? message = window.TrayHint;
        bool shown = message is { IsVisible: true, IsActive: false, IsMessage: true, Heading: "Selftest Tray Kasse", Body: Problem } && !window.IsVisible;
        if (message is not null)
        {
            SaveRenderProbe(message, "tray-note-error.png");
            AppHost.Current.Appearance.SetTheme(AppTheme.Light);
            await Settle(500);
            SaveRenderProbe(message, "tray-note-error-light.png");
            AppHost.Current.Appearance.SetTheme(AppTheme.Dark);
            await Settle(300);
        }

        window.ShowToast("Selftest Tray", "Zwei Konflikte beim Synchronisieren.", FAInfoBarSeverity.Warning);
        await Settle(600);
        TrayHintWindow? warning = window.TrayHint;
        bool replaced = warning is { IsMessage: true, Heading: "Selftest Tray" } && warning != message && message is { IsVisible: false };
        if (warning is not null)
            SaveRenderProbe(warning, "tray-note-warning.png");
        Check($"While hidden, a warning or an error shows as a corner note and the overview stays hidden; other messages show nothing (quiet {quiet}, error {shown}, replaced by the next {replaced})",
            quiet && shown && replaced);

        warning?.FindControl<Button>("OpenButton")?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle(600);
        Check("\"Remote-Manager öffnen\" on the note brings the overview back and closes the note", window.IsVisible && window.TrayHint is null);

        // A dialog needs a shown owner: the hidden overview comes back for it (KeePass master password, SSH host key).
        window.Close();
        await Settle(400);
        bool wasHidden = !window.IsVisible;
        Window owner = DialogOwner.Shown(window);
        await Settle(500);
        Check("A dialog that needs the overview as its owner brings the hidden overview back first", wasHidden && owner == window && window.IsVisible);

        window.SetCloseToTray(false);
        entry = window.MoreMenuItems().FirstOrDefault(i => i.Header as string == Strings.Tray_CloseToTray);
        Check("With the setting off again, closing the overview ends the app as before",
            !window.CloseToTray && entry is { IsChecked: false } && !MainWindow.HidesOnClose(window.CloseToTray, exitRequested: false, hasTrayIcon: true, User));
        session?.Close();
        await Settle(300);
    }

    private static async Task RunShortcutChecksAsync(MainWindow window)
    {
        Check("\"Mehr\" offers \"Tastenkürzel …\" with F1",
            window.MoreMenuItems().FirstOrDefault(i => i.Header as string == Strings.Shortcuts_Menu)?.InputGesture is { Key: Key.F1, KeyModifiers: KeyModifiers.None });

        // F1 opens it and F1 closes it again.
        void PressF1() => window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F1, Source = window });
        window.Activate();
        PressF1();
        await Settle(800);
        bool openedByKey = ShortcutsView.OpenDialog is not null && ShortcutsView.OpenContent is not null;
        PressF1();
        await Settle(600);
        Check("F1 opens the shortcut overview and closes it again", openedByKey && ShortcutsView.OpenDialog is null);

        // Not on top of another dialog of the overview: the editor keeps the keyboard.
        Task<bool> editing = ConnectionEditor.ShowAsync(window.Connections[0].Model, isNew: false);
        await Settle(800);
        PressF1();
        await Settle(500);
        bool stayedOut = ConnectionEditor.OpenDialog is not null && ShortcutsView.OpenDialog is null;
        ConnectionEditor.OpenDialog?.Hide();
        await editing;
        await Settle(400);
        Check("F1 does not put the shortcut overview on top of an open dialog", stayedOut);

        window.MoreMenuItems().FirstOrDefault(i => i.Header as string == Strings.Shortcuts_Menu)?.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Settle(800);
        FAContentDialog? dialog = ShortcutsView.OpenDialog;
        ShortcutsView? view = ShortcutsView.OpenContent;
        Check("\"Tastenkürzel …\" in the \"Mehr\" menu opens the shortcut overview", dialog is not null && view is not null);
        if (dialog is null || view is null)
            return;

        string[] areas = [.. view.Areas.Select(a => a.Title)];
        List<ShortcutRow> rows = [.. view.Rows];
        Check($"The overview groups the shortcuts by area ({string.Join(", ", areas.Zip(view.Areas, (title, area) => $"{title} {area.Rows.Count}"))})",
            areas.SequenceEqual([Strings.Shortcuts_Area_Overview, Strings.Shortcuts_Area_Connection, Strings.Shortcuts_Area_Launcher, Strings.Shortcuts_Area_Session])
            && view.Areas.All(a => a.Rows.Count >= 4) && rows.All(r => r.Label.Length > 0 && r.Parts.Count > 0));

        // Every shortcut a menu shows must be in the list, with the same German key names.
        List<string> shown = [.. window.MoreMenuItems().Select(i => i.InputGesture)
            .Concat(window.Tiles.ContextMenu!.Items.OfType<MenuItem>().Select(i => i.InputGesture))
            .OfType<KeyGesture>().Select(g => g.ToString("p", null)).Distinct()];
        string[] unlisted = [.. shown.Where(g => !rows.Any(r => r.GestureText == g))];
        Check($"Every shortcut the menus show is in the overview, in German ({string.Join(", ", shown)}{(unlisted.Length == 0 ? "" : "; missing: " + string.Join(", ", unlisted))})",
            unlisted.Length == 0 && shown.Contains("Strg+Umschalt+N") && shown.Contains("Entf") && shown.Contains("Eingabe")
            && rows.Any(r => r.GestureText == "Esc") && rows.Any(r => r.GestureText == "Strg+Eingabe"));

        ShortcutRow global = rows.First(r => r.Label == Strings.Shortcuts_LauncherGlobal);
        string wanted = window.Hotkey?.Gesture is { } gesture ? GlobalHotkey.Format(gesture) : Strings.Launcher_HotkeyNone;
        Check($"The launcher row shows the global hotkey that is registered now ({global.GestureText})", global.GestureText == wanted);

        // At the standard window size everything is on one page; the minimum window scrolls it.
        double width = window.Width, height = window.Height;
        await ResizeAsync(window, 1320, 840);
        int caps = view.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("keycap") && b.IsEffectivelyVisible);
        bool fits = DialogFits(window, dialog, out string bounds);
        ScrollViewer? scroller = view.FindAncestorOfType<ScrollViewer>();
        bool onePage = scroller is not null && scroller.Extent.Height <= scroller.Viewport.Height + 0.5;
        Check($"The keys are drawn as key caps and the dialog shows everything without scrolling at 1320 x 840 ({caps} caps, {bounds}, content {scroller?.Extent.Height:0} in {scroller?.Viewport.Height:0})",
            caps == rows.Sum(r => r.Parts.Count(p => p.IsKey)) && fits && onePage);
        SaveRenderProbe(window, "shortcuts.png");
        AppHost.Current.Appearance.SetTheme(AppTheme.Light);
        await Settle(700);
        SaveRenderProbe(window, "shortcuts-light.png");
        AppHost.Current.Appearance.SetTheme(AppTheme.Dark);
        await Settle(400);

        await ResizeAsync(window, window.MinWidth, window.MinHeight);
        bool fitsMinimum = DialogFits(window, dialog, out string minimumBounds);
        SaveRenderProbe(window, "shortcuts-minimum.png");
        Check($"The shortcut overview fits the minimum window and scrolls ({minimumBounds})",
            fitsMinimum && scroller is not null && scroller.Extent.Height > scroller.Viewport.Height);
        await ResizeAsync(window, width, height);
        dialog.Hide();
        await Settle(400);
        Check("The shortcut overview closes", ShortcutsView.OpenDialog is null);

        // The list claims Ctrl+A for the connections: the list box handles it, headers stay unselected.
        bool list = window.IsListView;
        window.SetListView(false);
        window.Select([]);
        await Settle(200);
        window.Tiles.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.A, KeyModifiers = KeyModifiers.Control, Source = window.Tiles });
        await Settle(300);
        int selected = window.SelectedConnections.Count, all = window.TileEntries.OfType<ConnectionItem>().Count();
        window.Select([]);
        window.SetListView(list);
        Check($"Strg+A selects every connection in view ({selected} of {all})", all > 0 && selected == all);
    }
}
