using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using RemoteManager.Core.Library;
using RemoteManager.Core.Model;

namespace RemoteManager.App.Views;

/// <summary>Where a drop lands relative to the entry under the pointer.</summary>
public enum DropPosition { Before, Into, After }

/// <summary>What is being dragged: connections (one or the selection) or one group (its header).</summary>
public sealed class OverviewDrag
{
    public IReadOnlyList<ConnectionItem> Connections { get; init; } = [];
    public ConnectionGroup? Group { get; init; }
}

/// <summary>A sidebar entry as drop target: a group, or "Alle" for the top level / no group.</summary>
public sealed record NavigationDropTarget(string? GroupId);

/// <summary>
/// Drag and drop in the overview: connections onto a group header (into it), onto a connection
/// (before or after it, into its group), or onto a sidebar group; a group header onto another header
/// (upper part: before it as a sibling, else nested into it). Order and membership are saved.
/// </summary>
public partial class MainWindow
{
    private static readonly DataFormat<OverviewDrag> DragFormat = DataFormat.CreateInProcessFormat<OverviewDrag>("RemoteManager.OverviewDrag");
    private const double DragThreshold = 6;

    private PointerPressedEventArgs? pressArgs;
    private Point pressPoint;
    private object? pressEntry;
    private IReadOnlyList<ConnectionItem> pressSelection = [];
    private ListBoxItem? dropMarked;

    /// <summary>
    /// Applies (or with <paramref name="apply"/> false only checks) a drop of <paramref name="drag"/> on
    /// <paramref name="target"/>: a <see cref="GroupHeaderItem"/>, a <see cref="ConnectionItem"/>, or a
    /// <see cref="NavigationDropTarget"/>. Returns false when the drop is not allowed.
    /// </summary>
    public bool Drop(OverviewDrag drag, object target, DropPosition position, bool apply = true)
    {
        ConnectionLibrary library = app.Library;
        if (drag.Group is { } group)
        {
            string? parent;
            ConnectionGroup? before = null;
            switch (target)
            {
                case GroupHeaderItem { Group: null }:
                    parent = null;
                    break;
                case GroupHeaderItem { Group: { } other } when other == group:
                    return false;
                case GroupHeaderItem { Group: { } other } when position == DropPosition.Before:
                    parent = other.ParentId;
                    before = other;
                    break;
                case GroupHeaderItem { Group: { } other }:
                    parent = other.Id;
                    break;
                case NavigationDropTarget nav when nav.GroupId != group.Id:
                    parent = nav.GroupId;
                    break;
                default:
                    return false;
            }

            // Paths stay unique: no two groups with the same name under one parent.
            if (!library.CanMoveGroup(group, parent) || (parent != group.ParentId && library.HasSiblingNamed(parent, group.Name, except: group)))
                return false;
            if (apply)
            {
                library.MoveGroup(group, parent, before);
                AfterGroupChange();
            }

            return true;
        }

        List<Connection> moving = [.. drag.Connections.Select(c => c.Model).Where(library.Contains)];
        if (moving.Count == 0)
            return false;

        string? groupId;
        Connection? beforeConnection = null;
        switch (target)
        {
            case GroupHeaderItem header:
                groupId = header.GroupId;
                break;
            case NavigationDropTarget nav:
                groupId = nav.GroupId;
                break;
            case ConnectionItem c when !drag.Connections.Contains(c) && library.Contains(c.Model):
                groupId = library.FindGroup(c.Model.GroupId)?.Id;
                // A sorted list has no manual order to drop into: only the group changes there.
                if (!(IsListView && sortColumn is not null))
                {
                    beforeConnection = position == DropPosition.Before
                        ? c.Model
                        : library.ConnectionsIn(groupId).Where(x => !moving.Contains(x)).SkipWhile(x => x != c.Model).Skip(1).FirstOrDefault();
                }

                break;
            default:
                return false;
        }

        if (apply)
        {
            library.MoveConnections(moving, groupId, beforeConnection);
            AfterGroupChange();
            Select(drag.Connections);
        }

        return true;
    }

