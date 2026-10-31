using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RemoteManager.App.Resources;
using RemoteManager.Core.Sync;

namespace RemoteManager.App.Sync;

/// <summary>
/// Open sync conflicts: a list on the left, both versions side by side on the right, and the choice
/// "Diese behalten", "Andere behalten", or (for a connection edited on both PCs) both, the other as a copy.
/// </summary>
public partial class SyncConflictsWindow : Window
{
    private static readonly IBrush Highlight = new SolidColorBrush(Color.FromArgb(0x30, 0xF5, 0x9E, 0x0B));

    private readonly SyncService service;

    public SyncConflictsWindow() : this(AppHost.Current.Sync)
    {
    }

    public SyncConflictsWindow(SyncService service)
    {
        InitializeComponent();
        this.service = service;
        ConflictList.SelectionChanged += (_, _) => ShowSelected();
        KeepMineButton.Click += async (_, _) => await ResolveSelectedAsync(SyncResolution.KeepMine);
        KeepOtherButton.Click += async (_, _) => await ResolveSelectedAsync(SyncResolution.KeepOther);
        KeepBothButton.Click += async (_, _) => await ResolveSelectedAsync(SyncResolution.KeepBoth);
        Reload();
    }

    /// <summary>The window while it is open (used by the self-test).</summary>
    public static SyncConflictsWindow? Current { get; private set; }

    public int Count => ConflictList.ItemCount;

    public static async Task ShowAsync(Window owner, SyncService service)
    {
        var window = new SyncConflictsWindow(service);
        Current = window;
        try
        {
            await window.ShowDialog(owner);
        }
        finally
        {
            Current = null;
        }
    }

    /// <summary>Applies a choice to the selected conflict and syncs it out.</summary>
    public async Task ResolveSelectedAsync(SyncResolution resolution)
    {
        if (Selected is not { } conflict)
            return;
        Actions.IsEnabled = false;
        try
        {
            string? copyName = resolution == SyncResolution.KeepBoth
                ? string.Format(CultureInfo.CurrentCulture, Strings.Sync_CopyName, NameOf(conflict.Local) ?? "")
                : null;
            await service.ResolveAsync(conflict.Key, resolution, copyName);
        }
        finally
        {
            Actions.IsEnabled = true;
        }

        Reload();
    }

    private SyncConflict? Selected => (ConflictList.SelectedItem as ListBoxItem)?.Tag as SyncConflict;

    private void Reload()
    {
        List<SyncConflict> conflicts = [.. service.CreateEngine(service.Folder).LoadConflicts().Values
            .OrderBy(c => c.DetectedAt).ThenBy(c => NameOf(c.Local) ?? NameOf(c.Remote.Fields), StringComparer.CurrentCultureIgnoreCase)];
        ConflictList.ItemsSource = conflicts.Select(CreateListItem).ToList();
        EmptyState.IsVisible = conflicts.Count == 0;
        Heading.IsVisible = Explanation.IsVisible = Actions.IsVisible = conflicts.Count > 0;
        Comparison.Children.Clear();
        if (conflicts.Count > 0)
            ConflictList.SelectedIndex = 0;
    }

