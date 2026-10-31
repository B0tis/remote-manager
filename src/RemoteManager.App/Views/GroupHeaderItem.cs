using System.ComponentModel;
using System.Globalization;
using Avalonia;
using RemoteManager.App.Resources;
using RemoteManager.Core.Model;

namespace RemoteManager.App.Views;

/// <summary>
/// A group's header row in the tile and list view: chevron, name, count, and (collapsed) an online
/// summary. <see cref="Group"/> null is the "Ohne Gruppe" section of ungrouped connections.
/// </summary>
public sealed class GroupHeaderItem(ConnectionGroup? group, int depth) : INotifyPropertyChanged
{
    private int count;
    private int online;
    private int offline;

    public ConnectionGroup? Group { get; } = group;
    public int Depth { get; } = depth;
    public string? GroupId => Group?.Id;

    public string Title => Group?.Name ?? Strings.Group_None;
    public Thickness Indent => new(Depth * 22, 0, 0, 0);
    public bool IsCollapsed => Group?.IsCollapsed ?? false;
    public bool CanCollapse => Group is not null;
    public bool ShowExpanded => CanCollapse && !IsCollapsed;
    public bool ShowCollapsed => CanCollapse && IsCollapsed;

    public string CountLabel => count.ToString(CultureInfo.CurrentCulture);

    /// <summary>Shown while collapsed: "5 online, 2 offline".</summary>
    public string Summary => IsCollapsed
        ? string.Format(CultureInfo.CurrentCulture, Strings.GroupHeader_Summary, online, offline)
        : "";

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Connections in the group and its subgroups that pass the current filter.</summary>
    public void SetCounts(int total, int onlineCount, int offlineCount)
    {
        count = total;
        online = onlineCount;
        offline = offlineCount;
        Changed();
    }

    public void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
