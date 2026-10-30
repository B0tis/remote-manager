using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using FluentAvalonia.UI.Controls;

namespace RemoteManager.App;

/// <summary>
/// The connection overview: sidebar (all, favorites, recent, groups, tags), tile and list view, search.
/// Every RDP session opens in its own <see cref="SessionWindow"/>.
/// </summary>
public partial class MainWindow : Window
{
    private readonly List<Connection> connections;
    private readonly ObservableCollection<Connection> visible = [];
    private readonly List<SessionWindow> sessionWindows = [];
    private string filter = "all";

    public MainWindow()
    {
        InitializeComponent();
        connections = ConnectionStore.Load();
        foreach (Connection c in connections)
            c.ReloadPreview();

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
        Nav.SelectionChanged += (_, _) =>
        {
            if (Nav.SelectedItem is FANavigationViewItem { Tag: string tag })
            {
                filter = tag;
                ApplyFilter();
            }
        };

        BuildNavigation();
        RefreshStatus();
    }

    public IReadOnlyList<Connection> Connections => connections;
    public IReadOnlyList<Connection> VisibleConnections => visible;
    public IReadOnlyList<SessionWindow> SessionWindows => sessionWindows;
    public bool IsListView => ListPanel.IsVisible;

    private ListBox ActiveList => IsListView ? RowList : TileList;
    private Connection? Selected => ActiveList.SelectedItem as Connection;

    /// <summary>Opens the connection in its own window, or brings its open window to the front.</summary>
    public SessionWindow? OpenSession(Connection connection)
    {
        if (connection.Type != ConnectionType.Rdp)
        {
            _ = new FAContentDialog
            {
                Title = $"{connection.TypeLabel}: {connection.Name}",
                Content = $"{connection.TypeLabel} sessions run in the tool's own window. Launching {connection.TypeLabel} from Remote-Manager arrives in Phase 2.",
                CloseButtonText = "OK",
            }.ShowAsync();
            return null;
        }

        if (sessionWindows.FirstOrDefault(w => w.Connection == connection) is { } open)
        {
            if (open.WindowState == WindowState.Minimized)
                open.WindowState = WindowState.Normal;
            open.Activate();
            return open;
        }

        var window = new SessionWindow(connection);
        window.SnapshotSaved += (_, _) => connection.ReloadPreview();
        window.SessionLoggedIn += (_, _) =>
        {
            connection.LastConnectedAt = DateTime.UtcNow;
            connection.Changed();
            Save();
        };
        window.Closed += (_, _) => sessionWindows.Remove(window);
        sessionWindows.Add(window);
        window.Show();
        return window;
    }

    public void SetListView(bool list)
    {
        Connection? selected = Selected;
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
            Item("All connections", FASymbol.Library, "all"),
            Item("Favorites", FASymbol.Star, "fav"),
            Item("Recent", FASymbol.Clock, "recent"),
        };

        string[] groups = [.. connections.Select(c => c.Group).Where(g => g.Length > 0).Distinct().Order()];
        if (groups.Length > 0)
        {
            items.Add(new FANavigationViewItemHeader { Content = "Groups" });
            items.AddRange(groups.Select(g => Item(g, FASymbol.Folder, $"group:{g}")));
        }

        string[] tags = [.. connections.SelectMany(c => c.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Order()];
        if (tags.Length > 0)
        {
            items.Add(new FANavigationViewItemHeader { Content = "Tags" });
            items.AddRange(tags.Select(t => Item(t, FASymbol.Tag, $"tag:{t}")));
        }

        Nav.MenuItemsSource = items;
        Nav.SelectedItem = items.OfType<FANavigationViewItem>().FirstOrDefault(i => (string?)i.Tag == filter)
            ?? items[0];
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        IEnumerable<Connection> query = filter switch
        {
            "fav" => connections.Where(c => c.IsFavorite),
            "recent" => connections.Where(c => c.LastConnectedAt is not null).OrderByDescending(c => c.LastConnectedAt),
            _ when filter.StartsWith("group:") => connections.Where(c => c.Group == filter["group:".Length..]),
            _ when filter.StartsWith("tag:") => connections.Where(c => c.Tags.Contains(filter["tag:".Length..], StringComparer.OrdinalIgnoreCase)),
            _ => connections,
        };
        if (filter != "recent")
            query = query.OrderBy(c => c.Group.Length == 0).ThenBy(c => c.Group).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);

        string search = SearchBox.Text?.Trim() ?? "";
        if (search.Length > 0)
            query = query.Where(c => Matches(c, search));

        Connection? selected = Selected;
        visible.Clear();
        foreach (Connection c in query)
            visible.Add(c);
        if (selected is not null && visible.Contains(selected))
            ActiveList.SelectedItem = selected;

