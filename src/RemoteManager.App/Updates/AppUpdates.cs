using System.Diagnostics;
using System.Globalization;
using System.Net;
using RemoteManager.App.Resources;
using RemoteManager.Core;
using RemoteManager.Core.Updates;

namespace RemoteManager.App.Updates;

/// <summary>An update the source offers, with the updater that found it (it downloads and applies it too).</summary>
public sealed record UpdateOffer(IAppUpdater Updater, AvailableUpdate Update)
{
    public string Version => Update.Version;
}

/// <summary>
/// The app's update settings and steps. Source: the environment variable <see cref="SourceVariable"/> (for a
/// fleet), else setting "update.source", else GitHub releases of B0tis/remote-manager. A GitHub token for the
/// private repository lives in the default credential source (Windows Credential Manager), never in the
/// database or the log. The database is backed up right before an update is applied (last three kept).
/// </summary>
public static class AppUpdates
{
    public const string SourceSetting = "update.source";
    public const string LastCheckSetting = "update.lastCheck";
    public const string SourceVariable = "REMOTE_MANAGER_UPDATE_SOURCE";

    /// <summary>Credential key of the GitHub token. The self-test points it at a "selftest-" key.</summary>
    internal static string TokenKey { get; set; } = "update-github-token";

    /// <summary>The self-test puts a fake updater here.</summary>
    internal static Func<UpdateSource, string?, IAppUpdater> CreateUpdater { get; set; } = (source, token) => new VelopackUpdater(source, token);

    /// <summary>The self-test points this at a temporary folder.</summary>
    internal static string BackupFolder { get; set; } = AppPaths.Backups;

    /// <summary>Set by Velopack's restart hook (Program) when this start follows an update.</summary>
    public static string? RestartedToVersion { get; internal set; }

    /// <summary>True when <see cref="SourceVariable"/> fixes the source; the About dialog then only shows it.</summary>
    public static bool SourceFromEnvironment => Environment.GetEnvironmentVariable(SourceVariable) is { Length: > 0 };

    /// <summary>The source as entered (empty for the default).</summary>
    public static string SourceText =>
        Environment.GetEnvironmentVariable(SourceVariable) is { Length: > 0 } fixedSource ? fixedSource : AppHost.Current.Library.GetSetting(SourceSetting) ?? "";

    public static UpdateSource Source => UpdateSource.Parse(SourceText) ?? UpdateSource.Default;

    public static bool IsInstalled => CreateUpdater(UpdateSource.Default, null).IsInstalled;

    public static string? InstallFolder => CreateUpdater(UpdateSource.Default, null).InstallFolder;

    /// <summary>Time of the last check (local), or null.</summary>
    public static DateTime? LastCheck =>
        DateTime.TryParse(AppHost.Current.Library.GetSetting(LastCheckSetting), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime last)
            ? last.ToLocalTime()
            : null;

    /// <summary>Saves the source; false when <paramref name="text"/> is neither a URL nor a full folder path.</summary>
    public static bool TrySetSource(string? text)
    {
        if (UpdateSource.Parse(text) is not { } source)
            return false;
        AppHost.Current.Library.SetSetting(SourceSetting, source == UpdateSource.Default ? null : source.Location);
        return true;
    }

    public static async Task<bool> HasTokenAsync() =>
        await AppHost.Current.Credentials.DefaultSource.ExistsAsync(TokenKey).ConfigureAwait(false);

    public static async Task SaveTokenAsync(string token) =>
        await AppHost.Current.Credentials.DefaultSource.WriteAsync(TokenKey, token.Trim()).ConfigureAwait(false);

    public static async Task DeleteTokenAsync() =>
        await AppHost.Current.Credentials.DefaultSource.DeleteAsync(TokenKey).ConfigureAwait(false);

    /// <summary>
    /// Asks the source for a newer version. At startup (<paramref name="startup"/>) only when the last check is
    /// older than <see cref="UpdateSchedule.Interval"/>. Null when there is none or the check was not due.
    /// Call on the UI thread (settings); the network part runs on the thread pool.
    /// </summary>
    public static async Task<UpdateOffer?> CheckAsync(bool startup, CancellationToken cancellationToken = default)
    {
        DateTime now = DateTime.UtcNow;
        if (startup && !UpdateSchedule.IsStartupCheckDue(AppHost.Current.Library.GetSetting(LastCheckSetting), now))
            return null;

        UpdateSource source = Source;
        string? token = source.CanUseToken ? await AppHost.Current.Credentials.DefaultSource.ReadAsync(TokenKey, cancellationToken).ConfigureAwait(true) : null;
        IAppUpdater updater = CreateUpdater(source, token);
        AppHost.Current.Library.SetSetting(LastCheckSetting, UpdateSchedule.Format(now));
        Trace.WriteLine($"Update check ({(startup ? "startup" : "manual")}) at {source.Kind} {source.Location}, token {(token is null ? "no" : "yes")}");

        AvailableUpdate? update = await updater.CheckAsync(cancellationToken).ConfigureAwait(true);
        Trace.WriteLine($"Update check: {(update is null ? "current" : "offers " + update.Version)}");
        return update is null ? null : new UpdateOffer(updater, update);
    }

    /// <summary>Copies the database to <see cref="BackupFolder"/> (newest three kept). Returns the copy.</summary>
    public static string BackupDatabase()
    {
        string backup = DatabaseBackup.Create(AppHost.Current.StoreReport.DatabasePath, BackupFolder, AppVersion.Current);
        Trace.WriteLine($"Database backup before update: {backup}");
        return backup;
    }

    /// <summary>A German message for a failed check or download.</summary>
    public static string Describe(Exception ex)
    {
        bool denied = false;
        for (Exception? e = ex; e is not null; e = e.InnerException)
            denied |= e is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden };

        string message = string.Format(CultureInfo.CurrentCulture, Strings.Update_Failed, ex.Message);
        return denied && Source.CanUseToken ? $"{message} {Strings.Update_PrivateRepoHint}" : message;
    }
}
