using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.VisualTree;
using RemoteManager.App.Resources;
using RemoteManager.App.Services;
using RemoteManager.Core.History;
using RemoteManager.Core.Search;

namespace RemoteManager.App.Views;

/// <summary>
/// The quick launcher: a command palette opened by the global hotkey or Ctrl+K. One field finds saved
/// connections (fuzzy, ranked by usage), app commands ("Aktionen", alone after "&gt;"), and reads a typed
/// address as a target to connect to once without saving it. With nothing typed it lists what was used
/// last, then the favorites. Up/Down choose (wrapping), Tab jumps to the next section, Enter runs the
/// selected row, Ctrl+Enter does its second action (show a connection in the overview, save a typed
/// address as a connection), Esc closes. The window is hidden, not closed, between uses.
/// </summary>
public partial class QuickLauncherWindow : Window
{
    /// <summary>In front of the text, only actions are searched; alone it lists all of them.</summary>
    public const char CommandPrefix = '>';

    private const int MaxConnections = 8;
    private const int MaxRecent = 5;
    private const int MaxFavorites = 5;

    /// <summary>Actions shown below connections; after <see cref="CommandPrefix"/> there is no limit.</summary>
    private const int MaxCommands = 4;

    /// <summary>
    /// The bar for an action to match (<see cref="FuzzySearch.Rank"/>): prefixes, initials ("nv"), and a
    /// skipped letter pass, single letters scattered over a label do not.
    /// </summary>
    private const int CommandMatchScore = 25;

    /// <summary>
    /// The bar for a connection: far lower, so "dc1" finds "dc01" and "stgam" finds "Selftest Gamma", but
    /// letters spread over a long description do not count as a hit.
    /// </summary>
    private const int ConnectionMatchScore = 10;

    private const double MaxListHeight = 440;

    /// <summary>Search row, footer, and the list's margins: what the card needs besides the list.</summary>
    private const double ChromeHeight = 122;

    private MainWindow? host;
    private IReadOnlyDictionary<string, UsageStats> usage = new Dictionary<string, UsageStats>();
    private List<object> rows = [];
    private List<ConnectionItem> recent = [];
    private List<ConnectionItem> favorites = [];
    private List<ConnectionItem> connections = [];
    private List<LauncherCommand> commands = [];
    private List<LauncherTarget> targets = [];
    private bool recording;
    private bool allowClose;

