
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

using RemoteManager.App.Services;
using RemoteManager.App.Views;
using RemoteManager.Core;
using RemoteManager.Core.History;
using RemoteManager.Core.Library;
using RemoteManager.Core.Model;
using RemoteManager.Core.Status;

namespace RemoteManager.App.Diagnostics;

/// <summary>
/// <c>--tour</c>: fills an empty data folder with demo connections and saves pictures of every main screen to
/// &lt;data&gt;\spike\tour-*.png (docs/ui.md shows them). Only runs when REMOTE_MANAGER_DATA points at another
/// folder than the user's own, so it can never add demo entries to real data. The statuses and previews are
/// made up; nothing is connected.
/// </summary>
internal static partial class SelfTest
{
    public static async Task<int> RunTourAsync(MainWindow window)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("REMOTE_MANAGER_DATA")))
        {
            System.Diagnostics.Trace.WriteLine("--tour needs REMOTE_MANAGER_DATA (a scratch data folder).");
            return 2;
        }

        try
        {
            await Settle(600);
            SeedTourData(window);
            await Settle(3200); // the real status checks end as "unknown"; the made-up statuses go on top
            ApplyTourStatuses(window);
            await RunTourShotsAsync(window);
            return 0;
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(AppPaths.Spike, "tour-error.txt"), ex.ToString());
            return 1;
        }
    }

    private static async Task RunTourShotsAsync(MainWindow window)
    {
        AppHost app = AppHost.Current;
        Appearance look = app.Appearance;
        window.WindowState = WindowState.Normal;
        window.Width = 1400;
        window.Height = 880;
        window.SetListView(false);
        window.SetDetailsVisible(true);
        window.ShowFilter(SidebarEntry.AllTag);
        look.SetTheme(AppTheme.Dark);
        look.SetAccent(Appearance.Accents[0]);
        look.SetTileSize(TileSize.Medium);
        await Settle(700);

        ConnectionItem? pick = window.Connections.FirstOrDefault(c => c.Name == "dc01");
        if (pick is not null)
            window.Select([pick]);
        await Settle(500);
        SaveRenderProbe(window, "tour-tiles.png");
        window.Activate();
        await Settle(600);
        SaveScreenProbe(window, "tour-tiles-screen.png");

        look.SetTileSize(TileSize.Large);
        await Settle(600);
        SaveRenderProbe(window, "tour-tiles-large.png");
        look.SetTileSize(TileSize.Small);
        await Settle(600);
        SaveRenderProbe(window, "tour-tiles-small.png");
        look.SetTileSize(TileSize.Medium);

        window.SetListView(true);
        await Settle(600);
        SaveRenderProbe(window, "tour-list.png");
        window.SetListView(false);

        window.SetTypeFilter(ConnectionType.Rdp);
        window.SetOnlineOnly(true);
        await Settle(500);
        SaveRenderProbe(window, "tour-filtered.png");
        window.ClearChipFilters();

        window.Width = 1024;
        window.Height = 680;
        await Settle(800);
        SaveRenderProbe(window, "tour-compact.png");
        window.ToggleSidebar();
        await Settle(500);
        SaveRenderProbe(window, "tour-compact-sidebar.png");
        window.ToggleSidebar();
        window.Width = 1400;
        window.Height = 880;
        await Settle(800);

        look.SetTheme(AppTheme.Light);
        await Settle(900);
        SaveRenderProbe(window, "tour-light.png");
        window.SetListView(true);
        await Settle(500);
        SaveRenderProbe(window, "tour-light-list.png");
        window.SetListView(false);
        look.SetTheme(AppTheme.Dark);

        foreach (AccentPreset accent in Appearance.Accents.Skip(1).Take(4))
        {
            look.SetAccent(accent);
            await Settle(500);
            SaveRenderProbe(window, $"tour-accent-{accent.Id}.png");
        }

        look.SetAccent(Appearance.Accents[0]);
        look.SetTheme(AppTheme.System);
        await Settle(300);
    }

    /// <summary>Groups, connections of every type, tags, favorites, history, and a few made-up previews.</summary>
    private static void SeedTourData(MainWindow window)
    {
        AppHost app = AppHost.Current;
        ConnectionLibrary library = app.Library;
        if (library.GetSetting("tour.seeded") == "1")
            return;

        foreach (ConnectionItem old in window.Connections.ToList())
            window.RemoveConnectionAsync(old).GetAwaiter().GetResult();
        foreach (ConnectionGroup group in library.Groups.ToList())
            library.RemoveGroup(group, GroupRemoval.DeleteContent);

        var now = DateTime.UtcNow;
        (string Group, string Name, ConnectionType Type, string Host, string Description, string[] Tags, bool Favorite, double? HoursAgo, bool Preview)[] demo =
        [
            ("Kanzlei Brandt", "dc01", ConnectionType.Rdp, "10.20.0.10", "Domain Controller, DNS, DHCP", ["prod", "ad"], true, 0.4, true),
            ("Kanzlei Brandt", "fileserver", ConnectionType.Rdp, "10.20.0.20", "Freigaben, Backup-Ziel", ["prod"], false, 26, true),
            ("Kanzlei Brandt", "Empfang-PC", ConnectionType.TeamViewer, "123 456 789", "Frau Meier, nur nach Absprache", ["arbeitsplatz"], false, 80, false),
            ("Kanzlei Brandt / Standort Nord", "nas-nord", ConnectionType.Sftp, "10.21.0.5", "Synology, Aktenarchiv", ["backup"], false, null, false),
            ("Kanzlei Brandt / Standort Nord", "drucker-vm", ConnectionType.Vnc, "10.21.0.31", "Druckserver, nur ansehen", [], false, 300, false),
            ("Laden Ost", "Kasse 1", ConnectionType.AnyDesk, "987 654 321", "Unattended Access", ["kasse"], true, 5, false),
            ("Laden Ost", "Kasse 2", ConnectionType.AnyDesk, "987 654 322", "Unattended Access", ["kasse"], false, null, false),
            ("Laden Ost", "backoffice", ConnectionType.Rdp, "192.168.40.12", "Warenwirtschaft", ["prod"], false, 49, true),
            ("Homelab", "botis-lp-srv", ConnectionType.Rdp, "botis-lp-srv", "Linux-Laptop, xrdp", ["linux", "homelab"], true, 1.5, true),
            ("Homelab", "botis-lp-srv (Shell)", ConnectionType.Ssh, "botis-lp-srv", "Docker, Backups", ["linux", "homelab"], false, 2, false),
            ("Homelab", "hyperv01", ConnectionType.Rdp, "192.168.1.10", "Hyper-V-Host", ["hyper-v"], false, 170, false),
            ("Homelab", "pi-hole", ConnectionType.Ssh, "192.168.1.2", "DNS-Filter", ["linux"], false, 400, false),
            ("Homelab", "werkstatt-pc", ConnectionType.RustDesk, "1 234 567 890", "Eigener Relay-Server", [], false, null, false),
            ("", "jump.example.net", ConnectionType.Ssh, "jump.example.net", "Sprunghost für Kundennetze", ["jump"], false, 12, false),
        ];

        int index = 0;
        foreach (var entry in demo)
        {
            ConnectionGroup? group = entry.Group.Length > 0 ? library.EnsureGroupPath(entry.Group) : null;
            var connection = new Connection
            {
                Name = entry.Name, Type = entry.Type, Host = entry.Host, Description = entry.Description,
                Tags = [.. entry.Tags], IsFavorite = entry.Favorite, GroupId = group?.Id,
                SortOrder = library.ConnectionsIn(group?.Id).Count(),
                UserName = entry.Type is ConnectionType.Rdp or ConnectionType.Ssh or ConnectionType.Sftp ? "admin" : "",
                LastConnectedAt = entry.HoursAgo is { } hours ? now.AddHours(-hours) : null,
                Notes = entry.Name == "dc01" ? "Wartungsfenster Sa 20 Uhr, vorher Herrn Brandt anrufen." : "",
            };
            if (entry.Preview)
            {
                connection.PreviewPath = $"{connection.Id}.png";
                SaveTourPreview(connection.ResolvedPreviewPath, index);
            }

            ConnectionItem item = window.Adopt(connection);
            item.ReloadPreview();
            if (entry.HoursAgo is { } ago)
            {
                for (int visit = 0; visit < 3; visit++)
                {
                    DateTime start = now.AddHours(-ago - (visit * 30));
                    long id = app.History.Start(connection.Id, HistoryResult.Connected, start);
                    app.History.End(id, start.AddMinutes(12 + (index * 7 % 50)));
                }
            }

            index++;
        }

        library.SetSetting("tour.seeded", "1");
        window.SyncWithLibrary();
    }

    private static void ApplyTourStatuses(MainWindow window)
    {
        var random = new Random(7);
        foreach (ConnectionItem item in window.Connections)
        {
            OnlineStatus status = item.Name switch
            {
                "fileserver" or "pi-hole" => OnlineStatus.Offline,
                "Kasse 2" or "werkstatt-pc" or "drucker-vm" => OnlineStatus.Unknown,
                _ => OnlineStatus.Online,
            };
            bool measured = item.Model.Type is ConnectionType.Rdp or ConnectionType.Ssh or ConnectionType.Sftp or ConnectionType.Vnc;
            // A short history, so the answer-time line of the detail panel has something to show.
            for (int i = 0; i < 18; i++)
            {
                bool dropout = status == OnlineStatus.Online && item.Name == "backoffice" && i is 6 or 7;
                OnlineStatus sample = dropout ? OnlineStatus.Unknown : status;
                item.SetStatus(sample, sample == OnlineStatus.Online && measured ? TimeSpan.FromMilliseconds(8 + random.Next(30) + (item.Name.Length * 2)) : null);
            }
        }

        window.ShowFilter(window.Filter);
    }

    /// <summary>A made-up desktop: wallpaper, a window or two, a taskbar. Enough to read as a session picture at tile size.</summary>
    private static void SaveTourPreview(string path, int seed)
    {
        var size = new PixelSize(528, 280);
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        (string From, string To)[] wallpapers =
        [
            ("#0B3A66", "#1C7FB8"), ("#3B1E5C", "#B64E8C"), ("#0E3B33", "#3FA37A"), ("#1E2A44", "#5B79C9"), ("#4A2A12", "#D0863A"),
        ];
        (string from, string to) = wallpapers[seed % wallpapers.Length];
        using (DrawingContext context = bitmap.CreateDrawingContext())
        {
            var wallpaper = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse(from), 0), new GradientStop(Color.Parse(to), 1) },
            };
            context.FillRectangle(wallpaper, new Avalonia.Rect(0, 0, size.Width, size.Height));

            void Window(double x, double y, double w, double h, string body)
            {
                context.DrawRectangle(new SolidColorBrush(Color.Parse(body)), null, new RoundedRect(new Avalonia.Rect(x, y, w, h), 5));
                context.DrawRectangle(new SolidColorBrush(Color.Parse("#22000000")), null, new RoundedRect(new Avalonia.Rect(x, y, w, 16), 5, 5, 0, 0));
                for (int line = 0; line < 6; line++)
                {
                    context.FillRectangle(new SolidColorBrush(Color.Parse("#30808A94")),
                        new Avalonia.Rect(x + 12, y + 28 + (line * 13), w * (0.35 + ((line * 37 + seed * 11) % 50 / 100.0)), 5));
                }
            }

            Window(46 + (seed % 3 * 20), 30, 250, 150, "#F4F6F8");
            Window(220 + (seed % 4 * 14), 78, 240, 140, "#1F242B");
            context.FillRectangle(new SolidColorBrush(Color.Parse("#E6101418")), new Avalonia.Rect(0, size.Height - 26, size.Width, 26));
            for (int icon = 0; icon < 7; icon++)
            {
                context.DrawRectangle(new SolidColorBrush(Color.Parse(icon == 2 ? "#FFFFFF" : "#8AFFFFFF")), null,
                    new RoundedRect(new Avalonia.Rect((size.Width / 2.0) - 80 + (icon * 23), size.Height - 19, 12, 12), 3));
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);


    }
}
