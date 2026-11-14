using System.Globalization;
using Avalonia.Controls;
using RemoteManager.App.Resources;
using RemoteManager.Core.Credentials.KeePass;

namespace RemoteManager.App.Credentials;

/// <summary>One row of the picker: no password, only what identifies the entry.</summary>
/// <param name="Detail">Group path and user name in one text: what the filter searches besides the title.</param>
/// <param name="Reference">What goes into the reference: the path, or the UUID when the path is not unique.</param>
public sealed record KeePassEntryChoice(string Title, string Detail, string Reference)
{
    /// <summary>"Group / Subgroup", or empty for an entry at the top.</summary>
    public string Group { get; init; } = "";

    public string UserName { get; init; } = "";
}

/// <summary>Lets the user pick a KeePass entry instead of typing its path.</summary>
public partial class KeePassEntryPicker : Window
{
    private readonly List<KeePassEntryChoice> all;

    public KeePassEntryPicker() : this("", [])
    {
    }

    public KeePassEntryPicker(string databasePath, IReadOnlyList<KdbxEntry> entries)
    {
        InitializeComponent();
        all = Choices(entries);
        DatabaseText.Text = Path.GetFileName(databasePath);
        ToolTip.SetTip(DatabaseText, databasePath);
        Show(all);

        FilterBox.TextChanged += (_, _) => ApplyFilter();
        EntryList.SelectionChanged += (_, _) => SelectButton.IsEnabled = EntryList.SelectedItem is not null;
        EntryList.DoubleTapped += (_, _) => Select();
        SelectButton.Click += (_, _) => Select();
        CancelButton.Click += (_, _) => Close(null);
        Opened += (_, _) => FilterBox.Focus();
    }

    /// <summary>The picker while open (used by the self-test).</summary>
    public static KeePassEntryPicker? Current { get; private set; }

    public IReadOnlyList<KeePassEntryChoice> Shown => (IReadOnlyList<KeePassEntryChoice>)EntryList.ItemsSource!;

    /// <summary>True while "nothing found" shows instead of entries (self-test).</summary>
    internal bool ShowsEmptyText => EmptyText.IsVisible;

    /// <summary>Types into the filter box (self-test).</summary>
    internal void Filter(string text) => FilterBox.Text = text;

    public void Select(KeePassEntryChoice choice) => Close(choice.Reference);

    public static async Task<string?> PickAsync(Window owner, string databasePath, IReadOnlyList<KdbxEntry> entries)
    {
        var picker = new KeePassEntryPicker(databasePath, entries);
        Current = picker;
        try
        {
            return await picker.ShowDialog<string?>(owner);
        }
        finally
        {
            Current = null;
        }
    }

    internal static List<KeePassEntryChoice> Choices(IReadOnlyList<KdbxEntry> entries)
    {
        return [.. entries
            .OrderBy(e => e.Path, StringComparer.CurrentCultureIgnoreCase)
            .Select(e => new KeePassEntryChoice(
                e.Title.Length > 0 ? e.Title : "-",
                string.Join("  ", new[] { string.Join(" / ", e.GroupPath), e.UserName }.Where(s => s.Length > 0)),
                PathFinds(e, entries) ? e.Path : e.Uuid)
            {
                Group = string.Join(" / ", e.GroupPath),
                UserName = e.UserName,
            })];
    }

    /// <summary>Whether the readable path finds exactly this entry again (titles and paths need not be unique).</summary>
    private static bool PathFinds(KdbxEntry entry, IReadOnlyList<KdbxEntry> entries)
    {
        try
        {
            return entry.Path.Length > 0 && ReferenceEquals(new KeePassReference("-", entry.Path).Resolve(entries), entry);
        }
        catch (Core.Credentials.CredentialSourceException)
        {
            return false;
        }
    }

    private void ApplyFilter()
    {
        string filter = FilterBox.Text?.Trim() ?? "";
        Show(filter.Length == 0
            ? all
            : all.Where(c => c.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase) || c.Detail.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList());
    }

    private void Show(List<KeePassEntryChoice> choices)
    {
        EntryList.ItemsSource = choices;
        EmptyText.IsVisible = choices.Count == 0;
        CountText.Text = choices.Count == 1 ? Strings.Picker_CountOne
            : string.Format(CultureInfo.CurrentCulture, Strings.Picker_Count, choices.Count);
    }

    private void Select()
    {
        if (EntryList.SelectedItem is KeePassEntryChoice choice)
            Select(choice);
    }
}
