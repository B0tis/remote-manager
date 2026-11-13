using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Connections;
using RemoteManager.App.Connections.RemoteTools;
using RemoteManager.App.Diagnostics;
using RemoteManager.App.Resources;
using RemoteManager.App.Services;
using RemoteManager.App.Sync;
using RemoteManager.Core.Model;
using RemoteManager.Core.Search;

namespace RemoteManager.App.Views;

/// <summary>
/// The quick launcher and its global hotkey (setting "launcher.hotkey", default Ctrl+Alt+R), and what the
/// launcher does in the overview: connect, reveal, run an app command, connect once to a typed address.
/// </summary>
public partial class MainWindow
{
    private const string HotkeySetting = "launcher.hotkey";

    private readonly Dictionary<string, ConnectionItem> directSessions = new(StringComparer.OrdinalIgnoreCase);
    private QuickLauncherWindow? launcher;
    private IReadOnlyList<LauncherCommand>? launcherCommands;

    public GlobalHotkey? Hotkey { get; private set; }
    public QuickLauncherWindow? QuickLauncher => launcher;

    /// <summary>The app commands the launcher offers under "Aktionen", in the order "&gt;" lists them.</summary>
    internal IReadOnlyList<LauncherCommand> LauncherCommands => launcherCommands ??= CreateLauncherCommands();

    /// <summary>A dialog (editor, import, sync, ...) is open in the overview.</summary>
    internal bool HasOpenDialog =>
        OverlayLayer.GetOverlayLayer(this) is { } layer && layer.GetVisualDescendants().OfType<FAContentDialog>().Any();

    /// <summary>Shows the launcher (hotkey, toolbar button, Ctrl+K).</summary>
    public void OpenLauncher()
    {
        launcher ??= new QuickLauncherWindow();
        launcher.ShowFor(this);
    }

    /// <summary>Registers <paramref name="gesture"/> as the global hotkey and saves it; false (old one kept) when Windows refuses it.</summary>
    public bool TrySetHotkey(KeyGesture gesture)
    {
        if (Hotkey is null)
            return false;
        KeyGesture? previous = Hotkey.Gesture;
        if (!Hotkey.Register(gesture))
        {
            if (previous is not null)
                Hotkey.Register(previous);
            return false;
        }

        app.Library.SetSetting(HotkeySetting, gesture.ToString());
        UpdateLauncherTip();
        return true;
    }

    /// <summary>Enter in the launcher: connect, and bring the overview up when the handler shows its dialog there.</summary>
    internal void LaunchFromLauncher(ConnectionItem connection)
    {
        OpenSession(connection);
        if (PlannedConnectionDialog.Current is not null || MissingToolDialog.Current is not null)
            BringToFront();
    }

    /// <summary>Ctrl+Enter in the launcher: show the connection in the overview with its details.</summary>
    internal void Reveal(ConnectionItem connection)
    {
        BringToFront();
        if (filter != "all")
        {
            filter = "all";
            BuildNavigation();
        }

        SearchBox.Text = "";
        if (connection.Model.GroupId is { } groupId)
        {
            // Expand the sections on the way to it.
            foreach (var group in app.Library.AncestorsOf(groupId).Where(g => g.IsCollapsed).ToList())
            {
                group.IsCollapsed = false;
                app.Library.UpdateGroup(group);
            }

            BuildNavigation();
        }

        Select([connection]);
        ActiveList.ScrollIntoView(connection);
        SetDetailsVisible(true);
    }

    /// <summary>
    /// Enter on an action in the launcher. The command runs the code of its menu entry; one that opens a
    /// dialog is skipped while another dialog is open in the overview (the overview comes to the front, so
    /// the open one is what the user sees). An unexpected error is logged and shown as a toast.
    /// </summary>
    internal async void RunLauncherCommand(LauncherCommand command)
    {
        if (command.ShowsOverview)
            BringToFront();
        if (command.OpensDialog && HasOpenDialog)
            return;

        try
        {
            await command.Run();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CrashLog.Write(ex, $"launcher command {command.Id}");
            ShowToast(command.Label, CrashLog.Describe(ex), FAInfoBarSeverity.Error);
        }
    }

