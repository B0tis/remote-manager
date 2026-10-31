namespace RemoteManager.Core.Model.Settings;

public enum SshAuthMethod { Password, KeyFile, Agent }

public enum PortForwardKind { Local, Remote, Dynamic }

/// <summary>One -L / -R / -D style forward.</summary>
public sealed record PortForward(PortForwardKind Kind, string BindAddress, int BindPort, string TargetHost, int TargetPort);

/// <summary>Options of SSH and SFTP connections.</summary>
public sealed record SshSettings : ConnectionSettings
{
    public SshAuthMethod AuthMethod { get; init; } = SshAuthMethod.Password;

    /// <summary>Private key file (its passphrase, if any, goes through the connection's credential).</summary>
    public string KeyPath { get; init; } = "";

    /// <summary>SSH connection to hop through, or null.</summary>
    public string? JumpHostId { get; init; }

    public IReadOnlyList<PortForward> PortForwards { get; init; } = [];

    /// <summary>Accepted host key (for example "SHA256:..."); empty until the first confirmed connect.</summary>
    public string HostKeyFingerprint { get; init; } = "";

    /// <summary>SFTP: folder to open first; SSH: ignored.</summary>
    public string RemoteDirectory { get; init; } = "";
}
