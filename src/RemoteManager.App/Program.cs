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

        AppOptions.Parse(args);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

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

    public static void Parse(string[] args)
    {
        foreach (string arg in args)
        {
            if (arg.Equals("--selftest", StringComparison.OrdinalIgnoreCase))
                SelfTest = true;
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
