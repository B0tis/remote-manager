namespace RemoteManager.Core.Model.Settings;

public enum RemoteToolMode { Control, FileTransfer, Vpn }

/// <summary>Options of TeamViewer, AnyDesk, and RustDesk connections (launched externally).</summary>
public sealed record RemoteToolSettings : ConnectionSettings
{
    /// <summary>Alias in the tool (TeamViewer device name, AnyDesk alias).</summary>
    public string Alias { get; init; } = "";
    public RemoteToolMode Mode { get; init; } = RemoteToolMode.Control;

    /// <summary>Appended to the command line as typed. Never put a password here.</summary>
    public string ExtraArguments { get; init; } = "";
}
