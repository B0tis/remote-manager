using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace RemoteManager.App;

/// <summary>
/// Automated checks (<c>--selftest</c>). Writes a log plus probe screenshots to
/// %LOCALAPPDATA%\RemoteManager\spike and exits with 0 when every executed check passed.
/// Checks that need a real RDP server only run when RM_SPIKE_HOST is set
/// (RM_SPIKE_USER / RM_SPIKE_DOMAIN / RM_SPIKE_PASSWORD prefill the form; never logged).
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
            Name = "Selftest live",
            Host = Environment.GetEnvironmentVariable("RM_SPIKE_HOST")!,
            UserName = Environment.GetEnvironmentVariable("RM_SPIKE_USER") ?? "",
            Domain = Environment.GetEnvironmentVariable("RM_SPIKE_DOMAIN") ?? "",
        };
        SessionWindow live = window.OpenSession(connection)!;
        live.StatusLogged += (_, message) => Log($"  [{live.Title}] {message}");
        await WaitFor(() => live.Rdp.IsClientReady, TimeSpan.FromSeconds(20));
        live.Connect();
        bool loggedIn = await WaitFor(() => live.IsLoggedIn, TimeSpan.FromSeconds(90));
        Check("Live session logs in without a credential prompt (NLA, stored credentials)", loggedIn);
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
