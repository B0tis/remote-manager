using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using RemoteManager.App.Connections;
using RemoteManager.App.Resources;
using RemoteManager.Core.Credentials;
using RemoteManager.Core.History;

namespace RemoteManager.App.Views;

/// <summary>
/// The right-hand column of the overview: preview, actions, host, logon (with inheritance), group,
/// status, start parameters, editable notes, and history of the selected connection; for several
/// selected connections the bulk actions.
/// </summary>
public partial class ConnectionDetailPanel : UserControl
{
    private MainWindow? host;
    private ConnectionItem? shown;
    private IReadOnlyList<ConnectionItem> selection = [];

    public ConnectionDetailPanel()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => host?.SetDetailsVisible(false);
        ConnectButton.Click += (_, _) => Act(c => host!.OpenSession(c));
        EditButton.Click += (_, _) => Act(c => host!.EditConnection(c));
        EditParametersButton.Click += (_, _) => Act(c => host!.EditConnection(c));
        CopyPasswordButton.Click += async (_, _) =>
        {
            if (host is not null && shown is not null)
                await host.CopyPasswordAsync(shown);
        };
        FavoriteButton.Click += (_, _) => Act(c => host!.SetFavorite([c], !c.IsFavorite));
        NotesBox.LostFocus += (_, _) => SaveNotes();

