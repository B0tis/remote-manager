using RemoteManager.Core.Updates;
using Velopack;
using Velopack.Sources;

namespace RemoteManager.App.Updates;

/// <summary>
/// Velopack (MIT) behind <see cref="IAppUpdater"/>. GitHub releases (with a token for a private repository; it
/// only goes into the Authorization header, Velopack logs URLs), a web folder, or a local or network folder.
/// </summary>
public sealed class VelopackUpdater(UpdateSource source, string? gitHubToken) : IAppUpdater
{
    private readonly UpdateManager manager = new(CreateSource(source, gitHubToken));

    public bool IsInstalled => manager.IsInstalled;

    /// <summary>Setup puts the app into &lt;install folder&gt;\current; Update.exe sits next to that.</summary>
    public string? InstallFolder => manager.IsInstalled ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)) : null;

    public async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        UpdateInfo? info = await Task.Run(manager.CheckForUpdatesAsync, cancellationToken).ConfigureAwait(false);
        return info is null ? null : new AvailableUpdate(info.TargetFullRelease.Version.ToString(), info);
    }

    public Task DownloadAsync(AvailableUpdate update, Action<int> progress, CancellationToken cancellationToken = default) =>
        Task.Run(() => manager.DownloadUpdatesAsync((UpdateInfo)update.Handle, progress, cancellationToken), cancellationToken);

    public void ApplyAndRestart(AvailableUpdate update) => manager.ApplyUpdatesAndRestart(((UpdateInfo)update.Handle).TargetFullRelease);

    private static IUpdateSource CreateSource(UpdateSource source, string? token) => source.Kind switch
    {
        UpdateSourceKind.GitHub => new GithubSource(source.Location, token, prerelease: false),
        UpdateSourceKind.Web => new SimpleWebSource(source.Location),
        _ => new SimpleFileSource(new DirectoryInfo(source.Location)),
    };
}
