using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Resources;
using RemoteManager.App.Updates;

namespace RemoteManager.App.Views;

/// <summary>
/// The icon in the notification area and "Beim Schließen im Infobereich weiterlaufen" (setting "app.closeToTray",
/// default off). The icon is always there: a click opens the overview, its menu opens the launcher and connects
/// favorites and the five connections used last. With the setting on, closing the overview only hides it:
/// sessions, status checks, and the global hotkey keep running, and "Beenden" in the icon's menu ends the app.
/// </summary>
public partial class MainWindow
{
    internal const string CloseToTraySetting = "app.closeToTray";
    internal const int TrayFavoritesLimit = 20;
    internal const int TrayRecentLimit = 5;
    private const string TrayHintSetting = "app.closeToTray.hintShown";

    private readonly Dictionary<Color, Bitmap> trayLamps = [];
    private readonly Dictionary<NativeMenu, string> trayShapes = [];
    private TrayIcon? trayIcon;
    private NativeMenuItem? trayFavorites;
    private NativeMenuItem? trayRecent;
    private TrayHintWindow? trayHint;
    private MenuItem? closeToTrayMenuItem;
    private bool exitRequested;
    private int trayRefreshQueued;

    /// <summary>The icon's menu (self-test). Entries of a connection carry its id as <see cref="NativeMenuItem.CommandParameter"/>.</summary>
    internal NativeMenu? TrayMenu => trayIcon?.Menu;
    internal TrayIcon? Tray => trayIcon;

    /// <summary>The note shown the first time the overview hides, while it is open (self-test).</summary>
    internal TrayHintWindow? TrayHint => trayHint;

    /// <summary>Closing the overview hides it in the notification area instead of ending the app.</summary>
    public bool CloseToTray => app.Library.GetSetting(CloseToTraySetting) == "1";

    public void SetCloseToTray(bool on)
    {
        app.Library.SetSetting(CloseToTraySetting, on ? "1" : null);
        if (closeToTrayMenuItem is not null)
            closeToTrayMenuItem.IsChecked = on;
    }

    /// <summary>
    /// Whether a close request only hides the overview. Never when the app is told to end ("Beenden", a shutdown
    /// of the app or of Windows), and never without an icon to come back through.
    /// </summary>
    internal static bool HidesOnClose(bool closeToTray, bool exitRequested, bool hasTrayIcon, WindowCloseReason reason) =>
        closeToTray && !exitRequested && hasTrayIcon && reason == WindowCloseReason.WindowClosing;

    /// <summary>Creates the icon and its menu. Called once by the app, after the window exists and before it is shown.</summary>
    internal void InitializeTray()
    {
        if (trayIcon is not null || Application.Current is not { } application)
            return;

        var open = new NativeMenuItem(Strings.Tray_Open);
        open.Click += (_, _) => FromTray(ShowFromTray);
        var quickLauncher = new NativeMenuItem(Strings.Menu_Launcher);
        quickLauncher.Click += (_, _) => FromTray(OpenLauncher);
        trayFavorites = new NativeMenuItem(Strings.Nav_Favorites) { Menu = new NativeMenu() };
        trayRecent = new NativeMenuItem(Strings.Nav_Recent) { Menu = new NativeMenu() };
        var quit = new NativeMenuItem(Strings.Tray_Quit);
        quit.Click += (_, _) => FromTray(ExitFromTray);

        var menu = new NativeMenu();
        foreach (NativeMenuItemBase entry in new NativeMenuItemBase[]
        {
            open, quickLauncher, new NativeMenuItemSeparator(),
            trayFavorites, trayRecent, new NativeMenuItemSeparator(),
            quit,
        })
        {
            menu.Add(entry);
        }

        using (Stream logo = AssetLoader.Open(new Uri($"avares://{typeof(App).Assembly.GetName().Name}/Assets/logo.ico")))
            trayIcon = new TrayIcon { Icon = new WindowIcon(logo), ToolTipText = Title, Menu = menu };
        trayIcon.Clicked += (_, _) => ShowFromTray();
        TrayIcon.SetIcons(application, [trayIcon]);

        // Windows shows the menu as the lists stand, so they follow the data: a favorite toggled, a connection
        // used, renamed, or removed (all saved through the library), a status result, another theme.
        app.Library.Changed += OnTrayDataChanged;
        app.Appearance.Changed += OnTrayDataChanged;
        app.Status.StatusChanged += OnTrayStatusChanged;
        Toast.PropertyChanged += OnToastChangedWhileHidden;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        Closed += (_, _) => DisposeTray();
        RefreshTrayMenu();
    }