        ConnectAllButton.Click += (_, _) => host?.OpenAll(selection);
        FavoriteAllButton.Click += (_, _) => host?.SetFavorite(selection, !selection.All(c => c.IsFavorite));
        MoveAllButton.Click += (_, _) => host?.MoveToGroupAsync(selection);
        DeleteAllButton.Click += (_, _) => host?.DeleteConnections(selection);
    }

    public ConnectionItem? Shown => shown;
    public bool IsMultiple => MultiView.IsVisible;
    public string NameShown => NameText.Text ?? "";
    public IReadOnlyList<string> HistoryLines => HistoryList.ItemsSource as IReadOnlyList<string> ?? [];
    public IReadOnlyList<string> ParameterLines => ParameterList.ItemsSource as IReadOnlyList<string> ?? [];
    public TextBox Notes => NotesBox;
    public bool ShowsLiveBadge => LiveBadge.IsVisible;
    public string LiveAge => LiveAgeText.Text ?? "";

    internal void Attach(MainWindow window) => host = window;

    /// <summary>Shows the selection: nothing, one connection, or the bulk actions for several.</summary>
    public void Show(IReadOnlyList<ConnectionItem> selected)
    {
        SaveNotes();
        if (shown is not null)
            shown.PropertyChanged -= OnShownChanged;
        selection = selected;
        shown = selected.Count == 1 ? selected[0] : null;
        if (shown is not null)
            shown.PropertyChanged += OnShownChanged;

        EmptyView.IsVisible = selected.Count == 0;
        SingleView.IsVisible = shown is not null;
        MultiView.IsVisible = selected.Count > 1;
        if (shown is not null)
            ShowSingle(shown, reloadNotes: true);
        else if (selected.Count > 1)
            ShowMultiple();
    }

    /// <summary>Re-reads the shown connection (after an edit, a status result, a new history entry).</summary>
    public void Refresh()
    {
        if (shown is not null)
            ShowSingle(shown, reloadNotes: !NotesBox.IsFocused);
        else if (selection.Count > 1)
            ShowMultiple();
    }

    private void ShowSingle(ConnectionItem c, bool reloadNotes)
    {
        AppHost app = AppHost.Current;
        PreviewBackground.Background = c.TypeBrush;
        PreviewIcon.Background = c.TypeBrush;
        PreviewInitials.Text = c.TypeInitials;
        PreviewImage.Source = c.Preview;
        PreviewImage.IsVisible = c.HasPreview;
        PreviewIcon.IsVisible = !c.HasPreview;
        UpdateLiveBadge(c);

        NameText.Text = c.Name;
        FavoriteOn.IsVisible = c.IsFavorite;
        FavoriteOff.IsVisible = !c.IsFavorite;
        ToolTip.SetTip(FavoriteButton, c.IsFavorite ? Strings.Action_Unfavorite : Strings.Action_Favorite);
        SubtitleText.Text = c.Description.Length > 0 ? $"{c.TypeLabel} · {c.Description}" : c.TypeLabel;
        TagList.ItemsSource = c.Model.Tags.Select(t => $"#{t}").ToList();
        TagList.IsVisible = c.Model.Tags.Count > 0;
        CopyPasswordButton.IsVisible = c.HasStoredPassword;

        HostText.Text = c.Handler.HasPort ? $"{c.Model.Host} : {c.Model.EffectivePort}" : c.Model.Host;
        EffectiveCredential logon = c.Credentials;
        string user = logon.Domain.Length > 0 ? $"{logon.Domain}\\{logon.UserName}" : logon.UserName;
        if (user.Length == 0)
            user = logon.HasSecret ? Strings.Details_PasswordOnly : Strings.Details_NoLogon;
        else if (logon.HasSecret)
            user = string.Format(CultureInfo.CurrentCulture, Strings.Details_UserWithPassword, user);
        UserText.Text = logon.InheritedFrom is { } group
            ? string.Format(CultureInfo.CurrentCulture, Strings.Details_Inherited, user, app.Library.PathOf(group.Id))
            : user;
        GroupText.Text = c.GroupLabel;
        StatusDot.Fill = c.StatusBrush;
        StatusText.Text = c.StatusCheckedAt is { } at
            ? $"{c.StatusLabel} · {string.Format(CultureInfo.CurrentCulture, Strings.Status_CheckedAt, at.ToString("T", CultureInfo.CurrentCulture))}"
            : c.StatusLabel;
        ToolTip.SetTip(StatusText, c.StatusHint);
        LastConnectedText.Text = c.LastConnectedLabel;

        ParameterList.ItemsSource = StartParameters.Describe(c.Model);
        if (reloadNotes)
            NotesBox.Text = c.Model.Notes;

        IReadOnlyList<string> history = app.Library.Contains(c.Model)
            ? [.. app.History.Recent(c.Id, 10).Select(Describe)]
            : [];
        HistoryList.ItemsSource = history;
        HistoryEmpty.IsVisible = history.Count == 0;
    }

    private void ShowMultiple()
    {
        MultiTitle.Text = string.Format(CultureInfo.CurrentCulture, Strings.Details_Selected, selection.Count);
        const int shownNames = 8;
        List<string> names = [.. selection.Take(shownNames).Select(c => c.Name)];
        if (selection.Count > shownNames)
            names.Add(string.Format(CultureInfo.CurrentCulture, Strings.Details_More, selection.Count - shownNames));
        MultiList.ItemsSource = names;
        FavoriteAllText.Text = selection.All(c => c.IsFavorite) ? Strings.Action_Unfavorite : Strings.Action_Favorite;
    }

    /// <summary>One history line: "30.10.2026 14:03 · Verbunden · 1 h 12 min".</summary>
    internal static string Describe(HistoryEntry entry)
    {
        string result = entry.Result switch
        {
            HistoryResult.Launched => Strings.History_Launched,
            HistoryResult.Failed => Strings.History_Failed,
            _ => Strings.History_Connected,
        };
        string start = entry.StartedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        if (entry.Duration is not { } duration)
            return $"{start} · {result}";

        string length = duration < TimeSpan.FromMinutes(1)
            ? Strings.History_UnderAMinute
            : duration < TimeSpan.FromHours(1)
                ? string.Format(CultureInfo.CurrentCulture, Strings.History_Minutes, (int)duration.TotalMinutes)
                : string.Format(CultureInfo.CurrentCulture, Strings.History_Hours, (int)duration.TotalHours, duration.Minutes);
        return $"{start} · {result} · {length}";
    }

    private void Act(Action<ConnectionItem> action)
    {
        if (host is not null && shown is not null)
            action(shown);
    }

    private void SaveNotes()
    {
        if (host is null || shown is null)
            return;
        string notes = NotesBox.Text?.Trim() ?? "";
        if (notes == shown.Model.Notes)
            return;
        shown.Model.Notes = notes;
        host.Save(shown);
    }

    /// <summary>"Live · vor 20 s" over the preview while the open session refreshes it.</summary>
    private void UpdateLiveBadge(ConnectionItem c)
    {
        LiveBadge.IsVisible = c.IsLivePreview && c.HasPreview;
        LiveAgeText.Text = c.LivePreviewAge;
    }

    private void OnShownChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The age ticks every few seconds; that needs only the badge, not the history query of a full refresh.
        if (e.PropertyName is nameof(ConnectionItem.IsLivePreview) or nameof(ConnectionItem.LivePreviewAge))
        {
            if (sender is ConnectionItem c)
                UpdateLiveBadge(c);
            return;
        }

        Refresh();
    }
}
