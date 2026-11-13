using System.ComponentModel;
using System.Globalization;
using Avalonia;
using FluentAvalonia.UI.Controls;
using RemoteManager.Core.Model;

namespace RemoteManager.App.Views;

/// <summary>
/// One row of the sidebar: a filter of the overview ("all", "fav", "recent", "active", "group:&lt;id&gt;",
/// "tag:&lt;name&gt;") with its count, or a section label ("Gruppen", "Tags"). Groups are listed flat, indented by
/// their depth; a collapsed group hides its subgroups.
/// </summary>
public sealed class SidebarEntry : INotifyPropertyChanged
{
    public const string AllTag = "all", FavoritesTag = "fav", RecentTag = "recent", ActiveTag = "active";
    public const string GroupPrefix = "group:", TagPrefix = "tag:", HeaderPrefix = "header:";

    private int? count;
    private bool showLamp;

    /// <summary>The filter this entry selects; a section label has a "header:" tag and selects nothing.</summary>
    public required string Tag { get; init; }
    public required string Label { get; init; }
    public FASymbol Symbol { get; init; } = FASymbol.Folder;
    public int Depth { get; init; }
    public bool IsHeader { get; init; }

    /// <summary>A section label with a "+" button (new group).</summary>
    public bool CanAdd { get; init; }

    /// <summary>The group of a "group:" entry.</summary>
    public ConnectionGroup? Group { get; init; }

    /// <summary>The tag of a "tag:" entry.</summary>
    public string? TagName { get; init; }

    public bool HasChildren { get; init; }
    public bool IsExpanded { get; init; }

    /// <summary>In the compact sidebar only the icon shows; the tooltip then names the entry.</summary>
    public bool ShowTip { get; init; }

    public string? Tip => ShowTip ? Label : null;
    public Thickness Indent => new(Depth * 14, 0, 0, 0);

    /// <summary>How many connections the filter shows; null for filters where a number says nothing ("recent").</summary>
    public int? Count
    {
        get => count;
        set
        {
            if (count == value)
                return;
            count = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountLabel)));
        }
    }

    public string CountLabel => count is { } n and > 0 ? n.ToString(CultureInfo.CurrentCulture) : "";

    /// <summary>A lamp on the icon: sessions are open.</summary>
    public bool ShowLamp
    {
        get => showLamp;
        set
        {
            if (showLamp == value)
                return;
            showLamp = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowLamp)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
