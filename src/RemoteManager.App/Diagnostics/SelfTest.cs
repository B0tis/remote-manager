using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using Microsoft.Data.Sqlite;
using RemoteManager.App.Connections;
using RemoteManager.App.Connections.Rdp;
using RemoteManager.App.Connections.RemoteTools;
using RemoteManager.App.Resources;
using RemoteManager.App.Services;
using RemoteManager.App.Views;
using RemoteManager.Core;
using RemoteManager.Core.Credentials;
using RemoteManager.Core.Library;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.Status;
using RemoteManager.Core.Storage;

namespace RemoteManager.App.Diagnostics;

/// <summary>
/// Automated checks (<c>--selftest</c>). Writes a log plus probe screenshots to
/// %LOCALAPPDATA%\RemoteManager\spike and exits with 0 when every executed check passed.
/// Checks that need a real RDP server only run when RM_SPIKE_HOST is set (RM_SPIKE_USER /
/// RM_SPIKE_DOMAIN fill the connection; RM_SPIKE_PASSWORD is stored as a temporary credential so the
/// window logs on by itself; never logged). Credential checks use "RemoteManager:selftest-*-&lt;run&gt;" only (see SelfTest.Isolation.cs).
/// Database checks run on a temporary folder; the real database is only read.
/// </summary>
internal static partial class SelfTest
{
    private static readonly List<string> Lines = [];
    private static int failures;

    public static async Task<int> RunAsync(MainWindow window)
    {
        string logPath = Path.Combine(AppPaths.Spike, "selftest.log");
        try
        {
            await RunChecksAsync(window);
        }
        catch (Exception ex)
        {
            Fail($"Unhandled exception: {ex}");
        }

        Log(failures == 0 ? "RESULT: PASS" : $"RESULT: FAIL ({failures} failed checks)");
        await File.WriteAllLinesAsync(logPath, Lines);
        return failures == 0 ? 0 : 1;
    }


