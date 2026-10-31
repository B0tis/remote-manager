using RemoteManager.Core.Library;
using RemoteManager.Core.Model;

namespace RemoteManager.Core.Credentials;

/// <summary>Logon data of a connection after inheritance; the secret stays behind <see cref="Secret"/>.</summary>
/// <param name="InheritedFrom">The group the values come from, or null when the connection has its own.</param>
public sealed record EffectiveCredential(string UserName, string Domain, CredentialRef? Secret, ConnectionGroup? InheritedFrom)
{
    public static readonly EffectiveCredential None = new("", "", null, null);

    public bool HasSecret => Secret is not null;
    public bool IsInherited => InheritedFrom is not null;
}

/// <summary>
/// Resolves which credentials a connection uses and reads or writes secrets through the registered
/// <see cref="ICredentialSource"/>s. Own passwords go to <see cref="DefaultSource"/>.
/// </summary>
public sealed class CredentialService
{
    private readonly Dictionary<string, ICredentialSource> sources;
    private readonly ConnectionLibrary library;

    public CredentialService(IEnumerable<ICredentialSource> sources, ConnectionLibrary library)
    {
        this.sources = sources.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        this.library = library;
        DefaultSource = this.sources.GetValueOrDefault(WindowsCredentialSource.SourceId)
            ?? this.sources.Values.FirstOrDefault(s => s.IsWritable)
            ?? throw new ArgumentException("No writable credential source.", nameof(sources));
    }

    public ICredentialSource DefaultSource { get; }
    public IReadOnlyCollection<ICredentialSource> Sources => sources.Values;

    public ICredentialSource? SourceFor(CredentialRef reference) => sources.GetValueOrDefault(reference.Source);

    /// <summary>
    /// The connection's own credentials when it sets a user or a secret; otherwise those of the
    /// nearest ancestor group that does. User, domain, and secret are inherited together, never mixed.
    /// </summary>
    public EffectiveCredential Resolve(Connection connection)
    {
        if (connection.UserName.Length > 0 || connection.Credential is not null)
            return new EffectiveCredential(connection.UserName, connection.Domain, connection.Credential, null);

        foreach (ConnectionGroup group in library.AncestorsOf(connection.GroupId))
        {
            if (group.UserName.Length > 0 || group.Credential is not null)
                return new EffectiveCredential(group.UserName, group.Domain, group.Credential, group);
        }

        return EffectiveCredential.None;
    }

    /// <summary>The secret behind <paramref name="reference"/>, or null (no reference, unknown source, or no entry).</summary>
    public async ValueTask<string?> ReadAsync(CredentialRef? reference, CancellationToken cancellationToken = default)
    {
        if (reference is null || SourceFor(reference) is not { } source)
            return null;
        return await source.ReadAsync(reference.Key, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stores a password for the connection itself and points <see cref="Connection.Credential"/> at it.</summary>
    public async ValueTask SaveOwnSecretAsync(Connection connection, string secret, CancellationToken cancellationToken = default)
    {
        await DefaultSource.WriteAsync(connection.Id, secret, cancellationToken).ConfigureAwait(false);
        connection.Credential = new CredentialRef(DefaultSource.Id, connection.Id);
    }

    /// <summary>
    /// Points the connection at a secret in a read-only source (KeePass, 1Password); a password the app had
    /// stored for it in the default source is deleted, so no stale copy stays behind.
    /// </summary>
    public async ValueTask UseReferenceAsync(Connection connection, CredentialRef reference, CancellationToken cancellationToken = default)
    {
        if (SourceFor(reference) is not { IsWritable: false })
            throw new ArgumentException($"{reference.Source} is not a read-only credential source.", nameof(reference));
        await DefaultSource.DeleteAsync(connection.Id, cancellationToken).ConfigureAwait(false);
        connection.Credential = reference;
    }

    /// <summary>
    /// Deletes the connection's own password from the default source (the app wrote it there) and clears
    /// the reference. A reference into a read-only source (password manager) is only dropped.
    /// </summary>
    public async ValueTask RemoveOwnSecretAsync(Connection connection, CancellationToken cancellationToken = default)
    {
        await DefaultSource.DeleteAsync(connection.Id, cancellationToken).ConfigureAwait(false);
        connection.Credential = null;
    }

    /// <summary>Stores a password on a group; connections in it without own credentials inherit it.</summary>
    public async ValueTask SaveGroupSecretAsync(ConnectionGroup group, string secret, CancellationToken cancellationToken = default)
    {
        string key = GroupKey(group);
        await DefaultSource.WriteAsync(key, secret, cancellationToken).ConfigureAwait(false);
        group.Credential = new CredentialRef(DefaultSource.Id, key);
    }

    /// <summary>
    /// Points the group at a secret in a read-only source (KeePass, 1Password); its connections inherit it like
    /// a stored password. A password the app had stored for the group is deleted, so no stale copy stays behind.
    /// </summary>
    public async ValueTask UseGroupReferenceAsync(ConnectionGroup group, CredentialRef reference, CancellationToken cancellationToken = default)
    {
        if (SourceFor(reference) is not { IsWritable: false })
            throw new ArgumentException($"{reference.Source} is not a read-only credential source.", nameof(reference));
        await DefaultSource.DeleteAsync(GroupKey(group), cancellationToken).ConfigureAwait(false);
        group.Credential = reference;
    }

    /// <summary>Deletes the group's own password from the default source and clears the reference (a password-manager reference is only dropped).</summary>
    public async ValueTask RemoveGroupSecretAsync(ConnectionGroup group, CancellationToken cancellationToken = default)
    {
        await DefaultSource.DeleteAsync(GroupKey(group), cancellationToken).ConfigureAwait(false);
        group.Credential = null;
    }

    private static string GroupKey(ConnectionGroup group) => $"group-{group.Id}";
}
