using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace RemoteManager.App;

/// <summary>
/// Automated checks (<c>--selftest</c>). Writes a log plus probe screenshots to
/// %LOCALAPPDATA%\RemoteManager\spike and exits with 0 when every executed check passed.
/// Checks that need a real RDP server only run when RM_SPIKE_HOST is set (RM_SPIKE_USER /
/// RM_SPIKE_DOMAIN fill the connection; RM_SPIKE_PASSWORD is stored as a temporary credential so the
/// window logs on by itself; never logged). Credential checks use "RemoteManager:selftest-*" only.
/// </summary>
internal static class SelfTest
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

        // --- Overview: store, tile view, list view ------------------------------------------------
        Check($"Overview lists the stored connections ({window.Connections.Count}, store {ConnectionStore.FilePath})",
            window.Connections.Count > 0 && window.VisibleConnections.Count == window.Connections.Count);
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
        Connection edited = window.Connections[0];
        string nameBefore = edited.Name;
        Task<bool> editing = ConnectionEditor.ShowAsync(edited, isNew: false, ["Homelab"]);
        await Settle(800);
        SaveScreenProbe(window, "edit-dialog.png");
        ConnectionEditor.OpenDialog?.Hide();
        Check("Edit dialog opens and Cancel leaves the connection unchanged", !await editing && edited.Name == nameBefore);

        await RunCredentialChecksAsync(window);

        // --- Two session windows, each with its own RDP client -------------------------------------
        // Temporary connections; they are not added to the store.
        var a = new Connection { Name = "Selftest A", Host = "127.0.0.1", Description = "Loopback: refused by the RDP client", UserName = "selftest" };
        var b = new Connection { Name = "Selftest B", Host = "rm-selftest-unreachable.invalid", Description = "DNS failure" };
        SessionWindow first = window.OpenSession(a)!;
        SessionWindow second = window.OpenSession(b)!;
        foreach (SessionWindow s in new[] { first, second })
            s.StatusLogged += (_, message) => Log($"  [{s.Title}] {message}");

        Check("Both session windows get a ready RDP client",
            await WaitFor(() => first.Rdp.IsClientReady && second.Rdp.IsClientReady, TimeSpan.FromSeconds(20)));
        Check("Window titles are the connection names", first.Title == a.Name && second.Title == b.Name);
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
            SessionWindow s = window.OpenSession(new Connection { Name = "Selftest custom", Host = custom })!;
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
            Log("SKIP live-session checks (RM_SPIKE_HOST not set): logon, snapshot as preview, resize, DPI.");

        // --- Airspace probe: a menu flyout over a visible RDP surface --------------------------------
        await RunAirspaceProbeAsync(first);

        first.Close();
        await Settle(500);
        Check("Closing the last session window destroys its HWND", !IsWindow(firstHost) && window.SessionWindows.Count == 0);
    }

    /// <summary>
    /// Credential Manager round trip, edit dialog, connections.json, session auto-connect, and the
    /// copy-password flow. Only uses "RemoteManager:selftest-*" targets and dummy values, and never
    /// touches the credentials of real connections. Values are never logged.
    /// </summary>
    private static async Task RunCredentialChecksAsync(MainWindow window)
    {
        string[] ids = ["selftest-roundtrip", "selftest-rdp", "selftest-anydesk"];
        foreach (string id in ids)
            CredentialStore.Delete(id); // leftovers of an aborted run

        // --- Round trip ---------------------------------------------------------------------------
        string secret = $"Rm-{Guid.NewGuid():N}-äß€";
        CredentialStore.Write(ids[0], secret);
        bool written = CredentialStore.Exists(ids[0]) && CredentialStore.Read(ids[0]) == secret;
        CredentialStore.Write(ids[0], secret + "2");
        bool replaced = CredentialStore.Read(ids[0]) == secret + "2";
        bool deleted = CredentialStore.Delete(ids[0]) && !CredentialStore.Exists(ids[0]) && CredentialStore.Read(ids[0]) is null
            && !CredentialStore.Delete(ids[0]);
        Check($"Credential Manager round trip ({CredentialStore.TargetFor(ids[0])}): write {written}, replace {replaced}, delete {deleted}",
            written && replaced && deleted);

        var rdp = new Connection
        {
            Id = ids[1], Name = "Selftest RDP", Host = "127.0.0.1", UserName = "selftest", Group = "Selftest",
            Description = "Stored password, loopback (refused)",
        };
        var tool = new Connection
        {
            Id = ids[2], Name = "Selftest AnyDesk", Type = ConnectionType.AnyDesk, Host = "123 456 789", Port = 0,
            Group = "Selftest", Description = "Stored password, copy flow", Tags = ["selftest"],
        };
        window.ShowUnsaved(rdp, true);
        window.ShowUnsaved(tool, true);
        IClipboard clipboard = window.Clipboard!;
        string? userClipboard = await clipboard.TryGetTextAsync();
        try
        {
            await RunEditorCredentialChecksAsync(window, rdp, secret);
            await RunSessionCredentialChecksAsync(window, rdp);
            await RunCopyPasswordChecksAsync(window, tool, clipboard);

            // Deleting a connection deletes its credential. The tool connection leaves the list first,
            // so the save inside RemoveConnection writes the user's connections unchanged.
            window.ShowUnsaved(tool, false);
            window.RemoveConnection(rdp);
            Check("Deleting a connection deletes its stored password", !CredentialStore.Exists(rdp.Id));
        }
        finally
        {
            window.ShowUnsaved(rdp, false);
            window.ShowUnsaved(tool, false);
            foreach (string id in ids)
                CredentialStore.Delete(id);
            if (userClipboard is not null)
                await clipboard.SetTextAsync(userClipboard);
        }
    }

    private static async Task RunEditorCredentialChecksAsync(MainWindow window, Connection rdp, string secret)
    {
        Task<bool> editing = ConnectionEditor.ShowAsync(rdp, isNew: false, ["Selftest"]);
        await Settle(800);
        ConnectionEditor editor = ConnectionEditor.OpenEditor!;
        editor.Password.Text = secret;
        await Settle(300);
        SaveRenderProbe(window, "edit-dialog-password.png");
        Check("Typing a password turns on \"Passwort speichern\"", editor.SavePassword.IsChecked == true);
        bool committed = editor.TryCommit(rdp);
        ConnectionEditor.OpenDialog?.Hide();
        await editing;
        Check("Saving the edit dialog writes the password to the Credential Manager",
            committed && CredentialStore.Read(rdp.Id) == secret && rdp.HasStoredPassword);

        editing = ConnectionEditor.ShowAsync(rdp, isNew: false, ["Selftest"]);
        await Settle(800);
        editor = ConnectionEditor.OpenEditor!;
        SaveRenderProbe(window, "edit-dialog-stored.png");
        Check("Reopened dialog shows \"Passwort gespeichert\" and never the value",
            editor.IsStoredPasswordShown && string.IsNullOrEmpty(editor.Password.Text));
        editor.RemovePassword.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle(300);
        SaveRenderProbe(window, "edit-dialog-remove.png");
        committed = editor.TryCommit(rdp);
        ConnectionEditor.OpenDialog?.Hide();
        await editing;
        Check("\"Entfernen\" plus Save deletes the credential", committed && !CredentialStore.Exists(rdp.Id) && !rdp.HasStoredPassword);

        // The JSON store never carries the password, neither for this connection nor for real ones.
        CredentialStore.Write(rdp.Id, secret);
        rdp.RefreshCredentialState();
        string json = ConnectionStore.Serialize(window.Connections);
        string onDisk = File.Exists(ConnectionStore.FilePath) ? await File.ReadAllTextAsync(ConnectionStore.FilePath) : "";
        var passwordKey = new Regex("\"[A-Za-z]*(Password|Secret)[A-Za-z]*\"\\s*:", RegexOptions.IgnoreCase);
        bool realLeak = window.Connections.Where(c => c.HasStoredPassword && c != rdp)
            .Any(c => CredentialStore.Read(c.Id) is { Length: > 0 } p && (onDisk.Contains(p) || json.Contains(p)));
        Check("connections.json has no password field and contains no stored password",
            !json.Contains(secret) && !passwordKey.IsMatch(json) && !passwordKey.IsMatch(onDisk) && !realLeak);
    }

    private static async Task RunSessionCredentialChecksAsync(MainWindow window, Connection rdp)
    {
        bool disconnected = false;
        SessionWindow s = window.OpenSession(rdp)!;
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

    private static async Task RunCopyPasswordChecksAsync(MainWindow window, Connection tool, IClipboard clipboard)
    {
        string secret = $"Rm-{Guid.NewGuid():N}";
        CredentialStore.Write(tool.Id, secret);
        tool.RefreshCredentialState();
        window.SetListView(false);
        window.Tiles.SelectedItem = tool;
        window.Tiles.ScrollIntoView(tool);
        await Settle(400);
        // Opening the menu runs its Opening handler, which decides whether "Passwort kopieren" shows.
        ContextMenu menu = window.Tiles.ContextMenu!;
        menu.Open(window.Tiles);
        await Settle(500);
        bool menuItem = menu.IsOpen && menu.Items.OfType<MenuItem>().Any(i => i.Header as string == "Passwort kopieren" && i.IsVisible);
        menu.Close();
        Check("Context menu offers \"Passwort kopieren\" for a connection with a stored password", menuItem);

        // Connect on a remote-tool entry: the dialog offers the same copy button.
        window.OpenSession(tool);
        await Settle(800);
        bool dialogButton = window.RemoteToolDialog?.PrimaryButtonText == "Passwort kopieren";
        SaveRenderProbe(window, "copy-password-dialog.png");
        window.RemoteToolDialog?.Hide();
        await Settle(300);
        Check("Remote-tool dialog offers \"Passwort kopieren\"", dialogButton);

        // Default delay first (for the probe), then a short one to see the clear happen.
        await window.CopyPasswordAsync(tool);
        await Settle(500);
        SaveRenderProbe(window, "copy-password-toast.png");
        bool copied = await clipboard.TryGetTextAsync() == secret;
        Check($"Copy puts the password on the clipboard and shows \"{window.ToastBar.Message}\"",
            copied && window.ToastBar.IsOpen && window.ToastBar.Message == "Kopiert, wird in 30 s geleert");

        TimeSpan clearAfter = TimeSpan.FromSeconds(2);
        await window.CopyPasswordAsync(tool, clearAfter);
        await Task.Delay(clearAfter + TimeSpan.FromSeconds(1));
        string? after = await clipboard.TryGetTextAsync();
        Check("Clipboard is cleared after the delay", string.IsNullOrEmpty(after) && !SecretClipboard.HasPending);

        await window.CopyPasswordAsync(tool, clearAfter);
        await clipboard.SetTextAsync("remote-manager selftest: copied by the user");
        await Task.Delay(clearAfter + TimeSpan.FromSeconds(1));
        Check("Something copied afterwards stays on the clipboard",
            await clipboard.TryGetTextAsync() == "remote-manager selftest: copied by the user");
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
        Check($"Failed connect to {s.Connection.Host} ({label}) raises Disconnected ({sw.Elapsed.TotalSeconds:F1}s, status \"{s.LastStatus}\")",
            raised && s.Rdp.IsClientReady && !s.IsSessionActive && !s.Rdp.IsVisible);
    }

    private static async Task RunLiveSessionChecksAsync(MainWindow window)
    {
        var connection = new Connection
        {
            Id = "selftest-live",
            Name = "Selftest live",
            Host = Environment.GetEnvironmentVariable("RM_SPIKE_HOST")!,
            UserName = Environment.GetEnvironmentVariable("RM_SPIKE_USER") ?? "",
            Domain = Environment.GetEnvironmentVariable("RM_SPIKE_DOMAIN") ?? "",
        };

        // With RM_SPIKE_PASSWORD the password goes through the Credential Manager, so the window
        // connects on its own (direct Windows logon); the credential is removed again afterwards.
        string? password = Environment.GetEnvironmentVariable("RM_SPIKE_PASSWORD");
        bool stored = !string.IsNullOrEmpty(password);
        if (stored)
            CredentialStore.Write(connection.Id, password!);
        try
        {
            await RunLiveSessionAsync(window, connection, stored);
        }
        finally
        {
            CredentialStore.Delete(connection.Id);
        }
    }

    private static async Task RunLiveSessionAsync(MainWindow window, Connection connection, bool stored)
    {
        SessionWindow live = window.OpenSession(connection)!;
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
        File.Delete(connection.ThumbnailPath);
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
}
