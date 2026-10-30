namespace RemoteManager.Core.Model;

/// <summary>
/// Every connection type the plan covers. Stored by name in the database, so the numeric values
/// may change, the names may not.
/// </summary>
public enum ConnectionType
{
    Rdp,
    TeamViewer,
    AnyDesk,
    RustDesk,
    Ssh,
    Sftp,
    Vnc,
}
