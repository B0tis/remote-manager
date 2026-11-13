using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Connections;
using RemoteManager.App.Connections.Rdp;
using RemoteManager.App.Resources;
using RemoteManager.App.Services;
using RemoteManager.App.Views;
using RemoteManager.Core.ImportExport;
using RemoteManager.Core.Library;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;

namespace RemoteManager.App.Diagnostics;

/// <summary>
/// The form language of the editors: every input the user can see has its label above it (a placeholder only gives
/// an example), the forms are split into the same sections for every connection type, the type selector shows each
/// type as its lettered chip, a closed RDP topic says what it holds, and an error is brought to where the user looks.
/// Saves a picture of every form in both themes ("form-*-dark.png", "form-*-light.png"), whole, also the part the
/// dialog scrolls away.
/// </summary>
internal static partial class SelfTest
{
    private static async Task RunFormChecksAsync(MainWindow window)
    {
        Appearance look = AppHost.Current.Appearance;
        AppTheme theme = look.Theme;
        try
        {
            look.SetTheme(AppTheme.Dark);
            await Settle(500);
            await RunEditorFormChecksAsync(window, look);
            RunRdpTopicChecks();
            await RunEditorErrorChecksAsync(window);
            await RunGroupFormChecksAsync(window, look);
            await RunImportFormChecksAsync(window, look);
        }
        finally
        {
            ConnectionEditor.OpenDialog?.Hide();
            GroupEditor.OpenDialog?.Hide();
            GroupDeleteDialog.Current?.Dialog.Hide();
            ImportExportCommands.OpenDialog?.Hide();
            ImportExportCommands.OpenSummary?.Hide();
            look.SetTheme(theme);
            await Settle(500);
        }
    }

    /// <summary>The connection editor for every type: sections, labels, the type selector.</summary>
    private static async Task RunEditorFormChecksAsync(MainWindow window, Appearance look)
    {
        AppHost app = AppHost.Current;
        List<IConnectionHandler> handlers = [.. app.Handlers.All];
        var problems = new List<string>();
        var sectionProblems = new List<string>();
        var chips = new List<string>();
        int inputs = 0;
        bool selectorComplete = false;
        int listedChips = 0;

        foreach (IConnectionHandler handler in handlers)
        {
            var model = new Connection
            {
                Id = TestId($"form-{handler.Type}"),
                Name = $"Formular {handler.DisplayName}",
                Type = handler.Type,
                Host = handler.HasPort ? "10.0.0.10" : "123 456 789",
                Description = "Domänencontroller am Standort Köln",
                Tags = ["prod", "hyper-v"],
                UserName = handler.UsesUserName ? "administrator" : "",
                GroupId = app.Library.Groups.FirstOrDefault()?.Id,
            };
            Task<bool> editing = ConnectionEditor.ShowAsync(model, isNew: false);
            await Settle(700);
            ConnectionEditor editor = ConnectionEditor.OpenEditor!;
            string type = handler.Type.ToString().ToLowerInvariant();
            await SaveFormProbesAsync(editor, window, look, $"form-editor-{type}");

            // The type selector: one entry per handler, and the selected one shows its chip and name.
            List<ConnectionTypeChoice> choices = [.. editor.TypeSelector.ItemsSource!.OfType<ConnectionTypeChoice>()];
            selectorComplete = choices.Count == handlers.Count
                && choices.Zip(handlers).All(pair => pair.First.Initials == pair.Second.Initials && pair.First.Name.Length > 0
                    && pair.First.Brush is ISolidColorBrush brush && brush.Color == pair.Second.AccentColor)
                && choices.Select(c => c.Name).Distinct().Count() == choices.Count;
            Border? chip = editor.TypeSelector.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("typeChip") && b.IsEffectivelyVisible);
            string shown = (chip?.Child as TextBlock)?.Text ?? "";
            chips.Add($"{shown} {(editor.TypeSelector.SelectedItem as ConnectionTypeChoice)?.Name}");
            if (shown != handler.Initials || (chip?.Background as ISolidColorBrush)?.Color != handler.AccentColor)
                problems.Add($"{handler.Type}: the selector shows \"{shown}\" instead of the chip {handler.Initials}");

            // The open list (a native popup, so its own visual is rendered): every type with its chip.
            if (handler == handlers[0])
            {
                editor.TypeSelector.IsDropDownOpen = true;
                await Settle(500);
                List<ComboBoxItem> entries = [.. editor.TypeSelector.GetRealizedContainers().OfType<ComboBoxItem>()];
                listedChips = entries.Count(e => e.GetVisualDescendants().OfType<Border>().Any(b => b.Classes.Contains("typeChip")));
                if (entries.FirstOrDefault()?.FindAncestorOfType<Avalonia.Controls.Primitives.PopupRoot>() is { } list)
                    SaveControlProbe(list, window, "form-type-selector.png");
                editor.TypeSelector.IsDropDownOpen = false;
                await Settle(300);
            }

            string[] expected = editor.SettingsEditor is null
                ? [Strings.Editor_Section_Connection, Strings.Editor_Section_Organize, Strings.Editor_Logon, Strings.Field_Notes]
                : [Strings.Editor_Section_Connection, Strings.Editor_Section_Organize, Strings.Editor_Logon, Strings.Editor_Section_StartParameters, Strings.Field_Notes];
            List<string?> sections = SectionsOf(editor);
            if (!sections.SequenceEqual(expected))
                sectionProblems.Add($"{handler.Type}: {string.Join(", ", sections)}");

            // Open every RDP topic, so its inputs count too.
            if (editor.SettingsEditor is RdpSettingsEditor rdp)
            {
                rdp.ExpandAll();
                await Settle(300);
                await SaveFormProbesAsync(editor, window, look, "form-editor-rdp-open");
            }

            (int count, List<string> unlabelled) = CheckLabels(editor);
            inputs += count;
            problems.AddRange(unlabelled.Select(p => $"{handler.Type}: {p}"));

            ConnectionEditor.OpenDialog?.Hide();
            await editing;
            await Settle(200);
        }

