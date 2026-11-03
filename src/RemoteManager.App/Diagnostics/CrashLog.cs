using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using RemoteManager.App.Connections.Ssh;
using RemoteManager.App.Resources;
using RemoteManager.App.Updates;
using RemoteManager.Core;
using RemoteManager.Core.Diagnostics;

namespace RemoteManager.App.Diagnostics;

/// <summary>
/// Crash reports in <c>&lt;data&gt;\logs\crash-*.log</c> (<see cref="AppPaths.Logs"/>, so REMOTE_MANAGER_DATA applies).
/// <see cref="InstallProcessHandlers"/> hooks the AppDomain and unobserved tasks at start, <see cref="InstallDispatcherHandler"/>
/// the UI thread once Avalonia runs. The handlers only write the report: what crashed the app before still does.
/// </summary>
internal static class CrashLog
{
    public static string Folder => AppPaths.Logs;

    public static void InstallProcessHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                Write(ex, e.IsTerminating ? "AppDomain (terminating)" : "AppDomain");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // The WebView2 start abort (window closed early) also reaches here from Avalonia's adapter task; not a crash.
            if (!e.Exception.Flatten().InnerExceptions.All(SshTerminalWindow.IsWebViewStartAbort))
                Write(e.Exception, "unobserved task");
        };
    }

    public static void InstallDispatcherHandler()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            // SshTerminalWindow marks the WebView2 start abort as handled (nothing lost); that is not a crash.
            if (!e.Handled && !SshTerminalWindow.IsWebViewStartAbort(e.Exception))
                Write(e.Exception, "UI thread");
        };
    }

    /// <summary>Writes a report; never throws. Returns the file, or null.</summary>
    public static string? Write(Exception exception, string source)
    {
        string version = AppVersion.Commit is { } commit ? $"{AppVersion.Current} ({commit})" : AppVersion.Current;
        string? path = CrashLogFile.Write(Folder, exception, source, version);
        Trace.WriteLine($"Crash log ({source}): {exception.GetType().FullName} -> {path ?? "not written"}");
        return path;
    }

    /// <summary>Short German text for the UI: the exception type and where the report is.</summary>
    public static string Describe(Exception exception) =>
        string.Format(CultureInfo.CurrentCulture, Strings.Common_UnexpectedError, exception.GetType().Name, Folder);
}
