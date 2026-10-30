using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Connections;
using RemoteManager.App.Connections.RemoteTools;
using RemoteManager.App.Resources;
using RemoteManager.App.Services;
using RemoteManager.Core.Model;
using RemoteManager.Core.Status;

namespace RemoteManager.App.Views;

/// <summary>
/// The connection overview: sidebar (all, favorites, recent, nested groups, tags), tile and list view,
/// search. Opening a connection goes through its type's <see cref="IConnectionHandler"/>; embedded
/// sessions get their own window, which this window tracks.
/// </summary>
public partial class MainWindow : Window, IConnectionHost
{
    private static readonly TimeSpan StatusInterval = TimeSpan.FromMinutes(2);

    private readonly AppHost app;
    private readonly List<ConnectionItem> items = [];
    private readonly ObservableCollection<ConnectionItem> visible = [];
    private readonly List<Window> sessionWindows = [];
    private readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer statusTimer = new() { Interval = StatusInterval };
    private string filter = "all";
    private bool clipboardCleared;
    private bool buildingNavigation;

    public MainWindow()
    {
        InitializeComponent();
        app = AppHost.Current;
        foreach (Connection connection in app.Library.Connections)
            items.Add(CreateItem(connection));

        TileList.ItemsSource = visible;
        RowList.ItemsSource = visible;
        TileList.ContextMenu = CreateContextMenu();
        RowList.ContextMenu = CreateContextMenu();
        foreach (ListBox list in new[] { TileList, RowList })
        {
            list.DoubleTapped += (_, _) => OpenSelected();
            list.SelectionChanged += (_, _) => ConnectButton.IsEnabled = Selected is not null;
            list.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                    OpenSelected();
            };
        }

        TileToggle.Click += (_, _) => SetListView(false);
        ListToggle.Click += (_, _) => SetListView(true);
        SearchBox.TextChanged += (_, _) => ApplyFilter();
        RefreshButton.Click += (_, _) => RefreshStatus();
        ConnectButton.Click += (_, _) => OpenSelected();
        AddButton.Click += (_, _) => AddConnection();
        RemoteToolsButton.Click += async (_, _) => await RemoteToolsDialog.ShowAsync();
        RemoteToolsDialog.LibraryChanged += OnLibraryChangedOutside;
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

        // Results arrive on a thread-pool thread; the items belong to the UI thread.
        app.Status.StatusChanged += OnStatusChanged;
        statusTimer.Tick += (_, _) => RefreshStatus();
        statusTimer.Start();

