using RemoteManager.Core.Credentials.KeePass;

namespace RemoteManager.Core.Credentials;

/// <summary>What the unlock prompt shows.</summary>
/// <param name="PreviousAttemptFailed">The last master password was wrong.</param>
public sealed record MasterPasswordRequest(string DatabasePath, string? KeyFilePath, bool PreviousAttemptFailed);

/// <summary>Asks the user for a KeePass master password (implemented by the app's unlock dialog).</summary>
public interface IMasterPasswordPrompt
{
    /// <summary>The typed master password ("" for none), or null when the user cancels.</summary>
    ValueTask<string?> AskAsync(MasterPasswordRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Read-only secrets from a KeePass database (.kdbx). The reference key is a <see cref="KeePassReference"/>
/// (database, optional key file, entry). The file is read and decrypted at connect time; the master password is
/// asked through <see cref="Prompt"/> and only its hash (the composite key) stays in memory for
/// <see cref="KeyLifetime"/>, so several connects in a row ask once. Nothing is written to disk.
/// </summary>
public sealed class KeePassCredentialSource : ICredentialSource
{
    public const string SourceId = "keepass";

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, (KdbxCompositeKey Key, DateTime LastUse)> keys = new(StringComparer.OrdinalIgnoreCase);

    public string Id => SourceId;
    public bool IsWritable => false;

    /// <summary>Always: the reader is built in. A missing file is reported when reading.</summary>
    public bool IsAvailable => true;

    /// <summary>Set by the app at startup; without it a locked database cannot be opened.</summary>
    public IMasterPasswordPrompt? Prompt { get; set; }

    /// <summary>How long an unlocked database stays unlocked after its last use (in memory only).</summary>
    public TimeSpan KeyLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Whether a composite key for the database is held (the next read will not ask).</summary>
    public bool IsUnlocked(string databasePath, string? keyFilePath)
    {
        lock (keys)
            return keys.TryGetValue(CacheKey(databasePath, keyFilePath), out var held) && DateTime.UtcNow - held.LastUse < KeyLifetime;
    }

    /// <summary>Forgets all held keys.</summary>
    public void LockAll()
    {
        lock (keys)
        {
            foreach ((KdbxCompositeKey key, _) in keys.Values)
                key.Dispose();
            keys.Clear();
        }
    }

    public async ValueTask<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        KeePassReference reference = KeePassReference.Parse(key);
        KdbxEntry entry = reference.Resolve(await ReadEntriesAsync(reference.Database, reference.KeyFile, cancellationToken).ConfigureAwait(false));
        return entry.Fields.TryGetValue(reference.FieldName, out string? value)
            ? value
            : throw new CredentialSourceException(CredentialError.EntryNotFound, $"{entry.Path} [{reference.FieldName}]");
    }

    public async ValueTask<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            return await ReadAsync(key, cancellationToken).ConfigureAwait(false) is not null;
        }
        catch (CredentialSourceException ex) when (ex.Error == CredentialError.EntryNotFound)
        {
            return false;
        }
    }

    public ValueTask WriteAsync(string key, string secret, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("KeePass databases are read-only here.");

    public ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

    /// <summary>
    /// All entries of the database (asks for the master password when needed). Used for the entry picker; the
    /// caller must not keep the result.
    /// </summary>
    public async Task<IReadOnlyList<KdbxEntry>> ReadEntriesAsync(string databasePath, string? keyFilePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(databasePath))
            throw new CredentialSourceException(CredentialError.FileNotFound, Path.GetFileName(databasePath));
        if (!string.IsNullOrEmpty(keyFilePath) && !File.Exists(keyFilePath))
            throw new CredentialSourceException(CredentialError.FileNotFound, Path.GetFileName(keyFilePath));

        // One unlock at a time: two connects started together ask once.
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string cacheKey = CacheKey(databasePath, keyFilePath);
            if (TakeHeldKey(cacheKey) is { } held)
            {
                try
                {
                    IReadOnlyList<KdbxEntry> entries = await Task.Run(() => KdbxReader.ReadEntries(databasePath, held), cancellationToken).ConfigureAwait(false);
                    Hold(cacheKey, held);
                    return entries;
                }
                catch (CredentialSourceException ex) when (ex.Error == CredentialError.WrongMasterKey)
                {
                    // The master password changed since; ask again.
                    held.Dispose();
                }
            }

            bool failed = false;
            while (true)
            {
                if (Prompt is not { } prompt)
                    throw new CredentialSourceException(CredentialError.Canceled, "no unlock prompt");
                string? password = await prompt.AskAsync(new MasterPasswordRequest(databasePath, NullIfEmpty(keyFilePath), failed), cancellationToken).ConfigureAwait(false)
                    ?? throw new CredentialSourceException(CredentialError.Canceled);

                foreach (KdbxCompositeKey candidate in Candidates(password, keyFilePath))
                {
                    try
                    {
                        IReadOnlyList<KdbxEntry> entries = await Task.Run(() => KdbxReader.ReadEntries(databasePath, candidate), cancellationToken).ConfigureAwait(false);
                        Hold(cacheKey, candidate);
                        return entries;
                    }
                    catch (CredentialSourceException ex) when (ex.Error == CredentialError.WrongMasterKey)
                    {
                        candidate.Dispose();
                    }
                    catch
                    {
                        candidate.Dispose();
                        throw;
                    }
                }

                failed = true;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// An empty input means "no master password" when there is a key file (KeePassXC) but may also be an
    /// empty one (KeePass 2 with the box ticked): try both.
    /// </summary>
    private static IEnumerable<KdbxCompositeKey> Candidates(string password, string? keyFilePath)
    {
        if (password.Length == 0 && !string.IsNullOrEmpty(keyFilePath))
            yield return KdbxCompositeKey.Create(null, keyFilePath);
        yield return KdbxCompositeKey.Create(password, NullIfEmpty(keyFilePath));
    }

    private KdbxCompositeKey? TakeHeldKey(string cacheKey)
    {
        lock (keys)
        {
            if (!keys.Remove(cacheKey, out var held))
                return null;
            if (DateTime.UtcNow - held.LastUse < KeyLifetime)
                return held.Key;
            held.Key.Dispose();
            return null;
        }
    }

    private void Hold(string cacheKey, KdbxCompositeKey key)
    {
        if (KeyLifetime <= TimeSpan.Zero)
        {
            key.Dispose();
            return;
        }

        lock (keys)
            keys[cacheKey] = (key, DateTime.UtcNow);
    }

    private static string CacheKey(string databasePath, string? keyFilePath) =>
        $"{Path.GetFullPath(databasePath)}|{(string.IsNullOrEmpty(keyFilePath) ? "" : Path.GetFullPath(keyFilePath))}";

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
