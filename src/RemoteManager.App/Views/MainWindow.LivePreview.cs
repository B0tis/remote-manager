using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Connections;
using RemoteManager.App.Resources;
using RemoteManager.Core.Preview;

namespace RemoteManager.App.Views;

/// <summary>
/// Live preview: while an embedded RDP or VNC session is connected, its tile and the detail panel show a
/// picture refreshed every few seconds ("Live" badge with the age). The interval is set in the "…" menu
/// (setting <see cref="LivePreviewScheduler.IntervalSetting"/>); each connection can opt out in its settings.
/// When the session closes, its final snapshot stays as before.
/// </summary>
public partial class MainWindow
{
    // How often the scheduler looks for due sessions and the badges update their age.
    private static readonly TimeSpan LivePreviewTick = TimeSpan.FromSeconds(5);

    private readonly LivePreviewScheduler livePreview = new();
    private readonly DispatcherTimer livePreviewTimer = new() { Interval = LivePreviewTick };

    internal LivePreviewScheduler LivePreview => livePreview;

    /// <summary>Sets and saves the interval; <see cref="TimeSpan.Zero"/> turns live previews off.</summary>
    internal void SetLivePreviewInterval(TimeSpan interval)
    {
        livePreview.Interval = interval;
        app.Library.SetSetting(LivePreviewScheduler.IntervalSetting, LivePreviewScheduler.FormatInterval(interval));
        UpdateLiveBadges();
    }

    /// <summary>Captures every due session and updates the badges (the timer calls it; the self-test too).</summary>
    internal async Task TickLivePreviewAsync()
    {
        await livePreview.TickAsync(DateTimeOffset.Now);
        UpdateLiveBadges();
    }

    private void InitializeLivePreview()
    {
        livePreview.Interval = LivePreviewScheduler.ParseInterval(app.Library.GetSetting(LivePreviewScheduler.IntervalSetting));
        livePreview.Captured += OnLivePreviewCaptured;
        livePreviewTimer.Tick += async (_, _) => await TickLivePreviewAsync();
        livePreviewTimer.Start();
        Closed += (_, _) => livePreviewTimer.Stop();
    }

    private MenuItem CreateLivePreviewMenu()
    {
        var parent = new MenuItem { Header = Strings.LivePreview_Menu, Icon = new FASymbolIcon { Symbol = FASymbol.Camera } };
        var choices = new List<MenuItem>();
        foreach (TimeSpan interval in LivePreviewScheduler.Choices)
        {
            var item = new MenuItem
            {
                Header = DescribeInterval(interval),
                ToggleType = MenuItemToggleType.Radio,
                GroupName = "livePreview",
                Tag = interval,
            };
            item.Click += (_, _) => SetLivePreviewInterval(interval);
            choices.Add(item);
        }

        parent.ItemsSource = choices;
        // The interval may change elsewhere (self-test); mark the current one whenever the menu opens.
        parent.SubmenuOpened += (_, _) =>
        {
            foreach (MenuItem item in choices)
                item.IsChecked = (TimeSpan)item.Tag! == livePreview.Interval;
        };
        return parent;
    }

    private static string DescribeInterval(TimeSpan interval) =>
        interval == TimeSpan.Zero ? Strings.LivePreview_Off
        : interval < TimeSpan.FromMinutes(1) ? string.Format(CultureInfo.CurrentCulture, Strings.LivePreview_EverySeconds, (int)interval.TotalSeconds)
        : string.Format(CultureInfo.CurrentCulture, Strings.LivePreview_EveryMinutes, (int)interval.TotalMinutes);

    /// <summary>Registers an opened session window; it stops being captured when it closes.</summary>
    private void TrackLivePreview(Window window, ConnectionItem connection)
    {
        if (window is not ILivePreviewSource source)
            return;

        livePreview.Add(source);
        window.Closed += (_, _) =>
        {
            livePreview.Remove(source);
            connection.LivePreviewAt = null;
        };
    }

    private void OnLivePreviewCaptured(ILivePreviewSource source, DateTimeOffset at)
    {
        ConnectionItem connection = ((ISessionWindow)source).Connection;
        if (connection.Model.PreviewPath is null)
        {
            connection.Model.PreviewPath = Path.GetFileName(connection.Model.ResolvedPreviewPath);
            Save(connection);
        }

        connection.ReloadPreview();
        connection.LivePreviewAt = at;
    }

    /// <summary>Badge and age per open session (also hides it after a disconnect, an opt-out, or "Aus").</summary>
    private void UpdateLiveBadges()
    {
        foreach (Window window in sessionWindows)
        {
            if (window is ILivePreviewSource source)
                ((ISessionWindow)window).Connection.LivePreviewAt = livePreview.LiveAt(source);
        }
    }
}