    private static async Task RunChecksAsync(MainWindow window)
    {
        Log($"Remote-Manager self-test {DateTime.Now:O}");
        Log($"OS {Environment.OSVersion}, .NET {Environment.Version}, {AppOptions.Describe()}");
        await Task.Delay(500);
        Log($"Window RenderScaling {window.RenderScaling}, transparency {window.ActualTransparencyLevel}");
        foreach (Screen screen in window.Screens.All)
            Log($"Screen {screen.DisplayName}: bounds {screen.Bounds}, scaling {screen.Scaling}, primary {screen.IsPrimary}");
        CheckAppIcon(window);

        // --- Overview: store, tile view, list view ------------------------------------------------
        AppHost app = AppHost.Current;
        Log($"Store {app.StoreReport.DatabasePath}: schema {SqliteMigrator.LatestVersion}, migrated from json {app.StoreReport.MigratedFromJson}, seeded {app.StoreReport.Seeded}");
        Check($"Overview lists the stored connections ({window.Connections.Count})",
            window.Connections.Count == app.Library.Connections.Count && window.VisibleConnections.Count == window.Connections.Count);
        Check("Every connection type has a handler",
            Enum.GetValues<ConnectionType>().All(app.Handlers.Supports) && window.Connections.All(c => c.Handler.Type == c.Model.Type));

        await RunDatabaseChecksAsync(app);
        await RunStatusCheckerChecksAsync(app);
        window.Activate();
        await Settle(2500); // let the status checks finish
        SaveScreenProbe(window, "overview-tiles.png");
        window.SetListView(true);
        await Settle(600);
        Check("List view toggle", window.IsListView);
        SaveScreenProbe(window, "overview-list.png");
        window.SetListView(false);
        await Settle(300);

        // The edit dialog is an in-window FAContentDialog; that is fine here because the overview never hosts a session.
        ConnectionItem edited = window.Connections[0];
        string nameBefore = edited.Name;
        Task<bool> editing = ConnectionEditor.ShowAsync(edited.Model, isNew: false);
        await Settle(800);
        SaveScreenProbe(window, "edit-dialog.png");
        ConnectionEditor.OpenDialog?.Hide();
        Check("Edit dialog opens and Cancel leaves the connection unchanged", !await editing && edited.Name == nameBefore);

        await RunOverviewChecksAsync(window);
        await RunGroupDeleteChecksAsync(window); // SelfTest.GroupDelete.cs
        await RunLayoutChecksAsync(window); // SelfTest.Layout.cs
        await RunCredentialChecksAsync(window);
        await RunRdpImportChecksAsync(window);

        await RunRemoteToolChecksAsync(window);
        await RunVncAndVaultChecksAsync(window); // SelfTest.VncAndVaults.cs
        await RunLivePreviewChecksAsync(window); // SelfTest.LivePreview.cs
        await RunSshChecksAsync(window);
        await RunUpdateChecksAsync(window); // SelfTest.Updates.cs
        await RunSyncChecksAsync(window); // SelfTest.Sync.cs

        // --- Two session windows, each with its own RDP client -------------------------------------
        // Temporary connections; they are not added to the store.
        ConnectionItem a = window.CreateItem(new Connection { Name = "Selftest A", Host = "127.0.0.1", Description = "Loopback: refused by the RDP client", UserName = "selftest" });
        ConnectionItem b = window.CreateItem(new Connection { Name = "Selftest B", Host = "rm-selftest-unreachable.invalid", Description = "DNS failure" });
        var first = (SessionWindow)window.OpenSession(a)!;
        var second = (SessionWindow)window.OpenSession(b)!;
        foreach (SessionWindow s in new[] { first, second })
            s.StatusLogged += (_, message) => Log($"  [{s.Title}] {message}");

        Check("Both session windows get a ready RDP client",
            await WaitFor(() => first.Rdp.IsClientReady && second.Rdp.IsClientReady, TimeSpan.FromSeconds(20)));
        Check("Window titles are the connection names", first.Title == a.Name && second.Title == b.Name);
        Check("Session windows carry the app icon", first.Icon is not null && HasWindowIcon(HandleOf(first)));
        Check("Opening the same connection again reuses its window",
            window.OpenSession(a) == first && window.SessionWindows.Count == 2);

        nint firstWindow = HandleOf(first), secondWindow = HandleOf(second), mainWindow = HandleOf(window);
        nint firstHost = first.Rdp.HostWindowHandle, secondHost = second.Rdp.HostWindowHandle;
        Log($"Session windows 0x{firstWindow:X} / 0x{secondWindow:X}, RDP hosts 0x{firstHost:X} / 0x{secondHost:X}, main 0x{mainWindow:X}");
        Check("Each RDP host HWND lives in its own top-level window",
            firstHost != 0 && secondHost != 0 && firstWindow != secondWindow && firstWindow != mainWindow
            && GetAncestor(firstHost, GaRoot) == firstWindow && GetAncestor(secondHost, GaRoot) == secondWindow);

        second.Activate();
        await Settle(800);
        SaveScreenProbe(second, "session-window.png");

        // --- Failed connects report Disconnected, and the same window can connect again ------------
        // The RDP client refuses loopback (reason 1800 here); the .invalid name exercises
        // DNS failure. RM_SPIKE_UNREACHABLE adds another host.
        foreach ((SessionWindow s, string label) in new[] { (first, "first"), (second, "second"), (first, "same window again") })
            await CheckFailedConnect(s, label);
        if (Environment.GetEnvironmentVariable("RM_SPIKE_UNREACHABLE") is { Length: > 0 } custom)
        {
            var s = (SessionWindow)window.OpenSession(window.CreateItem(new Connection { Name = "Selftest custom", Host = custom }))!;
            await WaitFor(() => s.Rdp.IsClientReady, TimeSpan.FromSeconds(20));
            await CheckFailedConnect(s, "RM_SPIKE_UNREACHABLE");
            s.Close();
        }

        second.Activate();
        await Settle(800);
        SaveScreenProbe(second, "session-window-disconnected.png");

        // --- Closing one window disposes its session and destroys its HWND -------------------------
        second.Close();
        await Settle(500);
        Check("Closing a session window destroys its RDP HWND and keeps the other session",
            !IsWindow(secondHost) && IsWindow(firstHost) && first.Rdp.IsClientReady
            && window.SessionWindows.Count == 1 && window.SessionWindows[0] == first);

        // --- Real server (optional) --------------------------------------------------------------------
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RM_SPIKE_HOST")))
            await RunLiveSessionChecksAsync(window);
        else
            Log("SKIP live-session checks (RM_SPIKE_HOST not set): logon, snapshot as preview, live RDP preview, resize, DPI.");

        // --- Airspace probe: a menu flyout over a visible RDP surface --------------------------------
        await RunAirspaceProbeAsync(first);

        first.Close();
        await Settle(500);
        Check("Closing the last session window destroys its HWND", !IsWindow(firstHost) && window.SessionWindows.Count == 0);
    }

