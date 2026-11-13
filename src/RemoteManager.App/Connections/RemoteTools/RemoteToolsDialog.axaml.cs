using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Resources;
using RemoteManager.App.Views;
using RemoteManager.Core.ImportExport;
using RemoteManager.Core.Model;
using RemoteManager.Core.RemoteTools;

namespace RemoteManager.App.Connections.RemoteTools;

/// <summary>
/// Settings of the remote tools, a card per topic: executable paths (detected, or set here; a lamp shows whether
/// the tool can start), the TeamViewer Web API token with "Jetzt abgleichen", and the AnyDesk CSV import. Opened
/// from the "Mehr" menu and from <see cref="MissingToolDialog"/>.
/// </summary>
public partial class RemoteToolsDialog : UserControl
{
    private readonly AppHost app;
    private readonly Dictionary<ConnectionType, (TextBox Box, TextBlock State)> paths = [];

    public RemoteToolsDialog()
    {
        InitializeComponent();
        app = AppHost.Current;
        foreach (RemoteToolInfo info in RemoteToolLocator.Tools)
            PathRows.Children.Add(CreatePathRow(info));

        SaveTokenButton.Click += async (_, _) => await SaveTokenAsync();
        DeleteTokenButton.Click += async (_, _) =>
        {
            await RunCredentialAsync(TeamViewerAccount.DeleteTokenAsync);
            await UpdateTokenStateAsync();
        };
        SyncButton.Click += async (_, _) => await SyncNowAsync();
        CsvButton.Click += async (_, _) =>
        {
            if (await PickFileAsync(Strings.RemoteTool_Csv_Import, "CSV", "*.csv") is { } file)
                await ImportCsvAsync(file);
        };
        TokenBox.PlaceholderText = Strings.RemoteTool_Sync_TokenPlaceholder;
        _ = UpdateTokenStateAsync();
    }

    /// <summary>Raised after a sync or import changed the library; the overview reloads its items.</summary>
    public static event EventHandler? LibraryChanged;

    /// <summary>The dialog and its content while open (used by the self-test).</summary>
    public static FAContentDialog? OpenDialog { get; private set; }
    public static RemoteToolsDialog? OpenContent { get; private set; }

    public FAInfoBar SyncResultBar => SyncResult;
    public FAInfoBar CsvResultBar => CsvResult;

    public static async Task ShowAsync(ConnectionType? focus = null)
    {
        var content = new RemoteToolsDialog();
        var dialog = new FAContentDialog
        {
            Title = Strings.RemoteTool_Settings_Title,
            Content = content,
            PrimaryButtonText = Strings.Action_Save,
            CloseButtonText = Strings.Action_Cancel,
            DefaultButton = FAContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += (_, e) => e.Cancel = !content.TrySavePaths();
        // Three cards: on a window with the room they all show at once.
        DialogKit.AllowHeight(dialog, 920);
        if (focus is { } type)
            dialog.Opened += (_, _) => content.paths[type].Box.Focus();

        OpenDialog = dialog;
        OpenContent = content;
        await dialog.ShowAsync();
        OpenDialog = null;
        OpenContent = null;
    }

    /// <summary>Sets a path box (self-test).</summary>
    internal void SetPath(ConnectionType type, string path) => paths[type].Box.Text = path;

    /// <summary>The message under a path box, or null while there is none (self-test).</summary>
    internal string? PathError(ConnectionType type) => paths[type].State.IsVisible ? paths[type].State.Text : null;

    /// <summary>Writes the path settings; false (and an error under the row) when a path is not an existing .exe.</summary>
    internal bool TrySavePaths()
    {
        bool valid = true;
        foreach ((ConnectionType type, (TextBox box, TextBlock state)) in paths)
        {
            string path = box.Text?.Trim().Trim('"') ?? "";
            bool wrong = path.Length > 0 && (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path));
            state.IsVisible = wrong;
            if (wrong)
            {
                state.Text = string.Format(CultureInfo.CurrentCulture, Strings.RemoteTool_Paths_Invalid, path);
                valid = false;
            }
        }

        if (!valid)
            return false;

        foreach ((ConnectionType type, (TextBox box, _)) in paths)
        {
            string path = box.Text?.Trim().Trim('"') ?? "";
            app.Library.Store.SetSetting(RemoteToolLocator.InfoFor(type).SettingKey, path.Length > 0 ? path : null);
        }

        return true;
    }

