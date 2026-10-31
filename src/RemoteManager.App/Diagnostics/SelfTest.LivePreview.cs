using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using RemoteManager.App.Connections;
using RemoteManager.App.Connections.Rdp;
using RemoteManager.App.Connections.Vnc;
using RemoteManager.App.Resources;
using RemoteManager.App.Views;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.Preview;

namespace RemoteManager.App.Diagnostics;

/// <summary>
/// Phase 4 live preview, driven through an embedded VNC session against <see cref="FakeVncServer"/> (there is no
/// RDP host here): the tile and detail panel show a scaled live picture with the "Live" badge, it refreshes after
/// the interval, the opt-out and "Aus" stop it, focus stays in the session, and closing keeps the final snapshot.
/// With RM_SPIKE_HOST the live RDP session checks capture, scaling, and the minimized skip as well.
/// </summary>
internal static partial class SelfTest
{
    private static readonly TimeSpan TestInterval = TimeSpan.FromSeconds(1);

    private static async Task RunLivePreviewChecksAsync(MainWindow window)
    {
        AppHost app = AppHost.Current;
        string? storedInterval = app.Library.GetSetting(LivePreviewScheduler.IntervalSetting);
        Check($"Live preview: default interval {LivePreviewScheduler.DefaultInterval.TotalSeconds} s, menu \"…\" › Live-Vorschau offers Aus to 5 min",
            LivePreviewScheduler.ParseInterval(null) == TimeSpan.FromSeconds(30) && LivePreviewMenu(window) is { Count: 5 } choices
            && choices[0].Header as string == Strings.LivePreview_Off);
        Check("Live preview: RDP and VNC settings have the per-connection opt-out, on by default",
            new RdpSettings().LivePreview && new VncSettings().LivePreview
            && new RdpSettingsEditor().LivePreview.Content as string == Strings.LivePreview_Connection
            && new VncSettingsEditor().LivePreview.Content as string == Strings.LivePreview_Connection);

        await SaveLivePreviewSettingsProbesAsync(window);

        using var server = new FakeVncServer(null);
        ConnectionItem vnc = window.CreateItem(new Connection
        {
            Id = TestId("live-vnc"), Name = "Selftest Live-Vorschau", Type = ConnectionType.Vnc, Host = "127.0.0.1", Port = server.Port,
            Description = "Selbsttest-Server, Live-Vorschau", Tags = ["selftest"],
        });
        string path = vnc.Model.ResolvedPreviewPath;
        window.ShowUnsaved(vnc, true);
        try
        {
            window.SetLivePreviewInterval(TestInterval);
            var session = (VncSessionWindow)window.OpenSession(vnc)!;
            session.StatusLogged += (_, message) => Log($"  [{session.Title}] {message}");
            bool connected = await WaitFor(() => session.IsConnected && session.Vnc.FrameCount > 0, TimeSpan.FromSeconds(15));
            session.Activate();
            await Settle(500);
            bool sessionActive = session.IsActive;

            await window.TickLivePreviewAsync();
            PixelSize expected = LivePreviewImage.ScaledSize(new PixelSize(FakeVncServer.Width, FakeVncServer.Height));
            Check($"Live preview: a connected VNC session becomes the tile picture at once, scaled to the tile ({vnc.Preview?.PixelSize} of {FakeVncServer.Width}x{FakeVncServer.Height})",
                connected && vnc.IsLivePreview && File.Exists(path) && vnc.Preview?.PixelSize == expected
                && expected.Width <= LivePreviewImage.TargetSize.Width && !File.Exists(path + ".live"));
            Check("Live preview: capturing leaves the focus in the session window",
                sessionActive && session.IsActive && !window.IsActive);

            // The tile and the detail panel show the badge with the age.
            window.Activate();
            window.Select([vnc]);
            window.SetDetailsVisible(true);
            window.Tiles.ScrollIntoView(vnc);
            await Settle(600);
            Check($"Live preview: the tile shows the \"Live\" badge with the age ({vnc.LivePreviewAge})",
                TileShowsLiveBadge(window, vnc) && vnc.LivePreviewAge == Strings.LivePreview_AgeNow);
            Check($"Live preview: the detail panel shows the live picture with the badge ({window.DetailPanel.LiveAge})",
                window.DetailPanel.Shown == vnc && window.DetailPanel.ShowsLiveBadge && window.DetailPanel.LiveAge == vnc.LivePreviewAge);
            SaveRenderProbe(window, "live-preview-overview.png");

            // Refresh after the interval (the server paints the same quadrants, so check the time and the file).
            DateTimeOffset? firstAt = vnc.LivePreviewAt;
            DateTime firstWrite = File.GetLastWriteTimeUtc(path);
            await Task.Delay(TestInterval + TimeSpan.FromMilliseconds(200));
            await window.TickLivePreviewAsync();
            Check("Live preview: the picture is captured again after the interval",
                vnc.LivePreviewAt > firstAt && File.GetLastWriteTimeUtc(path) > firstWrite);
            Check("Live preview: the age reads \"vor 20 s\" / \"vor 2 min\"",
                ConnectionItem.DescribeAge(TimeSpan.FromSeconds(20)) == "vor 20 s" && ConnectionItem.DescribeAge(TimeSpan.FromSeconds(150)) == "vor 2 min");

            // Opt-out for this connection: no capture and no badge.
            var settings = (VncSettings)vnc.Model.Settings;
            vnc.Model.Settings = settings with { LivePreview = false };
            DateTime beforeOptOut = File.GetLastWriteTimeUtc(path);
            await Task.Delay(TestInterval + TimeSpan.FromMilliseconds(200));
            await window.TickLivePreviewAsync();
            await Settle(200);
            Check("Live preview: \"Live-Vorschau für diese Verbindung\" off stops captures and hides the badge",
                !vnc.IsLivePreview && File.GetLastWriteTimeUtc(path) == beforeOptOut && !TileShowsLiveBadge(window, vnc) && !window.DetailPanel.ShowsLiveBadge);
            vnc.Model.Settings = settings;

            // "Aus" in the menu: same for every session.
            window.SetLivePreviewInterval(TimeSpan.Zero);
            await Task.Delay(TestInterval + TimeSpan.FromMilliseconds(200));
            await window.TickLivePreviewAsync();
            bool off = !vnc.IsLivePreview && File.GetLastWriteTimeUtc(path) == beforeOptOut
                && app.Library.GetSetting(LivePreviewScheduler.IntervalSetting) == "0";
            window.SetLivePreviewInterval(TestInterval);
            await window.TickLivePreviewAsync();
            Check("Live preview: \"Aus\" stops captures and is saved; switching it on again captures at once", off && vnc.IsLivePreview);

            // Closing keeps the final, full-size snapshot; later ticks do not touch it.
            session.Close();
            await Settle(500);
            DateTime finalWrite = File.GetLastWriteTimeUtc(path);
            await Task.Delay(TestInterval + TimeSpan.FromMilliseconds(200));
            await window.TickLivePreviewAsync();
            Check($"Live preview: closing the session keeps its final snapshot ({vnc.Preview?.PixelSize}) and removes the badge",
                !vnc.IsLivePreview && vnc.Preview?.PixelSize == new PixelSize(FakeVncServer.Width, FakeVncServer.Height)
                && File.GetLastWriteTimeUtc(path) == finalWrite && !window.SessionWindows.Contains(session));
        }
        finally
        {
            foreach (Window open in window.SessionWindows.Where(w => ((ISessionWindow)w).Connection == vnc).ToList())
                open.Close();
            window.ShowUnsaved(vnc, false);
            window.LivePreview.Interval = LivePreviewScheduler.ParseInterval(storedInterval);
            app.Library.SetSetting(LivePreviewScheduler.IntervalSetting, storedInterval);
            File.Delete(path);
        }
    }