    /// <summary>
    /// Credential Manager round trip, edit dialog, database, session auto-connect, and the
    /// copy-password flow. Only uses "RemoteManager:selftest-*" targets and dummy values, and never
    /// touches the credentials of real connections. Values are never logged.
    /// </summary>
    private static async Task RunCredentialChecksAsync(MainWindow window)
    {
        string[] ids = [TestId("roundtrip"), TestId("rdp"), TestId("anydesk")];

        // --- Round trip ---------------------------------------------------------------------------
        string secret = $"Rm-{Guid.NewGuid():N}-äß€";
        WindowsCredentialSource.Write(ids[0], secret);
        bool written = WindowsCredentialSource.Exists(ids[0]) && WindowsCredentialSource.Read(ids[0]) == secret;
        WindowsCredentialSource.Write(ids[0], secret + "2");
        bool replaced = WindowsCredentialSource.Read(ids[0]) == secret + "2";
        bool deleted = WindowsCredentialSource.Delete(ids[0]) && !WindowsCredentialSource.Exists(ids[0]) && WindowsCredentialSource.Read(ids[0]) is null
            && !WindowsCredentialSource.Delete(ids[0]);
        Check($"Credential Manager round trip ({WindowsCredentialSource.TargetFor(ids[0])}): write {written}, replace {replaced}, delete {deleted}",
            written && replaced && deleted);

        ConnectionItem rdp = window.CreateItem(new Connection
        {
            Id = ids[1], Name = "Selftest RDP", Host = "127.0.0.1", UserName = "selftest",
            Description = "Stored password, loopback (refused)",
        });
        ConnectionItem tool = window.CreateItem(new Connection
        {
            Id = ids[2], Name = "Selftest AnyDesk", Type = ConnectionType.AnyDesk, Host = "123 456 789",
            Description = "Stored password, copy flow", Tags = ["selftest"],
        });
        window.ShowUnsaved(rdp, true);
        window.ShowUnsaved(tool, true);
        IClipboard clipboard = window.Clipboard!;
        try
        {
            await RunEditorCredentialChecksAsync(window, rdp, secret);
            await RunSessionCredentialChecksAsync(window, rdp);
            await WithClipboardAsync(clipboard, () => RunCopyPasswordChecksAsync(window, tool, clipboard));

            // Deleting a connection deletes its credential (neither test connection is in the library).
            window.ShowUnsaved(tool, false);
            await window.RemoveConnectionAsync(rdp);
            Check("Deleting a connection deletes its stored password", !WindowsCredentialSource.Exists(rdp.Id) && rdp.Model.Credential is null);
        }
        finally
        {
            window.ShowUnsaved(rdp, false);
            window.ShowUnsaved(tool, false);
            foreach (string id in ids)
                WindowsCredentialSource.Delete(id);
        }
    }

    private static async Task RunEditorCredentialChecksAsync(MainWindow window, ConnectionItem rdp, string secret)
    {
        Task<bool> editing = ConnectionEditor.ShowAsync(rdp.Model, isNew: false);
        await Settle(800);
        ConnectionEditor editor = ConnectionEditor.OpenEditor!;
        editor.Password.Text = secret;
        await Settle(300);
        SaveRenderProbe(window, "edit-dialog-password.png");
        Check("Typing a password turns on \"Passwort speichern\"", editor.SavePassword.IsChecked == true);
        bool committed = await editor.TryCommitAsync(rdp.Model);
        ConnectionEditor.OpenDialog?.Hide();
        await editing;
        rdp.RefreshCredentialState();
        Check("Saving the edit dialog writes the password to the Credential Manager",
            committed && WindowsCredentialSource.Read(rdp.Id) == secret && rdp.HasStoredPassword
            && rdp.Model.Credential == new CredentialRef(WindowsCredentialSource.SourceId, rdp.Id));

        editing = ConnectionEditor.ShowAsync(rdp.Model, isNew: false);
        await Settle(800);
        editor = ConnectionEditor.OpenEditor!;
        SaveRenderProbe(window, "edit-dialog-stored.png");
        Check("Reopened dialog shows \"Passwort gespeichert\" and never the value",
            editor.IsStoredPasswordShown && string.IsNullOrEmpty(editor.Password.Text));
        editor.RemovePassword.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle(300);
        SaveRenderProbe(window, "edit-dialog-remove.png");
        committed = await editor.TryCommitAsync(rdp.Model);
        ConnectionEditor.OpenDialog?.Hide();
        await editing;
        rdp.RefreshCredentialState();
        Check("\"Entfernen\" plus Save deletes the credential",
            committed && !WindowsCredentialSource.Exists(rdp.Id) && !rdp.HasStoredPassword && rdp.Model.Credential is null);

        // Stored again for the session checks that follow.
        await AppHost.Current.Credentials.SaveOwnSecretAsync(rdp.Model, secret);
        rdp.RefreshCredentialState();
    }

