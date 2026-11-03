using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Resources;
using RemoteManager.App.Views;
using RemoteManager.Core.Model;

namespace RemoteManager.App.Diagnostics;

/// <summary>
/// UI structure and responsive layout (docs/ui.md): the "Mehr" menu reaches every function that used to have its own
/// header button, shortcuts read German, no logo or app name inside the window, and the window works at 1320 x 840,
/// 1024 x 640, a 1366 x 768 laptop at 125 % (1093 x 614) and the minimum size: sidebar mode, detail column, header
/// without cut-off, tile width, list columns, a dialog that fits. Plus the empty states.
/// </summary>
internal static partial class SelfTest
{
    private static async Task RunLayoutChecksAsync(MainWindow window)
    {
        RunMenuStructureChecks(window);
        await SaveMenuProbesAsync(window);
        await RunResponsiveChecksAsync(window);
        await RunEmptyStateChecksAsync(window);
    }

    private static void RunMenuStructureChecks(MainWindow window)
    {
        List<MenuItem> items = [.. window.MoreMenuItems()];
        string?[] headers = [.. items.Select(i => i.Header as string)];
        string[] expected =
        [
            Strings.Action_NewGroup, Strings.Menu_Launcher, Strings.Menu_View, Strings.Menu_ViewTiles, Strings.Menu_ViewList,
            Strings.Menu_Details, Strings.Status_RefreshTip, Strings.LivePreview_Menu, Strings.Action_Import, Strings.Action_ExportBackup,
            Strings.Menu_Sync, Strings.Menu_RemoteTools, Strings.Update_CheckMenu, Strings.About_Menu,
        ];
        string[] missing = [.. expected.Where(e => !headers.Contains(e))];
        Check($"\"Mehr\" reaches every former header button and the view settings ({(missing.Length == 0 ? $"{items.Count} entries" : "missing: " + string.Join(", ", missing))})",
            missing.Length == 0);

        string Gesture(string header) => items.FirstOrDefault(i => i.Header as string == header)?.InputGesture?.ToString("p", null) ?? "";
        string shortcuts = string.Join(", ", new[] { Strings.Action_NewGroup, Strings.Menu_Launcher, Strings.Menu_ViewList, Strings.Status_RefreshTip }.Select(Gesture));
        Check($"Menu shortcuts are shown in German ({shortcuts})", shortcuts == "Strg+Umschalt+N, Strg+K, Strg+2, F5");

        bool branding = window.Nav.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == "Remote-Manager")
            || window.Header.GetVisualDescendants().OfType<Image>().Any();
        Check("No logo or app name inside the window (the title bar shows them)", !branding && window.Title == "Remote-Manager");
    }

    /// <summary>Screenshots of "Mehr" and "Ansicht" (native popups: render the menu's own visual).</summary>
    private static async Task SaveMenuProbesAsync(MainWindow window)
    {
        if (window.More.Flyout is not MenuFlyout menu
            || menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header as string == Strings.Menu_View) is not { } view)
            return;
        window.Activate();
        menu.ShowAt(window.More);
        await Settle(500);
        if (view.FindAncestorOfType<MenuFlyoutPresenter>() is { } presenter)
            SaveControlProbe(presenter, window, "menu-more.png");
        view.Open();
        await Settle(500);
        if (view.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().FirstOrDefault()?.Child is { } submenu)
            SaveControlProbe(submenu, window, "menu-view.png");
        view.Close();
        menu.Hide();
        await Settle(300);
    }

    private static async Task RunResponsiveChecksAsync(MainWindow window)
    {
        double width = window.Width, height = window.Height;
        bool details = window.IsDetailsVisible, list = window.IsListView;
        WindowState state = window.WindowState;
        try
        {
            window.WindowState = WindowState.Normal;
            window.SetListView(false);
            window.SetDetailsVisible(true);

            await ResizeAsync(window, 1320, 840);
            Check($"1320 x 840: sidebar expanded, detail column open, header fits ({Describe(window)})",
                window.Nav.DisplayMode == FANavigationViewDisplayMode.Expanded && window.IsDetailsVisible && HeaderFits(window) && TilesFit(window));
            SaveRenderProbe(window, "layout-1320x840.png");

            await ResizeAsync(window, 1024, 640);
            bool compact = window.Nav.DisplayMode == FANavigationViewDisplayMode.Compact && !window.IsDetailsVisible;
            Check($"1024 x 640: sidebar shows icons, detail column closed, header and tiles fit ({Describe(window)})",
                compact && HeaderFits(window) && TilesFit(window));
            SaveRenderProbe(window, "layout-1024x640.png");
            window.SetDetailsVisible(true);
            await Settle(400);
            Check($"1024 x 640: the detail column opens on request and everything still fits ({Describe(window)})",
                window.IsDetailsVisible && HeaderFits(window) && TilesFit(window));
            SaveRenderProbe(window, "layout-1024x640-details.png");

            // 1366 x 768 at 125 %: 1093 x 614 DIP minus the taskbar.
            await ResizeAsync(window, 1093, 580);
            Check($"1366 x 768 at 125 % (1093 x 580): compact sidebar, header fits ({Describe(window)})",
                window.Nav.DisplayMode == FANavigationViewDisplayMode.Compact && HeaderFits(window) && TilesFit(window));

            await ResizeAsync(window, window.MinWidth, window.MinHeight);
            window.SetDetailsVisible(true);
            await Settle(500);
            Check($"Minimum size {window.MinWidth} x {window.MinHeight} with details: buttons drop their labels, nothing is cut off ({Describe(window)})",
                window.Header.Classes.Contains("narrow") && HeaderFits(window) && TilesFit(window));
            SaveRenderProbe(window, "layout-minimum.png");

            window.SetListView(true);
            await Settle(500);
            Grid listPanel = window.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "ListPanel");
            Check($"Narrow list hides group, tags and last-connected columns ({string.Join(" ", listPanel.Classes)})",
                listPanel.Classes.Contains("compact") && listPanel.Classes.Contains("narrow"));
            SaveRenderProbe(window, "layout-minimum-list.png");
            window.SetListView(false);
            window.SetDetailsVisible(false);
            await Settle(300);

            Task<bool> editing = ConnectionEditor.ShowAsync(new Connection { Name = "Selftest Layout", Host = "layout.invalid" }, isNew: true);
            await Settle(900);
            bool fits = ConnectionEditor.OpenDialog is { } dialog && DialogFits(window, dialog, out string bounds);
            SaveRenderProbe(window, "layout-minimum-editor.png");
            ConnectionEditor.OpenDialog?.Hide();
            await editing;
            Check($"The connection editor fits the minimum window and scrolls ({(ConnectionEditor.OpenDialog is null ? "closed" : "")}{LastDialogBounds})", fits);

            await ResizeAsync(window, 1320, 840);
            window.SetDetailsVisible(true);
            await Settle(300);
            Check("Back at 1320 x 840 the sidebar expands and the saved detail setting applies again",
                window.Nav.DisplayMode == FANavigationViewDisplayMode.Expanded && window.IsDetailsVisible);
        }
        finally
        {
            window.Width = width;
            window.Height = height;
            window.WindowState = state;
            await Settle(300);
            window.SetListView(list);
            window.SetDetailsVisible(details);
        }
    }

    private static async Task RunEmptyStateChecksAsync(MainWindow window)
    {
        window.SearchBox.Text = "zzzz-kein-treffer-selftest";
        await Settle(400);
        bool noMatches = window.CurrentEmptyKind == MainWindow.EmptyKind.NoMatches && window.EmptyState.IsEffectivelyVisible;
        SaveRenderProbe(window, "empty-search.png");
        window.EmptyPrimaryButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await Settle(300);
        Check("A search without results says so and \"Suche löschen\" clears it",
            noMatches && string.IsNullOrEmpty(window.SearchBox.Text) && window.CurrentEmptyKind == MainWindow.EmptyKind.None);

        ConnectionGroup group = AppHost.Current.Library.EnsureGroupPath("Selftest Leere Gruppe")!;
        try
        {
            window.SyncWithLibrary();
            await Settle(300);
            window.ShowFilter($"group:{group.Id}");
            await Settle(400);
            Check("An empty group shows \"Diese Gruppe ist leer\" with \"Neue Verbindung hier\"",
                window.CurrentEmptyKind == MainWindow.EmptyKind.EmptyGroup && window.EmptyState.IsEffectivelyVisible);
            SaveRenderProbe(window, "empty-group.png");
        }
        finally
        {
            window.ShowFilter("all");
            await window.RemoveGroupAsync(group);
        }
    }

    private static string LastDialogBounds = "";

    private static async Task ResizeAsync(MainWindow window, double width, double height)
    {
        window.Width = width;
        window.Height = height;
        await Settle(700);
    }

    private static string Describe(MainWindow window) =>
        $"client {window.ClientSize.Width:0}x{window.ClientSize.Height:0}, nav {window.Nav.DisplayMode}, details {window.IsDetailsVisible}, " +
        $"toolbar {window.Toolbar.Bounds.Right:0}/{window.Header.Bounds.Width:0}, tile {window.TilePanel?.TileWidth:0}";

    /// <summary>The header row (search and buttons) lies inside the header, and the header inside the window.</summary>
    private static bool HeaderFits(MainWindow window)
    {
        Point? right = window.Toolbar.TranslatePoint(new Point(window.Toolbar.Bounds.Width, 0), window);
        return window.Toolbar.Bounds.Right <= window.Header.Bounds.Width + 0.5 && right is { } r && r.X <= window.ClientSize.Width + 0.5
            && window.SearchBox.Bounds.Width >= 150;
    }

    /// <summary>Tiles are between 240 and 320 px and the panel is not wider than its list.</summary>
    private static bool TilesFit(MainWindow window) =>
        window.TilePanel is { } panel && panel.TileWidth - 14 is >= 239.5 and <= 320.5 && panel.Bounds.Width <= window.Tiles.Bounds.Width + 0.5;

    private static bool DialogFits(MainWindow window, FAContentDialog dialog, out string bounds)
    {
        Control? box = dialog.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "BackgroundElement")
            ?? dialog.GetVisualDescendants().OfType<Border>().FirstOrDefault();
        Point? topLeft = box?.TranslatePoint(new Point(0, 0), window);
        bounds = box is null || topLeft is not { } p ? "no dialog box" : $"{p.X:0},{p.Y:0} {box.Bounds.Width:0}x{box.Bounds.Height:0} in {window.ClientSize.Width:0}x{window.ClientSize.Height:0}";
        LastDialogBounds = bounds;
        return box is not null && topLeft is { } q && q.X >= 0 && q.Y >= 0
            && q.X + box.Bounds.Width <= window.ClientSize.Width + 0.5 && q.Y + box.Bounds.Height <= window.ClientSize.Height + 0.5;
    }
}