    private void HookDragDrop(ListBox list)
    {
        list.AddHandler(PointerPressedEvent, OnItemPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        list.AddHandler(PointerMovedEvent, OnItemMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        list.AddHandler(PointerReleasedEvent, (_, _) => pressArgs = null, RoutingStrategies.Tunnel, handledEventsToo: true);
        DragDrop.SetAllowDrop(list, true);
        list.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        list.AddHandler(DragDrop.DragLeaveEvent, (_, _) => MarkDrop(null, DropPosition.Into));
        list.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void HookNavigationDrop()
    {
        DragDrop.SetAllowDrop(Nav, true);
        Nav.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (e.Handled)
                return;
            e.DragEffects = e.DataTransfer.TryGetValue(DragFormat) is { } drag && NavigationTargetAt(e.Source) is { } target && Drop(drag, target, DropPosition.Into, apply: false)
                ? DragDropEffects.Move
                : DragDropEffects.None;
        });
        Nav.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (!e.Handled && e.DataTransfer.TryGetValue(DragFormat) is { } drag && NavigationTargetAt(e.Source) is { } target)
                Drop(drag, target, DropPosition.Into);
        });
    }

    private static NavigationDropTarget? NavigationTargetAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<FANavigationViewItem>(includeSelf: true)?.Tag switch
        {
            "all" => new NavigationDropTarget(null),
            string tag when tag.StartsWith("group:", StringComparison.Ordinal) => new NavigationDropTarget(tag["group:".Length..]),
            _ => null,
        };

    private void OnItemPressed(object? sender, PointerPressedEventArgs e)
    {
        pressArgs = null;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || (e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        if (EntryAt(e.Source) is not { } entry)
            return;

        // Pressing a selected tile may reset the selection; keep it so the whole selection is dragged.
        pressSelection = SelectedConnections;
        pressArgs = e;
        pressPoint = e.GetPosition(this);
        pressEntry = entry;
    }

    private async void OnItemMoved(object? sender, PointerEventArgs e)
    {
        if (pressArgs is not { } start || pressEntry is null)
            return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            pressArgs = null;
            return;
        }

        Point point = e.GetPosition(this);
        if (Math.Abs(point.X - pressPoint.X) < DragThreshold && Math.Abs(point.Y - pressPoint.Y) < DragThreshold)
            return;
        pressArgs = null;

        OverviewDrag? drag = pressEntry switch
        {
            GroupHeaderItem { Group: { } group } => new OverviewDrag { Group = group },
            ConnectionItem c => new OverviewDrag { Connections = pressSelection.Contains(c) ? pressSelection : [c] },
            _ => null,
        };
        if (drag is null)
            return;

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(DragFormat, drag));
        try
        {
            await DragDrop.DoDragDropAsync(start, transfer, DragDropEffects.Move);
        }
        finally
        {
            MarkDrop(null, DropPosition.Into);
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.DataTransfer.TryGetValue(DragFormat) is { } drag && DropTargetAt(drag, e) is { } target)
        {
            e.DragEffects = DragDropEffects.Move;
            MarkDrop(target.Container, target.Position);
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
            MarkDrop(null, DropPosition.Into);
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        MarkDrop(null, DropPosition.Into);
        if (e.DataTransfer.TryGetValue(DragFormat) is { } drag && DropTargetAt(drag, e) is { } target)
            Drop(drag, target.Container.Content!, target.Position);
    }

    private (ListBoxItem Container, DropPosition Position)? DropTargetAt(OverviewDrag drag, DragEventArgs e)
    {
        if (ContainerAt(e.Source) is not { Content: { } entry } container)
            return null;

        Point p = e.GetPosition(container);
        Size size = container.Bounds.Size;
        DropPosition position = entry switch
        {
            GroupHeaderItem => drag.Group is not null && p.Y < size.Height * 0.35 ? DropPosition.Before : DropPosition.Into,
            _ when IsListView => p.Y < size.Height / 2 ? DropPosition.Before : DropPosition.After,
            _ => p.X < size.Width / 2 ? DropPosition.Before : DropPosition.After,
        };
        return Drop(drag, entry, position, apply: false) ? (container, position) : null;
    }

    private void MarkDrop(ListBoxItem? container, DropPosition position)
    {
        if (dropMarked is not null && dropMarked != container)
            dropMarked.Classes.RemoveAll(["dropInto", "dropBefore", "dropAfter"]);
        dropMarked = container;
        if (container is null)
            return;
        container.Classes.Set("dropInto", position == DropPosition.Into);
        container.Classes.Set("dropBefore", position == DropPosition.Before);
        container.Classes.Set("dropAfter", position == DropPosition.After);
    }
}