    private static async Task RunSessionCredentialChecksAsync(MainWindow window, ConnectionItem rdp)
    {
        bool disconnected = false;
        var s = (SessionWindow)window.OpenSession(rdp)!;
        s.StatusLogged += (_, message) => Log($"  [{s.Title}] {message}");
        s.SessionDisconnected += (_, _) => disconnected = true;
        bool cardHidden = !s.IsConnectCardVisible;
        bool raised = await WaitFor(() => disconnected, TimeSpan.FromSeconds(60));
        await Settle(300);
        Check($"With a stored password the session connects right away, without the card (status \"{s.LastStatus}\")",
            cardHidden && raised && s.UsedStoredPassword && s.IsConnectCardVisible);

        // A real rejection needs a server; render the state for review and check the message.
        s.ShowStoredPasswordRejected();
        await Settle(400);
        SaveRenderProbe(s, "session-stored-password-rejected.png");
        Check("Rejected stored password shows the card with a clear message", s.IsConnectCardVisible && s.CardMessageText.Contains("abgelehnt"));
        s.Close();
        await Settle(300);
    }

    private static async Task RunCopyPasswordChecksAsync(MainWindow window, ConnectionItem tool, IClipboard clipboard)
    {
        string secret = $"Rm-{Guid.NewGuid():N}";
        await AppHost.Current.Credentials.SaveOwnSecretAsync(tool.Model, secret);
        tool.RefreshCredentialState();
        window.SetListView(false);
        window.Tiles.SelectedItem = tool;
        window.Tiles.ScrollIntoView(tool);
        await Settle(400);
        // Opening the menu runs its Opening handler, which decides whether "Passwort kopieren" shows.
        ContextMenu menu = window.Tiles.ContextMenu!;
        menu.Open(window.Tiles);
        await Settle(500);
        bool menuItem = menu.IsOpen && menu.Items.OfType<MenuItem>().Any(i => i.Header as string == Strings.Action_CopyPassword && i.IsVisible);
        menu.Close();
        Check("Context menu offers \"Passwort kopieren\" for a connection with a stored password", menuItem);

        // Connect on a remote-tool entry when the tool is not installed (detection switched off, so a
        // real AnyDesk on this machine is never started): a clear message with path setting and download.
        RemoteToolLauncher.LocatorOverride = FakeLocator(null);
        try
        {
            window.OpenSession(tool);
            await Settle(800);
            FAContentDialog? missing = MissingToolDialog.Current;
            bool clear = missing is not null && missing.Title as string == string.Format(CultureInfo.CurrentCulture, Strings.RemoteTool_Missing_Title, "AnyDesk")
                && missing.PrimaryButtonText == Strings.RemoteTool_Missing_SetPath && missing.SecondaryButtonText == Strings.RemoteTool_Missing_Download;
            SaveRenderProbe(window, "remote-tool-missing.png");
            missing?.Hide();
            await Settle(300);
            Check("Missing tool shows \"AnyDesk nicht gefunden\" with \"Pfad festlegen\" and \"Herunterladen\"", clear);
        }
        finally
        {
            RemoteToolLauncher.LocatorOverride = null;
        }

        // Default delay first (for the probe), then a short one to see the clear happen.
        await window.CopyPasswordAsync(tool);
        await Settle(500);
        SaveRenderProbe(window, "copy-password-toast.png");
        bool copied = await ReadClipboardAsync(clipboard, t => t == secret) == secret;
        Check($"Copy puts the password on the clipboard and shows \"{window.ToastBar.Message}\"",
            copied && window.ToastBar.IsOpen && window.ToastBar.Message == string.Format(CultureInfo.CurrentCulture, Strings.Password_Copied, 30));

        TimeSpan clearAfter = TimeSpan.FromSeconds(2);
        await window.CopyPasswordAsync(tool, clearAfter);
        await Task.Delay(clearAfter + TimeSpan.FromSeconds(1));
        string? after = await ReadClipboardAsync(clipboard, string.IsNullOrEmpty);
        Check("Clipboard is cleared after the delay", string.IsNullOrEmpty(after) && !SecretClipboard.HasPending);

        await window.CopyPasswordAsync(tool, clearAfter);
        await clipboard.SetTextAsync("remote-manager selftest: copied by the user");
        await Task.Delay(clearAfter + TimeSpan.FromSeconds(1));
        Check("Something copied afterwards stays on the clipboard",
            await ReadClipboardAsync(clipboard, t => t == "remote-manager selftest: copied by the user") == "remote-manager selftest: copied by the user");
    }

