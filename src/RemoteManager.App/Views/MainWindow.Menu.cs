using Avalonia.Controls;
using Avalonia.Input;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Connections.RemoteTools;
using RemoteManager.App.Resources;
using RemoteManager.App.Sync;

namespace RemoteManager.App.Views;

/// <summary>
/// The header's "Mehr" menu: everything that is not a primary action (docs/ui.md lists the structure).
/// Shortcuts are shown with <see cref="MenuItem.InputGesture"/> and handled in <see cref="OnKeyDown"/>.
/// </summary>
public partial class MainWindow
{
    private MenuItem? launcherMenuItem;
    private MenuItem? tilesMenuItem;
    private MenuItem? listMenuItem;
    private MenuItem? detailsMenuItem;

    internal static FASymbolIcon MenuIcon(FASymbol symbol) => new() { Symbol = symbol };

    internal static MenuItem Item(string header, FASymbol? symbol = null, KeyGesture? gesture = null) =>
        new() { Header = header, Icon = symbol is { } s ? MenuIcon(s) : null, InputGesture = gesture };

    private MenuFlyout CreateMoreMenu()
    {
        var newGroup = Item(Strings.Action_NewGroup, FASymbol.NewFolder, new KeyGesture(Key.N, KeyModifiers.Control | KeyModifiers.Shift));
        newGroup.Click += async (_, _) => await CreateGroupAsync(CurrentGroupId);
        launcherMenuItem = Item(Strings.Menu_Launcher, FASymbol.Keyboard, new KeyGesture(Key.K, KeyModifiers.Control));
        launcherMenuItem.Click += (_, _) => OpenLauncher();
        UpdateLauncherTip();

        (MenuItem import, MenuItem export) = ImportExportCommands.CreateMenuItems(this);

        var sync = Item(Strings.Menu_Sync, FASymbol.Sync);
        sync.Click += async (_, _) => await SyncDialog.ShowAsync(app.Sync);
        var tools = Item(Strings.Menu_RemoteTools, FASymbol.Settings);
        ToolTip.SetTip(tools, Strings.RemoteTool_Settings_Tip);
        tools.Click += async (_, _) => await RemoteToolsDialog.ShowAsync();

        var check = Item(Strings.Update_CheckMenu, FASymbol.Download);
        check.Click += async (_, _) => await CheckForUpdatesAsync(startup: false);
        var about = Item(Strings.About_Menu, FASymbol.Help);
        about.Click += async (_, _) => await AboutView.ShowAsync(this);

        var menu = new MenuFlyout();
        foreach (Control entry in new Control[]
        {
            newGroup, launcherMenuItem, new Separator(),
            CreateViewMenu(), new Separator(),
            import, export, new Separator(),
            sync, tools, new Separator(),
            check, about,
        })
        {
            menu.Items.Add(entry);
        }

        return menu;
    }

    /// <summary>"Ansicht": tiles or list, detail column, status refresh, live preview interval.</summary>
    private MenuItem CreateViewMenu()
    {
        tilesMenuItem = Item(Strings.Menu_ViewTiles, FASymbol.ViewAll, new KeyGesture(Key.D1, KeyModifiers.Control));
        tilesMenuItem.ToggleType = MenuItemToggleType.Radio;
        tilesMenuItem.GroupName = "overviewView";
        tilesMenuItem.Click += (_, _) => SetListView(false);
        listMenuItem = Item(Strings.Menu_ViewList, FASymbol.List, new KeyGesture(Key.D2, KeyModifiers.Control));
        listMenuItem.ToggleType = MenuItemToggleType.Radio;
        listMenuItem.GroupName = "overviewView";
        listMenuItem.Click += (_, _) => SetListView(true);
        detailsMenuItem = Item(Strings.Menu_Details, FASymbol.DockRight, new KeyGesture(Key.I, KeyModifiers.Control));
        detailsMenuItem.ToggleType = MenuItemToggleType.CheckBox;
        detailsMenuItem.Click += (_, _) => SetDetailsVisible(!IsDetailsVisible);
        var refresh = Item(Strings.Status_RefreshTip, FASymbol.Refresh, new KeyGesture(Key.F5));
        refresh.Click += (_, _) => RefreshStatus();

        var view = Item(Strings.Menu_View, FASymbol.View);
        view.ItemsSource = new Control[] { tilesMenuItem, listMenuItem, new Separator(), detailsMenuItem, new Separator(), refresh, CreateLivePreviewMenu() };
        view.SubmenuOpened += (_, _) =>
        {
            tilesMenuItem.IsChecked = !IsListView;
            listMenuItem.IsChecked = IsListView;
            detailsMenuItem.IsChecked = IsDetailsVisible;
        };
        return view;
    }

    /// <summary>Every menu item of the "Mehr" menu, submenus included (self-test, docs).</summary>
    internal IEnumerable<MenuItem> MoreMenuItems()
    {
        static IEnumerable<MenuItem> Walk(IEnumerable<object?> entries) => entries.OfType<MenuItem>()
            .SelectMany(m => Walk(m.ItemsSource?.Cast<object?>() ?? m.Items.Cast<object?>()).Prepend(m));
        return MoreButton.Flyout is MenuFlyout menu ? Walk(menu.Items.Cast<object?>()) : [];
    }
}
