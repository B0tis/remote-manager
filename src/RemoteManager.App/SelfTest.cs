using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace RemoteManager.App;

/// <summary>
/// Automated spike checks (<c>--selftest</c>). Writes a log plus probe screenshots to
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
        Log($"Remote-Manager spike self-test {DateTime.Now:O}");
        Log($"OS {Environment.OSVersion}, .NET {Environment.Version}, {AppOptions.Describe()}");
        await Task.Delay(500);
        Log($"Window RenderScaling {window.RenderScaling}");
        foreach (Screen screen in window.Screens.All)
            Log($"Screen {screen.DisplayName}: bounds {screen.Bounds}, scaling {screen.Scaling}, primary {screen.IsPrimary}");

        // --- Two sessions, both get a live ActiveX client --------------------------------------------
        SessionView first = window.Sessions[0];
        SessionView second = window.AddSession();
        foreach (SessionView s in window.Sessions)
            s.StatusLogged += (_, message) => Log($"  [{s.Title}] {message}");

        Check("Both RDP clients ready",
            await WaitFor(() => first.Rdp.IsClientReady && second.Rdp.IsClientReady, TimeSpan.FromSeconds(20)));

        // --- Tab switching via IsVisible keeps the native window ------------------------------------
        // Idle surfaces are hidden; show both as if connected so the switch is observable.
        first.Rdp.IsVisible = second.Rdp.IsVisible = true;
        nint firstHost = first.Rdp.HostWindowHandle, secondHost = second.Rdp.HostWindowHandle;
        Log($"Host HWNDs: first 0x{firstHost:X}, second 0x{secondHost:X}");
        bool stable = true;
        for (int i = 0; i < 6; i++)
        {
            SessionView shown = i % 2 == 0 ? first : second;
            SessionView hidden = i % 2 == 0 ? second : first;
            window.SelectSession(shown);
            await Settle(300);
            bool ok = first.Rdp.HostWindowHandle == firstHost && second.Rdp.HostWindowHandle == secondHost
                && IsWindow(firstHost) && IsWindow(secondHost)
                && first.Rdp.IsClientReady && second.Rdp.IsClientReady
                && IsWindowVisible(shown.Rdp.HostWindowHandle) && !IsWindowVisible(hidden.Rdp.HostWindowHandle);
            stable &= ok;
            if (!ok)
                Log($"  switch {i}: shownVisible={IsWindowVisible(shown.Rdp.HostWindowHandle)} hiddenVisible={IsWindowVisible(hidden.Rdp.HostWindowHandle)}");
        }

        Check("Tab switching keeps both HWNDs and clients; only the selected one is visible", stable);
        first.Rdp.IsVisible = second.Rdp.IsVisible = false;

        // --- Closing a tab disposes its session and destroys its native window -----------------------
        SessionView third = window.AddSession();
        bool thirdReady = await WaitFor(() => third.Rdp.IsClientReady, TimeSpan.FromSeconds(20));
        nint thirdHost = third.Rdp.HostWindowHandle;
        window.CloseSession(third);
        await Settle(500);
        Check("Closing a tab disposes its RDP view and destroys its HWND",
            thirdReady && thirdHost != 0 && !IsWindow(thirdHost) && window.Sessions.Count == 2);

        // --- Disconnect handling: unresolvable host, then reuse the same view ------------------------
        window.SelectSession(second);
        await Settle(300);
        // 127.0.0.1 has no RDP listener on this box (fast "connection refused"); the .invalid name
        // exercises DNS failure. RM_SPIKE_UNREACHABLE overrides both.
        string[] unreachable = Environment.GetEnvironmentVariable("RM_SPIKE_UNREACHABLE") is { Length: > 0 } custom
            ? [custom]
            : ["127.0.0.1", "rm-spike-unreachable.invalid"];
        for (int attempt = 1; attempt <= unreachable.Length; attempt++)
        {
            bool disconnected = false;
            void OnDisconnected(object? s, EventArgs e) => disconnected = true;
            second.SessionDisconnected += OnDisconnected;
            second.Host = unreachable[attempt - 1];
            var sw = Stopwatch.StartNew();
            second.Connect();
            bool raised = await WaitFor(() => disconnected, TimeSpan.FromSeconds(120));
            second.SessionDisconnected -= OnDisconnected;
            Check($"Failed connect to {unreachable[attempt - 1]} raises Disconnected ({sw.Elapsed.TotalSeconds:F1}s, status \"{second.LastStatus}\")",
                raised && second.Rdp.IsClientReady && !second.IsSessionActive);
        }

        // --- Real server (optional) --------------------------------------------------------------------
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RM_SPIKE_HOST")))
            await RunLiveSessionChecksAsync(window, first, second);
        else
            Log("SKIP live-session checks (RM_SPIKE_HOST not set): logon, screenshot, resize, tab switch while connected.");

        // --- Airspace probes -------------------------------------------------------------------------
        await RunAirspaceProbesAsync(window, first);
    }

    private static async Task RunLiveSessionChecksAsync(MainWindow window, SessionView live, SessionView other)
    {
        window.SelectSession(live);
        await Settle(300);
        live.Connect();
        bool loggedIn = await WaitFor(() => live.IsLoggedIn, TimeSpan.FromSeconds(90));
        Check("Live session logs in without a credential prompt (NLA, stored credentials)", loggedIn);
        if (!loggedIn)
            return;

        await Task.Delay(TimeSpan.FromSeconds(5));
        string? png = live.SaveSnapshot("selftest-live");
        Check($"Live snapshot is a non-blank PNG ({png})", png is not null && File.Exists(png) && !CapturedBlank(live));

        // Tab switch while connected.
        window.SelectSession(other);
        await Settle(1000);
        SessionFrame? hiddenFrame = SessionCapture.Capture(live.Rdp.HostWindowHandle);
        Log($"Capture while tab hidden: {(hiddenFrame is null ? "failed" : hiddenFrame.IsBlank ? "blank" : $"ok via {hiddenFrame.Method}")}");
        window.SelectSession(live);
        await Settle(1000);
        Check("Session survives a tab switch", live.IsSessionActive && live.IsLoggedIn);

        // Resize: dynamic resolution should follow the viewport after the debounce.
        PixelSize before = live.LastRemoteDesktopSize;
        window.Width -= 240;
        window.Height -= 120;
        await Settle(4000);
        PixelSize viewport = live.Rdp.ViewportPixelSize;
        Log($"Resize: remote desktop {before} -> {live.LastRemoteDesktopSize}, viewport {viewport}");
        Check("Dynamic resolution follows a window resize", live.LastRemoteDesktopSize == viewport);
        window.Width += 240;
        window.Height += 120;
        await Settle(2000);

        // Monitor move (only meaningful with a second screen at a different scaling).
        Screen? current = window.Screens.ScreenFromWindow(window);
        Screen? target = window.Screens.All.FirstOrDefault(s => current is null || s.Scaling != current.Scaling);
        if (target is not null && current is not null)
        {
            window.Position = new PixelPoint(target.WorkingArea.X + 50, target.WorkingArea.Y + 50);
            await Settle(4000);
            Log($"Moved to {target.DisplayName} (scaling {target.Scaling}): RenderScaling {window.RenderScaling}, viewport {live.Rdp.ViewportPixelSize}, remote {live.LastRemoteDesktopSize}");
            live.SaveSnapshot("selftest-other-monitor");
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
    }

    private static bool CapturedBlank(SessionView view) =>
        SessionCapture.Capture(view.Rdp.HostWindowHandle) is not { IsBlank: false };

    private static async Task RunAirspaceProbesAsync(MainWindow window, SessionView view)
    {
        window.SelectSession(view);
        await Settle(500);
        // The surface is normally hidden while idle; show it so the probes have a native window to overlap.
        view.Rdp.IsVisible = true;
        await Settle(500);
        Log($"Airspace probes on '{view.Title}' (RDP host HWND visible {IsWindowVisible(view.Rdp.HostWindowHandle)})");
        int before = CountVisibleTopLevelWindows();
        view.MenuButton.Flyout!.ShowAt(view.MenuButton);
        await Settle(600);
        int after = CountVisibleTopLevelWindows();
        SaveScreenProbe(window, "probe-menu.png");
        Log($"MenuFlyout open: visible top-level windows of this process {before} -> {after} (popup is {(after > before ? "a native window" : "in-window")})");
        view.MenuButton.Flyout.Hide();
        await Settle(300);

        var (dialog, closed) = view.ShowInWindowDialog(useAirspaceWorkaround: false);
        await Settle(800);
        SaveScreenProbe(window, "probe-contentdialog-no-workaround.png");
        dialog.Hide();
        await closed;
        await Settle(300);

        (dialog, closed) = view.ShowInWindowDialog(useAirspaceWorkaround: true);
        await Settle(800);
        SaveScreenProbe(window, "probe-contentdialog-workaround.png");
        dialog.Hide();
        await closed;
        Log("Airspace probe screenshots written; inspect them to see what is drawn above the RDP surface.");
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out Rect rect);
}
