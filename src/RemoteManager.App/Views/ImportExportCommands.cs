using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Resources;
using RemoteManager.Core.ImportExport;

namespace RemoteManager.App.Views;

/// <summary>
/// "Importieren …" and "Sicherung exportieren (JSON) …" for the overview's "more" menu. Kept out of
/// <see cref="MainWindow"/> so the window only wires the menu.
/// </summary>
internal static class ImportExportCommands
{
    /// <summary>The import dialog and its form while open (used by the self-test).</summary>
    public static FAContentDialog? OpenDialog { get; private set; }
    public static ImportView? OpenView { get; private set; }

    /// <summary>The summary dialog while open (used by the self-test).</summary>
    public static FAContentDialog? OpenSummary { get; private set; }

    public static MenuFlyout CreateMenu(MainWindow window)
    {
        var import = new MenuItem { Header = Strings.Action_Import, Icon = new FASymbolIcon { Symbol = FASymbol.Download } };
        var export = new MenuItem { Header = Strings.Action_ExportBackup, Icon = new FASymbolIcon { Symbol = FASymbol.Save } };
        import.Click += async (_, _) => await ShowImportAsync(window);
        export.Click += async (_, _) => await ExportAsync(window);
        // Items, not ItemsSource: MainWindow.Updates.cs appends its entries.
        var menu = new MenuFlyout();
        menu.Items.Add(import);
        menu.Items.Add(export);
        return menu;
    }

    public static async Task ShowImportAsync(MainWindow window)
    {
        var view = new ImportView();
        var dialog = new FAContentDialog
        {
            Title = Strings.Import_Title,
            Content = view,
            PrimaryButtonText = Strings.Import_Start,
            CloseButtonText = Strings.Action_Cancel,
            DefaultButton = FAContentDialogButton.Primary,
        };
        ImportSummary? summary = null;
        dialog.PrimaryButtonClick += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            summary = await view.RunAsync();
            e.Cancel = summary is null;
            deferral.Complete();
        };

        OpenDialog = dialog;
        OpenView = view;
        await dialog.ShowAsync();
        OpenDialog = null;
        OpenView = null;

        if (summary is not null)
        {
            window.SyncWithLibrary();
            await ShowSummaryAsync(summary);
        }
    }

    public static async Task ShowSummaryAsync(ImportSummary summary)
    {
        var content = new StackPanel { Spacing = 10, Width = 480 };
        content.Children.Add(new TextBlock
        {
            Text = string.Format(CultureInfo.CurrentCulture, Strings.Import_Summary, summary.AddedConnections, summary.ReplacedConnections,
                summary.SkippedDuplicates, summary.AddedGroups, summary.StoredPasswords),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        if (summary.Warnings.Count > 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = string.Format(CultureInfo.CurrentCulture, Strings.Import_Warnings, summary.Warnings.Count),
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                Margin = new Avalonia.Thickness(0, 6, 0, 0),
            });
            content.Children.Add(new ScrollViewer
            {
                MaxHeight = 220,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new ItemsControl
                {
                    ItemsSource = summary.Warnings,
                    ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((text, _) =>
                        new TextBlock { Text = $"• {text}", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = 0.75, FontSize = 12, Margin = new Avalonia.Thickness(0, 2) }),
                },
            });
        }

        var dialog = new FAContentDialog { Title = Strings.Import_SummaryTitle, Content = content, CloseButtonText = Strings.Common_Ok };
        OpenSummary = dialog;
        await dialog.ShowAsync();
        OpenSummary = null;
    }

    public static async Task ExportAsync(MainWindow window)
    {
        IExporter exporter = AppHost.Current.Exporters.First(e => e is JsonBackupExporter);
        IStorageFile? file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Strings.Export_PickerTitle,
            SuggestedFileName = $"remote-manager-{DateTime.Now:yyyy-MM-dd}{exporter.FileExtension}",
            DefaultExtension = exporter.FileExtension.TrimStart('.'),
            FileTypeChoices = [new FilePickerFileType(Strings.Export_FileTypeName) { Patterns = [$"*{exporter.FileExtension}"] }],
        });
        if (file is null)
            return;

        try
        {
            await using (Stream stream = await file.OpenWriteAsync())
            {
                if (stream.CanSeek)
                    stream.SetLength(0);
                await ExportToAsync(exporter, stream);
            }

            window.ShowToast(Strings.Export_ToastTitle, string.Format(CultureInfo.CurrentCulture, Strings.Export_Done, file.Name), FAInfoBarSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            window.ShowToast(Strings.Export_ToastTitle, string.Format(CultureInfo.CurrentCulture, Strings.Export_Failed, ex.Message), FAInfoBarSeverity.Error);
        }
    }

    /// <summary>The whole library (groups and connections, no passwords) as <paramref name="exporter"/> writes it.</summary>
    public static Task ExportToAsync(IExporter exporter, Stream stream)
    {
        AppHost app = AppHost.Current;
        return exporter.ExportAsync(new ExportData(app.Library.Groups, app.Library.Connections), stream);
    }
}