        PageTitle.Text = filter switch
        {
            "fav" => "Favorites",
            "recent" => "Recent",
            _ when filter.StartsWith("group:") => filter["group:".Length..],
            _ when filter.StartsWith("tag:") => $"#{filter["tag:".Length..]}",
            _ => "All connections",
        };
        UpdateSubtitle();
        UpdateEmptyState();
    }

    private static bool Matches(Connection c, string search) =>
        c.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
        || c.Host.Contains(search, StringComparison.OrdinalIgnoreCase)
        || c.Description.Contains(search, StringComparison.OrdinalIgnoreCase)
        || c.Group.Contains(search, StringComparison.OrdinalIgnoreCase)
        || c.TypeLabel.Contains(search, StringComparison.OrdinalIgnoreCase)
        || c.Tags.Any(t => t.Contains(search.TrimStart('#'), StringComparison.OrdinalIgnoreCase));

    private void UpdateSubtitle()
    {
        int online = visible.Count(c => c.Status == OnlineStatus.Online);
        PageSubtitle.Text = $"{visible.Count} {(visible.Count == 1 ? "connection" : "connections")} · {online} online";
    }

    private void UpdateEmptyState() => EmptyState.IsVisible = visible.Count == 0;

    private async void RefreshStatus()
    {
        RefreshButton.IsEnabled = false;
        await Task.WhenAll(connections.Select(c => c.CheckStatusAsync()));
        RefreshButton.IsEnabled = true;
        UpdateSubtitle();
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
            Group = filter.StartsWith("group:") ? filter["group:".Length..] : "",
            IsFavorite = filter == "fav",
        };
        if (!await ConnectionEditor.ShowAsync(connection, isNew: true, Groups()))
            return;

        connections.Add(connection);
        SaveAndRefresh(connection);
    }

    private async void EditConnection(Connection connection)
    {
        if (!await ConnectionEditor.ShowAsync(connection, isNew: false, Groups()))
            return;

        SaveAndRefresh(connection);
    }

    private async void DeleteConnection(Connection connection)
    {
        var dialog = new FAContentDialog
        {
            Title = $"Delete {connection.Name}?",
            Content = "The connection and its preview image are removed. This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
        };
        if (await dialog.ShowAsync() != FAContentDialogResult.Primary)
            return;

        sessionWindows.FirstOrDefault(w => w.Connection == connection)?.Close();
        connections.Remove(connection);
        if (File.Exists(connection.ThumbnailPath))
            File.Delete(connection.ThumbnailPath);
        Save();
        BuildNavigation();
    }

    private void SaveAndRefresh(Connection connection)
    {
        Save();
        BuildNavigation();
        ActiveList.SelectedItem = visible.Contains(connection) ? connection : null;
        CheckStatus(connection);
    }

    private async void CheckStatus(Connection connection)
    {
        await connection.CheckStatusAsync();
        UpdateSubtitle();
    }

    private IEnumerable<string> Groups() => connections.Select(c => c.Group).Where(g => g.Length > 0).Distinct().Order();

    private void Save() => ConnectionStore.Save(connections);

    private ContextMenu CreateContextMenu()
    {
        var connect = new MenuItem { Header = "Connect", Icon = new FASymbolIcon { Symbol = FASymbol.Play } };
        var edit = new MenuItem { Header = "Edit…", Icon = new FASymbolIcon { Symbol = FASymbol.Edit } };
        var favorite = new MenuItem { Icon = new FASymbolIcon { Symbol = FASymbol.Star } };
        var duplicate = new MenuItem { Header = "Duplicate", Icon = new FASymbolIcon { Symbol = FASymbol.Copy } };
        var delete = new MenuItem { Header = "Delete…", Icon = new FASymbolIcon { Symbol = FASymbol.Delete } };
        var menu = new ContextMenu { ItemsSource = new object[] { connect, edit, favorite, duplicate, new Separator(), delete } };

        menu.Opening += (_, e) =>
        {
            e.Cancel = Selected is null;
            favorite.Header = Selected?.IsFavorite == true ? "Remove from favorites" : "Add to favorites";
        };
        connect.Click += (_, _) => OpenSelected();
        edit.Click += (_, _) => { if (Selected is { } c) EditConnection(c); };
        delete.Click += (_, _) => { if (Selected is { } c) DeleteConnection(c); };
        favorite.Click += (_, _) =>
        {
            if (Selected is not { } c)
                return;
            c.IsFavorite = !c.IsFavorite;
            Save();
            ApplyFilter();
        };
        duplicate.Click += (_, _) =>
        {
            if (Selected is not { } c)
                return;
            var copy = new Connection
            {
                Name = $"{c.Name} (copy)", Type = c.Type, Host = c.Host, Port = c.Port, Description = c.Description,
                Group = c.Group, Tags = [.. c.Tags], UserName = c.UserName, Domain = c.Domain,
                DisplayMode = c.DisplayMode, WinKeysToSession = c.WinKeysToSession,
            };
            connections.Add(copy);
            SaveAndRefresh(copy);
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

    protected override void OnClosed(EventArgs e)
    {
        // Session windows are independent top-levels; closing the overview ends the app, so close them too.
        foreach (SessionWindow window in sessionWindows.ToList())
            window.Close();
        base.OnClosed(e);
    }
}