    internal async Task SyncNowAsync()
    {
        SyncButton.IsEnabled = false;
        try
        {
            DeviceSyncReport report = await TeamViewerAccount.SyncAsync();
            Show(SyncResult, FAInfoBarSeverity.Success, string.Format(CultureInfo.CurrentCulture, Strings.RemoteTool_Sync_Result,
                report.Added, report.Updated, report.Unchanged, report.MarkedRemoved));
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (TeamViewerApiException ex)
        {
            Show(SyncResult, FAInfoBarSeverity.Error, TeamViewerAccount.Describe(ex));
        }
        catch (Win32Exception ex)
        {
            Show(SyncResult, FAInfoBarSeverity.Error, string.Format(CultureInfo.CurrentCulture, Strings.Password_ReadFailed, ex.Message));
        }
        finally
        {
            SyncButton.IsEnabled = true;
        }
    }

    internal async Task ImportCsvAsync(string file)
    {
        var importer = new AnyDeskCsvImporter();
        ImportResult result;
        try
        {
            result = await importer.ImportAsync(file);
        }
        catch (IOException ex)
        {
            Show(CsvResult, FAInfoBarSeverity.Error, string.Format(CultureInfo.CurrentCulture, Strings.RemoteTool_Csv_Error, ex.Message));
            return;
        }

        int known = AnyDeskCsvImporter.RemoveKnown(app.Library.Connections, result);
        if (result.Connections.Count > 0)
        {
            string? groupId = app.Library.EnsureGroupPath(Strings.RemoteTool_Csv_Group)?.Id;
            foreach (Connection connection in result.Connections)
            {
                connection.GroupId = groupId;
                app.Library.Add(connection);
            }

            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }

        Show(CsvResult, result.Connections.Count > 0 || known > 0 ? FAInfoBarSeverity.Success : FAInfoBarSeverity.Warning,
            string.Format(CultureInfo.CurrentCulture, Strings.RemoteTool_Csv_Result, result.Connections.Count, known, result.Warnings.Count)
            + (result.Warnings.Count > 0 ? "\n" + string.Join("\n", result.Warnings.Take(5)) : ""));
    }

    /// <summary>
    /// One tool: a lamp with its name and what was found, then the path box. The detected path shows as the
    /// placeholder of the empty box, so the box always reads as the path in use.
    /// </summary>
    private Control CreatePathRow(RemoteToolInfo info)
    {
        ToolLocation location = RemoteToolLauncher.Locator.Locate(info.Type);
        string? configured = app.Library.Store.GetSetting(info.SettingKey);
        string? detected = RemoteToolLauncher.Locator.Detect(info.Type);

        var lamp = new Ellipse { Classes = { "lamp" } };
        DialogKit.SetLamp(lamp, location switch
        {
            { ConfiguredPathMissing: true } => "fault",
            { Path: not null } => "ok",
            _ => "idle",
        });
        var name = new TextBlock { Text = info.ProductName, FontWeight = Avalonia.Media.FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var found = new TextBlock
        {
            Classes = { "caption" },
            VerticalAlignment = VerticalAlignment.Center,
            Text = location switch
            {
                { ConfiguredPathMissing: true } => Strings.RemoteTool_State_SetMissing,
                { Path: not null, FromSetting: true } => Strings.RemoteTool_State_Set,
                { Path: not null } => Strings.RemoteTool_State_Found,
                _ => Strings.RemoteTool_State_Missing,
            },
        };
        // Only offered while there is nothing to start.
        var download = new Button
        {
            Classes = { "link" },
            Content = Strings.RemoteTool_Missing_Download,
            IsVisible = location.Path is null,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(0, -3, -6, -3),
        };
        ToolTip.SetTip(download, info.DownloadUrl);
        download.Click += (_, _) => MissingToolDialog.OpenUrl(info.DownloadUrl);

        var box = new TextBox
        {
            Classes = { "mono" },
            Text = configured ?? "",
            PlaceholderText = detected ?? Strings.RemoteTool_Paths_Auto,
        };
        var browse = new Button { Content = new FASymbolIcon { Symbol = FASymbol.OpenFolder, FontSize = 15 }, Width = 36, Height = 32, Padding = new Avalonia.Thickness(0) };
        ToolTip.SetTip(browse, Strings.RemoteTool_Paths_Browse);
        browse.Click += async (_, _) =>
        {
            if (await PickFileAsync(info.ProductName, info.ProductName, info.ExeName, "*.exe") is { } file)
                box.Text = file;
        };

        // Shown only for a path that cannot be used: the configured one is gone, or the typed one is no .exe.
        var state = new TextBlock
        {
            FontSize = 12.5,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            IsVisible = location.ConfiguredPathMissing,
            Text = location.ConfiguredPathMissing
                ? string.Format(CultureInfo.CurrentCulture, Strings.RemoteTool_Paths_ConfiguredMissing, configured)
                : "",
        };
        // The frame shows and hides with its text.
        Border problem = DialogKit.Note(state, "error");
        problem.Margin = new Avalonia.Thickness(0, 8, 0, 0);
        problem[!IsVisibleProperty] = state[!IsVisibleProperty];
        paths[info.Type] = (box, state);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,8,Auto,10,*,Auto"), Margin = new Avalonia.Thickness(1, 0, 0, 6) };
        Place(header, lamp, 0);
        Place(header, name, 2);
        Place(header, found, 4);
        Place(header, download, 5);
        var input = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,Auto") };
        Place(input, box, 0);
        Place(input, browse, 2);
        return new StackPanel { Children = { header, input, problem } };
    }

    private static void Place(Grid grid, Control control, int column)
    {
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
    }

    private async Task SaveTokenAsync()
    {
        string token = TokenBox.Text?.Trim() ?? "";
        if (token.Length == 0)
            return;
        await RunCredentialAsync(() => TeamViewerAccount.SaveTokenAsync(token));
        TokenBox.Text = "";
        await UpdateTokenStateAsync();
    }

    private async Task RunCredentialAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Win32Exception ex)
        {
            Show(SyncResult, FAInfoBarSeverity.Error, string.Format(CultureInfo.CurrentCulture, Strings.Password_SaveFailed, ex.Message));
        }
    }

    private async Task UpdateTokenStateAsync()
    {
        bool has = await TeamViewerAccount.HasTokenAsync();
        TokenState.Text = has ? Strings.RemoteTool_Sync_TokenStored : Strings.RemoteTool_Sync_NoToken;
        DialogKit.SetLamp(TokenLamp, has ? "ok" : "idle");
        DeleteTokenButton.IsEnabled = has;
        SyncButton.IsEnabled = has;
    }

    private static void Show(FAInfoBar bar, FAInfoBarSeverity severity, string message)
    {
        bar.Severity = severity;
        bar.Message = message;
        bar.IsOpen = true;
    }

    private async Task<string?> PickFileAsync(string title, string typeName, params string[] patterns)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage)
            return null;
        IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(typeName) { Patterns = patterns }],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