    /// <summary>
    /// Enter on "Direkt verbinden": opens a session for a connection made from the typed target. It is not
    /// added to the library or the overview. The same target again brings its window to the front. When the
    /// window closes, the preview picture and a password saved from the logon card go with it.
    /// </summary>
    internal Window? ConnectDirect(AdHocTarget target, ConnectionType type)
    {
        string key = $"{type}:{target.Label}";
        if (directSessions.TryGetValue(key, out ConnectionItem? open))
            return OpenSession(open);

        ConnectionItem item = CreateItem(target.ToConnection(type));
        Window? window = OpenSession(item);
        if (window is null)
            return null;

        directSessions[key] = item;
        window.Closed += async (_, _) =>
        {
            directSessions.Remove(key);
            await DiscardConnectionAsync(item);

            // A live preview that was being encoded while the window closed is written a moment later.
            await Task.Delay(TimeSpan.FromSeconds(2));
            string preview = item.Model.ResolvedPreviewPath;
            if (File.Exists(preview))
                File.Delete(preview);
        };
        return window;
    }

    /// <summary>Ctrl+Enter on "Direkt verbinden": the editor for a new connection, filled with the typed target.</summary>
    internal async void AddConnectionFor(AdHocTarget target, ConnectionType type)
    {
        BringToFront();
        if (HasOpenDialog)
            return;

        Connection connection = target.ToConnection(type);
        connection.GroupId = CurrentGroupId;
        if (!await ConnectionEditor.ShowAsync(connection, isNew: true))
            return;

        connection.SortOrder = app.Library.ConnectionsIn(connection.GroupId).Count();
        app.Library.Add(connection);
        ConnectionItem item = CreateItem(connection);
        items.Add(item);
        Refresh(item);
    }

