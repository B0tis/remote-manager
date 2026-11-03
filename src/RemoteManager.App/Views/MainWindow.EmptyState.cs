using System.Globalization;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Resources;

namespace RemoteManager.App.Views;

/// <summary>What the overview shows when nothing is visible: why, and the obvious next step.</summary>
public partial class MainWindow
{
    /// <summary>The kinds of empty overview (self-test).</summary>
    public enum EmptyKind { None, NoConnections, NoMatches, EmptyGroup, NoFavorites, NoRecent, Empty }

    private Action? emptyPrimary, emptySecondary;

    /// <summary>The empty state currently shown (None when there are connections to show).</summary>
    public EmptyKind CurrentEmptyKind { get; private set; }

    private void InitializeEmptyState()
    {
        EmptyPrimaryButton.Click += (_, _) => emptyPrimary?.Invoke();
        EmptySecondaryButton.Click += (_, _) => emptySecondary?.Invoke();
    }

    private sealed record EmptyContent(FASymbol Icon, string Title, string Message,
        string? Primary = null, Action? OnPrimary = null, string? Secondary = null, Action? OnSecondary = null);

    private void UpdateEmptyState()
    {
        string search = SearchBox.Text?.Trim() ?? "";
        EmptyKind kind = visible.Count > 0 ? EmptyKind.None
            : items.Count == 0 ? EmptyKind.NoConnections
            : search.Length > 0 ? EmptyKind.NoMatches
            : CurrentGroupId is not null ? EmptyKind.EmptyGroup
            : filter == "fav" ? EmptyKind.NoFavorites
            : filter == "recent" ? EmptyKind.NoRecent
            : EmptyKind.Empty;
        CurrentEmptyKind = kind;
        EmptyState.IsVisible = kind != EmptyKind.None;
        if (kind == EmptyKind.None)
            return;

        string? groupId = CurrentGroupId;
        EmptyContent content = kind switch
        {
            EmptyKind.NoConnections => new EmptyContent(FASymbol.Library, Strings.Empty_NoConnections_Title, Strings.Empty_NoConnections_Message,
                Strings.Action_New, () => AddConnection(), Strings.Action_Import,
                () => _ = ImportExportCommands.ShowImportAsync(this)),
            EmptyKind.NoMatches => new EmptyContent(FASymbol.Find, string.Format(CultureInfo.CurrentCulture, Strings.Empty_NoMatches_Title, search),
                Strings.Empty_NoMatches_Message, Strings.Empty_ClearSearch, () => SearchBox.Text = "", null, null),
            EmptyKind.EmptyGroup => new EmptyContent(FASymbol.Folder, Strings.Empty_Group_Title, Strings.Empty_Group_Message,
                Strings.Action_NewConnectionHere, () => AddConnection(groupId), null, null),
            EmptyKind.NoFavorites => new EmptyContent(FASymbol.Star, Strings.Empty_Favorites_Title, Strings.Empty_Favorites_Message),
            EmptyKind.NoRecent => new EmptyContent(FASymbol.Clock, Strings.Empty_Recent_Title, Strings.Empty_Recent_Message),
            _ => new EmptyContent(FASymbol.Find, Strings.Empty_NoMatch, ""),
        };
        (FASymbol icon, string title, string message, string? primary, Action? onPrimary, string? secondary, Action? onSecondary) = content;

        EmptyIcon.Symbol = icon;
        EmptyTitle.Text = title;
        EmptyMessage.Text = message;
        EmptyMessage.IsVisible = message.Length > 0;
        emptyPrimary = onPrimary;
        emptySecondary = onSecondary;
        EmptyPrimaryButton.Content = primary;
        EmptyPrimaryButton.IsVisible = primary is not null;
        EmptySecondaryButton.Content = secondary;
        EmptySecondaryButton.IsVisible = secondary is not null;
    }
}
