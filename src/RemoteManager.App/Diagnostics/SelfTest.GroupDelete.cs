using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Resources;
using RemoteManager.App.Views;
using RemoteManager.Core.Library;
using RemoteManager.Core.Model;
using RemoteManager.Core.Storage;

namespace RemoteManager.App.Diagnostics;

/// <summary>
/// Deleting groups the way the UI does it: right-click a sidebar entry or a section header, "Gruppe löschen …",
/// answer the dialog; Delete on a selected sidebar group. Covers cancel, an empty group, content moved to the
/// parent, and content deleted, each checked in memory, in the window, and in the database.
/// </summary>
internal static partial class SelfTest
{
    private static async Task RunGroupDeleteChecksAsync(MainWindow window)
    {
        AppHost app = AppHost.Current;
        ConnectionLibrary library = app.Library;
        string databasePath = app.StoreReport.DatabasePath;
        ConnectionLibrary Reopened() => new(new SqliteConnectionStore(databasePath));
        var created = new List<ConnectionItem>();
        window.SetListView(false);
        try
        {
            // Cancel, then delete an empty group from its sidebar context menu.
            ConnectionGroup empty = library.EnsureGroupPath("Selftest Leer")!;
            window.SyncWithLibrary();
            await Settle(500);
            GroupDeleteDialog? asked = await OpenGroupDeleteAsync(window, SidebarItem(window, empty));
            bool emptyDialog = asked is { HasContent: false };
            asked?.Dialog.Hide();
            await Settle(400);
            bool kept = library.FindGroup(empty.Id) is not null;
            asked = await OpenGroupDeleteAsync(window, SidebarItem(window, empty));
            asked?.Dialog.Hide(FAContentDialogResult.Primary);
            await Settle(600);
            Check("Deleting an empty group from the sidebar menu: simple confirmation, cancel keeps it, \"Löschen\" removes it (also on disk and in the sidebar)",
                emptyDialog && kept && library.FindGroup(empty.Id) is null && Reopened().FindGroup(empty.Id) is null && SidebarItem(window, empty) is null);

            // A group with a connection and a subgroup, deleted from its section header: content moves up (default).
            ConnectionGroup outer = library.EnsureGroupPath("Selftest Ordner")!;
            ConnectionGroup inner = library.EnsureGroupPath("Selftest Ordner / Selftest Unterordner")!;
            ConnectionItem direct = window.Adopt(new Connection { Name = "Selftest Ordner direkt", Host = "rm-selftest-a.invalid", GroupId = outer.Id });
            ConnectionItem nested = window.Adopt(new Connection { Name = "Selftest Ordner tief", Host = "rm-selftest-b.invalid", GroupId = inner.Id });
            created.AddRange([direct, nested]);
            window.SyncWithLibrary();
            await Settle(500);
            window.Tiles.ScrollIntoView(window.TileEntries.OfType<GroupHeaderItem>().First(h => h.Group == outer));
            await Settle(400);
            ListBoxItem? header = window.Tiles.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(c => c.Content is GroupHeaderItem h && h.Group == outer);
            asked = await OpenGroupDeleteAsync(window, header);
            SaveRenderProbe(window, "group-delete-dialog.png");
            bool offersChoice = asked is { HasContent: true, Choice: GroupRemoval.MoveContentUp };
            asked?.Dialog.Hide(FAContentDialogResult.Primary);
            await Settle(600);
            ConnectionLibrary reopened = Reopened();
            Check("Deleting a group with content from its section header offers \"move to parent\" (default) and moves connections and subgroups up",
                offersChoice && library.FindGroup(outer.Id) is null && inner.ParentId is null && direct.Model.GroupId is null && nested.Model.GroupId == inner.Id
                && window.Connections.Contains(direct) && window.Connections.Contains(nested)
                && reopened.FindGroup(outer.Id) is null && reopened.FindGroup(inner.Id)?.ParentId is null
                && reopened.FindConnection(direct.Id)?.GroupId is null && reopened.FindConnection(nested.Id)?.GroupId == inner.Id);

            // Delete on the selected sidebar group, "delete everything": the group and its connections go.
            FANavigationViewItem? innerItem = SidebarItem(window, inner);
            window.Nav.SelectedItem = innerItem;
            await Settle(400);
            window.Nav.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Delete, Source = innerItem });
            await Settle(800);
            asked = GroupDeleteDialog.Current;
            if (asked is not null)
                asked.Choice = GroupRemoval.DeleteContent;
            asked?.Dialog.Hide(FAContentDialogResult.Primary);
            await Settle(600);
            reopened = Reopened();
            Check("Delete on a selected sidebar group asks; \"Alles mit löschen\" removes the group with its connections (window, sidebar, disk)",
                asked is { HasContent: true } && library.FindGroup(inner.Id) is null && !library.Contains(nested.Model) && !window.Connections.Contains(nested)
                && SidebarItem(window, inner) is null && reopened.FindGroup(inner.Id) is null && reopened.FindConnection(nested.Id) is null
                && library.Contains(direct.Model));

