using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using RemoteManager.App.Resources;
using RemoteManager.Core.Model;
using RemoteManager.Core.Status;

namespace RemoteManager.App.Views;

/// <summary>
/// The filter chips below the header: one per connection type that occurs in what the sidebar filter and the
/// search show, with its count, plus "Nur online". They narrow the overview further and are not saved: a
/// forgotten filter must not hide connections after the next start.
/// </summary>
public partial class MainWindow
{
    private ConnectionType? typeFilter;
    private bool onlineOnly;
    private IReadOnlyList<ConnectionItem> lastScope = [];

    /// <summary>The type the chips narrow the overview to; null shows every type.</summary>
    public ConnectionType? TypeFilter => typeFilter;

    public bool OnlineOnly => onlineOnly;

    /// <summary>The chips (self-test).</summary>
    public IEnumerable<ToggleButton> FilterChips => FilterBar.Children.OfType<ToggleButton>();

    public void SetTypeFilter(ConnectionType? type)
    {
        typeFilter = type;
        ApplyFilter();
    }

    public void SetOnlineOnly(bool on)
    {
        onlineOnly = on;
        ApplyFilter();
    }

    /// <summary>The chips hide connections that the sidebar filter and the search would show.</summary>
    private bool ChipsHideConnections => (typeFilter is not null || onlineOnly) && lastScope.Count > 0;

    /// <summary>Removes the chip filters (the empty state's "Filter zurücksetzen").</summary>
    public void ClearChipFilters()
    {
        typeFilter = null;
        onlineOnly = false;
        ApplyFilter();
    }

    /// <summary>Narrows <paramref name="scoped"/> (sidebar filter and search applied) by the chips and rebuilds them.</summary>
    private IEnumerable<ConnectionItem> ApplyChipFilters(IReadOnlyList<ConnectionItem> scoped)
    {
        lastScope = scoped;
        UpdateFilterBar(scoped);
        IEnumerable<ConnectionItem> result = scoped;
        if (typeFilter is { } type)
            result = result.Where(c => c.Model.Type == type);
        if (onlineOnly)
            result = result.Where(c => c.Status == OnlineStatus.Online);
        return result;
    }

    /// <summary>New counts on the chips that are there; rebuilds the bar only when the set of chips changes.</summary>
    private void UpdateFilterCounts(IReadOnlyList<ConnectionItem> scoped)
    {
        List<(string Label, int Count)> wanted = [(Strings.Filter_AllTypes, scoped.Count)];
        foreach (ConnectionType type in Enum.GetValues<ConnectionType>())
        {
            int count = scoped.Count(c => c.Model.Type == type);
            if (count > 0 || typeFilter == type)
                wanted.Add((app.Handlers[type].DisplayName, count));
        }

        wanted.Add((Strings.Filter_OnlineOnly, scoped.Count(c => c.Status == OnlineStatus.Online)));
        List<ToggleButton> chips = [.. FilterChips];
        if (chips.Count != wanted.Count || chips.Zip(wanted).Any(p => ChipLabel(p.First) != p.Second.Label))
        {
            UpdateFilterBar(scoped);
            return;
        }

        foreach ((ToggleButton chip, (string _, int count)) in chips.Zip(wanted))
        {
            if (chip.Content is StackPanel { Children: [_, TextBlock number] })
                number.Text = count.ToString(CultureInfo.CurrentCulture);
        }
    }

    private static string? ChipLabel(ToggleButton chip) =>
        chip.Content is StackPanel { Children: [TextBlock label, ..] } ? label.Text : null;

    private void UpdateFilterBar(IReadOnlyList<ConnectionItem> scoped)
    {
        FilterBar.Children.Clear();
        FilterBar.Children.Add(Chip(Strings.Filter_AllTypes, scoped.Count, typeFilter is null, () => SetTypeFilter(null)));

        // In the order the types are declared (RDP first), only those that occur; a selected type stays even at zero.
        foreach (ConnectionType type in Enum.GetValues<ConnectionType>())
        {
            int count = scoped.Count(c => c.Model.Type == type);
            if (count == 0 && typeFilter != type)
                continue;
            ConnectionType captured = type;
            FilterBar.Children.Add(Chip(app.Handlers[type].DisplayName, count, typeFilter == type,
                () => SetTypeFilter(typeFilter == captured ? null : captured)));
        }

        var online = Chip(Strings.Filter_OnlineOnly, scoped.Count(c => c.Status == OnlineStatus.Online), onlineOnly, () => SetOnlineOnly(!onlineOnly));
        online.Margin = new Avalonia.Thickness(10, 0, 0, 0);
        FilterBar.Children.Add(online);
    }

    private static ToggleButton Chip(string label, int count, bool isChecked, Action onClick)
    {
        var text = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        text.Children.Add(new TextBlock { Text = label });
        text.Children.Add(new TextBlock { Text = count.ToString(CultureInfo.CurrentCulture), Opacity = 0.6 });
        var chip = new ToggleButton { Content = text, IsChecked = isChecked };
        chip.Classes.Add("chip");
        chip.Click += (_, _) => onClick();
        return chip;
    }
}
