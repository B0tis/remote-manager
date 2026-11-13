using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Connections;
using RemoteManager.App.Resources;
using RemoteManager.App.Sync;
using RemoteManager.Core.Model;

namespace RemoteManager.App.Views;

/// <summary>How much room the sidebar takes in the layout.</summary>
public enum SidebarMode { Expanded, Compact }

/// <summary>
/// The sidebar: all, favorites, recent, open sessions, the group tree, and tags, each with its count. A plain
/// list of <see cref="SidebarEntry"/> rows, rebuilt whenever groups, tags, or counts may have changed.
/// From <see cref="WideLayoutWidth"/> it is expanded (or icons only, if the user folded it with the ☰ button);
/// below that it shows icons only and ☰ opens it over the content until an entry is chosen (docs/ui.md).
/// </summary>
public partial class MainWindow
{
    private const string SidebarSetting = "overview.sidebar";
    private const double SidebarExpandedWidth = 240, SidebarCompactWidth = 54;

    private List<SidebarEntry> sidebarEntries = [];
    private bool sidebarWanted = true;
    private bool sidebarFloating;

    /// <summary>Expanded while the window is wide and the user did not fold the sidebar; otherwise icons only.</summary>
    public SidebarMode SidebarMode => wideLayout && sidebarWanted ? SidebarMode.Expanded : SidebarMode.Compact;

    /// <summary>The compact sidebar is opened over the content.</summary>
    public bool IsSidebarFloating => sidebarFloating;

    public IReadOnlyList<SidebarEntry> SidebarEntries => sidebarEntries;
    public ListBox Navigation => NavList;

    private bool SidebarShowsLabels => SidebarMode == SidebarMode.Expanded || sidebarFloating;

    /// <summary>The sidebar entry of a filter tag, or null (a subgroup of a collapsed group, a tag in the compact sidebar).</summary>
    public SidebarEntry? FindSidebarEntry(string tag) => sidebarEntries.FirstOrDefault(e => e.Tag == tag);

    /// <summary>Folds or unfolds the sidebar (☰): saved on a wide window, floating on a narrow one.</summary>
    public void ToggleSidebar()
    {
        if (wideLayout)
        {
            sidebarWanted = !sidebarWanted;
            app.Library.SetSetting(SidebarSetting, sidebarWanted ? null : "compact");
        }
        else
        {
            sidebarFloating = !sidebarFloating;
        }

        ApplySidebarMode();
    }

    private void InitializeSidebar()
    {
        sidebarWanted = app.Library.GetSetting(SidebarSetting) != "compact";
        NavList.SelectionChanged += OnSidebarSelectionChanged;
        NavList.ContainerPrepared += (_, e) => PrepareSidebarContainer(e.Container);
        NavList.KeyDown += OnNavigationKeyDown;
        // The "+" of the group label and the chevron of a group act without selecting the row.
        NavList.AddHandler(Button.ClickEvent, OnSidebarButtonClick);
        SidebarToggle.Click += (_, _) => ToggleSidebar();
        SidebarScrim.PointerPressed += (_, _) => CloseFloatingSidebar();
        SidebarStatus.Click += async (_, _) => await SyncDialog.ShowAsync(app.Sync);
        HookNavigationDrop();
    }

    private void CloseFloatingSidebar()
    {
        if (!sidebarFloating)
            return;
        sidebarFloating = false;
        ApplySidebarMode();
    }

    /// <summary>Widths, labels, and the floating state; then the entries, because the compact sidebar lists fewer.</summary>
    private void ApplySidebarMode()
    {
        bool labels = SidebarShowsLabels;
        SidebarSlot.Width = SidebarMode == SidebarMode.Expanded ? SidebarExpandedWidth : SidebarCompactWidth;
        Sidebar.Width = labels ? SidebarExpandedWidth : SidebarCompactWidth;
        Sidebar.Classes.Set("floating", sidebarFloating);
        SidebarScrim.IsVisible = sidebarFloating;
        NavList.Classes.Set("compact", !labels);
        SidebarStatus.Classes.Set("compactSidebar", !labels);
        TitleBarBrand.IsVisible = Bounds.Width == 0 || Bounds.Width >= 760;
        BuildNavigation();
    }

