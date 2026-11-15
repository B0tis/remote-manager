using Avalonia;

namespace RemoteManager.App;

internal static class Program
{
    /// <summary>
    /// Command-line switches for the spike:
    ///   --composition=winui|dcomp|redirection|lowlatency  (Win32 composition mode, default: Avalonia's choice)
    ///   --overlay-popups                                   (draw popups inside the window instead of as native windows)
    ///   --selftest                                         (run the automated spike checks and exit, see SelfTest.cs)
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        // Self-test stand-in for an external remote tool (see FakeRemoteTool); no UI. Setup never sets this variable.
        if (Environment.GetEnvironmentVariable(Diagnostics.FakeRemoteTool.Variable) is { Length: > 0 } report)
            return Diagnostics.FakeRemoteTool.Run(report, args);

        // Before anything else of the app: Setup and the updater start the exe with hook arguments and expect it
        // to exit. No auto-apply at startup: an update is only applied from the app, after the database backup.
        Velopack.VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .OnRestarted(version => Updates.AppUpdates.RestartedToVersion = version.ToString())
            .Run();

        // Crash reports in <data>\logs; the UI thread's handler follows in App once Avalonia runs.
        Diagnostics.CrashLog.InstallProcessHandlers();

        AppOptions.Parse(args);

        // A second start (the window was closed to the tray) shows the running instance and ends, before any of
        // Avalonia starts. Test runs are not guarded: they use their own data folder.
        bool guarded = !(AppOptions.SelfTest || AppOptions.Tour || AppOptions.LiveTest);
        using Services.SingleInstance? instance = guarded ? Services.SingleInstance.TryStart(ShowRunningWindow) : null;
        if (guarded && instance is null)
            return 0;

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Another start asked for the window (thread-pool thread).</summary>
    private static void ShowRunningWindow() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            ((Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
                ?.MainWindow as Views.MainWindow)?.ShowFromTray());

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

        var win32 = new Win32PlatformOptions { OverlayPopups = AppOptions.OverlayPopups };
        if (AppOptions.CompositionMode is { } mode)
            win32.CompositionMode = [mode];

        return builder.With(win32);
    }
}

internal static class AppOptions
{
    public static Win32CompositionMode? CompositionMode { get; private set; }
    public static bool OverlayPopups { get; private set; }
    public static bool SelfTest { get; private set; }

    /// <summary>--tour: demo data and pictures of the main screens (Diagnostics/SelfTest.Tour.cs).</summary>
    public static bool Tour { get; private set; }

    /// <summary>--livetest: only the checks against a real server (Diagnostics/SelfTest.cs, RunLiveOnlyAsync).</summary>
    public static bool LiveTest { get; private set; }

    public static void Parse(string[] args)
    {
        foreach (string arg in args)
        {
            if (arg.Equals("--selftest", StringComparison.OrdinalIgnoreCase))
                SelfTest = true;
            else if (arg.Equals("--tour", StringComparison.OrdinalIgnoreCase))
                Tour = true;
            else if (arg.Equals("--livetest", StringComparison.OrdinalIgnoreCase))
                LiveTest = true;
            else if (arg.Equals("--overlay-popups", StringComparison.OrdinalIgnoreCase))
                OverlayPopups = true;
            else if (arg.StartsWith("--composition=", StringComparison.OrdinalIgnoreCase))
            {
                CompositionMode = arg["--composition=".Length..].ToLowerInvariant() switch
                {
                    "winui" => Win32CompositionMode.WinUIComposition,
                    "dcomp" => Win32CompositionMode.DirectComposition,
                    "redirection" => Win32CompositionMode.RedirectionSurface,
                    "lowlatency" => Win32CompositionMode.LowLatencyDxgiSwapChain,
                    _ => null
                };
            }
        }
    }

    public static string Describe() =>
        $"composition={(CompositionMode?.ToString() ?? "default")}, overlayPopups={OverlayPopups}";
}
