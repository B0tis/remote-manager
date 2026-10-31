using Renci.SshNet.Common;

namespace RemoteManager.Core.Ssh;

public enum HostKeyStatus
{
    /// <summary>Matches the stored key.</summary>
    Trusted,
    /// <summary>No key stored yet (first connect).</summary>
    Unknown,
    /// <summary>A different key than the stored one: possibly a reinstalled server, possibly an attack.</summary>
    Changed,
}

/// <summary>The key a server presented, with the fingerprints users compare ("SHA256:..." as OpenSSH shows it).</summary>
public sealed record SshHostKey(string Algorithm, string Sha256Fingerprint, string Md5Fingerprint, int KeyLength)
{
    public const string Sha256Prefix = "SHA256:";

    public static SshHostKey From(HostKeyEventArgs e) =>
        new(e.HostKeyName, Sha256Prefix + e.FingerPrintSHA256, e.FingerPrintMD5, e.KeyLength);

    /// <summary>Compares with a stored fingerprint: "SHA256:..." (also without the prefix) or a legacy MD5 "aa:bb:...".</summary>
    public HostKeyStatus CheckAgainst(string? known)
    {
        string stored = (known ?? "").Trim();
        if (stored.Length == 0)
            return HostKeyStatus.Unknown;

        if (stored.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase))
            stored = stored[Sha256Prefix.Length..];
        else if (stored.StartsWith("MD5:", StringComparison.OrdinalIgnoreCase))
            stored = stored[4..];

        bool matches = string.Equals(stored.TrimEnd('='), Sha256Fingerprint[Sha256Prefix.Length..].TrimEnd('='), StringComparison.Ordinal)
            || string.Equals(stored, Md5Fingerprint, StringComparison.OrdinalIgnoreCase);
        return matches ? HostKeyStatus.Trusted : HostKeyStatus.Changed;
    }
}

/// <summary>
/// The server's key is not trusted yet: the connection was refused before logon. The caller asks the
/// user, stores <see cref="Presented"/> on the connection when they accept, and connects again.
/// </summary>
public sealed class SshHostKeyException(SshLogin login, SshHostKey presented, HostKeyStatus status)
    : Exception($"Host key of {login.Host}:{login.Port} is {status}: {presented.Algorithm} {presented.Sha256Fingerprint}")
{
    public SshLogin Login { get; } = login;
    public SshHostKey Presented { get; } = presented;
    public HostKeyStatus Status { get; } = status;
}