    private static async Task CheckFailedConnect(SessionWindow s, string label)
    {
        bool disconnected = false;
        void OnDisconnected(object? sender, EventArgs e) => disconnected = true;
        s.SessionDisconnected += OnDisconnected;
        var sw = Stopwatch.StartNew();
        s.Connect();
        bool raised = await WaitFor(() => disconnected, TimeSpan.FromSeconds(120));
        s.SessionDisconnected -= OnDisconnected;
        await Settle(200);
        Check($"Failed connect to {s.Connection.Model.Host} ({label}) raises Disconnected ({sw.Elapsed.TotalSeconds:F1}s, status \"{s.LastStatus}\")",
            raised && s.Rdp.IsClientReady && !s.IsSessionActive && !s.Rdp.IsVisible);
    }

    private static async Task RunLiveSessionChecksAsync(MainWindow window)
    {
        ConnectionItem connection = window.CreateItem(new Connection
        {
            Id = TestId("live"),
            Name = "Selftest live",
            Host = Environment.GetEnvironmentVariable("RM_SPIKE_HOST")!,
            UserName = Environment.GetEnvironmentVariable("RM_SPIKE_USER") ?? "",
            Domain = Environment.GetEnvironmentVariable("RM_SPIKE_DOMAIN") ?? "",
        });

        // With RM_SPIKE_PASSWORD the password goes through the Credential Manager, so the window
        // connects on its own (direct Windows logon); the credential is removed again afterwards.
        string? password = Environment.GetEnvironmentVariable("RM_SPIKE_PASSWORD");
        bool stored = !string.IsNullOrEmpty(password);
        if (stored)
        {
            await AppHost.Current.Credentials.SaveOwnSecretAsync(connection.Model, password!);
            connection.RefreshCredentialState();
        }

        try
        {
            await RunLiveSessionAsync(window, connection, stored);
        }
        finally
        {
            WindowsCredentialSource.Delete(connection.Id);
        }
    }

    private static async Task RunLiveSessionAsync(MainWindow window, ConnectionItem connection, bool stored)
    {
        var live = (SessionWindow)window.OpenSession(connection)!;
        live.StatusLogged += (_, message) => Log($"  [{live.Title}] {message}");
        await WaitFor(() => live.Rdp.IsClientReady, TimeSpan.FromSeconds(20));
        if (!stored)
            live.Connect();
        bool loggedIn = await WaitFor(() => live.IsLoggedIn, TimeSpan.FromSeconds(90));
        Check($"Live session logs in without a credential prompt (NLA, {(stored ? "stored password, auto-connect" : "typed password")})",
            loggedIn && live.UsedStoredPassword == stored);
        if (!loggedIn)
        {
            live.Close();
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(5));
        string? png = live.SaveSnapshot("selftest-live");
        Check($"Live snapshot is a non-blank PNG ({png})", png is not null && File.Exists(png) && !CapturedBlank(live));
        Check("Snapshot becomes the connection preview", connection.HasPreview);
        SaveScreenProbe(live, "session-window-live.png");
        await RunLiveRdpPreviewChecksAsync(window, live, connection); // SelfTest.LivePreview.cs

        // Resize: dynamic resolution should follow the viewport after the debounce.
        PixelSize before = live.LastRemoteDesktopSize;
        live.Width -= 240;
        live.Height -= 120;
        await Settle(4000);
        PixelSize viewport = live.Rdp.ViewportPixelSize;
        Log($"Resize: remote desktop {before} -> {live.LastRemoteDesktopSize}, viewport {viewport}");
        Check("Dynamic resolution follows a window resize", live.LastRemoteDesktopSize == viewport);

        // Monitor move (only meaningful with a second screen at a different scaling).
        Screen? current = live.Screens.ScreenFromWindow(live);
        Screen? target = live.Screens.All.FirstOrDefault(s => current is null || s.Scaling != current.Scaling);
        if (target is not null && current is not null)
        {
            live.Position = new PixelPoint(target.WorkingArea.X + 50, target.WorkingArea.Y + 50);
            await Settle(4000);
            Log($"Moved to {target.DisplayName} (scaling {target.Scaling}): RenderScaling {live.RenderScaling}, viewport {live.Rdp.ViewportPixelSize}, remote {live.LastRemoteDesktopSize}");
            Check("Session alive after moving to a monitor with different scaling", live.IsLoggedIn);
        }
        else
        {
            Log("SKIP DPI move: no second screen with a different scaling.");
        }

        live.Disconnect();
        bool gone = await WaitFor(() => !live.IsSessionActive, TimeSpan.FromSeconds(20));
        Check($"User disconnect leaves a thumbnail ({live.LastThumbnailPath})",
            gone && live.LastThumbnailPath is not null && File.Exists(live.LastThumbnailPath));
        live.Close();
        File.Delete(connection.Model.ResolvedPreviewPath);
    }

