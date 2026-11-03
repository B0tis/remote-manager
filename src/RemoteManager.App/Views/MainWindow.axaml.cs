using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Connections;
using RemoteManager.App.Connections.RemoteTools;
using RemoteManager.App.Resources;
using RemoteManager.App.Services;
using RemoteManager.Core.History;
using RemoteManager.Core.Model;
using RemoteManager.Core.Status;

namespace RemoteManager.App.Views;

/// <summary>Sortable columns of the list view.</summary>
public enum OverviewColumn { Status, Name, Type, Host, Group, Tags, Description, LastConnected }

/// <summary>
/// The connection overview: sidebar (all, favorites, recent, nested groups, tags), tile and list view
/// with collapsible group sections, search, and the detail panel on the right. Opening a connection goes
/// through its type's <see cref="IConnectionHandler"/>; embedded sessions get their own window, which
/// this window tracks. Groups and drag and drop: MainWindow.Groups.cs and MainWindow.DragDrop.cs; the
/// quick launcher: MainWindow.Launcher.cs.
/// </summary>
public partial class MainWindow : Window, IConnectionHost
{
    private const string ViewSetting = "overview.view";
    private const string DetailsSetting = "overview.details";
    private const string SortSetting = "overview.sort";
    private static readonly TimeSpan StatusInterval = TimeSpan.FromMinutes(2);

    private readonly AppHost app;
    private readonly List<ConnectionItem> items = [];
    private readonly List<ConnectionItem> visible = [];
    private readonly List<Window> sessionWindows = [];
    private readonly Dictionary<ConnectionItem, long> openHistory = [];
    private readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer statusTimer = new() { Interval = StatusInterval };
    private List<object> tileEntries = [];
    private List<object> rowEntries = [];
    private string filter = "all";
    private OverviewColumn? sortColumn;
    private bool sortDescending;
    private bool clipboardCleared;
    private bool buildingNavigation;
    private string? navigationSignature;
    private bool updatingSelection;

    public MainWindow()
    {
        InitializeComponent();
        app = AppHost.Current;
        foreach (Connection connection in app.Library.Connections)
            items.Add(CreateItem(connection));

        TileList.ContextMenu = CreateContextMenu();
        RowList.ContextMenu = CreateContextMenu();
        foreach (ListBox list in new[] { TileList, RowList })
        {
            list.DoubleTapped += (_, e) =>
            {
                if (EntryAt(e.Source) is ConnectionItem)
                    OpenSelected();
            };
            list.Tapped += (_, e) =>
            {
                if (EntryAt(e.Source) is GroupHeaderItem header)
                    ToggleCollapsed(header);
            };
            list.SelectionChanged += (_, _) => OnSelectionChanged(list);
            list.ContainerPrepared += (_, e) => PrepareContainer(e.Container);
            list.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                    OpenSelected();
                else if (e.Key == Key.Delete && SelectedConnections.Count > 0)
                    DeleteConnections(SelectedConnections);
                else if (e.Key == Key.F2 && Selected is { } single)
                    EditConnection(single);
            };
            HookDragDrop(list);
        }

