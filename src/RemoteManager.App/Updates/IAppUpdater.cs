namespace RemoteManager.App.Updates;

/// <summary>A newer version the source offers; <see cref="Handle"/> is the updater's own description of it.</summary>
public sealed record AvailableUpdate(string Version, object Handle);

/// <summary>
/// Checks one update source, downloads, and applies. <see cref="VelopackUpdater"/> in the app; the self-test
/// puts a fake in its place (<see cref="AppUpdates.CreateUpdater"/>).
/// </summary>
public interface IAppUpdater
{
    /// <summary>False for a build that was not installed by Setup (dev build, the self-test): it cannot update itself.</summary>
    bool IsInstalled { get; }

    /// <summary>The install folder, or null when not installed.</summary>
    string? InstallFolder { get; }

    /// <summary>The newer version, or null when this one is current.</summary>
    Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default);

    Task DownloadAsync(AvailableUpdate update, Action<int> progress, CancellationToken cancellationToken = default);

    /// <summary>Ends the app, installs the downloaded update, and starts the new version. Does not return for the real updater.</summary>
    void ApplyAndRestart(AvailableUpdate update);
}
