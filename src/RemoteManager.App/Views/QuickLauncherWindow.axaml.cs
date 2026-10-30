using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using RemoteManager.App.Resources;
using RemoteManager.App.Services;
using RemoteManager.Core.History;
using RemoteManager.Core.Search;

namespace RemoteManager.App.Views;

/// <summary>
/// The quick launcher: a small search window opened by the global hotkey. Type, Enter connects;
/// Up/Down choose, Ctrl+Enter shows the connection in the overview, Esc closes. Results are ranked by
/// fuzzy match and usage (history). The window is hidden, not closed, between uses.
/// </summary>
public partial class QuickLauncherWindow : Window
{
    private const int MaxResults = 8;

    private MainWindow? host;
    private IReadOnlyDictionary<string, UsageStats> usage = new Dictionary<string, UsageStats>();
    private bool recording;
    private bool allowClose;

    public QuickLauncherWindow()
    {
        InitializeComponent();
        QueryBox.TextChanged += (_, _) => UpdateResults();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        ResultList.DoubleTapped += (_, _) => Launch(reveal: false);
        ChangeHotkeyButton.Click += (_, _) => StartRecording();
        Deactivated += (_, _) =>
        {
            if (!recording)
                Hide();
        };
    }

    public IReadOnlyList<ConnectionItem> Results => ResultList.ItemsSource as IReadOnlyList<ConnectionItem> ?? [];
    public ConnectionItem? SelectedResult => ResultList.SelectedItem as ConnectionItem;
    public bool IsRecordingHotkey => recording;

    public string Query
    {
        get => QueryBox.Text ?? "";
        set => QueryBox.Text = value;
    }

    /// <summary>Shows the launcher centered in the upper part of the screen the overview is on, with an empty query.</summary>
    internal void ShowFor(MainWindow main)
    {
        host = main;
        recording = false;
        usage = main.App.History.Usage(DateTime.UtcNow.AddDays(-90));
        UpdateHotkeyText();
        QueryBox.Text = "";
        UpdateResults();

        Screen? screen = main.Screens.ScreenFromWindow(main) ?? main.Screens.Primary;
        if (screen is not null)
        {
            PixelRect area = screen.WorkingArea;
            int width = (int)(Width * screen.Scaling);
            Position = new PixelPoint(area.X + (area.Width - width) / 2, area.Y + area.Height / 5);
        }

        Show();
        Activate();
        QueryBox.Focus();
    }

    /// <summary>Connects the selected result (or shows it in the overview with <paramref name="reveal"/>).</summary>
    public void Launch(bool reveal)
    {
        if (host is null || SelectedResult is not { } item)
            return;
        Hide();
        if (reveal)
            host.Reveal(item);
        else
            host.LaunchFromLauncher(item);
    }

    /// <summary>Closes the window for real (the app is ending); otherwise closing only hides it.</summary>
    internal void CloseForGood()
    {
        allowClose = true;
        Close();
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
        DateTime now = DateTime.UtcNow;
        List<ConnectionItem> results = [.. FuzzySearch.Rank(Query, host.Connections.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase),
            c => c.SearchFields, c => FuzzySearch.UsageBonus(usage.GetValueOrDefault(c.Id), now)).Take(MaxResults)];
        ResultList.ItemsSource = results;
        ResultList.SelectedIndex = results.Count > 0 ? 0 : -1;
        ResultList.IsVisible = results.Count > 0;
        NoResults.IsVisible = results.Count == 0;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (recording)
        {
            RecordHotkey(e);
            return;
        }

        int count = Results.Count;
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
            case Key.Down when count > 0:
                ResultList.SelectedIndex = (ResultList.SelectedIndex + 1) % count;
                ResultList.ScrollIntoView(ResultList.SelectedIndex);
                e.Handled = true;
                break;
            case Key.Up when count > 0:
                ResultList.SelectedIndex = (ResultList.SelectedIndex - 1 + count) % count;
                ResultList.ScrollIntoView(ResultList.SelectedIndex);
                e.Handled = true;
                break;
        }

        // Keep typing in the search box even after clicking a result.
        if (!e.Handled && !QueryBox.IsFocused)
            QueryBox.Focus();
    }

    private void StartRecording()
    {
        recording = true;
        HotkeyText.Text = Strings.Launcher_PressHotkey;
        ChangeHotkeyButton.IsEnabled = false;
        Focus();
    }

    private void RecordHotkey(KeyEventArgs e)
    {
        e.Handled = true;
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        recording = false;
        ChangeHotkeyButton.IsEnabled = true;
        if (e.Key == Key.Escape || host is null)
        {
            UpdateHotkeyText();
            QueryBox.Focus();
            return;
        }

        var gesture = new KeyGesture(e.Key, e.KeyModifiers);
        if (!GlobalHotkey.IsValid(gesture))
            HotkeyText.Text = Strings.Launcher_HotkeyInvalid;
        else if (!host.TrySetHotkey(gesture))
            HotkeyText.Text = string.Format(CultureInfo.CurrentCulture, Strings.Launcher_HotkeyTaken, GlobalHotkey.Format(gesture));
        else
            UpdateHotkeyText();
        QueryBox.Focus();
    }

    private void UpdateHotkeyText() =>
        HotkeyText.Text = host?.Hotkey?.Gesture is { } gesture
            ? string.Format(CultureInfo.CurrentCulture, Strings.Launcher_HotkeyLabel, GlobalHotkey.Format(gesture))
            : Strings.Launcher_HotkeyNone;
}