            // A tag in the sidebar (1Remote imports bring tags, not folders): right-click, "Tag entfernen …".
            const string tag = "selftest-tag-entfernen";
            direct.Model.Tags = [.. direct.Model.Tags, tag];
            window.Save(direct);
            window.SyncWithLibrary();
            await Settle(500);
            FANavigationViewItem? tagItem = SidebarTag(window, tag);
            bool menuOpened = await RightClickAsync(window, tagItem);
            if (tagItem?.ContextMenu?.ItemsSource?.OfType<MenuItem>().FirstOrDefault(m => m.Header as string == Strings.Action_RemoveTag) is { } remove)
            {
                remove.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                tagItem.ContextMenu.Close();
            }

            await Settle(600);
            bool tagAsked = window.TagRemoveDialog is not null;
            window.TagRemoveDialog?.Hide(FAContentDialogResult.Primary);
            await Settle(600);
            Check("A sidebar tag has a context menu; \"Tag entfernen …\" removes it from its connections, which stay (also on disk)",
                menuOpened && tagAsked && !direct.Model.Tags.Contains(tag) && library.Contains(direct.Model) && SidebarTag(window, tag) is null
                && Reopened().FindConnection(direct.Id) is { } saved && !saved.Tags.Contains(tag));
        }
        finally
        {
            foreach (ConnectionItem c in created.Where(c => window.Connections.Contains(c)))
                await window.RemoveConnectionAsync(c);
            foreach (ConnectionGroup g in library.Groups.Where(g => g.Name.StartsWith("Selftest", StringComparison.Ordinal)).ToList())
                await window.RemoveGroupAsync(g);
        }
    }

    private static FANavigationViewItem? SidebarItem(MainWindow window, ConnectionGroup group)
    {
        static IEnumerable<FANavigationViewItem> Flatten(IEnumerable<FANavigationViewItem> items) =>
            items.SelectMany(i => (i.MenuItemsSource?.OfType<FANavigationViewItem>() is { } children ? Flatten(children) : []).Prepend(i));
        return window.Nav.MenuItemsSource?.OfType<FANavigationViewItem>() is { } items
            ? Flatten(items).FirstOrDefault(i => (string?)i.Tag == $"group:{group.Id}")
            : null;
    }

    private static FANavigationViewItem? SidebarTag(MainWindow window, string tag) =>
        window.Nav.MenuItemsSource?.OfType<FANavigationViewItem>().FirstOrDefault(i => (string?)i.Tag == $"tag:{tag}");

    /// <summary>Right-clicks <paramref name="owner"/> (routed pointer events); true when its context menu opened.</summary>
    private static async Task<bool> RightClickAsync(MainWindow window, Control? owner)
    {
        if (owner is null)
            return false;
        Visual target = owner.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault() ?? (Visual)owner;
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        Point position = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window) ?? default;
        ((Interactive)target).RaiseEvent(new PointerPressedEventArgs(target, pointer, window, position, 1,
            new PointerPointProperties(RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed), KeyModifiers.None, 1));
        ((Interactive)target).RaiseEvent(new PointerReleasedEventArgs(target, pointer, window, position, 2,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.RightButtonReleased), KeyModifiers.None, MouseButton.Right));
        await Settle(400);
        return owner.ContextMenu is { IsOpen: true };
    }

    /// <summary>Right-clicks <paramref name="owner"/>, clicks "Gruppe löschen …", returns the open dialog.</summary>
    private static async Task<GroupDeleteDialog?> OpenGroupDeleteAsync(MainWindow window, Control? owner)
    {
        if (!await RightClickAsync(window, owner))
        {
            owner?.ContextMenu?.Close();
            return null;
        }

        if (owner!.ContextMenu is not { IsOpen: true } menu
            || menu.ItemsSource?.OfType<MenuItem>().FirstOrDefault(m => m.Header as string == Strings.Action_DeleteGroup) is not { IsEnabled: true } delete)
        {
            owner.ContextMenu?.Close();
            return null;
        }

        delete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
        await Settle(600);
        return GroupDeleteDialog.Current;
    }
}
