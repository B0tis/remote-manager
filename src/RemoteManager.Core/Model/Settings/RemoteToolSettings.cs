namespace RemoteManager.Core.Model.Settings;

/// <summary>Session kind. Not every tool knows every mode (see <see cref="RemoteTools.RemoteToolCommandLine.ModesOf"/>).</summary>
public enum RemoteToolMode { Control, FileTransfer, Vpn, PortForward }

/// <summary>Where a remote-tool connection came from; synced ones are matched again by <see cref="RemoteToolSettings.ExternalId"/>.</summary>
public enum RemoteToolSource { Manual, TeamViewerApi, AnyDeskCsv }

/// <summary>Options of TeamViewer, AnyDesk, and RustDesk connections (launched externally).</summary>
public sealed record RemoteToolSettings : ConnectionSettings
{
    /// <summary>Alias in the tool (TeamViewer device name, AnyDesk alias). A sync overwrites it.</summary>
    public string Alias { get; init; } = "";
    public RemoteToolMode Mode { get; init; } = RemoteToolMode.Control;

    /// <summary>
    /// Appended to the command line, split like a command line, <c>{id}</c> replaced by the remote ID.
    /// Never a password: <see cref="RemoteTools.RemoteToolCommandLine.FindSecretArgument"/> rejects password switches.
    /// </summary>
    public string ExtraArguments { get; init; } = "";

    /// <summary>
    /// TeamViewer (<c>-p</c>) and RustDesk (<c>--password</c>) only: pass the stored password on the command
    /// line, where every process on the machine can read it. Off by default; the UI warns when it is on.
    /// AnyDesk always gets the password through stdin and ignores this.
    /// </summary>
    public bool CredentialOnCommandLine { get; init; }

    /// <summary>TeamViewer <c>-q</c>: 1 automatic, 2 quality, 3 speed, 4 custom, 5 default; 0 = not passed.</summary>
    public int Quality { get; init; }

    /// <summary>TeamViewer <c>-a</c>: 0 full, 1 confirm all, 2 view and show, 3 custom, 9 default; -1 = not passed.</summary>
    public int AccessControl { get; init; } = -1;

    /// <summary>AnyDesk <c>--full-screen</c>.</summary>
    public bool FullScreen { get; init; }

    /// <summary>AnyDesk <c>--plain</c>: window without border and menu.</summary>
    public bool Plain { get; init; }

    public RemoteToolSource Source { get; init; } = RemoteToolSource.Manual;

    /// <summary>Id at the source (TeamViewer device_id), used to match the device on the next sync.</summary>
    public string ExternalId { get; init; } = "";

    public DateTime? LastSyncedAt { get; init; }

    /// <summary>The source no longer lists the device. It is kept (with notes and tags), only marked.</summary>
    public bool RemovedAtSource { get; init; }
}
