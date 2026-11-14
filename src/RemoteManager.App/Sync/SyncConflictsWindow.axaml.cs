using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Resources;
using RemoteManager.Core.Model;
using RemoteManager.Core.Sync;

namespace RemoteManager.App.Sync;

/// <summary>
/// Open sync conflicts: the list on the left; on the right both versions field by field (this PC, the other PC)
/// with what differs marked, and under each version the button that decides for it. A connection edited on both
/// PCs can also keep both, the version of this PC as a copy.
/// </summary>
public partial class SyncConflictsWindow : Window
{
    /// <summary>Fewer characters in common than this, and a value is marked as a whole instead of in part.</summary>
    private const int MinCommonCharacters = 3;

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
        CloseButton.Click += (_, _) => Close();
        // The marks are painted with the theme's brushes.
        ActualThemeVariantChanged += (_, _) => ShowSelected();
        Reload();
    }

    /// <summary>The window while it is open (used by the self-test).</summary>
    public static SyncConflictsWindow? Current { get; private set; }

    public int Count => ConflictList.ItemCount;

    /// <summary>True while the "nothing left to decide" view shows (self-test).</summary>
    public bool ShowsEmptyState => EmptyState.IsVisible;

    /// <summary>What the two decisions of the selected conflict are called, and whether "keep both" is offered (self-test).</summary>
    internal string? KeepMineLabel => KeepMineButton.Content as string;
    internal string? KeepOtherLabel => KeepOtherButton.Content as string;
    internal bool OffersKeepBoth => KeepBothButton.IsVisible;

    /// <summary>Selects a conflict by its position in the list (self-test).</summary>
    internal void Select(int index) => ConflictList.SelectedIndex = index;

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
        CountText.Text = conflicts.Count.ToString(CultureInfo.CurrentCulture);
        EmptyState.IsVisible = conflicts.Count == 0;
        Workspace.IsVisible = conflicts.Count > 0;
        Comparison.Children.Clear();
        if (conflicts.Count > 0)
            ConflictList.SelectedIndex = 0;
        else
            CloseButton.Focus();
    }

    private ListBoxItem CreateListItem(SyncConflict conflict)
    {
        var name = new TextBlock
        {
            Text = NameOf(conflict.Local) ?? NameOf(conflict.Remote.Fields) ?? "?",
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var what = new TextBlock { Text = Summarize(conflict), TextWrapping = TextWrapping.Wrap };
        what.Classes.Add("caption");
        return new ListBoxItem { Content = new StackPanel { Spacing = 2, Children = { name, what } }, Tag = conflict };
    }

    private void ShowSelected()
    {
        Comparison.Children.Clear();
        Comparison.RowDefinitions.Clear();
        if (Selected is not { } conflict)
            return;

        string other = OtherDevice(conflict);
        bool deletedHere = conflict.Local is null, deletedThere = conflict.Remote.IsDeleted;
        Heading.Text = NameOf(conflict.Local) ?? NameOf(conflict.Remote.Fields) ?? "?";
        KindText.Text = KindLabel(conflict.Key);
        Explanation.Text = Summarize(conflict);
        MineCaption.Text = deletedHere ? Strings.Sync_Conflict_DeletedHereNote : Strings.Sync_Conflict_ThisPcState;
        OtherTitle.Text = other;
        OtherCaption.Text = string.Format(CultureInfo.CurrentCulture, Strings.Sync_Conflict_LastChange, conflict.Remote.Modified.ToLocalTime());

        // Each button names what it does to this record; the accent marks the answer that keeps the data.
        KeepMineButton.Content = deletedHere ? Strings.Sync_KeepDeleted : deletedThere ? Strings.Sync_KeepExisting : Strings.Sync_KeepMine;
        KeepOtherButton.Content = deletedHere ? Strings.Sync_Restore : deletedThere ? Strings.Sync_DeleteHereToo : Strings.Sync_KeepOther;
        KeepMineButton.Classes.Set("accent", !deletedHere);
        KeepOtherButton.Classes.Set("accent", deletedHere);
        KeepOtherButton.Classes.Set("danger", deletedThere);
        KeepBothButton.IsVisible = !conflict.IsDeleteConflict && conflict.Key.Kind == SyncKey.Connection;

        // Edited on both: the fields in conflict. Edit against delete: what identifies the version that still
        // exists, so its filled fields only. Both in the order of the edit dialog, not of the field keys.
        SyncFields? shown = conflict.Local ?? conflict.Remote.Fields;
        IEnumerable<string> fields = conflict.IsDeleteConflict
            ? shown?.Keys.Where(f => !f.StartsWith(SyncFieldMap.SettingsPrefix, StringComparison.Ordinal) && SaysSomething(f, shown.Get(f))) ?? []
            : conflict.Fields;
        fields = fields.OrderBy(f => Array.IndexOf(FieldOrder, f) is >= 0 and int position ? position : FieldOrder.Length)
            .ThenBy(f => f, StringComparer.Ordinal);

        foreach (string field in fields)
        {
            string? mine = deletedHere ? null : Display(field, conflict.Local?.Get(field));
            string? theirs = deletedThere ? null : Display(field, conflict.Remote.Fields?.Get(field));
            AddRow(field, mine, theirs);
        }

        // The deleted side has no values: one statement over all its rows instead of a dash in each.
        if (Comparison.RowDefinitions.Count > 0 && (deletedHere || deletedThere))
        {
            AddDeletedColumn(deletedHere ? 1 : 2,
                deletedHere ? Strings.Sync_Conflict_DeletedHereNote : string.Format(CultureInfo.CurrentCulture, Strings.Sync_Conflict_DeletedThereNote, other));
        }
    }

    /// <summary>Fields in the order a person reads a connection or group; settings and unknown fields follow.</summary>
    private static readonly string[] FieldOrder =
        ["name", "type", "host", "port", "userName", "domain", "credential", "group", "parent", "description", "tags", "notes", "favorite", "sortOrder"];

    /// <summary>False for a field that is empty or at its default: it would not help to recognize the record.</summary>
    private static bool SaysSomething(string field, string? json) => field switch
    {
        "sortOrder" => false,
        "favorite" => json == "true",
        "port" => json is not (null or "0" or "null"),
        "group" or "parent" => true,
        _ => json is not (null or "null" or "\"\"" or "[]"),
    };

    /// <summary>One field: its name, the value on this PC, the value on the other PC. Null is a deleted side.</summary>
    private void AddRow(string field, string? mine, string? theirs)
    {
        int row = Comparison.RowDefinitions.Count;
        Comparison.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        if (row > 0)
        {
            var line = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Top, Background = Brush("RmStrokeBrush") };
            Grid.SetRow(line, row);
            Grid.SetColumnSpan(line, 3);
            Comparison.Children.Add(line);
        }

        var label = new TextBlock { Text = FieldLabel(field), TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = Brush("RmTextSecondaryBrush") };
        Place(label, row, 0);
        bool mono = field is "host" or "port";
        if (mine is not null)
            Place(Value(mine, theirs, mono), row, 1);
        if (theirs is not null)
            Place(Value(theirs, mine, mono), row, 2);
    }

    private void Place(TextBlock text, int row, int column)
    {
        text.Margin = new Thickness(14, 10, 14, 10);
        text.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetRow(text, row);
        Grid.SetColumn(text, column);
        Comparison.Children.Add(text);
    }

    /// <summary>
    /// A value with the part marked that differs from <paramref name="other"/>: the text between the common start
    /// and the common end, or all of it when the two have little in common. Without another version nothing is marked.
    /// </summary>
    private TextBlock Value(string value, string? other, bool mono)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        if (mono)
            text.Classes.Add("mono");
        if (other is null || other == value)
        {
            text.Text = value;
            return text;
        }

        int start = 0;
        while (start < value.Length && start < other.Length && value[start] == other[start])
            start++;
        int end = 0;
        while (end < value.Length - start && end < other.Length - start && value[^(end + 1)] == other[^(end + 1)])
            end++;
        if (start + end < MinCommonCharacters)
            start = end = 0;

        IBrush quiet = Brush("RmTextSecondaryBrush");
        string middle = value[start..^end];
        if (start > 0)
            text.Inlines!.Add(new Run(value[..start]) { Foreground = quiet });
        if (middle.Length > 0)
        {
            text.Inlines!.Add(new Run(middle)
            {
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush("RmTextBrush"),
                Background = Brush("SystemFillColorCautionBackgroundBrush"),
            });
        }

        if (end > 0)
            text.Inlines!.Add(new Run(value[^end..]) { Foreground = quiet });
        return text;
    }

    private void AddDeletedColumn(int column, string statement)
    {
        var icon = new FASymbolIcon { Symbol = FASymbol.Delete, FontSize = 18, Foreground = Brush("RmTextTertiaryBrush") };
        var text = new TextBlock { Text = statement, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Foreground = Brush("RmTextSecondaryBrush") };
        var cell = new Border
        {
            Margin = new Thickness(8),
            CornerRadius = new CornerRadius(7),
            Background = Brush("RmInsetBrush"),
            Child = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12), Children = { icon, text } },
        };
        Grid.SetColumn(cell, column);
        Grid.SetRowSpan(cell, Comparison.RowDefinitions.Count);
        Comparison.Children.Add(cell);
    }

    private IBrush Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out object? value) && value is IBrush brush ? brush : Brushes.Transparent;

    private string Display(string field, string? json)
    {
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
            case "type":
                // The name the rest of the app uses ("RDP"); a type of a newer build stays as it was written.
                string? type = SyncFieldMap.Display(json);
                return Enum.TryParse(type, out ConnectionType known) && AppHost.Current.Handlers.Supports(known)
                    ? AppHost.Current.Handlers[known].DisplayName
                    : type ?? "";
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

    /// <summary>What happened, as a sentence: "Hier und auf DESKTOP-OP geändert."</summary>
    private static string Summarize(SyncConflict conflict)
    {
        string what = string.Format(CultureInfo.CurrentCulture,
            conflict.Local is null ? Strings.Sync_Conflict_DeletedHere
            : conflict.Remote.IsDeleted ? Strings.Sync_Conflict_DeletedThere
            : Strings.Sync_Conflict_EditedBoth, OtherDevice(conflict));
        return string.Concat(what[..1].ToUpper(CultureInfo.CurrentCulture), what.AsSpan(1), ".");
    }

    private static string OtherDevice(SyncConflict conflict) =>
        conflict.Remote.DeviceName.Length > 0 ? conflict.Remote.DeviceName : Strings.Sync_OtherDevice;

    private static string? NameOf(SyncFields? fields) => fields?.Get("name") is { } json ? SyncFieldMap.Display(json) : null;
}