    /// <summary>The checkable entry of the "Mehr" menu.</summary>
    private MenuItem CreateCloseToTrayMenuItem()
    {
        closeToTrayMenuItem = Item(Strings.Tray_CloseToTray);
        closeToTrayMenuItem.ToggleType = MenuItemToggleType.CheckBox;
        closeToTrayMenuItem.IsChecked = CloseToTray;
        ToolTip.SetTip(closeToTrayMenuItem, Strings.Tray_CloseToTray_Tip);
        closeToTrayMenuItem.Click += (_, _) =>
        {
            bool on = !CloseToTray;
            SetCloseToTray(on);
            ShowToast(Strings.Tray_Toast_Title, on ? Strings.Tray_Toast_On : Strings.Tray_Toast_Off, FAInfoBarSeverity.Informational);
        };
        return closeToTrayMenuItem;
    }

    /// <summary>Brings the overview back (icon click, "Remote-Manager öffnen").</summary>
    public void ShowFromTray()
    {
        trayHint?.Close();
        BringToFront();
    }

    /// <summary>"Beenden": closes the overview for real, whatever the setting says. The app ends with it.</summary>
    public void ExitFromTray()
    {
        exitRequested = true;
        Close();
    }

    /// <summary>Connects like the launcher does: the session opens, and the overview only comes up when a handler asks something there.</summary>
    internal void ConnectFromTray(string connectionId)
    {
        if (items.FirstOrDefault(i => i.Id == connectionId) is { } connection)
            LaunchFromLauncher(connection);
    }

    /// <summary>Hides the overview instead of closing it; the first time a note says where it went.</summary>
    private void HideToTray()
    {
        // Opened is raised again when the window comes back; the "updated to version" message belongs to the first showing only.
        AppUpdates.RestartedToVersion = null;
        Hide();
        if (app.Library.GetSetting(TrayHintSetting) == "1")
            return;

        app.Library.SetSetting(TrayHintSetting, "1");
        trayHint = new TrayHintWindow();
        trayHint.Closed += (_, _) => trayHint = null;
        trayHint.ShowFor(this, Hotkey?.Gesture);
    }

    /// <summary>
    /// Runs a menu command after the menu is gone. The menu is a window of the app that is active while its entry
    /// is clicked: a dialog opened right away would land in it, and a window brought to the front would lose the
    /// focus again when the menu closes.
    /// </summary>
    private static void FromTray(Action action) => Dispatcher.UIThread.Post(action);

    private void OnTrayDataChanged(object? sender, EventArgs e) => QueueTrayRefresh();

    private void OnTrayStatusChanged(object? sender, Core.Status.StatusChangedEventArgs e) => QueueTrayRefresh();

    /// <summary>Changes come in bursts (an import, a round of status checks) and from other threads: one refresh for all of them.</summary>
    private void QueueTrayRefresh()
    {
        if (Interlocked.Exchange(ref trayRefreshQueued, 1) == 0)
            Dispatcher.UIThread.Post(RefreshTrayMenu, DispatcherPriority.Background);
    }

    /// <summary>Fills "Favoriten" (by name) and "Zuletzt verwendet" (newest first) from the connections of the library.</summary>
    internal void RefreshTrayMenu()
    {
        Interlocked.Exchange(ref trayRefreshQueued, 0);
        if (trayIcon is null || trayFavorites?.Menu is not { } favorites || trayRecent?.Menu is not { } recent)
            return;

        List<ConnectionItem> saved = [.. items.Where(i => app.Library.Contains(i.Model))];
        List<ConnectionItem> starred = [.. saved.Where(i => i.IsFavorite).OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)];
        List<ConnectionItem> used = [.. saved.Where(i => i.Model.LastConnectedAt is not null)
            .OrderByDescending(i => i.Model.LastConnectedAt).Take(TrayRecentLimit)];

