using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Resources;
using RemoteManager.Core.Library;
using RemoteManager.Core.Model;

namespace RemoteManager.App.Views;

/// <summary>Group management: create, edit (name, parent, logon), delete, collapse, and the group context menu.</summary>
public partial class MainWindow
{
    /// <summary>Asks for a new group under <paramref name="parentId"/>; returns it after it was added, or null on cancel.</summary>
    internal async Task<ConnectionGroup?> CreateGroupAsync(string? parentId)
    {
        var group = new ConnectionGroup { ParentId = parentId };
        (bool saved, string? parent) = await GroupEditor.ShowAsync(group, isNew: true);
        if (!saved)
            return null;

        group.ParentId = parent;
        group.SortOrder = app.Library.ChildrenOf(parent).Count();
        app.Library.AddGroup(group);
        AfterGroupChange();
        return group;
    }

    /// <summary>Edits name, parent, description, and logon of a group.</summary>
    internal async Task EditGroupAsync(ConnectionGroup group)
    {
        string? oldParent = group.ParentId;
        (bool saved, string? parent) = await GroupEditor.ShowAsync(group, isNew: false);
        if (!saved)
            return;

        app.Library.UpdateGroup(group);
        if (parent != oldParent)
            app.Library.MoveGroup(group, parent);
        foreach (ConnectionItem item in items)
            item.RefreshCredentialState();
        AfterGroupChange();
    }

    /// <summary>Asks how to delete the group (content to the parent, or everything) and deletes it.</summary>
    internal async Task DeleteGroupAsync(ConnectionGroup group)
    {
        if (await GroupDeleteDialog.AskAsync(app.Library, group) is { } removal)
            await RemoveGroupAsync(group, removal);
    }

    /// <summary>
    /// Removes the group and the stored passwords of every removed group. <see cref="GroupRemoval.MoveContentUp"/>
    /// moves subgroups and connections to its parent; <see cref="GroupRemoval.DeleteContent"/> also deletes them,
    /// with their windows, preview images, and own passwords.
    /// </summary>
    internal async Task RemoveGroupAsync(ConnectionGroup group, GroupRemoval removal = GroupRemoval.MoveContentUp)
    {
        if (removal == GroupRemoval.DeleteContent)
        {
            foreach (ConnectionItem item in items.Where(i => app.Library.IsInGroup(i.Model, group.Id)).ToList())
                await DiscardConnectionAsync(item);
        }

        (IReadOnlyList<ConnectionGroup> removed, _) = app.Library.RemoveGroup(group, removal);
        foreach (ConnectionGroup gone in removed.Where(g => g.Credential is not null))
        {
            try
            {
                await app.Credentials.RemoveGroupSecretAsync(gone);
            }
            catch (Win32Exception ex)
            {
                ShowToast(gone.Name, string.Format(CultureInfo.CurrentCulture, Strings.Password_RemoveFailed, ex.Message), FAInfoBarSeverity.Error);
            }
        }

        if (removed.Any(g => filter == $"group:{g.Id}"))
            filter = "all";
        foreach (ConnectionItem item in items)
            item.RefreshCredentialState();
        AfterGroupChange();
    }