    /// <summary>Live RDP session (RM_SPIKE_HOST): capture through the snapshot path, scaling, and the minimized skip.</summary>
    private static async Task RunLiveRdpPreviewChecksAsync(MainWindow window, SessionWindow live, ConnectionItem connection)
    {
        TimeSpan interval = window.LivePreview.Interval;
        try
        {
            window.LivePreview.Interval = TestInterval;
            await Task.Delay(TestInterval);
            await window.TickLivePreviewAsync();
            PixelSize size = connection.Preview?.PixelSize ?? default;
            Check($"Live preview (RDP): the logged-on session becomes the tile picture, scaled ({size})",
                connection.IsLivePreview && size.Width > 0 && size.Width <= Math.Max(LivePreviewImage.TargetSize.Width, live.LastRemoteDesktopSize.Width));

            DateTimeOffset? before = connection.LivePreviewAt;
            live.WindowState = WindowState.Minimized;
            await Settle(1500);
            await window.TickLivePreviewAsync();
            bool skipped = connection.LivePreviewAt == before && connection.IsLivePreview;
            live.WindowState = WindowState.Normal;
            await Settle(1500);
            await window.TickLivePreviewAsync();
            Check("Live preview (RDP): a minimized session keeps the last picture and is captured again when restored",
                skipped && connection.LivePreviewAt > before);
        }
        finally
        {
            window.LivePreview.Interval = interval;
        }
    }

