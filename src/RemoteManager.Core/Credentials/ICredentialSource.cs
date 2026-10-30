namespace RemoteManager.Core.Credentials;

/// <summary>
/// Somewhere secrets live outside the database: the Windows Credential Manager (writable), KeePass
/// (<see cref="KeePassCredentialSource"/>) and 1Password (<see cref="OnePasswordCredentialSource"/>, both read-only). Implementations are found by <see cref="Extensibility.PluginCatalog"/> (public,
/// non-abstract, parameterless constructor), so a new source is a new file in this folder.
/// Async because some sources (password manager CLIs) take a while; never block the UI on them.
/// </summary>
public interface ICredentialSource
{
    /// <summary>Stable id stored in <see cref="Model.CredentialRef.Source"/>.</summary>
    string Id { get; }

    /// <summary>Whether the app may write and delete secrets here (password managers are read-only).</summary>
    bool IsWritable { get; }

    /// <summary>Whether this source is usable on this machine right now.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// The secret, or null when there is none for <paramref name="key"/>. Password managers throw
    /// <see cref="CredentialSourceException"/> when they cannot deliver (locked, canceled, entry missing).
    /// </summary>
    ValueTask<string?> ReadAsync(string key, CancellationToken cancellationToken = default);

    ValueTask<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces the secret. Throws <see cref="NotSupportedException"/> when not writable.</summary>
    ValueTask WriteAsync(string key, string secret, CancellationToken cancellationToken = default);

    /// <summary>Removes the secret; false when there was none.</summary>
    ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);
}