    /// <summary>Delete in the sidebar deletes the selected group, or removes the selected tag (after asking).</summary>
    private void OnNavigationKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || e.KeyModifiers != KeyModifiers.None || Nav.SelectedItem is not FANavigationViewItem { Tag: string tag })
            return;
        if (tag.StartsWith("group:", StringComparison.Ordinal) && app.Library.FindGroup(tag["group:".Length..]) is { } group)
        {
            e.Handled = true;
            _ = DeleteGroupAsync(group);
        }
        else if (tag.StartsWith("tag:", StringComparison.Ordinal))
        {
            e.Handled = true;
            _ = RemoveTagAsync(tag["tag:".Length..]);
        }
    }

    /// <summary>
    /// Asks, then removes a tag from every connection (the connections stay). Imports from 1Remote, which has no
    /// folders, bring tags instead; in the sidebar they look like folders and are removed the same way.
    /// </summary>
    internal async Task RemoveTagAsync(string tag)
    {
        int count = items.Count(i => i.Model.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
        var dialog = new FAContentDialog
        {
            Title = string.Format(CultureInfo.CurrentCulture, Strings.TagRemove_Title, tag),
            Content = new TextBlock
            {
                Text = string.Format(CultureInfo.CurrentCulture, count == 1 ? Strings.TagRemove_MessageOne : Strings.TagRemove_Message, count),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                MaxWidth = 460,
            },
            PrimaryButtonText = Strings.TagRemove_Action,
            CloseButtonText = Strings.Action_Cancel,
            DefaultButton = FAContentDialogButton.Close,
        };
        TagRemoveDialog = dialog;
        FAContentDialogResult result = await dialog.ShowAsync();
        TagRemoveDialog = null;
        if (result != FAContentDialogResult.Primary)
            return;

        app.Library.RemoveTag(tag);
        if (string.Equals(filter, $"tag:{tag}", StringComparison.OrdinalIgnoreCase))
            filter = "all";
        foreach (ConnectionItem item in items)
            item.Changed();
        AfterGroupChange();
    }

    /// <summary>The "remove tag" question while it is shown (self-test).</summary>
    internal FAContentDialog? TagRemoveDialog { get; private set; }

    private ContextMenu CreateTagMenu(string tag)
    {
        var remove = new MenuItem
        {
            Header = Strings.Action_RemoveTag,
            Icon = new FASymbolIcon { Symbol = FASymbol.Delete },
            InputGesture = new KeyGesture(Key.Delete),
        };
        remove.Click += async (_, _) => await RemoveTagAsync(tag);
        return new ContextMenu { ItemsSource = new object[] { remove } };
    }

    /// <summary>Collapses or expands a group section; the state is saved and the sidebar follows.</summary>
    public void ToggleCollapsed(GroupHeaderItem header)
    {
        if (header.Group is not { } group)
            return;
        group.IsCollapsed = !group.IsCollapsed;
        app.Library.UpdateGroup(group);
        if (Nav.MenuItemsSource?.OfType<FANavigationViewItem>() is { } navItems
            && Flatten(navItems).FirstOrDefault(i => (string?)i.Tag == $"group:{group.Id}") is { MenuItemsSource: not null } item)
        {
            buildingNavigation = true;
            item.IsExpanded = !group.IsCollapsed;
            buildingNavigation = false;
            SelectNavigationItem(Nav.SelectedItem as FANavigationViewItem);
        }

        ApplyFilter();
    }

    /// <summary>Asks for a target group and moves the connections there (end of the group).</summary>
    internal async void MoveToGroupAsync(IReadOnlyList<ConnectionItem> connections)
    {
        List<Connection> moving = [.. connections.Select(c => c.Model).Where(app.Library.Contains)];
        if (moving.Count == 0)
            return;

        List<string?> ids = [null, .. app.Library.DescendantsOf(null).Select(g => g.Id)];
        var picker = new ComboBox
        {
            ItemsSource = ids.Select(id => id is null ? Strings.Group_None : app.Library.PathOf(id)).ToList(),
            SelectedIndex = Math.Max(0, ids.IndexOf(moving[0].GroupId)),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            MinWidth = 320,
        };
        var dialog = new FAContentDialog
        {
            Title = moving.Count == 1
                ? string.Format(CultureInfo.CurrentCulture, Strings.Move_Title, connections[0].Name)
                : string.Format(CultureInfo.CurrentCulture, Strings.Move_TitleMany, moving.Count),
            Content = picker,
            PrimaryButtonText = Strings.Action_Move,
            CloseButtonText = Strings.Action_Cancel,
            DefaultButton = FAContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != FAContentDialogResult.Primary || picker.SelectedIndex < 0)
            return;

        app.Library.MoveConnections(moving, ids[picker.SelectedIndex]);
        AfterGroupChange();
    }

    /// <summary>After groups or memberships changed: labels, sidebar, sections, inherited logons.</summary>
    private void AfterGroupChange()
    {
        RefreshLabels();
        BuildNavigation();
        Details.Refresh();
    }

    /// <summary>Context menu of a group header or sidebar group; <paramref name="group"/> null is "Ohne Gruppe".</summary>
    private ContextMenu CreateGroupMenu(ConnectionGroup? group)
    {
        var connectAll = new MenuItem { Header = Strings.Action_ConnectAll, Icon = new FASymbolIcon { Symbol = FASymbol.Play } };
        var newConnection = new MenuItem { Header = Strings.Action_NewConnectionHere, Icon = new FASymbolIcon { Symbol = FASymbol.Add } };
        var newGroup = new MenuItem
        {
            Header = group is null ? Strings.Action_NewGroup : Strings.Action_NewSubgroup,
            Icon = new FASymbolIcon { Symbol = FASymbol.NewFolder },
        };
        var edit = new MenuItem { Header = Strings.Action_EditGroup, Icon = new FASymbolIcon { Symbol = FASymbol.Edit } };
        var delete = new MenuItem { Header = Strings.Action_DeleteGroup, Icon = new FASymbolIcon { Symbol = FASymbol.Delete } };
        object[] entries = group is null
            ? [connectAll, newConnection, newGroup]
            : [connectAll, newConnection, newGroup, edit, new Separator(), delete];
        var menu = new ContextMenu { ItemsSource = entries };

        List<ConnectionItem> Members() => group is null
            ? [.. items.Where(c => app.Library.FindGroup(c.Model.GroupId) is null)]
            : [.. items.Where(c => app.Library.IsInGroup(c.Model, group.Id))];

        menu.Opening += (_, _) =>
        {
            int count = Members().Count;
            connectAll.IsEnabled = count > 0;
            connectAll.Header = string.Format(CultureInfo.CurrentCulture, Strings.Action_ConnectCount, count);
        };
        connectAll.Click += (_, _) => OpenAll(Members());
        newConnection.Click += (_, _) => AddConnection(group?.Id);
        newGroup.Click += async (_, _) => await CreateGroupAsync(group?.Id);
        edit.Click += async (_, _) => await EditGroupAsync(group!);
        delete.Click += async (_, _) => await DeleteGroupAsync(group!);
        return menu;
    }
}