        FillTrayMenu(favorites, [.. starred.Take(TrayFavoritesLimit)], Strings.Empty_Favorites_Title, more: starred.Count > TrayFavoritesLimit);
        FillTrayMenu(recent, used, Strings.Empty_Recent_Title, more: false);
        trayFavorites.IsEnabled = starred.Count > 0;
        trayRecent.IsEnabled = used.Count > 0;
    }

    /// <summary>
    /// One entry per connection. The entries are only replaced when the connections or their order changed;
    /// otherwise name, lamp, and tooltip are updated in place, so an open menu does not jump on a status result.
    /// </summary>
    private void FillTrayMenu(NativeMenu menu, IReadOnlyList<ConnectionItem> connections, string emptyText, bool more)
    {
        string shape = string.Join('\n', connections.Select(c => c.Id)) + (more ? "\n+" : "");
        if (!trayShapes.TryGetValue(menu, out string? current) || current != shape)
        {
            trayShapes[menu] = shape;
            menu.Items.Clear();
            foreach (ConnectionItem connection in connections)
            {
                string id = connection.Id;
                var entry = new NativeMenuItem { CommandParameter = id };
                entry.Click += (_, _) => FromTray(() => ConnectFromTray(id));
                menu.Add(entry);
            }

            if (connections.Count == 0)
            {
                // Its parent is disabled then; the line keeps the arrow that marks a submenu.
                menu.Add(new NativeMenuItem(emptyText) { IsEnabled = false });
            }
            else if (more)
            {
                var all = new NativeMenuItem(Strings.Tray_AllFavorites);
                all.Click += (_, _) => FromTray(() =>
                {
                    ShowFromTray();
                    ShowFilter(SidebarEntry.FavoritesTag);
                });
                menu.Add(new NativeMenuItemSeparator());
                menu.Add(all);
            }
        }

        for (int i = 0; i < connections.Count; i++)
        {
            ConnectionItem connection = connections[i];
            var entry = (NativeMenuItem)menu.Items[i];
            // A single underscore would mark an access key and vanish from the name.
            entry.Header = connection.Name.Replace("_", "__", StringComparison.Ordinal);
            entry.Icon = TrayLamp(connection.StatusBrush);
            entry.ToolTip = connection.HostLabel.Length > 0
                ? string.Format(CultureInfo.CurrentCulture, Strings.Tray_EntryTip, connection.TypeLabel, connection.HostLabel, connection.StatusLabel)
                : $"{connection.TypeLabel}\n{connection.StatusLabel}";
        }
    }

    /// <summary>The status lamp of an entry: a dot in the status color of the theme that is shown.</summary>
    private Bitmap? TrayLamp(IBrush brush)
    {
        if (brush is not ISolidColorBrush { Color: var color })
            return null;
        if (!trayLamps.TryGetValue(color, out Bitmap? lamp))
        {
            // 16 x 16 at twice the density, so the dot stays round on a scaled screen.
            var target = new RenderTargetBitmap(new PixelSize(32, 32), new Vector(192, 192));
            using (DrawingContext context = target.CreateDrawingContext())
                context.DrawEllipse(new SolidColorBrush(color), null, new Point(8, 8), 4, 4);
            trayLamps[color] = lamp = target;
        }

        return lamp;
    }

    /// <summary>
    /// A warning or an error must not go unseen because the overview is hidden (a connection started from the
    /// icon's menu or the launcher that fails, a sync conflict): the overview comes back with the message.
    /// </summary>
    private void OnToastChangedWhileHidden(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if ((e.Property == FAInfoBar.IsOpenProperty || e.Property == FAInfoBar.MessageProperty)
            && Toast.IsOpen && !IsVisible && trayIcon is not null
            && Toast.Severity is FAInfoBarSeverity.Warning or FAInfoBarSeverity.Error)
        {
            ShowFromTray();
        }
    }

    /// <summary>
    /// The update restart ends the process without closing a window; without this the old icon would stay next
    /// to the new one until the pointer moves over it. Only on the UI thread, where the icon lives.
    /// </summary>
    private void OnProcessExit(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
            trayIcon?.Dispose();
    }

    private void DisposeTray()
    {
        app.Library.Changed -= OnTrayDataChanged;
        app.Appearance.Changed -= OnTrayDataChanged;
        app.Status.StatusChanged -= OnTrayStatusChanged;
        Toast.PropertyChanged -= OnToastChangedWhileHidden;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        trayHint?.Close();
        trayIcon?.Dispose();
        trayIcon = null;
    }
}
