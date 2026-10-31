using System.Globalization;
using System.Text.Json;
using System.Xml;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Microsoft.Data.Sqlite;
using RemoteManager.App.Resources;
using RemoteManager.Core.ImportExport;

namespace RemoteManager.App.Views;

/// <summary>
/// The import form: pick a format (one <see cref="IImporter"/> each, found by the plugin catalog), one or
/// more files, and optionally a group to import under. <see cref="RunAsync"/> merges the files into the
/// library through <see cref="ImportService"/>.
/// </summary>
public partial class ImportView : UserControl
{
    // Most used first; any other importer (a new file in Core/ImportExport) follows by name.
    private static readonly string[] PreferredOrder = ["1remote", "rdcman", "mremoteng", "rdp-file", "remote-manager-backup"];

    private readonly List<IImporter> importers;
    private List<string> files = [];

    public ImportView()
    {
        InitializeComponent();
        AppHost app = AppHost.Current;
        importers = [.. app.Importers.OrderBy(i => Array.IndexOf(PreferredOrder, i.Id) is >= 0 and var index ? index : PreferredOrder.Length).ThenBy(i => i.FormatName, StringComparer.CurrentCulture)];
        FormatBox.ItemsSource = importers.Select(i => i.FormatName).ToList();
        FormatBox.SelectedIndex = 0;
        FormatBox.SelectionChanged += (_, _) => SetFiles([]);
        TargetBox.ItemsSource = app.Library.GroupPaths().ToList();
        BrowseButton.Click += async (_, _) => await BrowseAsync();
    }

    public IImporter Importer => importers[Math.Max(0, FormatBox.SelectedIndex)];

    public void SelectFormat(string importerId) => FormatBox.SelectedIndex = Math.Max(0, importers.FindIndex(i => i.Id == importerId));

    public void SetFiles(IReadOnlyList<string> paths)
    {
        files = [.. paths];
        FileBox.Text = files.Count switch
        {
            0 => "",
            1 => files[0],
            _ => string.Join("; ", files.Select(Path.GetFileName)),
        };
        ShowError(null);
    }

    public string? TargetGroup
    {
        get => TargetBox.Text;
        set => TargetBox.Text = value;
    }

    /// <summary>Imports the chosen files; null (with the error shown in the form) when nothing was imported.</summary>
    public async Task<ImportSummary?> RunAsync()
    {
        if (files.Count == 0)
        {
            ShowError(Strings.Import_Error_NoFile);
            return null;
        }

        IImporter importer = Importer;
        if (files.FirstOrDefault(f => !importer.CanImport(f)) is { } wrong)
        {
            ShowError(string.Format(CultureInfo.CurrentCulture, Strings.Import_Error_WrongFormat, importer.FormatName) + $" ({Path.GetFileName(wrong)})");
            return null;
        }

        AppHost app = AppHost.Current;
        var service = new ImportService(app.Library, app.Credentials);
        ImportSummary? total = null;
        foreach (string file in files)
        {
            try
            {
                ImportResult result = await importer.ImportAsync(file);
                ImportSummary summary = await service.ApplyAsync(result, string.IsNullOrWhiteSpace(TargetBox.Text) ? null : TargetBox.Text);
                total = total is null ? summary : Add(total, summary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or XmlException or JsonException or SqliteException or FormatException)
            {
                ShowError(string.Format(CultureInfo.CurrentCulture, Strings.Import_Failed, $"{Path.GetFileName(file)}: {ex.Message}"));
                return total;
            }
        }

        return total;
    }

    private static ImportSummary Add(ImportSummary a, ImportSummary b) => new(
        a.AddedConnections + b.AddedConnections, a.ReplacedConnections + b.ReplacedConnections, a.SkippedDuplicates + b.SkippedDuplicates,
        a.AddedGroups + b.AddedGroups, a.StoredPasswords + b.StoredPasswords, [.. a.Warnings, .. b.Warnings], [.. a.Connections, .. b.Connections]);

    private async Task BrowseAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;

        IImporter importer = Importer;
        IReadOnlyList<IStorageFile> picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Import_Title,
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType(importer.FormatName) { Patterns = [.. importer.FileExtensions.Select(e => $"*{e}")] }],
        });
        List<string> paths = [.. picked.Select(f => f.TryGetLocalPath()).OfType<string>()];
        if (paths.Count > 0)
            SetFiles(paths);
    }

    private void ShowError(string? message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = message is not null;
    }
}
