using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Diagnostics;
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

    /// <summary>"Importieren …" and "Sicherung exportieren (JSON) …" for the "Mehr" menu (MainWindow.Menu.cs).</summary>
    public static (MenuItem Import, MenuItem Export) CreateMenuItems(MainWindow window)
    {
        var import = new MenuItem { Header = Strings.Action_Import, Icon = new FASymbolIcon { Symbol = FASymbol.Download } };
        var export = new MenuItem { Header = Strings.Action_ExportBackup, Icon = new FASymbolIcon { Symbol = FASymbol.Save } };
        // async void handlers: an exception here would reach the dispatcher and end the app.
        import.Click += async (_, _) => await GuardAsync(window, Strings.Import_Title, () => ShowImportAsync(window));
        export.Click += async (_, _) => await GuardAsync(window, Strings.Export_ToastTitle, () => ExportAsync(window));
        return (import, export);
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
            try
            {
                summary = await view.RunAsync();
                e.Cancel = summary is null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // RunAsync reports file problems itself; this is the last line so the dialog stays usable.
                CrashLog.Write(ex, "import dialog");
                view.ShowUnexpectedError(ex);
                summary = null;
                e.Cancel = true;
            }
            finally
            {
                deferral.Complete();
            }
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

    /// <summary>Runs a menu command; an unexpected error is logged and shown as a toast instead of ending the app.</summary>
    private static async Task GuardAsync(MainWindow window, string title, Func<Task> command)
    {
        try
        {
            await command();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CrashLog.Write(ex, title);
            window.ShowToast(title, CrashLog.Describe(ex), FAInfoBarSeverity.Error);
        }
    }

    public static async Task ShowSummaryAsync(ImportSummary summary)
    {
        // The figures side by side, like a row of counters: what came in, what was already there, what else was created.
        var figures = new UniformGrid { Rows = 1 };
        figures.Children.Add(Figure(summary.AddedConnections, Strings.Import_Stat_Added, null, first: true));
        figures.Children.Add(Figure(summary.ReplacedConnections, Strings.Import_Stat_Replaced));
        figures.Children.Add(Figure(summary.SkippedDuplicates, Strings.Import_Stat_Skipped, Strings.Import_Stat_SkippedTip));
        figures.Children.Add(Figure(summary.AddedGroups, Strings.Import_Stat_Groups));
        figures.Children.Add(Figure(summary.StoredPasswords, Strings.Import_Stat_Passwords, Strings.Import_Stat_PasswordsTip));
        var counters = new Border { Child = figures, Padding = new Thickness(0, 12) };
        counters.Classes.Add("well");
        // A screen reader gets the figures as one sentence instead of ten loose words.
        AutomationProperties.SetName(counters, string.Format(CultureInfo.CurrentCulture, Strings.Import_Summary, summary.AddedConnections,
            summary.ReplacedConnections, summary.SkippedDuplicates, summary.AddedGroups, summary.StoredPasswords));

        var content = new StackPanel { Width = 480, Children = { counters } };
        if (summary.StoredPasswords > 0)
        {
            Border stored = FormFields.Note(Strings.Import_PasswordHint, NoteKind.Secret);
            stored.Margin = new Thickness(0, 12, 0, 0);
            content.Children.Add(stored);
        }

        if (summary.Warnings.Count > 0)
        {
            content.Children.Add(FormFields.SectionHead(string.Format(CultureInfo.CurrentCulture, Strings.Import_Warnings, summary.Warnings.Count)));
            content.Children.Add(new ScrollViewer
            {
                MaxHeight = 220,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new ItemsControl { ItemsSource = summary.Warnings, ItemTemplate = new FuncDataTemplate<string>((text, _) => Warning(text)) },
            });
        }

        var dialog = new FAContentDialog { Title = Strings.Import_SummaryTitle, Content = content, CloseButtonText = Strings.Common_Ok };
        OpenSummary = dialog;
        await dialog.ShowAsync();
        OpenSummary = null;
    }

    /// <summary>One counter of the summary: the figure in the display face, below it what it counts. A zero steps back.</summary>
    private static Border Figure(int value, string label, string? tip = null, bool first = false)
    {
        var figure = new TextBlock { Text = value.ToString(CultureInfo.CurrentCulture), FontSize = 26, FontWeight = FontWeight.SemiBold };
        figure.Classes.Add("display");
        if (value == 0)
            figure.Classes.Add("faint");
        var caption = new TextBlock { Text = label };
        caption.Classes.Add("hint");
        var cell = new Border
        {
            Padding = new Thickness(14, 0, 8, 0),
            BorderThickness = new Thickness(first ? 0 : 1, 0, 0, 0),
            Child = new StackPanel { Spacing = 2, Children = { figure, caption } },
        };
        cell.Bind(Border.BorderBrushProperty, cell.GetResourceObservable("RmStrokeBrush"));
        if (tip is not null)
        {
            ToolTip.SetTip(cell, tip);
            // The stroke alone does not take the pointer; the whole cell should show the tip.
            cell.Background = Brushes.Transparent;
        }

        return cell;
    }

    /// <summary>One remark of the importer (something it left out or changed), as a line of a list.</summary>
    private static Grid Warning(string text)
    {
        var mark = new Ellipse { Width = 5, Height = 5, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(1, 7, 0, 0) };
        mark.Bind(Shape.FillProperty, mark.GetResourceObservable("SystemFillColorCautionBrush"));
        var line = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
        Grid.SetColumn(line, 1);
        return new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10, Margin = new Thickness(0, 0, 0, 8), Children = { mark, line } };
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