    public QuickLauncherWindow()
    {
        InitializeComponent();
        QueryBox.TextChanged += (_, _) => UpdateResults();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        ResultList.ContainerPrepared += (_, e) => e.Container.Classes.Set("section", e.Container is ContentControl { Content: LauncherSection });
        ResultList.SelectionChanged += (_, _) => UpdateHints();
        ResultList.DoubleTapped += (_, e) =>
        {
            if (IsInRow(e.Source))
                Launch(reveal: false);
        };
        // A click selects a row; the next key should still land in the search field.
        ResultList.PointerReleased += (_, _) => QueryBox.Focus();
        // The transparent margin around the card belongs to the window, but reads as "outside".
        Backdrop.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.Source, Backdrop) && !recording)
                Hide();
        };
        ChangeHotkeyButton.Click += (_, _) => ShowHotkeySettings();
        ResetHotkeyButton.Click += (_, _) => ApplyHotkey(GlobalHotkey.Default);
        HotkeyDoneButton.Click += (_, _) => ShowSearch();
        Deactivated += (_, _) =>
        {
            if (!recording)
                Hide();
        };
    }

    /// <summary>The connections listed, top to bottom: the matches of a query, or recent then favorites for an empty one.</summary>
    public IReadOnlyList<ConnectionItem> Results => connections;

    /// <summary>"Zuletzt verwendet" of an empty query, most recent first.</summary>
    public IReadOnlyList<ConnectionItem> Recent => recent;

    /// <summary>"Favoriten" of an empty query: the favorites that are not already under <see cref="Recent"/>.</summary>
    public IReadOnlyList<ConnectionItem> Favorites => favorites;

    /// <summary>The actions listed for the query.</summary>
    public IReadOnlyList<LauncherCommand> Commands => commands;

    /// <summary>The "Direkt verbinden" rows for a typed address, one per offered type.</summary>
    public IReadOnlyList<LauncherTarget> AdHocTargets => targets;

    /// <summary>The section labels shown, top to bottom.</summary>
    public IReadOnlyList<string> Sections => [.. rows.OfType<LauncherSection>().Select(s => s.Title)];

    public ConnectionItem? SelectedResult => ResultList.SelectedItem as ConnectionItem;

    /// <summary>The selected row: a <see cref="ConnectionItem"/>, <see cref="LauncherCommand"/>, or <see cref="LauncherTarget"/>.</summary>
    public object? SelectedRow
    {
        get => ResultList.SelectedItem;
        set
        {
            if (value is not null and not LauncherSection && rows.Contains(value))
                ResultList.SelectedItem = value;
        }
    }

    /// <summary>The hotkey view waits for a key combination.</summary>
    public bool IsRecordingHotkey => recording;

    /// <summary>The hotkey view is shown instead of the search.</summary>
    public bool IsHotkeyViewShown => HotkeyView.IsVisible;

    /// <summary>What the hotkey view says about the last attempt.</summary>
    public string HotkeyMessage => HotkeyNoteTitle.Text ?? "";

    public string Query
    {
        get => QueryBox.Text ?? "";
        set
        {
            QueryBox.Text = value;
            // As if typed: the caret ends up behind the text.
            QueryBox.CaretIndex = value.Length;
        }
    }

    /// <summary>Shows the launcher centered in the upper part of the screen the overview is on, with an empty query.</summary>
    internal void ShowFor(MainWindow main)
    {
        host = main;
        usage = main.App.History.Usage(DateTime.UtcNow.AddDays(-90));
        ShowSearch();
        QueryBox.Text = "";
        UpdateResults();

        Screen? screen = main.Screens.ScreenFromWindow(main) ?? main.Screens.Primary;
        if (screen is not null)
        {
            PixelRect area = screen.WorkingArea;
            double scale = screen.Scaling;
            int top = area.Height / 5;
            // The window is wider and taller than the card by the shadow margin: place the card, not the window.
            Position = new PixelPoint(area.X + (area.Width - (int)(Width * scale)) / 2, area.Y + top - (int)(Card.Margin.Top * scale));
            // On a small screen the list gives way, so the footer stays on it.
            ResultList.MaxHeight = Math.Clamp(((area.Height - top) / scale) - ChromeHeight - 24, 160, MaxListHeight);
        }

        Show();
        Activate();
        QueryBox.Focus();
    }

    /// <summary>
    /// Runs the selected row and closes the launcher. A connection is opened (shown in the overview with
    /// <paramref name="reveal"/>), an action runs, a typed address gets a one-time session (with
    /// <paramref name="reveal"/>: the editor for a new connection, filled with it).
    /// </summary>
    public void Launch(bool reveal)
    {
        if (host is null)
            return;

        switch (ResultList.SelectedItem)
        {
            case ConnectionItem connection:
                Hide();
                if (reveal)
                    host.Reveal(connection);
                else
                    host.LaunchFromLauncher(connection);
                break;
            case LauncherTarget target:
                Hide();
                if (reveal)
                    host.AddConnectionFor(target.Target, target.Type);
                else
                    host.ConnectDirect(target.Target, target.Type);
                break;
            case LauncherCommand command:
                if (!command.KeepsLauncherOpen)
                    Hide();
                host.RunLauncherCommand(command);
                break;
        }
    }

    /// <summary>Selects the next (or previous) row, wrapping at the ends. Section labels are skipped.</summary>
    public void MoveSelection(int delta)
    {
        List<object> selectable = [.. rows.Where(r => r is not LauncherSection)];
        if (selectable.Count == 0)
            return;
        int index = ResultList.SelectedItem is { } selected ? selectable.IndexOf(selected) : -1;
        int next = index < 0 ? (delta > 0 ? 0 : selectable.Count - 1) : ((index + delta) % selectable.Count + selectable.Count) % selectable.Count;
        SelectRow(selectable[next]);
    }

    /// <summary>Selects the first row of the next (or previous) section, wrapping at the ends.</summary>
    public void MoveSection(int delta)
    {
        // The first row of every section.
        List<object> starts = [];
        for (int i = 1; i < rows.Count; i++)
        {
            if (rows[i - 1] is LauncherSection && rows[i] is not LauncherSection)
                starts.Add(rows[i]);
        }

        if (starts.Count == 0)
            return;
        int selected = ResultList.SelectedItem is { } row ? rows.IndexOf(row) : -1;
        int current = starts.FindLastIndex(s => rows.IndexOf(s) <= selected);
        int next = current < 0 ? 0 : ((current + delta) % starts.Count + starts.Count) % starts.Count;
        SelectRow(starts[next]);
    }

    /// <summary>Shows the hotkey view and waits for the new combination ("Ändern", or the action "Globalen Hotkey ändern").</summary>
    public void ShowHotkeySettings()
    {
        SearchView.IsVisible = false;
        HotkeyView.IsVisible = true;
        recording = true;
        ShowHotkeyNote(Strings.Launcher_PressHotkey, Strings.Launcher_Hotkey_Rules, warning: false);
        UpdateHotkeyView();
        // Keys now go to the window: the search field is hidden.
        Focus();
    }

    /// <summary>Closes the window for real (the app is ending); otherwise closing only hides it.</summary>
    internal void CloseForGood()
    {
        allowClose = true;
        Close();
    }

    /// <summary>
    /// Tries <paramref name="gesture"/> as the global hotkey. An unusable or refused combination leaves the
    /// current hotkey as it is and keeps the view waiting, with the reason; an accepted one is saved.
    /// </summary>
    internal void ApplyHotkey(KeyGesture gesture)
    {
        if (host is null)
            return;

        if (!GlobalHotkey.IsValid(gesture))
        {
            ShowHotkeyNote(Strings.Launcher_HotkeyInvalid, "", warning: true);
            return;
        }

        string name = GlobalHotkey.Format(gesture);
        KeyGesture? before = host.Hotkey?.Gesture;
        if (host.TrySetHotkey(gesture))
        {
            recording = false;
            ShowHotkeyNote(string.Format(CultureInfo.CurrentCulture, Strings.Launcher_Hotkey_Saved, name), "", warning: false);
        }
        else
        {
            // TrySetHotkey registered the previous combination again.
            ShowHotkeyNote(before is null
                    ? string.Format(CultureInfo.CurrentCulture, Strings.Launcher_Hotkey_RefusedNone, name)
                    : string.Format(CultureInfo.CurrentCulture, Strings.Launcher_Hotkey_Refused, name, GlobalHotkey.Format(before)),
                "", warning: true);
        }

        UpdateHotkeyView();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!allowClose)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    private void UpdateResults()
    {
        if (host is null)
            return;

        string query = Query.Trim();
        recent = [];
        favorites = [];
        connections = [];
        commands = [];
        targets = [];
        rows = [];

        if (query.StartsWith(CommandPrefix))
        {
            string term = query[1..].Trim();
            commands = term.Length == 0 ? [.. host.LauncherCommands] : MatchCommands(term, int.MaxValue);
        }
        else if (query.Length == 0)
        {
            recent = [.. host.Connections.Select(c => (Item: c, At: LastUsed(c))).Where(c => c.At is not null)
                .OrderByDescending(c => c.At).ThenBy(c => c.Item.Name, StringComparer.CurrentCultureIgnoreCase).Take(MaxRecent).Select(c => c.Item)];
            favorites = [.. host.Connections.Where(c => c.IsFavorite && !recent.Contains(c))
                .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).Take(MaxFavorites)];
            connections = [.. recent, .. favorites];
        }
        else
        {
            DateTime now = DateTime.UtcNow;
            connections = [.. FuzzySearch.Rank(query, host.Connections.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase),
                c => c.SearchFields, c => FuzzySearch.UsageBonus(usage.GetValueOrDefault(c.Id), now), ConnectionMatchScore).Take(MaxConnections)];
            commands = MatchCommands(query, MaxCommands);

            // A single word is more likely a search than a host: it is only offered when nothing else was found.
            if (AdHocTarget.Parse(query) is { } target && (target.IsExplicit || (connections.Count == 0 && commands.Count == 0)))
                targets = [.. target.SuggestedTypes.Select(type => new LauncherTarget(target, type, host.App.Handlers[type]))];
        }

        if (query.Length == 0)
        {
            AddSection(Strings.Nav_Recent, recent);
            AddSection(Strings.Nav_Favorites, favorites);
        }
        else if (targets.Count > 0 && !connections.Any(c => string.Equals(c.Model.Host, targets[0].Target.Host, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(c.Name, query, StringComparison.CurrentCultureIgnoreCase)))
        {
            // An address that is no saved connection: what was typed comes first, so Enter connects to exactly that.
            AddSection(Strings.Launcher_Section_Direct, targets);
            AddSection(Strings.Launcher_Section_Connections, connections);
        }
        else
        {
            AddSection(Strings.Launcher_Section_Connections, connections);
            AddSection(Strings.Launcher_Section_Direct, targets);
        }

        AddSection(Strings.Launcher_Section_Commands, commands);

        ResultList.ItemsSource = rows;
        ResultList.SelectedItem = rows.FirstOrDefault(r => r is not LauncherSection);
        ResultList.IsVisible = rows.Count > 0;
        EmptyPanel.IsVisible = rows.Count == 0;
        if (rows.Count > 0)
        {
            ResultList.ScrollIntoView(0);
        }
        else if (query.Length == 0)
        {
            EmptyTitle.Text = Strings.Launcher_Empty_Title;
            EmptyText.Text = Strings.Launcher_Empty_Text;
        }
        else if (query.StartsWith(CommandPrefix))
        {
            EmptyTitle.Text = string.Format(CultureInfo.CurrentCulture, Strings.Launcher_NoCommand_Title, query[1..].Trim());
            EmptyText.Text = Strings.Launcher_NoCommand_Text;
        }
        else
        {
            EmptyTitle.Text = string.Format(CultureInfo.CurrentCulture, Strings.Launcher_NoMatch_Title, query);
            EmptyText.Text = Strings.Launcher_NoMatch_Text;
        }

        UpdateHints();
    }

    private void AddSection<T>(string title, IReadOnlyList<T> items)
        where T : class
    {
        if (items.Count == 0)
            return;
        rows.Add(new LauncherSection(title));
        rows.AddRange(items);
    }

    private List<LauncherCommand> MatchCommands(string term, int limit) =>
        [.. FuzzySearch.Rank(term, host!.LauncherCommands, c => c.SearchFields, minimumScorePerLetter: CommandMatchScore).Take(limit)];

    /// <summary>The later of the last history entry and the connection's own "last connected".</summary>
    private DateTime? LastUsed(ConnectionItem connection)
    {
        DateTime? fromHistory = usage.TryGetValue(connection.Id, out UsageStats? stats) ? stats.LastUsed : null;
        DateTime? fromModel = connection.Model.LastConnectedAt?.ToUniversalTime();
        return fromHistory is null || fromModel > fromHistory ? fromModel ?? fromHistory : fromHistory;
    }

    private void SelectRow(object row)
    {
        ResultList.SelectedItem = row;
        // Bring the section label above a first row along, so the list does not look cut off.
        int index = rows.IndexOf(row);
        if (index > 0 && rows[index - 1] is LauncherSection)
            ResultList.ScrollIntoView(index - 1);
        ResultList.ScrollIntoView(index);
    }

    /// <summary>The footer names what Enter and Ctrl+Enter do with the selected row.</summary>
    private void UpdateHints()
    {
        (string? primary, string? secondary) = ResultList.SelectedItem switch
        {
            ConnectionItem => (Strings.Launcher_Hint_Connect, Strings.Launcher_Hint_Reveal),
            LauncherTarget => (Strings.Launcher_Hint_ConnectOnce, Strings.Launcher_Hint_SaveAs),
            LauncherCommand => (Strings.Launcher_Hint_Run, null),
            _ => ((string?)null, (string?)null),
        };
        PrimaryHint.IsVisible = primary is not null;
        PrimaryHintText.Text = primary;
        SecondaryHint.IsVisible = secondary is not null;
        SecondaryHintText.Text = secondary;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (HotkeyView.IsVisible)
        {
            OnHotkeyViewKey(e);
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                Hide();
                e.Handled = true;
                break;
            case Key.Enter:
                Launch(reveal: e.KeyModifiers.HasFlag(KeyModifiers.Control));
                e.Handled = true;
                break;
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Tab:
                // Also when there is nowhere to go: the focus stays in the search field.
                MoveSection(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                e.Handled = true;
                break;
        }

        // Keep typing in the search box even after clicking a result.
        if (!e.Handled && !QueryBox.IsFocused)
            QueryBox.Focus();
    }

    private void OnHotkeyViewKey(KeyEventArgs e)
    {
        e.Handled = true;
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        if (!recording)
        {
            // The new hotkey is saved: any closing key goes back to the search.
            if (e.Key is Key.Escape or Key.Enter)
                ShowSearch();
        }
        else if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            ShowSearch();
        }
        else
        {
            ApplyHotkey(new KeyGesture(e.Key, e.KeyModifiers));
        }
    }

    private void ShowSearch()
    {
        recording = false;
        HotkeyView.IsVisible = false;
        SearchView.IsVisible = true;
        UpdateHotkeyFooter();
        QueryBox.Focus();
    }

    private void ShowHotkeyNote(string title, string text, bool warning)
    {
        HotkeyNoteTitle.Text = title;
        HotkeyNoteText.Text = text;
        HotkeyNoteText.IsVisible = text.Length > 0;
        HotkeyNote.Classes.Set("warning", warning);
    }

    private void UpdateHotkeyView()
    {
        KeyGesture? gesture = host?.Hotkey?.Gesture;
        FillKeys(CurrentHotkeyKeys, gesture, large: true);
        CurrentHotkeyNone.IsVisible = gesture is null;
        ResetHotkeyButton.Content = string.Format(CultureInfo.CurrentCulture, Strings.Launcher_Hotkey_Reset, GlobalHotkey.Format(GlobalHotkey.Default));
        ResetHotkeyButton.IsVisible = !GlobalHotkey.Default.Equals(gesture);
        HotkeyDoneButton.Content = recording ? Strings.Action_Cancel : Strings.Launcher_Hotkey_Done;
    }

    private void UpdateHotkeyFooter()
    {
        KeyGesture? gesture = host?.Hotkey?.Gesture;
        HotkeyText.Text = gesture is null ? Strings.Launcher_HotkeyNone : Strings.Launcher_HotkeyLabel;
        FillKeys(HotkeyKeys, gesture, large: false);
        ChangeHotkeyButton.Content = gesture is null ? Strings.Launcher_Hotkey_Set : Strings.Launcher_ChangeHotkey;
    }

    /// <summary>One key cap per key of the combination.</summary>
    private static void FillKeys(StackPanel panel, KeyGesture? gesture, bool large)
    {
        panel.Children.Clear();
        panel.IsVisible = gesture is not null;
        if (gesture is null)
            return;

        foreach (string key in GlobalHotkey.Format(gesture).Split('+'))
        {
            var cap = new Border { Child = new TextBlock { Text = key } };
            cap.Classes.Add("keycap");
            if (large)
                cap.Classes.Add("large");
            panel.Children.Add(cap);
        }
    }

    /// <summary>The event came from inside a row (not from the list's margin or its scroll bar).</summary>
    private static bool IsInRow(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is ListBoxItem)
                return true;
        }

        return false;
    }
}