    /// <summary>
    /// SQLite store on a temporary folder: one-time migration from a connections.json, round trip of
    /// every field, and no password in the database file. Then the real database: no secret column and
    /// none of the stored passwords in the file. Only "RemoteManager:selftest-*" credentials are written.
    /// </summary>
    private static async Task RunDatabaseChecksAsync(AppHost app)
    {
        string folder = Path.Combine(Path.GetTempPath(), $"rm-selftest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        string credentialId = TestId("db-1");
        string secret = $"Rm-{Guid.NewGuid():N}-äß€";
        try
        {
            string json = Path.Combine(folder, DataStore.LegacyJsonFileName);
            await File.WriteAllTextAsync(json, $$"""
                [
                  { "Id": "{{credentialId}}", "Name": "dc01", "Type": "Rdp", "Host": "10.0.0.10", "Port": 3389, "Description": "Domain controller",
                    "Group": "Kunde A", "Tags": ["prod", "ad"], "UserName": "admin", "Domain": "CORP", "DisplayMode": 2,
                    "WinKeysToSession": true, "LastConnectedAt": "2026-10-30T19:00:00Z", "IsFavorite": true },
                  { "Id": "{{TestId("db-2")}}", "Name": "Kasse 2", "Type": "AnyDesk", "Host": "987 654 321", "Port": 0, "Description": "",
                    "Group": "Laden", "Tags": ["kasse"], "UserName": "", "Domain": "", "DisplayMode": 0, "WinKeysToSession": true,
                    "LastConnectedAt": null, "IsFavorite": false }
                ]
                """);
            WindowsCredentialSource.Write(credentialId, secret);

            var windows = new WindowsCredentialSource();
            (ConnectionLibrary library, DataStoreReport report) = await DataStore.OpenAsync(folder, windows);
            Connection? dc01 = library.FindConnection(credentialId);
            Check($"Database: connections.json migrated once ({report.MigratedFromJson} connections, backup {Path.GetFileName(report.JsonBackupPath)})",
                report.MigratedFromJson == 2 && !File.Exists(json) && File.Exists(json + ".bak") && library.Connections.Count == 2
                && dc01 is { Name: "dc01", Host: "10.0.0.10", UserName: "admin", Domain: "CORP", IsFavorite: true }
                && dc01.Tags.SequenceEqual(["prod", "ad"]) && library.PathOf(dc01.GroupId) == "Kunde A"
                && dc01.Credential == new CredentialRef(WindowsCredentialSource.SourceId, credentialId)
                && dc01.SettingsAs<RdpSettings>() is { Display: RdpDisplayMode.FixedResolution, DesktopWidth: 1920 });

            // Round trip: change every kind of field, reopen from disk, compare.
            ConnectionGroup site = library.EnsureGroupPath("Kunde A / Standort 1")!;
            dc01!.GroupId = site.Id;
            dc01.Notes = "Wartung Sa 20 Uhr";
            dc01.Tags = ["prod", "ad", "dc"];
            dc01.Settings = dc01.SettingsAs<RdpSettings>() with { GatewayHost = "gw.example.com", Redirections = new RdpRedirections { Drives = true } };
            library.Update(dc01);
            var reopened = new ConnectionLibrary(new SqliteConnectionStore(report.DatabasePath));
            Connection? again = reopened.FindConnection(credentialId);
            (ConnectionLibrary second, DataStoreReport secondReport) = await DataStore.OpenAsync(folder, windows);
            Check("Database: round trip keeps group path, notes, tags, settings, and credential reference; no second migration",
                again is not null && reopened.PathOf(again.GroupId) == "Kunde A / Standort 1" && again.Notes == dc01.Notes
                && again.Tags.SequenceEqual(dc01.Tags) && again.Settings == dc01.Settings && again.Credential == dc01.Credential
                && secondReport.MigratedFromJson == 0 && second.Connections.Count == 2);

            Check("Database: the password is in the Credential Manager and not in the database file",
                WindowsCredentialSource.Read(credentialId) == secret && !FileContains(report.DatabasePath, secret) && !HasSecretColumn(report.DatabasePath));

            // The real database: same rules, checked against every password stored for its connections.
            string real = app.StoreReport.DatabasePath;
            int stored = 0;
            bool leak = false;
            foreach (Connection c in app.Library.Connections.Where(c => c.Credential is not null))
            {
                if (await app.Credentials.ReadAsync(c.Credential) is { Length: > 0 } password)
                {
                    stored++;
                    leak |= FileContains(real, password);
                }
            }

            Check($"Real database has no secret column and none of its {stored} stored passwords", !HasSecretColumn(real) && !leak);
        }
        finally
        {
            WindowsCredentialSource.Delete(credentialId);
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException ex)
            {
                Log($"  could not delete {folder}: {ex.Message}");
            }
        }
    }