        Check($"Editor: every visible input of every type has its label above it, placeholders only give examples ({inputs} inputs, {handlers.Count} types{Problems(problems)})",
            inputs > handlers.Count * 8 && problems.Count == 0);
        Check($"Editor: every type has the sections Verbindung, Einordnung, Anmeldung, Startparameter, Notizen{Problems(sectionProblems)}",
            sectionProblems.Count == 0);
        Check($"The type selector lists every handler as a lettered chip in its color with a name ({string.Join(", ", chips)}; {listedChips} chips in the open list)",
            selectorComplete && chips.Count == handlers.Count && listedChips == handlers.Count);
    }

    /// <summary>A closed topic of the RDP start parameters says in its header what it holds.</summary>
    private static void RunRdpTopicChecks()
    {
        var editor = new RdpSettingsEditor();
        editor.Load(new RdpSettings());
        IReadOnlyList<string> defaults = editor.TopicSummaries;
        editor.Load(FullRdpSettings);
        IReadOnlyList<string> full = editor.TopicSummaries;
        Check($"RDP topics summarize their settings in the header (defaults: {string.Join(" | ", defaults)}; all set: {string.Join(" | ", full)})",
            defaults.Count == 5 && defaults.All(s => s.Length > 0)
            && defaults[0] == Strings.Rdp_Summary_Fit && defaults[2] == Strings.Rdp_Summary_NoGateway && defaults[4] == Strings.Rdp_Summary_Desktop
            && full[0].Contains(string.Format(CultureInfo.CurrentCulture, Strings.Rdp_Summary_Size, FullRdpSettings.DesktopWidth, FullRdpSettings.DesktopHeight), StringComparison.Ordinal)
            && full[2].Contains(FullRdpSettings.GatewayHost, StringComparison.Ordinal)
            && !full.SequenceEqual(defaults));
    }

    /// <summary>An error is shown as a note and scrolled to, although it is the last thing in a long form.</summary>
    private static async Task RunEditorErrorChecksAsync(MainWindow window)
    {
        var model = new Connection { Id = TestId("form-error"), Name = "Formular Fehler", Host = "10.0.0.10" };
        Task<bool> editing = ConnectionEditor.ShowAsync(model, isNew: true);
        await Settle(700);
        ConnectionEditor editor = ConnectionEditor.OpenEditor!;
        bool hiddenAtFirst = !editor.ErrorBanner.IsVisible;
        editor.Password.Text = "";
        editor.SavePassword.IsChecked = true;
        bool refused = !await editor.TryCommitAsync(model);
        await Settle(500);
        bool inView = editor.ErrorBanner.IsEffectivelyVisible && editor.FindAncestorOfType<ScrollViewer>() is { } scroll
            && editor.ErrorBanner.TranslatePoint(new Point(0, editor.ErrorBanner.Bounds.Height), scroll) is { } bottom
            && bottom.Y <= scroll.Bounds.Height + 0.5 && bottom.Y - editor.ErrorBanner.Bounds.Height >= -0.5;
        SaveRenderProbe(window, "form-editor-error.png");
        Check("Editor: a refused save shows the reason as an error note and scrolls it into view",
            hiddenAtFirst && refused && inView && editor.ErrorBanner.Classes.Contains("error"));
        ConnectionEditor.OpenDialog?.Hide();
        await editing;
    }

    /// <summary>Group editor, group logon panel, group delete dialog.</summary>
    private static async Task RunGroupFormChecksAsync(MainWindow window, Appearance look)
    {
        ConnectionLibrary library = AppHost.Current.Library;
        ConnectionGroup group = library.EnsureGroupPath("Selftest Formular")!;
        library.EnsureGroupPath("Selftest Formular / Server");
        group.UserName = "administrator";
        group.Domain = "FIRMA";
        try
        {
            Task<(bool Saved, string? ParentId)> editing = GroupEditor.ShowAsync(group, isNew: false);
            await Settle(700);
            GroupEditor editor = GroupEditor.OpenEditor!;
            await SaveFormProbesAsync(editor, window, look, "form-group-editor");
            (int editorInputs, List<string> problems) = CheckLabels(editor);
            List<string?> sections = SectionsOf(editor);
            GroupEditor.OpenDialog?.Hide();
            await editing;

            // The flyout is a native popup, which a render does not include: the same panel in its own window.
            var panel = new GroupLogonPanel(group);
            var host = new Window
            {
                Title = "Anmeldung der Gruppe",
                Width = 388,
                SizeToContent = SizeToContent.Height,
                ShowInTaskbar = false,
                Content = new Border { Padding = new Thickness(24), Child = panel },
            };
            host.Show(window);
            await Settle(300);
            await SaveFormProbesAsync(panel, host, look, "form-group-logon");
            (int panelInputs, List<string> panelProblems) = CheckLabels(panel);
            host.Close();
            problems.AddRange(panelProblems);
            Check($"Group editor and group logon: sections \"{string.Join("\", \"", sections)}\", every input labelled ({editorInputs} + {panelInputs} inputs{Problems(problems)})",
                sections.SequenceEqual([Strings.GroupEditor_Section, Strings.GroupEditor_Logon]) && editorInputs >= 6 && panelInputs >= 4 && problems.Count == 0);

            Task<GroupRemoval?> asking = GroupDeleteDialog.AskAsync(library, group);
            await Settle(700);
            GroupDeleteDialog asked = GroupDeleteDialog.Current!;
            var content = (Control)asked.Dialog.Content!;
            Border? Warning() => content.GetLogicalDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("note") && b.Classes.Contains("warning"));
            bool quietAtFirst = Warning() is { IsVisible: false };
            SaveRenderProbe(window, "form-group-delete-window.png");
            asked.Choice = GroupRemoval.DeleteContent;
            await Settle(300);
            bool warns = Warning() is { IsEffectivelyVisible: true };
            await SaveFormProbesAsync(content, window, look, "form-group-delete");
            asked.Dialog.Hide();
            Check("Group delete dialog: \"Alles mit löschen\" shows the warning note, cancel deletes nothing",
                quietAtFirst && warns && await asking is null && library.FindGroup(group.Id) is not null);
        }
        finally
        {
            GroupEditor.OpenDialog?.Hide();
            GroupDeleteDialog.Current?.Dialog.Hide();
            await window.RemoveGroupAsync(group, GroupRemoval.DeleteContent);
        }
    }

    /// <summary>Import dialog and import summary.</summary>
    private static async Task RunImportFormChecksAsync(MainWindow window, Appearance look)
    {
        Task importing = ImportExportCommands.ShowImportAsync(window);
        await Settle(700);
        ImportView view = ImportExportCommands.OpenView!;
        TextBlock? hint = view.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "FormatHint");
        var withoutHint = new List<string>();
        foreach (string id in new[] { "1remote", "rdcman", "mremoteng", "rdp-file", "remote-manager-backup" })
        {
            view.SelectFormat(id);
            if (view.Importer.Id != id || hint is not { IsVisible: true, Text.Length: > 0 })
                withoutHint.Add(id);
        }

        view.SelectFormat("mremoteng");
        view.SetFiles([@"C:\Users\du\AppData\Roaming\mRemoteNG\confCons.xml"]);
        view.TargetGroup = "Import / mRemoteNG";
        await Settle(200);
        await SaveFormProbesAsync(view, window, look, "form-import");
        (int inputs, List<string> problems) = CheckLabels(view);
        ImportExportCommands.OpenDialog?.Hide();
        await importing;
        Check($"Import dialog: format, file and target group are labelled, each format says where its file is ({inputs} inputs{Problems([.. problems, .. withoutHint])})",
            inputs == 3 && problems.Count == 0 && withoutHint.Count == 0);

        var summary = new ImportSummary(12, 1, 3, 2, 5,
        [
            "Intelligente Gruppe „Alle Drucker“ übersprungen (ihre Regeln haben hier keine Entsprechung).",
            "„fileserver-alt“: Das Passwort ließ sich nicht entschlüsseln und wurde nicht übernommen.",
        ], []);
        Task shown = ImportExportCommands.ShowSummaryAsync(summary);
        await Settle(700);
        var content = (Control)ImportExportCommands.OpenSummary!.Content!;
        List<string?> figures = [.. content.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("display")).Select(t => t.Text)];
        int remarks = content.GetVisualDescendants().OfType<TextBlock>().Count(t => summary.Warnings.Contains(t.Text ?? ""));
        await SaveFormProbesAsync(content, window, look, "form-import-summary");
        ImportExportCommands.OpenSummary?.Hide();
        await shown;
        Check($"Import summary: five counters ({string.Join(", ", figures)}) and the importer's remarks as a list ({remarks})",
            figures.SequenceEqual(["12", "1", "3", "2", "5"]) && remarks == summary.Warnings.Count);
    }

    /// <summary>The visible section headings of a form, top to bottom.</summary>
    private static List<string?> SectionsOf(Control form) =>
        [.. form.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("section") && t.IsEffectivelyVisible).Select(t => t.Text)];

    /// <summary>
    /// Counts the inputs the user can see and names those without a visible label, and those whose placeholder
    /// only repeats the label (it should give an example).
    /// </summary>
    private static (int Inputs, List<string> Problems) CheckLabels(Control form)
    {
        var problems = new List<string>();
        int inputs = 0;
        foreach (Control input in form.GetLogicalDescendants().OfType<Control>().Where(c => FormFields.IsInput(c) && c.IsEffectivelyVisible))
        {
            inputs++;
            string? placeholder = input switch
            {
                TextBox box => box.PlaceholderText,
                AutoCompleteBox box => box.PlaceholderText,
                ComboBox box => box.PlaceholderText,
                _ => null,
            };
            if (AutomationProperties.GetLabeledBy(input) is not TextBlock { IsEffectivelyVisible: true, Text.Length: > 0 } label)
                problems.Add($"{input.Name} has no label");
            else if (placeholder == label.Text)
                problems.Add($"{input.Name}: the placeholder repeats the label");
            else if (label.TranslatePoint(default, form) is not { } labelAt || input.TranslatePoint(default, form) is not { } inputAt || labelAt.Y >= inputAt.Y)
                problems.Add($"{input.Name}: the label is not above the input");
        }

        return (inputs, problems);
    }

    private static string Problems(IReadOnlyCollection<string> problems) => problems.Count == 0 ? "" : "; " + string.Join("; ", problems);

    /// <summary>Saves "<paramref name="name"/>-dark.png" and "-light.png" of a form and returns to the dark theme.</summary>
    private static async Task SaveFormProbesAsync(Control form, Window host, Appearance look, string name)
    {
        SaveFormProbe(form, host, $"{name}-dark.png");
        look.SetTheme(AppTheme.Light);
        await Settle(500);
        SaveFormProbe(form, host, $"{name}-light.png");
        look.SetTheme(AppTheme.Dark);
        await Settle(300);
    }

    /// <summary>
    /// Renders a whole form on the dialog surface, with the dialog's padding around it. Unlike a render of the
    /// window this includes what the dialog scrolls away, so a long form can be judged in one picture.
    /// </summary>
    private static void SaveFormProbe(Control form, Window theme, string name)
    {
        const double padding = 24;
        Size size = form.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0)
            return;
        double scale = theme.RenderScaling;
        var dpi = new Vector(96 * scale, 96 * scale);
        IBrush background = theme.TryFindResource("ContentDialogBackground", theme.ActualThemeVariant, out object? brush) && brush is IBrush b ? b : Brushes.Black;

        // Text needs an opaque surface under it to be drawn the way the screen shows it: the form gets the
        // dialog's color as its own background while it is rendered.
        AvaloniaProperty<IBrush?>? property = form switch
        {
            Avalonia.Controls.Primitives.TemplatedControl => Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty,
            Panel => Panel.BackgroundProperty,
            _ => null,
        };
        using var content = new RenderTargetBitmap(PixelSize.FromSize(size, scale), dpi);
        IDisposable? painted = property is null ? null : form.SetValue(property, background, Avalonia.Data.BindingPriority.Animation);
        try
        {
            content.Render(form);
        }
        finally
        {
            painted?.Dispose();
        }

        Size framed = size.Inflate(new Thickness(padding));
        using var composed = new RenderTargetBitmap(PixelSize.FromSize(framed, scale), dpi);
        using (DrawingContext context = composed.CreateDrawingContext())
        {
            context.FillRectangle(background, new Avalonia.Rect(framed));
            context.DrawImage(content, new Avalonia.Rect(new Point(padding, padding), size));
        }

        string path = Path.Combine(Core.AppPaths.Spike, name);
        composed.Save(path, PngBitmapEncoderOptions.Default);
        Log($"  probe saved: {path}");
    }
}