    /// <summary>Rebuilds the sidebar entries, keeps the current filter selected, and applies the filter.</summary>
    private void BuildNavigation()
    {
        bool labels = SidebarShowsLabels;
        var entries = new List<SidebarEntry>
        {
            Entry(SidebarEntry.AllTag, Strings.Nav_All, FASymbol.Library),
            Entry(SidebarEntry.FavoritesTag, Strings.Nav_Favorites, FASymbol.Star),
            Entry(SidebarEntry.RecentTag, Strings.Nav_Recent, FASymbol.Clock),
            Entry(SidebarEntry.ActiveTag, Strings.Nav_Active, FASymbol.Remote),
        };

        if (labels)
            entries.Add(new SidebarEntry { Tag = SidebarEntry.HeaderPrefix + "groups", Label = Strings.Nav_Groups, IsHeader = true, CanAdd = true });
        foreach (ConnectionGroup group in app.Library.ChildrenOf(null))
            AddGroup(group, 0);

        string[] tags = [.. items.SelectMany(c => c.Model.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase)];
        if (labels && tags.Length > 0)
        {
            entries.Add(new SidebarEntry { Tag = SidebarEntry.HeaderPrefix + "tags", Label = Strings.Nav_Tags, IsHeader = true });
            entries.AddRange(tags.Select(t => new SidebarEntry { Tag = SidebarEntry.TagPrefix + t, Label = t, Symbol = FASymbol.Tag, TagName = t }));
        }

        // A filter whose group or tag is gone falls back to "all".
        bool valid = filter is SidebarEntry.AllTag or SidebarEntry.FavoritesTag or SidebarEntry.RecentTag or SidebarEntry.ActiveTag
            || (CurrentGroupId is { } groupId && app.Library.FindGroup(groupId) is not null)
            || (filter.StartsWith(SidebarEntry.TagPrefix, StringComparison.Ordinal)
                && tags.Contains(filter[SidebarEntry.TagPrefix.Length..], StringComparer.OrdinalIgnoreCase));
        if (!valid)
            filter = SidebarEntry.AllTag;

        sidebarEntries = entries;
        UpdateSidebarCounts();
        buildingNavigation = true;
        NavList.ItemsSource = entries;
        NavList.SelectedItem = entries.FirstOrDefault(e => string.Equals(e.Tag, filter, StringComparison.OrdinalIgnoreCase));
        buildingNavigation = false;
        ApplyFilter();

        SidebarEntry Entry(string tag, string label, FASymbol symbol) =>
            new() { Tag = tag, Label = label, Symbol = symbol, ShowTip = !labels };

        void AddGroup(ConnectionGroup group, int depth)
        {
            ConnectionGroup[] children = [.. app.Library.ChildrenOf(group.Id)];
            entries.Add(new SidebarEntry
            {
                Tag = SidebarEntry.GroupPrefix + group.Id,
                Label = group.Name,
                Symbol = FASymbol.Folder,
                Depth = labels ? depth : 0,
                Group = group,
                HasChildren = labels && children.Length > 0,
                IsExpanded = !group.IsCollapsed,
                ShowTip = !labels,
            });
            // The compact sidebar has room for the top level only.
            if (!labels || group.IsCollapsed)
                return;
            foreach (ConnectionGroup child in children)
                AddGroup(child, depth + 1);
        }
    }

    /// <summary>Counts next to the entries; cheap enough to run after every change (a session opened, a star toggled).</summary>
    private void UpdateSidebarCounts()
    {
        foreach (SidebarEntry entry in sidebarEntries)
        {
            entry.Count = entry.Tag switch
            {
                SidebarEntry.AllTag => items.Count,
                SidebarEntry.FavoritesTag => items.Count(c => c.IsFavorite),
                SidebarEntry.ActiveTag => sessionWindows.Count,
                _ when entry.Group is { } group => items.Count(c => app.Library.IsInGroup(c.Model, group.Id)),
                _ when entry.TagName is { } tag => items.Count(c => c.Model.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)),
                _ => null,
            };
            if (entry.Tag == SidebarEntry.ActiveTag)
                entry.ShowLamp = sessionWindows.Count > 0;
        }
    }

    private void OnSidebarSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (buildingNavigation)
            return;

        if (NavList.SelectedItem is not SidebarEntry { IsHeader: false } entry)
        {
            // A section label is not a filter: stay on the current one.
            buildingNavigation = true;
            NavList.SelectedItem = FindSidebarEntry(filter);
            buildingNavigation = false;
            return;
        }

        filter = entry.Tag;
        if (sidebarFloating)
            CloseFloatingSidebar(); // rebuilds and applies the filter
        else
            ApplyFilter();
    }

    private void OnSidebarButtonClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button { Tag: SidebarEntry entry } button)
            return;

        if (button.Classes.Contains("navAdd"))
        {
            e.Handled = true;
            _ = CreateGroupAsync(null);
        }
        else if (button.Classes.Contains("navChevron") && entry.Group is { } group)
        {
            e.Handled = true;
            SetCollapsed(group, !group.IsCollapsed);
        }
    }

    private void PrepareSidebarContainer(Control container)
    {
        SidebarEntry? entry = (container as ContentControl)?.Content as SidebarEntry;
        container.Classes.Set("navHeader", entry is { IsHeader: true });
        container.ContextMenu = entry switch
        {
            { Group: { } group } => CreateGroupMenu(group),
            { TagName: { } tag } => CreateTagMenu(tag),
            _ => null,
        };
    }

    /// <summary>The sidebar entry under an event source (drop target, self-test).</summary>
    private static SidebarEntry? SidebarEntryAt(object? source) =>
        ((source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true))?.Content as SidebarEntry;

    /// <summary>A session window opened or closed: the count and lamp of "Aktive Sitzungen", the tile badges, and that filter's list.</summary>
    private void OnSessionsChanged()
    {
        HashSet<ConnectionItem> open = [.. sessionWindows.Select(w => ((ISessionWindow)w).Connection)];
        foreach (ConnectionItem item in items)
            item.IsSessionOpen = open.Contains(item);
        UpdateSidebarCounts();
        if (filter == SidebarEntry.ActiveTag)
            ApplyFilter();
    }
}