    /// <summary>Screenshots of the interval menu and the RDP opt-out in the editor (for the docs).</summary>
    private static async Task SaveLivePreviewSettingsProbesAsync(MainWindow window)
    {
        window.Activate();
        if (window.More.Flyout is MenuFlyout menu && (menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header as string == Strings.LivePreview_Menu)) is { } parent)
        {
            menu.ShowAt(window.More);
            await Settle(400);
            parent.Open();
            await Settle(600);
            // Menus are native popups: render the submenu's own visual (a screen copy may miss it).
            if (parent.GetVisualDescendants().OfType<Popup>().FirstOrDefault()?.Child is { } submenu)
                SaveControlProbe(submenu, window, "live-preview-menu.png");
            parent.Close();
            menu.Hide();
            await Settle(300);
        }

        var rdp = new Connection { Name = "Selftest Live RDP", Host = "rdp.example.invalid" };
        Task<bool> editing = ConnectionEditor.ShowAsync(rdp, isNew: true);
        await Settle(800);
        SaveRenderProbe(window, "live-preview-rdp-optout.png");
        ConnectionEditor.OpenDialog?.Hide();
        await editing;
    }

    private static void SaveControlProbe(Control control, Window theme, string name)
    {
        double scale = theme.RenderScaling;
        Size size = control.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0)
            return;
        var dpi = new Vector(96 * scale, 96 * scale);
        using var content = new RenderTargetBitmap(PixelSize.FromSize(size, scale), dpi);
        content.Render(control);
        IBrush background = theme.TryFindResource("SolidBackgroundFillColorBaseBrush", theme.ActualThemeVariant, out object? brush)
            && brush is IBrush b ? b : Brushes.Black;
        using var composed = new RenderTargetBitmap(PixelSize.FromSize(size, scale), dpi);
        using (DrawingContext context = composed.CreateDrawingContext())
        {
            context.FillRectangle(background, new Avalonia.Rect(size));
            context.DrawImage(content, new Avalonia.Rect(size));
        }

        string path = Path.Combine(Core.AppPaths.Spike, name);
        composed.Save(path, PngBitmapEncoderOptions.Default);
        Log($"  probe saved: {path}");
    }

    private static IReadOnlyList<MenuItem>? LivePreviewMenu(MainWindow window) =>
        (window.More.Flyout as MenuFlyout)?.Items.OfType<MenuItem>()
            .FirstOrDefault(m => m.Header as string == Strings.LivePreview_Menu)?.ItemsSource?.OfType<MenuItem>().ToList();

    private static bool TileShowsLiveBadge(MainWindow window, ConnectionItem connection) =>
        window.Tiles.ContainerFromItem(connection) is { } tile
        && tile.GetVisualDescendants().OfType<Border>().Any(b => ToolTip.GetTip(b) as string == Strings.LivePreview_BadgeTip && b.IsEffectivelyVisible);
}