    private ListBoxItem CreateListItem(SyncConflict conflict)
    {
        var panel = new StackPanel { Spacing = 2, Margin = new Avalonia.Thickness(2, 4) };
        panel.Children.Add(new TextBlock { Text = NameOf(conflict.Local) ?? NameOf(conflict.Remote.Fields) ?? "?", FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = $"{KindLabel(conflict.Key)} · {Summary(conflict)}", FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
        return new ListBoxItem { Content = panel, Tag = conflict };
    }

    private void ShowSelected()
    {
        Comparison.Children.Clear();
        Comparison.RowDefinitions.Clear();
        if (Selected is not { } conflict)
            return;

        string other = OtherDevice(conflict);
        Heading.Text = NameOf(conflict.Local) ?? NameOf(conflict.Remote.Fields) ?? "?";
        Explanation.Text = string.Format(CultureInfo.CurrentCulture, Strings.Sync_Conflict_Explanation, KindLabel(conflict.Key), Summary(conflict),
            other, conflict.Remote.Modified.ToLocalTime());
        KeepBothButton.IsVisible = !conflict.IsDeleteConflict && conflict.Key.Kind == SyncKey.Connection;

        // Edited on both: the fields in conflict. Edit against delete: every field of the version that still exists.
        SyncFields? shown = conflict.Local ?? conflict.Remote.Fields;
        IEnumerable<string> fields = conflict.IsDeleteConflict
            ? shown?.Keys.Where(f => !f.StartsWith(SyncFieldMap.SettingsPrefix, StringComparison.Ordinal)) ?? []
            : conflict.Fields;

        AddRow(Strings.Sync_Conflict_Field,
            conflict.Local is null ? Strings.Sync_Conflict_ThisPcDeleted : Strings.Sync_Conflict_ThisPc,
            conflict.Remote.IsDeleted ? string.Format(CultureInfo.CurrentCulture, Strings.Sync_Conflict_OtherDeleted, other) : other,
            header: true, differs: false);
        foreach (string field in fields)
        {
            string? mine = conflict.Local?.Get(field), theirs = conflict.Remote.Fields?.Get(field);
            AddRow(FieldLabel(field), Display(field, mine, conflict.Local is null), Display(field, theirs, conflict.Remote.IsDeleted),
                header: false, differs: mine != theirs);
        }
    }

    private void AddRow(string label, string mine, string theirs, bool header, bool differs)
    {
        int row = Comparison.RowDefinitions.Count;
        Comparison.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        string[] cells = [label, mine, theirs];
        for (int column = 0; column < cells.Length; column++)
        {
            var text = new TextBlock
            {
                Text = cells[column],
                TextWrapping = TextWrapping.Wrap,
                FontWeight = header ? FontWeight.SemiBold : FontWeight.Normal,
                Opacity = column == 0 && !header ? 0.7 : 1,
                VerticalAlignment = VerticalAlignment.Top,
            };
            var cell = new Border
            {
                Child = text,
                Padding = new Avalonia.Thickness(8, 6),
                Margin = new Avalonia.Thickness(0, 0, 4, 4),
                CornerRadius = new Avalonia.CornerRadius(4),
                Background = differs && column > 0 ? Highlight : Brushes.Transparent,
            };
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column);
            Comparison.Children.Add(cell);
        }
    }

    private string Display(string field, string? json, bool deleted)
    {
        if (deleted)
            return "—";
        if (json is null)
            return Strings.Sync_Value_None;
        switch (field)
        {
            case "group" or "parent":
                string? id = SyncFieldMap.Display(json);
                return string.IsNullOrEmpty(id) ? Strings.Sync_Value_NoGroup
                    : service.Library.FindGroup(id) is null ? Strings.Sync_Value_UnknownGroup
                    : service.Library.PathOf(id);
            case "favorite":
                return json == "true" ? Strings.Sync_Value_Yes : Strings.Sync_Value_No;
            case "credential":
                return json == "null" ? Strings.Sync_Value_None : Strings.Sync_Value_CredentialRef;
        }

        string text = SyncFieldMap.Display(json) ?? "";
        return text.Length == 0 ? Strings.Sync_Value_Empty : text;
    }

    private static string FieldLabel(string field) => field switch
    {
        "name" => Strings.Sync_Field_Name,
        "type" => Strings.Sync_Field_Type,
        "host" => Strings.Sync_Field_Host,
        "port" => Strings.Sync_Field_Port,
        "description" => Strings.Sync_Field_Description,
        "notes" => Strings.Sync_Field_Notes,
        "tags" => Strings.Sync_Field_Tags,
        "favorite" => Strings.Sync_Field_Favorite,
        "group" => Strings.Sync_Field_Group,
        "parent" => Strings.Sync_Field_Parent,
        "sortOrder" => Strings.Sync_Field_SortOrder,
        "userName" => Strings.Sync_Field_UserName,
        "domain" => Strings.Sync_Field_Domain,
        "credential" => Strings.Sync_Field_Credential,
        _ when field.StartsWith(SyncFieldMap.SettingsPrefix, StringComparison.Ordinal) =>
            string.Format(CultureInfo.CurrentCulture, Strings.Sync_Field_Setting, field[SyncFieldMap.SettingsPrefix.Length..]),
        _ => field,
    };

    private static string KindLabel(SyncKey key) => key.Kind == SyncKey.Group ? Strings.Sync_Kind_Group : Strings.Sync_Kind_Connection;

    private static string Summary(SyncConflict conflict) =>
        conflict.Local is null ? string.Format(CultureInfo.CurrentCulture, Strings.Sync_Conflict_DeletedHere, OtherDevice(conflict))
        : conflict.Remote.IsDeleted ? string.Format(CultureInfo.CurrentCulture, Strings.Sync_Conflict_DeletedThere, OtherDevice(conflict))
        : string.Format(CultureInfo.CurrentCulture, Strings.Sync_Conflict_EditedBoth, OtherDevice(conflict));

    private static string OtherDevice(SyncConflict conflict) =>
        conflict.Remote.DeviceName.Length > 0 ? conflict.Remote.DeviceName : Strings.Sync_OtherDevice;

    private static string? NameOf(SyncFields? fields) => fields?.Get("name") is { } json ? SyncFieldMap.Display(json) : null;
}