    /// <summary>The background status checker: blocked or slow targets end as "Unbekannt" and never block the UI thread.</summary>
    private static async Task RunStatusCheckerChecksAsync(AppHost app)
    {
        // 10.255.255.1 is not routed (the connect times out), .invalid never resolves.
        Connection[] targets =
        [
            new() { Name = "blackhole", Host = "10.255.255.1" },
            new() { Name = "dns", Host = "rm-selftest-status.invalid" },
            new() { Name = "teamviewer", Type = ConnectionType.TeamViewer, Host = "123" },
        ];
        var results = new Dictionary<Connection, OnlineStatus>();
        void OnChanged(object? sender, StatusChangedEventArgs e)
        {
            if (targets.Contains(e.Connection))
                lock (results)
                    results[e.Connection] = e.Status;
        }

        app.Status.StatusChanged += OnChanged;
        try
        {
            var watch = Stopwatch.StartNew();
            Task checking = app.Status.CheckAsync(targets);
            TimeSpan started = watch.Elapsed;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Input);
            TimeSpan responsive = watch.Elapsed;
            await checking;
            Check($"Status check runs in the background (call returned after {started.TotalMilliseconds:F0} ms, UI answered after {responsive.TotalMilliseconds:F0} ms, done after {watch.Elapsed.TotalSeconds:F1} s)",
                started < TimeSpan.FromMilliseconds(200) && responsive < TimeSpan.FromMilliseconds(500));
            Check($"Timeout, DNS failure, and remote tools show as unknown ({string.Join(", ", results.Select(r => $"{r.Key.Name}={r.Value}"))})",
                results.Count == targets.Length && results.Values.All(v => v == OnlineStatus.Unknown));
        }
        finally
        {
            app.Status.StatusChanged -= OnChanged;
        }
    }

    private static bool FileContains(string path, string text)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return Encoding.UTF8.GetString(bytes).Contains(text, StringComparison.Ordinal)
            || Encoding.Unicode.GetString(bytes).Contains(text, StringComparison.Ordinal);
    }

    private static bool HasSecretColumn(string databasePath)
    {
        using var db = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        db.Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master m JOIN pragma_table_info(m.name) c " +
            "WHERE m.type = 'table' AND (lower(c.name) LIKE '%password%' OR lower(c.name) LIKE '%secret%')";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static bool CapturedBlank(SessionWindow window) =>
        SessionCapture.Capture(window.Rdp.HostWindowHandle) is not { IsBlank: false };

    private static async Task RunAirspaceProbeAsync(SessionWindow window)
    {
        window.Activate();
        await Settle(500);
        // The surface is normally hidden while idle; show it so the probe has a native window to overlap.
        window.Rdp.IsVisible = true;
        await Settle(500);
        int before = CountVisibleTopLevelWindows();
        window.MenuButton.Flyout!.ShowAt(window.MenuButton);
        await Settle(600);
        int after = CountVisibleTopLevelWindows();
        SaveScreenProbe(window, "probe-menu.png");
        Check($"Menu flyout opens as a native window above the RDP surface (visible top-levels {before} -> {after})", after > before);
        window.MenuButton.Flyout.Hide();
        window.Rdp.IsVisible = false;
        await Settle(300);
    }

    private static nint HandleOf(Window window) => window.TryGetPlatformHandle()?.Handle ?? 0;

    /// <summary>
    /// Renders the window's own visual tree to a PNG. Unlike a screen copy this never contains other
    /// windows, whatever is in front of ours. It cannot show native child windows (the RDP surface)
    /// or popups (menus), so the airspace probes stay screen copies.
    /// </summary>
    private static void SaveRenderProbe(Window window, string name)
    {
        double scale = window.RenderScaling;
        var dpi = new Vector(96 * scale, 96 * scale);
        Size size = window.ClientSize;
        using var content = new RenderTargetBitmap(PixelSize.FromSize(size, scale), dpi);
        content.Render(window);

        // Mica is transparent in a render; put the theme's solid base color behind it.
        IBrush background = window.TryFindResource("SolidBackgroundFillColorBaseBrush", window.ActualThemeVariant, out object? brush)
            && brush is IBrush b ? b : Brushes.Black;
        using var composed = new RenderTargetBitmap(PixelSize.FromSize(size, scale), dpi);
        using (DrawingContext context = composed.CreateDrawingContext())
        {
            context.FillRectangle(background, new Avalonia.Rect(size));
            context.DrawImage(content, new Avalonia.Rect(size));
        }

        string path = Path.Combine(AppPaths.Spike, name);
        composed.Save(path, PngBitmapEncoderOptions.Default);
        Log($"  probe saved: {path}");
    }

    private static void SaveScreenProbe(Window window, string name)
    {
        if (window.TryGetPlatformHandle() is not { } handle || !GetWindowRect(handle.Handle, out Rect rect))
            return;

        // Screen copy of the window area: shows what is actually composed on top.
        SessionFrame? frame = SessionCapture.CaptureScreen(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        if (frame is null)
            return;
        string path = Path.Combine(AppPaths.Spike, name);
        frame.SavePng(path);
        Log($"  probe saved: {path}");
    }

    private static int CountVisibleTopLevelWindows()
    {
        int count = 0;
        uint pid = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint owner);
            if (owner == pid && IsWindowVisible(hwnd))
                count++;
            return true;
        }, 0);
        return count;
    }

    private static async Task<bool> WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
                return true;
            await Task.Delay(100);
        }

        return condition();
    }

    private static async Task Settle(int milliseconds)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Task.Delay(milliseconds);
    }

    private static void Check(string name, bool passed)
    {
        if (!passed)
            failures++;
        Log($"{(passed ? "PASS" : "FAIL")} {name}");
    }

    private static void Fail(string message)
    {
        failures++;
        Log($"FAIL {message}");
    }

    private static void Log(string line)
    {
        Lines.Add(line);
        Trace.WriteLine(line);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    private const uint GaRoot = 2;

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint window, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out Rect rect);

    /// <summary>App icon: embedded in the exe (ApplicationIcon), set on the main window, and given to any new window by the App style.</summary>
    private static void CheckAppIcon(MainWindow window)
    {
        string exe = Environment.ProcessPath ?? "";
        Check($"The exe embeds the app icon ({Path.GetFileName(exe)})", ExtractIconExW(exe, -1, 0, 0, 0) > 0);
        Check("The main window has the app icon (Avalonia and Win32)", window.Icon is not null && HasWindowIcon(HandleOf(window)));
        var probe = new Window();
        probe.ApplyStyling();
        Check("A new window gets the app icon from the App style", probe.Icon is not null);
    }

    private static bool HasWindowIcon(nint window) =>
        SendMessageW(window, WmGetIcon, IconSmall, 0) != 0 && SendMessageW(window, WmGetIcon, IconBig, 0) != 0;

    private const uint WmGetIcon = 0x007F;
    private const nint IconSmall = 0, IconBig = 1;

    [DllImport("user32.dll")]
    private static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string file, int index, nint large, nint small, uint count);
}