    /// <summary>
    /// Every command calls what its menu entry or header button calls (MainWindow.Menu.cs,
    /// MainWindow.Appearance.cs), so the launcher and the menu cannot drift apart.
    /// </summary>
    private List<LauncherCommand> CreateLauncherCommands()
    {
        Appearance look = app.Appearance;
        const KeyModifiers ctrl = KeyModifiers.Control;

        static Task Now(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        LauncherCommand Theme(string id, AppTheme theme, string label, FASymbol symbol, string keywords) => new()
        {
            Id = id, Label = label, Symbol = symbol, Keywords = keywords,
            IsActive = () => look.Theme == theme,
            Run = () => Now(() => look.SetTheme(theme)),
        };

        LauncherCommand Size(string id, TileSize size, string name, FASymbol symbol) => new()
        {
            Id = id, Symbol = symbol, Keywords = Strings.Launcher_Keywords_TileSize,
            Label = string.Format(CultureInfo.CurrentCulture, Strings.Launcher_Command_TileSize, name),
            IsActive = () => look.TileSize == size,
            ShowsOverview = true,
            Run = () => Now(() => SetTileSize(size)),
        };

        return
        [
            new()
            {
                Id = "connection.new", Label = Strings.Action_New, Symbol = FASymbol.Add, Keywords = Strings.Launcher_Keywords_NewConnection,
                Gesture = new KeyGesture(Key.N, ctrl), ShowsOverview = true, OpensDialog = true,
                Run = () => Now(() => AddConnection()),
            },
            new()
            {
                Id = "group.new", Label = Strings.Action_NewGroup, Symbol = FASymbol.NewFolder, Keywords = Strings.Launcher_Keywords_NewGroup,
                Gesture = new KeyGesture(Key.N, ctrl | KeyModifiers.Shift), ShowsOverview = true, OpensDialog = true,
                Run = () => CreateGroupAsync(CurrentGroupId),
            },
            new()
            {
                Id = "import", Label = Strings.Action_Import, Symbol = FASymbol.Import, Keywords = Strings.Launcher_Keywords_Import,
                ShowsOverview = true, OpensDialog = true,
                Run = () => ImportExportCommands.ShowImportAsync(this),
            },
            new()
            {
                Id = "export", Label = Strings.Action_ExportBackup, Symbol = FASymbol.Save, Keywords = Strings.Launcher_Keywords_Export,
                ShowsOverview = true,
                Run = () => ImportExportCommands.ExportAsync(this),
            },
            new()
            {
                Id = "sync", Label = Strings.Menu_Sync, Symbol = FASymbol.Sync, Keywords = Strings.Launcher_Keywords_Sync,
                ShowsOverview = true, OpensDialog = true,
                Run = () => SyncDialog.ShowAsync(app.Sync),
            },
            new()
            {
                Id = "status.refresh", Label = Strings.Status_RefreshTip, Symbol = FASymbol.Refresh, Keywords = Strings.Launcher_Keywords_Refresh,
                Gesture = new KeyGesture(Key.F5),
                Run = () => Now(RefreshStatus),
            },
            new()
            {
                Id = "view.tiles", Label = Strings.Launcher_Command_ViewTiles, Symbol = FASymbol.ViewAll, Keywords = Strings.Launcher_Keywords_ViewTiles,
                Gesture = new KeyGesture(Key.D1, ctrl), IsActive = () => !IsListView, ShowsOverview = true,
                Run = () => Now(() => SetListView(false)),
            },
            new()
            {
                Id = "view.list", Label = Strings.Launcher_Command_ViewList, Symbol = FASymbol.List, Keywords = Strings.Launcher_Keywords_ViewList,
                Gesture = new KeyGesture(Key.D2, ctrl), IsActive = () => IsListView, ShowsOverview = true,
                Run = () => Now(() => SetListView(true)),
            },
            Theme("theme.light", AppTheme.Light, Strings.Appearance_Theme_Light, FASymbol.WeatherSunny, Strings.Launcher_Keywords_ThemeLight),
            Theme("theme.dark", AppTheme.Dark, Strings.Appearance_Theme_Dark, FASymbol.WeatherMoon, Strings.Launcher_Keywords_ThemeDark),
            Theme("theme.system", AppTheme.System, Strings.Appearance_Theme_System, FASymbol.DarkTheme, Strings.Launcher_Keywords_ThemeSystem),
            Size("tiles.small", TileSize.Small, Strings.TileSize_Small, FASymbol.ZoomOut),
            Size("tiles.medium", TileSize.Medium, Strings.TileSize_Medium, FASymbol.Zoom),
            Size("tiles.large", TileSize.Large, Strings.TileSize_Large, FASymbol.ZoomIn),
            new()
            {
                Id = "tools", Label = Strings.Menu_RemoteTools, Symbol = FASymbol.Settings, Keywords = Strings.Launcher_Keywords_RemoteTools,
                ShowsOverview = true, OpensDialog = true,
                Run = () => RemoteToolsDialog.ShowAsync(),
            },
            new()
            {
                Id = "hotkey", Label = Strings.Launcher_Command_Hotkey, Symbol = FASymbol.Keyboard, Keywords = Strings.Launcher_Keywords_Hotkey,
                KeepsLauncherOpen = true,
                Run = () => Now(() => launcher?.ShowHotkeySettings()),
            },
            new()
            {
                Id = "updates", Label = Strings.Update_CheckMenu, Symbol = FASymbol.Download, Keywords = Strings.Launcher_Keywords_Updates,
                ShowsOverview = true,
                Run = () => CheckForUpdatesAsync(startup: false),
            },
            new()
            {
                Id = "about", Label = Strings.About_Menu, Symbol = FASymbol.Help, Keywords = Strings.Launcher_Keywords_About,
                ShowsOverview = true, OpensDialog = true,
                Run = () => AboutView.ShowAsync(this),
            },
        ];
    }

    private void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    private void StartHotkey()
    {
        if (Hotkey is not null)
            return;
        Hotkey = new GlobalHotkey(this);
        Hotkey.Pressed += (_, _) => OpenLauncher();
        KeyGesture gesture = GlobalHotkey.Parse(app.Library.GetSetting(HotkeySetting));
        if (!Hotkey.Register(gesture))
        {
            ShowToast(Strings.Launcher_Title, string.Format(CultureInfo.CurrentCulture, Strings.Launcher_HotkeyTaken, GlobalHotkey.Format(gesture)),
                FAInfoBarSeverity.Warning);
        }

        UpdateLauncherTip();
    }

    private void UpdateLauncherTip()
    {
        string tip = Hotkey?.Gesture is { } gesture
            ? string.Format(CultureInfo.CurrentCulture, Strings.Launcher_ButtonTip, GlobalHotkey.Format(gesture))
            : Strings.Launcher_Title;
        ToolTip.SetTip(LauncherButton, tip);
        if (launcherMenuItem is not null)
            ToolTip.SetTip(launcherMenuItem, tip);
    }

    private void StopLauncher()
    {
        Hotkey?.Dispose();
        Hotkey = null;
        launcher?.CloseForGood();
        launcher = null;
    }
}