        BuildNavigation();
        RefreshStatus();
    }

    public IReadOnlyList<ConnectionItem> Connections => items;
    public IReadOnlyList<ConnectionItem> VisibleConnections => visible;
    public IReadOnlyList<Window> SessionWindows => sessionWindows;
    public bool IsListView => ListPanel.IsVisible;
    public FAInfoBar ToastBar => Toast;
    public ListBox Tiles => TileList;
    public Task? StatusRefresh { get; private set; }

    Window IConnectionHost.Owner => this;
    AppHost IConnectionHost.App => app;

    private ListBox ActiveList => IsListView ? RowList : TileList;
    private ConnectionItem? Selected => ActiveList.SelectedItem as ConnectionItem;

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

        window.Closed += (_, _) => sessionWindows.Remove(window);
        sessionWindows.Add(window);
        return window;
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
        catch (Win32Exception ex)
        {
            ShowToast(connection.Name, string.Format(CultureInfo.CurrentCulture, Strings.Password_ReadFailed, ex.Message), FAInfoBarSeverity.Error);
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

    public void MarkConnected(ConnectionItem connection)
    {
        connection.Model.LastConnectedAt = DateTime.UtcNow;
        connection.Changed();
        Save(connection);
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

    internal ConnectionItem CreateItem(Connection connection)
    {
        var item = new ConnectionItem(connection, app);
        item.ReloadPreview();
        return item;
    }

    public void SetListView(bool list)
    {
        ConnectionItem? selected = Selected;
        ListPanel.IsVisible = list;
        TileList.IsVisible = !list;
        TileToggle.IsChecked = !list;
        ListToggle.IsChecked = list;
        ActiveList.SelectedItem = selected;
        UpdateEmptyState();
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
        if (tags.Length > 0)
        {
            items.Add(new FANavigationViewItemHeader { Content = Strings.Nav_Tags });
            items.AddRange(tags.Select(t => Item(t, FASymbol.Tag, $"tag:{t}")));
        }

        buildingNavigation = true;
        Nav.MenuItemsSource = items;
        Nav.SelectedItem = Flatten(items.OfType<FANavigationViewItem>()).FirstOrDefault(i => (string?)i.Tag == filter) ?? items[0];
        buildingNavigation = false;
        if (Nav.SelectedItem is FANavigationViewItem { Tag: string selectedTag })
            filter = selectedTag;
        ApplyFilter();

        FANavigationViewItem GroupItem(ConnectionGroup group)
        {
            var item = Item(group.Name, FASymbol.Folder, $"group:{group.Id}");
            FANavigationViewItem[] children = [.. app.Library.ChildrenOf(group.Id).Select(GroupItem)];
            if (children.Length > 0)
            {
                item.MenuItemsSource = children;
                item.IsExpanded = !group.IsCollapsed;
                // Collapsed groups stay collapsed after a restart.
                item.PropertyChanged += (_, e) =>
                {
                    if (e.Property == FANavigationViewItem.IsExpandedProperty && !buildingNavigation && group.IsCollapsed == item.IsExpanded)
                    {
                        group.IsCollapsed = !item.IsExpanded;
                        app.Library.UpdateGroup(group);
                    }
                };
            }

            return item;
        }
    }

    private static IEnumerable<FANavigationViewItem> Flatten(IEnumerable<FANavigationViewItem> items) =>
        items.SelectMany(i => (i.MenuItemsSource?.OfType<FANavigationViewItem>() is { } children ? Flatten(children) : []).Prepend(i));

    private void ApplyFilter()
    {
        IEnumerable<ConnectionItem> query = filter switch
        {
            "fav" => items.Where(c => c.IsFavorite),
            "recent" => items.Where(c => c.Model.LastConnectedAt is not null).OrderByDescending(c => c.Model.LastConnectedAt),
            _ when filter.StartsWith("group:", StringComparison.Ordinal) => items.Where(c => app.Library.IsInGroup(c.Model, filter["group:".Length..])),
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

        ConnectionItem? selected = Selected;
        visible.Clear();
        foreach (ConnectionItem c in query)
            visible.Add(c);
        if (selected is not null && visible.Contains(selected))
            ActiveList.SelectedItem = selected;

        PageTitle.Text = filter switch
        {
            "fav" => Strings.Nav_Favorites,
            "recent" => Strings.Nav_Recent,
            _ when filter.StartsWith("group:", StringComparison.Ordinal) => app.Library.PathOf(filter["group:".Length..]),
            _ when filter.StartsWith("tag:", StringComparison.Ordinal) => $"#{filter["tag:".Length..]}",
            _ => Strings.Nav_All,
        };
        UpdateSubtitle();
        UpdateEmptyState();
    }

    private Dictionary<string, int> GroupOrder()
    {
        var order = new Dictionary<string, int>();
        void Visit(string? parentId)
        {
            foreach (ConnectionGroup group in app.Library.ChildrenOf(parentId))
            {
                if (order.TryAdd(group.Id, order.Count))
                    Visit(group.Id);
            }
        }

        Visit(null);
        return order;
    }

    private void UpdateSubtitle()
    {
        int online = visible.Count(c => c.Status == OnlineStatus.Online);
        PageSubtitle.Text = string.Format(CultureInfo.CurrentCulture,
            visible.Count == 1 ? Strings.Subtitle_One : Strings.Subtitle_Many, visible.Count, online);
    }

    private void UpdateEmptyState() => EmptyState.IsVisible = visible.Count == 0;

    /// <summary>A sync or import added or changed connections in the library: add items for new ones, refresh the rest.</summary>
    private void OnLibraryChangedOutside(object? sender, EventArgs e)
    {
        foreach (Connection connection in app.Library.Connections.Where(c => items.All(i => i.Model != c)))
            items.Add(CreateItem(connection));
        foreach (ConnectionItem item in items)
            item.Changed();
        BuildNavigation();
        RefreshStatus();
    }

    private void OnStatusChanged(object? sender, StatusChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            foreach (ConnectionItem item in items.Where(i => i.Model == e.Connection))
                item.Status = e.Status;
            UpdateSubtitle();
        });

    /// <summary>Checks every connection in the background; the UI stays responsive meanwhile.</summary>
    private async void RefreshStatus()
    {
        if (StatusRefresh is { IsCompleted: false })
            return;
        RefreshButton.IsEnabled = false;
        StatusRefresh = app.Status.CheckAsync(items.Select(i => i.Model));
        try
        {
            await StatusRefresh;
        }
        catch (OperationCanceledException)
        {
        }

        RefreshButton.IsEnabled = true;
    }

    private void OpenSelected()
    {
        if (Selected is { } c)
            OpenSession(c);
    }

    private async void AddConnection()
    {
        var connection = new Connection
        {
            GroupId = filter.StartsWith("group:", StringComparison.Ordinal) ? filter["group:".Length..] : null,
            IsFavorite = filter == "fav",
        };
        if (!await ConnectionEditor.ShowAsync(connection, isNew: true))
            return;

        app.Library.Add(connection);
        ConnectionItem item = CreateItem(connection);
        items.Add(item);
        Refresh(item);
    }

    private async void EditConnection(ConnectionItem connection)
    {
        if (!await ConnectionEditor.ShowAsync(connection.Model, isNew: false))
            return;

        Save(connection);
        connection.RefreshCredentialState();
        connection.Changed();
        Refresh(connection);
    }

    private async void DeleteConnection(ConnectionItem connection)
    {
        var dialog = new FAContentDialog
        {
            Title = string.Format(CultureInfo.CurrentCulture, Strings.Delete_Title, connection.Name),
            Content = Strings.Delete_Message,
            PrimaryButtonText = Strings.Action_Delete,
            CloseButtonText = Strings.Action_Cancel,
        };
        if (await dialog.ShowAsync() == FAContentDialogResult.Primary)
            await RemoveConnectionAsync(connection);
    }

    /// <summary>Removes the connection with its window, preview image, and own stored password.</summary>
    internal async Task RemoveConnectionAsync(ConnectionItem connection)
    {
        sessionWindows.FirstOrDefault(w => ((ISessionWindow)w).Connection == connection)?.Close();
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

        if (app.Library.Contains(connection.Model))
            app.Library.Remove(connection.Model);
        BuildNavigation();
    }

    /// <summary>After an add or edit: rebuild the sidebar (groups, tags), keep the selection, check the status.</summary>
    private void Refresh(ConnectionItem connection)
    {
        // Group paths of every item may have changed (new group); refresh their labels.
        foreach (ConnectionItem item in items)
            item.Changed(nameof(ConnectionItem.GroupLabel));
        BuildNavigation();
        ActiveList.SelectedItem = visible.Contains(connection) ? connection : null;
        _ = app.Status.CheckAsync([connection.Model]);
    }

    private ContextMenu CreateContextMenu()
    {
        var connect = new MenuItem { Header = Strings.Action_Connect, Icon = new FASymbolIcon { Symbol = FASymbol.Play } };
        var edit = new MenuItem { Header = Strings.Action_Edit, Icon = new FASymbolIcon { Symbol = FASymbol.Edit } };
        var favorite = new MenuItem { Icon = new FASymbolIcon { Symbol = FASymbol.Star } };
        var copyPassword = new MenuItem { Header = Strings.Action_CopyPassword, Icon = new FASymbolIcon { Symbol = FASymbol.Permissions } };
        var fileTransfer = new MenuItem { Header = Strings.RemoteTool_Action_FileTransfer, Icon = new FASymbolIcon { Symbol = FASymbol.OpenFile } };
        var duplicate = new MenuItem { Header = Strings.Action_Duplicate, Icon = new FASymbolIcon { Symbol = FASymbol.Copy } };
        var delete = new MenuItem { Header = Strings.Action_DeleteEllipsis, Icon = new FASymbolIcon { Symbol = FASymbol.Delete } };
        var menu = new ContextMenu { ItemsSource = new object[] { connect, fileTransfer, copyPassword, edit, favorite, duplicate, new Separator(), delete } };

        menu.Opening += (_, e) =>
        {
            e.Cancel = Selected is null;
            favorite.Header = Selected?.IsFavorite == true ? Strings.Action_Unfavorite : Strings.Action_Favorite;
            copyPassword.IsVisible = Selected?.HasStoredPassword == true;
            fileTransfer.IsVisible = Selected?.Handler is IFileTransferHandler;
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
        connect.Click += (_, _) => OpenSelected();
        edit.Click += (_, _) =>
        {
            if (Selected is { } c)
                EditConnection(c);
        };
        delete.Click += (_, _) =>
        {
            if (Selected is { } c)
                DeleteConnection(c);
        };
        favorite.Click += (_, _) =>
        {
            if (Selected is not { } c)
                return;
            c.IsFavorite = !c.IsFavorite;
            Save(c);
            ApplyFilter();
        };
        duplicate.Click += (_, _) =>
        {
            if (Selected is not { } c)
                return;
            // The copy gets a new id and therefore no stored password of its own.
            Connection copy = c.Model.Duplicate(string.Format(CultureInfo.CurrentCulture, Strings.Duplicate_Name, c.Name));
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
            }
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
        RemoteToolsDialog.LibraryChanged -= OnLibraryChangedOutside;
        // Session windows are independent top-levels; closing the overview ends the app, so close them too.
        foreach (Window window in sessionWindows.ToList())
            window.Close();
        base.OnClosed(e);
    }
}