        // The star on a tile toggles the favorite without selecting or opening the tile.
        TileList.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is Button { Tag: ConnectionItem c } button && button.Classes.Contains("star"))
            {
                SetFavorite([c], !c.IsFavorite);
                e.Handled = true;
            }
        });
        foreach (Button header in ColumnHeaders.Children.OfType<Button>())
            header.Click += (_, _) => CycleSort(Enum.Parse<OverviewColumn>((string)header.Tag!));

        Details.Attach(this);
        TileToggle.Click += (_, _) => SetListView(false);
        ListToggle.Click += (_, _) => SetListView(true);
        DetailsToggle.Click += (_, _) => SetDetailsVisible(DetailsToggle.IsChecked == true);
        SearchBox.TextChanged += (_, _) => ApplyFilter();
        ConnectButton.Click += (_, _) => OpenSelected();
        AddButton.Click += (_, _) => AddConnection();

        InitializeUpdates();
        InitializeLivePreview(); // MainWindow.LivePreview.cs
        MoreButton.Flyout = CreateMoreMenu(); // MainWindow.Menu.cs
        InitializeEmptyState(); // MainWindow.EmptyState.cs
        InitializeLayout(); // MainWindow.Layout.cs
        RemoteToolsDialog.LibraryChanged += OnLibraryChangedOutside;
        HookSync();
        toastTimer.Tick += (_, _) =>
        {
            toastTimer.Stop();
            Toast.IsOpen = false;
        };
        Nav.SelectionChanged += (_, _) =>
        {
            if (!buildingNavigation && Nav.SelectedItem is FANavigationViewItem { Tag: string tag })
            {
                filter = tag;
                ApplyFilter();
            }
        };
        HookNavigationDrop();
        Nav.KeyDown += OnNavigationKeyDown;

        // Results arrive on a thread-pool thread; the items belong to the UI thread.
        app.Status.StatusChanged += OnStatusChanged;
        statusTimer.Tick += (_, _) => RefreshStatus();
        statusTimer.Start();

        LoadViewState();
        BuildNavigation();
        UpdateColumnHeaders();
        RefreshStatus();
        Opened += (_, _) => StartHotkey();
    }

    public IReadOnlyList<ConnectionItem> Connections => items;
    public IReadOnlyList<ConnectionItem> VisibleConnections => visible;
    public IReadOnlyList<object> TileEntries => tileEntries;
    public IReadOnlyList<object> RowEntries => rowEntries;
    public IReadOnlyList<Window> SessionWindows => sessionWindows;
    public bool IsListView => ListPanel.IsVisible;
    public FAInfoBar ToastBar => Toast;
    public ListBox Tiles => TileList;
    public ListBox Rows => RowList;
    public ConnectionDetailPanel DetailPanel => Details;

    public Button More => MoreButton;
    public Task? StatusRefresh { get; private set; }
    public OverviewColumn? SortColumn => sortColumn;
    public bool SortDescending => sortDescending;
    public string Filter => filter;
    public AppHost App => app;

    Window IConnectionHost.Owner => this;
    AppHost IConnectionHost.App => app;

    private ListBox ActiveList => IsListView ? RowList : TileList;
    private ConnectionItem? Selected => ActiveList.SelectedItem as ConnectionItem;

    /// <summary>The selected connections of the active view, in display order.</summary>
    public IReadOnlyList<ConnectionItem> SelectedConnections
    {
        get
        {
            HashSet<ConnectionItem> selected = [.. (ActiveList.SelectedItems?.OfType<ConnectionItem>() ?? [])];
            return [.. (IsListView ? rowEntries : tileEntries).OfType<ConnectionItem>().Where(selected.Contains)];
        }
    }

    /// <summary>The group the sidebar shows, or null for every other filter.</summary>
    private string? CurrentGroupId => filter.StartsWith("group:", StringComparison.Ordinal) ? filter["group:".Length..] : null;

    /// <summary>
    /// Opens the connection through its handler. An embedded session gets its own window (an open one is
    /// brought to the front instead); external tools return null.
    /// </summary>
    public Window? OpenSession(ConnectionItem connection)
    {
        if (sessionWindows.FirstOrDefault(w => ((ISessionWindow)w).Connection == connection) is { } open)
        {
            if (open.WindowState == WindowState.Minimized)
                open.WindowState = WindowState.Normal;
            open.Activate();
            return open;
        }

        Window? window = connection.Handler.Open(connection, this);
        if (window is null)
            return null;
        if (window is not ISessionWindow)
            throw new InvalidOperationException($"{window.GetType().Name} must implement {nameof(ISessionWindow)}.");

        window.Closed += (_, _) =>
        {
            sessionWindows.Remove(window);
            EndHistory(connection);
        };
        sessionWindows.Add(window);
        TrackLivePreview(window, connection);
        return window;
    }

    /// <summary>Opens every given connection (group context menu, bulk action).</summary>
    internal void OpenAll(IReadOnlyList<ConnectionItem> connections)
    {
        foreach (ConnectionItem c in connections.ToList())
            OpenSession(c);
    }

    /// <summary>
    /// Copies the stored password (own or inherited) to the clipboard; it is cleared after
    /// <paramref name="clearAfter"/> (default 30 s) if the clipboard still holds it.
    /// </summary>
    public async Task CopyPasswordAsync(ConnectionItem connection, TimeSpan? clearAfter = null)
    {
        string? password;
        try
        {
            password = await app.Credentials.ReadAsync(connection.Credentials.Secret);
        }
        catch (Exception ex) when (Credentials.CredentialErrors.IsCredentialFailure(ex))
        {
            // Credential Manager error, or a password manager that is locked, canceled, or missing the entry.
            ShowToast(connection.Name, Credentials.CredentialErrors.Describe(ex), FAInfoBarSeverity.Error);
            return;
        }

        if (password is null || Clipboard is not { } clipboard)
        {
            connection.RefreshCredentialState();
            ShowToast(connection.Name, Strings.Password_NoneStored, FAInfoBarSeverity.Warning);
            return;
        }

        TimeSpan delay = clearAfter ?? SecretClipboard.DefaultClearDelay;
        await SecretClipboard.CopyAsync(clipboard, password, delay);
        ShowToast(connection.Name, string.Format(CultureInfo.CurrentCulture, Strings.Password_Copied, delay.TotalSeconds), FAInfoBarSeverity.Success);
    }

    public void ShowToast(string title, string message, FAInfoBarSeverity severity)
    {
        Toast.Title = title;
        Toast.Message = message;
        Toast.Severity = severity;
        Toast.IsOpen = true;
        toastTimer.Stop();
        toastTimer.Start();
    }

    /// <summary>Records the logon (last connected, a history entry that ends when the session window closes) and saves.</summary>
    public void MarkConnected(ConnectionItem connection)
    {
        connection.Model.LastConnectedAt = DateTime.UtcNow;
        connection.Changed();
        Save(connection);
        if (app.Library.Contains(connection.Model))
        {
            EndHistory(connection);
            openHistory[connection] = app.History.Start(connection.Id, HistoryResult.Connected);
        }

        if (Details.Shown == connection)
            Details.Refresh();
    }

    /// <summary>Saves the connection if it is in the library (self-test connections are not).</summary>
    public void Save(ConnectionItem connection) => app.Library.Update(connection.Model);

    /// <summary>Self-test only: lists a connection in the overview without saving it to the library.</summary>
    internal void ShowUnsaved(ConnectionItem connection, bool show)
    {
        if (show)
            items.Add(connection);
        else
            items.Remove(connection);
        ApplyFilter();
    }

    /// <summary>Adds a connection to the library and the overview (self-test; the editor path is <see cref="AddConnection"/>).</summary>
    internal ConnectionItem Adopt(Connection connection)
    {
        app.Library.Add(connection);
        ConnectionItem item = CreateItem(connection);
        items.Add(item);
        RefreshLabels();
        BuildNavigation();
        return item;
    }

    internal ConnectionItem CreateItem(Connection connection)
    {
        var item = new ConnectionItem(connection, app);
        item.ReloadPreview();
        return item;
    }

    public void SetListView(bool list)
    {
        IReadOnlyList<ConnectionItem> selected = SelectedConnections;
        ListPanel.IsVisible = list;
        TileList.IsVisible = !list;
        TileToggle.IsChecked = !list;
        ListToggle.IsChecked = list;
        Select(selected);
        app.Library.SetSetting(ViewSetting, list ? "list" : "tiles");
        UpdateEmptyState();
    }

    /// <summary>
    /// Shows or hides the detail column. On a wide window the choice is saved; on a narrow one (see
    /// MainWindow.Layout.cs) it only lasts until the window gets wide again.
    /// </summary>
    public void SetDetailsVisible(bool show)
    {
        if (IsWideLayout)
        {
            detailsWanted = show;
            app.Library.SetSetting(DetailsSetting, show ? "1" : "0");
        }
        else
        {
            detailsOnNarrow = show;
        }

        ApplyDetailsVisibility();
    }

    /// <summary>Sorts the list view by a column (null: group sections in their saved order). The tiles always keep the group order.</summary>
    public void SetSort(OverviewColumn? column, bool descending = false)
    {
        sortColumn = column;
        sortDescending = column is not null && descending;
        app.Library.SetSetting(SortSetting, column is { } c ? $"{c}:{(sortDescending ? "desc" : "asc")}" : null);
        UpdateColumnHeaders();
        ApplyFilter();
    }

    /// <summary>Selects the connections in the active view (and shows them in the detail panel).</summary>
    public void Select(IEnumerable<ConnectionItem> connections)
    {
        ListBox list = ActiveList;
        updatingSelection = true;
        list.SelectedItems?.Clear();
        foreach (ConnectionItem c in connections)
        {
            if ((IsListView ? rowEntries : tileEntries).Contains(c))
                list.SelectedItems?.Add(c);
        }

        updatingSelection = false;
        OnSelectionChanged(list);
    }

    private void CycleSort(OverviewColumn column)
    {
        if (sortColumn != column)
            SetSort(column);
        else if (!sortDescending)
            SetSort(column, descending: true);
        else
            SetSort(null);
    }

    private void UpdateColumnHeaders()
    {
        foreach (Button header in ColumnHeaders.Children.OfType<Button>())
        {
            var column = Enum.Parse<OverviewColumn>((string)header.Tag!);
            string label = column switch
            {
                OverviewColumn.Status => "●",
                OverviewColumn.Name => Strings.Column_Name,
                OverviewColumn.Type => Strings.Column_Type,
                OverviewColumn.Host => Strings.Column_Host,
                OverviewColumn.Group => Strings.Column_Group,
                OverviewColumn.Tags => Strings.Column_Tags,
                OverviewColumn.Description => Strings.Column_Description,
                _ => Strings.Column_LastConnected,
            };
            bool sorted = sortColumn == column;
            header.Content = sorted ? $"{label} {(sortDescending ? "▼" : "▲")}" : label;
            header.Classes.Set("sorted", sorted);
            ToolTip.SetTip(header, string.Format(CultureInfo.CurrentCulture, Strings.Column_SortTip, column == OverviewColumn.Status ? Strings.Column_Status : label));
        }
    }

    private void LoadViewState()
    {
        if (app.Library.GetSetting(SortSetting) is { } sort && sort.Split(':') is [var name, var direction]
            && Enum.TryParse(name, out OverviewColumn column))
        {
            sortColumn = column;
            sortDescending = direction == "desc";
        }

        bool list = app.Library.GetSetting(ViewSetting) == "list";
        ListPanel.IsVisible = list;
        TileList.IsVisible = !list;
        TileToggle.IsChecked = !list;
        ListToggle.IsChecked = list;
        detailsWanted = app.Library.GetSetting(DetailsSetting) != "0";
        ApplyDetailsVisibility();
    }

    /// <summary>After an import: items for new connections, fresh labels for changed ones, sidebar and status.</summary>
    public void SyncWithLibrary()
    {
        foreach (Connection connection in app.Library.Connections)
        {
            if (!items.Exists(i => i.Model == connection))
                items.Add(CreateItem(connection));
        }

        foreach (ConnectionItem item in items)
        {
            item.RefreshCredentialState();
            item.Changed();
        }

        BuildNavigation();
        RefreshStatus();
    }

    private void BuildNavigation()
    {
        static FANavigationViewItem Item(string text, FASymbol symbol, string tag) =>
            new() { Content = text, Tag = tag, IconSource = new FASymbolIconSource { Symbol = symbol } };

        var items = new List<object>
        {
            Item(Strings.Nav_All, FASymbol.Library, "all"),
            Item(Strings.Nav_Favorites, FASymbol.Star, "fav"),
            Item(Strings.Nav_Recent, FASymbol.Clock, "recent"),
        };

        List<FANavigationViewItem> groups = [.. app.Library.ChildrenOf(null).Select(GroupItem)];
        if (groups.Count > 0)
        {
            items.Add(new FANavigationViewItemHeader { Content = Strings.Nav_Groups });
            items.AddRange(groups);
        }

        string[] tags = [.. this.items.SelectMany(c => c.Model.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase)];

        // Replacing the items restarts FANavigationView's selection animation; rebuilding it several times
        // in a row keeps the UI thread busy. Only rebuild when groups or tags changed.
        string signature = string.Join("|", app.Library.DescendantsOf(null).Select(g => $"{g.Id}:{g.Name}:{g.ParentId}:{g.IsCollapsed}"))
            + "#" + string.Join("|", tags);
        if (signature == navigationSignature && Nav.MenuItemsSource?.OfType<FANavigationViewItem>() is { } current)
        {
            if ((Nav.SelectedItem as FANavigationViewItem)?.Tag as string != filter)
                SelectNavigationItem(Flatten(current).FirstOrDefault(i => (string?)i.Tag == filter));

            ApplyFilter();
            return;
        }

        navigationSignature = signature;
        if (tags.Length > 0)
        {
            items.Add(new FANavigationViewItemHeader { Content = Strings.Nav_Tags });
            items.AddRange(tags.Select(t => Item(t, FASymbol.Tag, $"tag:{t}")));
        }

        // Clear the selection before replacing the items and select again after layout: FANavigationView
        // re-posts its selection animation until the selected item's indicator exists, which never
        // happens for an item that is not laid out yet and starves the UI thread.
        FANavigationViewItem selected = Flatten(items.OfType<FANavigationViewItem>()).FirstOrDefault(i => (string?)i.Tag == filter)
            ?? (FANavigationViewItem)items[0];
        buildingNavigation = true;
        Nav.SelectedItem = null;
        Nav.MenuItemsSource = items;
        buildingNavigation = false;
        filter = (string)selected.Tag!;
        Dispatcher.UIThread.Post(() =>
        {
            if (Nav.MenuItemsSource == items)
                SelectNavigationItem(selected);
        }, DispatcherPriority.Loaded);
        ApplyFilter();

        FANavigationViewItem GroupItem(ConnectionGroup group)
        {
            var item = Item(group.Name, FASymbol.Folder, $"group:{group.Id}");
            item.ContextMenu = CreateGroupMenu(group);
            FANavigationViewItem[] children = [.. app.Library.ChildrenOf(group.Id).Select(GroupItem)];
            if (children.Length > 0)
            {
                item.MenuItemsSource = children;
                item.IsExpanded = !group.IsCollapsed;
                // Collapsed groups stay collapsed after a restart; the overview sections follow.
                item.PropertyChanged += (_, e) =>
                {
                    if (e.Property == FANavigationViewItem.IsExpandedProperty && !buildingNavigation && group.IsCollapsed == item.IsExpanded)
                    {
                        group.IsCollapsed = !item.IsExpanded;
                        app.Library.UpdateGroup(group);
                        ApplyFilter();
                    }
                };
            }

            return item;
        }
    }

    /// <summary>
    /// Marks the sidebar entry of the current filter. An entry inside a collapsed group is not shown, so
    /// it is not marked either (see the selection animation note in <see cref="BuildNavigation"/>).
    /// </summary>
    private void SelectNavigationItem(FANavigationViewItem? item)
    {
        bool hidden = item?.Tag is string tag && tag.StartsWith("group:", StringComparison.Ordinal)
            && app.Library.AncestorsOf(tag["group:".Length..]).Skip(1).Any(g => g.IsCollapsed);
        buildingNavigation = true;
        Nav.SelectedItem = hidden ? null : item;
        buildingNavigation = false;
    }

    private static IEnumerable<FANavigationViewItem> Flatten(IEnumerable<FANavigationViewItem> items) =>
        items.SelectMany(i => (i.MenuItemsSource?.OfType<FANavigationViewItem>() is { } children ? Flatten(children) : []).Prepend(i));

    private void ApplyFilter()
    {
        IEnumerable<ConnectionItem> query = filter switch
        {
            "fav" => items.Where(c => c.IsFavorite),
            "recent" => items.Where(c => c.Model.LastConnectedAt is not null).OrderByDescending(c => c.Model.LastConnectedAt),
            _ when CurrentGroupId is { } id => items.Where(c => app.Library.IsInGroup(c.Model, id)),
            _ when filter.StartsWith("tag:", StringComparison.Ordinal) => items.Where(c => c.Model.Tags.Contains(filter["tag:".Length..], StringComparer.OrdinalIgnoreCase)),
            _ => items,
        };
        if (filter != "recent")
        {
            // Same order as the sidebar: groups depth-first by their sort order, ungrouped last.
            Dictionary<string, int> groupOrder = GroupOrder();
            query = query.OrderBy(c => c.Model.GroupId is { } id && groupOrder.TryGetValue(id, out int index) ? index : int.MaxValue)
                .ThenBy(c => c.Model.SortOrder).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase);
        }

        string search = SearchBox.Text?.Trim() ?? "";
        if (search.Length > 0)
            query = query.Where(c => c.Matches(search));

        IReadOnlyList<ConnectionItem> selected = SelectedConnections;
        visible.Clear();
        visible.AddRange(query);

        // "All" and a group show collapsible group sections; favorites, recent, and tags a flat list.
        bool sections = filter == "all" || CurrentGroupId is not null;
        tileEntries = sections ? BuildSections(search.Length > 0) : [.. visible];
        rowEntries = sortColumn is { } column ? [.. Sorted(visible, column, sortDescending)] : sections ? [.. tileEntries] : [.. visible];
        updatingSelection = true;
        TileList.ItemsSource = tileEntries;
        RowList.ItemsSource = rowEntries;
        updatingSelection = false;
        Select(selected);

        PageTitle.Text = filter switch
        {
            "fav" => Strings.Nav_Favorites,
            "recent" => Strings.Nav_Recent,
            _ when CurrentGroupId is { } id => app.Library.PathOf(id),
            _ when filter.StartsWith("tag:", StringComparison.Ordinal) => $"#{filter["tag:".Length..]}",
            _ => Strings.Nav_All,
        };
        UpdateSubtitle();
        UpdateEmptyState();
    }

    /// <summary>
    /// Header rows and connections for the visible connections: each group (depth-first, with its
    /// count and, collapsed, an online summary) followed by its own connections, then "Ohne Gruppe".
    /// Empty groups stay visible as drop targets unless a search is active.
    /// </summary>
    private List<object> BuildSections(bool searching)
    {
        var entries = new List<object>();
        ILookup<string?, ConnectionItem> byGroup = visible.ToLookup(c => c.Model.GroupId);

        void Visit(ConnectionGroup group, int depth)
        {
            List<ConnectionItem> inside = [.. visible.Where(c => app.Library.IsInGroup(c.Model, group.Id))];
            if (searching && inside.Count == 0)
                return;
            var header = new GroupHeaderItem(group, depth);
            header.SetCounts(inside.Count, inside.Count(c => c.Status == OnlineStatus.Online), inside.Count(c => c.Status == OnlineStatus.Offline));
            entries.Add(header);
            if (group.IsCollapsed)
                return;
            entries.AddRange(byGroup[group.Id]);
            foreach (ConnectionGroup child in app.Library.ChildrenOf(group.Id))
                Visit(child, depth + 1);
        }

        if (CurrentGroupId is { } rootId && app.Library.FindGroup(rootId) is { } root)
        {
            Visit(root, 0);
            return entries;
        }

        foreach (ConnectionGroup group in app.Library.ChildrenOf(null))
            Visit(group, 0);

        List<ConnectionItem> ungrouped = [.. visible.Where(c => app.Library.FindGroup(c.Model.GroupId) is null)];
        if (app.Library.Groups.Count > 0 && (ungrouped.Count > 0 || !searching))
        {
            var header = new GroupHeaderItem(null, 0);
            header.SetCounts(ungrouped.Count, ungrouped.Count(c => c.Status == OnlineStatus.Online), ungrouped.Count(c => c.Status == OnlineStatus.Offline));
            entries.Add(header);
        }

        entries.AddRange(ungrouped);
        return entries;
    }

    private static IEnumerable<ConnectionItem> Sorted(IEnumerable<ConnectionItem> source, OverviewColumn column, bool descending)
    {
        StringComparer text = StringComparer.CurrentCultureIgnoreCase;
        IOrderedEnumerable<ConnectionItem> sorted = column switch
        {
            // Ascending: online first, then offline, then unknown.
            OverviewColumn.Status => Order(source, c => c.Status switch { OnlineStatus.Online => 0, OnlineStatus.Offline => 1, _ => 2 }, descending),
            OverviewColumn.Type => Order(source, c => c.TypeLabel, descending, text),
            OverviewColumn.Host => Order(source, c => c.Model.Host, descending, text),
            OverviewColumn.Group => Order(source, c => c.GroupPath, descending, text),
            OverviewColumn.Tags => Order(source, c => string.Join(",", c.Model.Tags), descending, text),
            OverviewColumn.Description => Order(source, c => c.Description, descending, text),
            OverviewColumn.LastConnected => Order(source, c => c.Model.LastConnectedAt ?? DateTime.MinValue, descending),
            _ => Order(source, c => c.Name, descending, text),
        };
        return sorted.ThenBy(c => c.Name, text);
    }

    private static IOrderedEnumerable<ConnectionItem> Order<TKey>(IEnumerable<ConnectionItem> source, Func<ConnectionItem, TKey> key, bool descending, IComparer<TKey>? comparer = null) =>
        descending ? source.OrderByDescending(key, comparer) : source.OrderBy(key, comparer);

    private Dictionary<string, int> GroupOrder()
    {
        var order = new Dictionary<string, int>();
        foreach (ConnectionGroup group in app.Library.DescendantsOf(null))
            order.TryAdd(group.Id, order.Count);
        return order;
    }

    private void UpdateSubtitle()
    {
        int online = visible.Count(c => c.Status == OnlineStatus.Online);
        PageSubtitle.Text = string.Format(CultureInfo.CurrentCulture,
            visible.Count == 1 ? Strings.Subtitle_One : Strings.Subtitle_Many, visible.Count, online);
    }


    private void OnSelectionChanged(ListBox list)
    {
        if (updatingSelection || list != ActiveList)
            return;

        // Group headers are rows for collapsing and dragging, never part of the selection.
        if (list.SelectedItems?.OfType<GroupHeaderItem>().ToList() is { Count: > 0 } headers)
        {
            updatingSelection = true;
            foreach (GroupHeaderItem header in headers)
                list.SelectedItems.Remove(header);
            updatingSelection = false;
        }

        IReadOnlyList<ConnectionItem> selected = SelectedConnections;
        ConnectButton.IsEnabled = selected.Count > 0;
        Details.Show(selected);
    }

    private void PrepareContainer(Control container)
    {
        bool header = container is ContentControl { Content: GroupHeaderItem };
        container.Classes.Set("header", header);
        container.ContextMenu = container is ContentControl { Content: GroupHeaderItem h } ? CreateGroupMenu(h.Group) : null;
    }

    /// <summary>The overview entry (connection or group header) under an event source.</summary>
    private static object? EntryAt(object? source) => ContainerAt(source)?.Content;

    private static ListBoxItem? ContainerAt(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is ListBoxItem item)
                return item;
        }

        return null;
    }

    /// <summary>A sync or import added or changed connections in the library: add items for new ones, refresh the rest.</summary>
    private void OnLibraryChangedOutside(object? sender, EventArgs e) => SyncWithLibrary();

    private void OnStatusChanged(object? sender, StatusChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            foreach (ConnectionItem item in items.Where(i => i.Model == e.Connection))
                item.Status = e.Status;
            UpdateSubtitle();
            UpdateHeaderCounts();
        });

    /// <summary>Online summaries of the group headers after status results.</summary>
    private void UpdateHeaderCounts()
    {
        foreach (GroupHeaderItem header in tileEntries.Concat(rowEntries).OfType<GroupHeaderItem>().Distinct())
        {
            List<ConnectionItem> inside = header.Group is { } g
                ? [.. visible.Where(c => app.Library.IsInGroup(c.Model, g.Id))]
                : [.. visible.Where(c => app.Library.FindGroup(c.Model.GroupId) is null)];
            header.SetCounts(inside.Count, inside.Count(c => c.Status == OnlineStatus.Online), inside.Count(c => c.Status == OnlineStatus.Offline));
        }
    }

    /// <summary>Checks every connection in the background; the UI stays responsive meanwhile.</summary>
    private async void RefreshStatus()
    {
        if (StatusRefresh is { IsCompleted: false })
            return;
        StatusRefresh = app.Status.CheckAsync(items.Select(i => i.Model));
        try
        {
            await StatusRefresh;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OpenSelected()
    {
        if (Selected is { } c)
            OpenSession(c);
    }

    private async void AddConnection(string? groupId = null)
    {
        var connection = new Connection
        {
            GroupId = groupId ?? CurrentGroupId,
            IsFavorite = filter == "fav",
        };
        if (!await ConnectionEditor.ShowAsync(connection, isNew: true))
            return;

        connection.SortOrder = app.Library.ConnectionsIn(connection.GroupId).Count();
        app.Library.Add(connection);
        ConnectionItem item = CreateItem(connection);
        items.Add(item);
        Refresh(item);
    }

    internal async void EditConnection(ConnectionItem connection)
    {
        if (!await ConnectionEditor.ShowAsync(connection.Model, isNew: false))
            return;

        Save(connection);
        connection.RefreshCredentialState();
        connection.Changed();
        Refresh(connection);
    }

    internal void SetFavorite(IReadOnlyList<ConnectionItem> connections, bool favorite)
    {
        foreach (ConnectionItem c in connections)
        {
            c.IsFavorite = favorite;
            Save(c);
        }

        ApplyFilter();
        Details.Refresh();
    }

    internal async void DeleteConnections(IReadOnlyList<ConnectionItem> connections)
    {
        if (connections.Count == 0)
            return;
        var dialog = new FAContentDialog
        {
            Title = connections.Count == 1
                ? string.Format(CultureInfo.CurrentCulture, Strings.Delete_Title, connections[0].Name)
                : string.Format(CultureInfo.CurrentCulture, Strings.Delete_TitleMany, connections.Count),
            Content = Strings.Delete_Message,
            PrimaryButtonText = Strings.Action_Delete,
            CloseButtonText = Strings.Action_Cancel,
        };
        if (await dialog.ShowAsync() != FAContentDialogResult.Primary)
            return;
        foreach (ConnectionItem c in connections.ToList())
            await RemoveConnectionAsync(c);
    }

    /// <summary>Removes the connection with its window, preview image, and own stored password.</summary>
    internal async Task RemoveConnectionAsync(ConnectionItem connection)
    {
        await DiscardConnectionAsync(connection);
        if (app.Library.Contains(connection.Model))
            app.Library.Remove(connection.Model);
        BuildNavigation();
    }

    /// <summary>Closes the connection's window and drops its item, preview image, and own stored password (not the library entry).</summary>
    private async Task DiscardConnectionAsync(ConnectionItem connection)
    {
        sessionWindows.FirstOrDefault(w => ((ISessionWindow)w).Connection == connection)?.Close();
        openHistory.Remove(connection);
        items.Remove(connection);
        string preview = connection.Model.ResolvedPreviewPath;
        if (File.Exists(preview))
            File.Delete(preview);
        try
        {
            await app.Credentials.RemoveOwnSecretAsync(connection.Model);
        }
        catch (Win32Exception ex)
        {
            ShowToast(connection.Name, string.Format(CultureInfo.CurrentCulture, Strings.Password_RemoveFailed, ex.Message), FAInfoBarSeverity.Error);
        }
    }

    /// <summary>After an add or edit: rebuild the sidebar (groups, tags), select the connection, check the status.</summary>
    private void Refresh(ConnectionItem connection)
    {
        RefreshLabels();
        BuildNavigation();
        Select([connection]);
        _ = app.Status.CheckAsync([connection.Model]);
    }

    /// <summary>Group paths of every item may have changed (new, renamed, or moved group).</summary>
    private void RefreshLabels()
    {
        foreach (ConnectionItem item in items)
            item.Changed();
    }

    private void EndHistory(ConnectionItem connection)
    {
        if (openHistory.Remove(connection, out long entry))
            app.History.End(entry);
        if (Details.Shown == connection)
            Details.Refresh();
    }

    private ContextMenu CreateContextMenu()
    {
        // Open, then edit and organize, then delete (docs/ui.md). Gestures are handled in the list's KeyDown.
        var connect = Item(Strings.Action_Connect, FASymbol.Play, new KeyGesture(Key.Enter));
        var fileTransfer = Item(Strings.RemoteTool_Action_FileTransfer, FASymbol.OpenFile);
        var copyPassword = Item(Strings.Action_CopyPassword, FASymbol.Permissions);
        var edit = Item(Strings.Action_Edit, FASymbol.Edit, new KeyGesture(Key.F2));
        var duplicate = Item(Strings.Action_Duplicate, FASymbol.Copy);
        var move = Item(Strings.Action_MoveToGroup, FASymbol.MoveToFolder);
        var favorite = Item("", FASymbol.Star);
        var delete = Item(Strings.Action_DeleteEllipsis, FASymbol.Delete, new KeyGesture(Key.Delete));
        var menu = new ContextMenu
        {
            ItemsSource = new object[] { connect, fileTransfer, copyPassword, new Separator(), edit, duplicate, move, favorite, new Separator(), delete },
        };

        menu.Opening += (_, e) =>
        {
            IReadOnlyList<ConnectionItem> selected = SelectedConnections;
            e.Cancel = selected.Count == 0;
            bool single = selected.Count == 1;
            connect.Header = single ? Strings.Action_Connect : string.Format(CultureInfo.CurrentCulture, Strings.Action_ConnectCount, selected.Count);
            favorite.Header = selected.Count > 0 && selected.All(c => c.IsFavorite) ? Strings.Action_Unfavorite : Strings.Action_Favorite;
            copyPassword.IsVisible = single && selected[0].HasStoredPassword;
            edit.IsVisible = single;
            duplicate.IsVisible = single;
            fileTransfer.IsVisible = single && selected[0].Handler is IFileTransferHandler;
        };
        fileTransfer.Click += (_, _) =>
        {
            if (Selected is { Handler: IFileTransferHandler handler } c)
                handler.OpenFileTransfer(c, this);
        };
        copyPassword.Click += async (_, _) =>
        {
            if (Selected is { } c)
                await CopyPasswordAsync(c);
        };
        connect.Click += (_, _) => OpenAll(SelectedConnections);
        edit.Click += (_, _) =>
        {
            if (Selected is { } c)
                EditConnection(c);
        };
        delete.Click += (_, _) => DeleteConnections(SelectedConnections);
        move.Click += (_, _) => MoveToGroupAsync(SelectedConnections);
        favorite.Click += (_, _) =>
        {
            IReadOnlyList<ConnectionItem> selected = SelectedConnections;
            SetFavorite(selected, !selected.All(c => c.IsFavorite));
        };
        duplicate.Click += (_, _) =>
        {
            if (Selected is not { } c)
                return;
            // The copy gets a new id and therefore no stored password of its own.
            Connection copy = c.Model.Duplicate(string.Format(CultureInfo.CurrentCulture, Strings.Duplicate_Name, c.Name));
            copy.SortOrder = app.Library.ConnectionsIn(copy.GroupId).Count();
            app.Library.Add(copy);
            ConnectionItem item = CreateItem(copy);
            items.Add(item);
            Refresh(item);
        };
        return menu;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Control)
        {
            switch (e.Key)
            {
                case Key.D1: SetListView(false); e.Handled = true; break;
                case Key.D2: SetListView(true); e.Handled = true; break;
                case Key.F: SearchBox.Focus(); e.Handled = true; break;
                case Key.N: AddConnection(); e.Handled = true; break;
                case Key.K: OpenLauncher(); e.Handled = true; break;
                case Key.I: SetDetailsVisible(!IsDetailsVisible); e.Handled = true; break;
            }
        }
        else if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.N)
        {
            _ = CreateGroupAsync(CurrentGroupId);
            e.Handled = true;
        }
        else if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.F5)
        {
            RefreshStatus();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // A copied password must not outlive the app on the clipboard: clear it first, then close for real.
        if (!clipboardCleared && SecretClipboard.HasPending)
        {
            e.Cancel = true;
            clipboardCleared = true;
            Dispatcher.UIThread.Post(async () =>
            {
                await SecretClipboard.ClearPendingAsync();
                Close();
            });
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        statusTimer.Stop();
        app.Status.StatusChanged -= OnStatusChanged;
        StopLauncher();
        RemoteToolsDialog.LibraryChanged -= OnLibraryChangedOutside;
        UnhookSync();
        // Session windows are independent top-levels; closing the overview ends the app, so close them too.
        foreach (Window window in sessionWindows.ToList())
            window.Close();
        base.OnClosed(e);
    }
}
