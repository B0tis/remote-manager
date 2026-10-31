using System.Text.Json;
using RemoteManager.Core.Library;
using RemoteManager.Core.Model;

namespace RemoteManager.Core.Sync;

/// <summary>What the first sync of a device does with records it already has.</summary>
public enum SyncJoin
{
    /// <summary>Merge them with the sync file (default; both sets end up on every device).</summary>
    Merge,
    /// <summary>Replace them with the content of the sync file (only when this device never synced).</summary>
    ReplaceLocal,
}

public enum SyncResolution
{
    /// <summary>This device's version wins.</summary>
    KeepMine,
    /// <summary>The other device's version wins.</summary>
    KeepOther,
    /// <summary>The record takes the other version; this device's version becomes a new connection.</summary>
    KeepBoth,
}

/// <param name="Pushed">Records this device wrote to the sync file.</param>
/// <param name="Pulled">Records changed on this device.</param>
/// <param name="SkippedCopies">Conflict copies that could not be opened (wrong passphrase, damaged); left in place.</param>
public sealed record SyncResult(int Pushed, int Pulled, int NewConflicts, int OpenConflicts, int MergedCopies, bool Wrote, IReadOnlyList<string> SkippedCopies);

/// <summary>
/// One device's sync: reads and decrypts the sync file (and conflict copies), merges with
/// <see cref="SyncMerge"/>, writes the file back when it changed, then applies the result to the library and
/// stores the new bases and conflicts. Call it from the thread that owns the library (the UI thread);
/// decryption and file access run on the thread pool.
/// </summary>
public sealed class SyncEngine(ConnectionLibrary library, SyncStateStore state, ISyncTransport transport, SyncDevice device)
{
    private static readonly TimeSpan DamagedRetryDelay = TimeSpan.FromSeconds(1);

    public SyncDevice Device => device;
    public ISyncTransport Transport => transport;
    public ConnectionLibrary Library => library;

    public IReadOnlyDictionary<SyncKey, SyncConflict> LoadConflicts() => state.LoadConflicts();

    /// <summary>True once this device has synced at least once with the current folder.</summary>
    public bool HasSynced => state.LoadBases().Count > 0;

    /// <summary>Checks that <paramref name="passphrase"/> opens the sync file; false when there is no file yet.</summary>
    public async Task<bool> ProbeAsync(string passphrase, CancellationToken cancellationToken = default)
    {
        (SyncBlob? main, _, _) = await ReadMainAsync(passphrase, cancellationToken);
        return main is not null;
    }

    public async Task<SyncResult> SyncAsync(string passphrase, SyncJoin join = SyncJoin.Merge, CancellationToken cancellationToken = default)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                SyncResult result = await RunOnceAsync(passphrase, join, cancellationToken);
                return result;
            }
            catch (SyncException ex) when (ex.Error == SyncError.ChangedDuringWrite && attempt < 3)
            {
                // Another device wrote in between: start over with its version.
            }
        }
    }

    private async Task<SyncResult> RunOnceAsync(string passphrase, SyncJoin join, CancellationToken cancellationToken)
    {
        (SyncBlob? main, SyncSnapshot snapshot, SyncKdf kdf) = await ReadMainAsync(passphrase, cancellationToken);
        IReadOnlyList<SyncBlob> copies = await Task.Run(() => transport.ReadConflictCopiesAsync(cancellationToken), cancellationToken);

        Dictionary<SyncKey, SyncFields> local = CaptureLocal();
        Dictionary<SyncKey, SyncRecord> bases = state.LoadBases();
        Dictionary<SyncKey, SyncConflict> conflicts = state.LoadConflicts();
        Dictionary<SyncKey, SyncRecord> remote = snapshot.Records.ToDictionary(r => r.Key);
        DateTime now = DateTime.UtcNow;

        SyncMergeResult result;
        var current = new Dictionary<SyncKey, SyncFields?>(local.Select(kv => KeyValuePair.Create(kv.Key, (SyncFields?)kv.Value)));
        var consumed = new List<SyncBlob>();
        var skipped = new List<string>();
        if (join == SyncJoin.ReplaceLocal && bases.Count == 0 && main is not null)
        {
            result = new SyncMergeResult { Remote = remote, Bases = new(remote) };
            foreach (SyncKey key in local.Keys.Where(k => remote.GetValueOrDefault(k) is not { IsDeleted: false }))
                current[key] = null;
            foreach (SyncRecord record in remote.Values.Where(r => !r.IsDeleted))
                current[record.Key] = record.Fields;
        }
        else
        {
            result = SyncMerge.Run(local, bases, remote, conflicts, device, now);
            Overlay(current, result.LocalChanges);
            foreach (SyncBlob copy in copies)
            {
                SyncSnapshot other;
                try
                {
                    other = await Task.Run(() => Open(copy, passphrase, out _), cancellationToken);
                }
                catch (SyncException)
                {
                    skipped.Add(copy.Name);
                    continue;
                }

                // A conflict copy counts where it is newer than the main file or was written in parallel to it.
                var overlay = new Dictionary<SyncKey, SyncRecord>(result.Remote);
                foreach (SyncRecord record in other.Records.Where(r => SyncMerge.AddsTo(result.Remote.GetValueOrDefault(r.Key), r)))
                    overlay[record.Key] = record;
                SyncMergeResult next = SyncMerge.Run(Settled(current), result.Bases, overlay, result.Conflicts, device, now);
                Overlay(current, next.LocalChanges);
                next.Pushed += result.Pushed;
                result = next;
                consumed.Add(copy);
            }
        }

        bool wrote = false;
        if (main is null ? result.Remote.Count > 0 : consumed.Count > 0 || result.Remote.Any(kv => remote.GetValueOrDefault(kv.Key)?.Rev != kv.Value.Rev))
        {
            var written = new SyncSnapshot { Records = [.. result.Remote.Values.OrderBy(r => r.Kind, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal)] };
            byte[] data = await Task.Run(() => SyncCrypto.Encrypt(JsonSerializer.SerializeToUtf8Bytes(written, SyncStateStore.Json), passphrase, kdf), cancellationToken);
            await Task.Run(() => transport.WriteAsync(data, main?.Version, cancellationToken), cancellationToken);
            wrote = true;
        }

        // Apply what changed here. A record the user edited while we were writing keeps the edit and its old
        // base, so the next sync merges the edit instead of overwriting it.
        var changes = new Dictionary<SyncKey, SyncFields?>();
        foreach ((SyncKey key, SyncFields? fields) in current.Where(kv => !SyncFields.Same(kv.Value, local.GetValueOrDefault(kv.Key))))
        {
            if (SyncFields.Same(CaptureOne(key), local.GetValueOrDefault(key)))
                changes[key] = fields;
            else if (bases.TryGetValue(key, out SyncRecord? old))
                result.Bases[key] = old;
            else
                result.Bases.Remove(key);
        }

        int repaired = Apply(changes);
        state.Save(result.Bases.Values, result.Conflicts.Values);
        foreach (SyncBlob copy in consumed)
            await Task.Run(() => transport.DeleteConflictCopyAsync(copy, cancellationToken), cancellationToken);

        var outcome = new SyncResult(result.Pushed, changes.Count, result.Conflicts.Keys.Count(k => !conflicts.ContainsKey(k)), result.Conflicts.Count,
            consumed.Count, wrote, skipped);
        if (repaired > 0)
        {
            // References repaired after the merge (group deleted elsewhere) go out right away.
            SyncResult again = await RunOnceAsync(passphrase, SyncJoin.Merge, cancellationToken);
            outcome = outcome with { Pushed = outcome.Pushed + again.Pushed, Wrote = outcome.Wrote || again.Wrote };
        }

        return outcome;
    }

    /// <summary>Applies the user's choice for a conflict. Run a sync afterwards to send it to the other devices.</summary>
    /// <param name="copyName">Name of the copy for <see cref="SyncResolution.KeepBoth"/>.</param>
    public void Resolve(SyncKey key, SyncResolution resolution, string? copyName = null)
    {
        Dictionary<SyncKey, SyncConflict> conflicts = state.LoadConflicts();
        Dictionary<SyncKey, SyncRecord> bases = state.LoadBases();
        if (!conflicts.Remove(key, out SyncConflict? conflict))
            return;

        SyncFields? mine = CaptureOne(key);
        var changes = new Dictionary<SyncKey, SyncFields?>();
        if (conflict.IsDeleteConflict)
        {
            // The other version becomes the base; "mine" then goes out as a change against it on the next sync.
            bases[key] = conflict.Remote;
            if (resolution != SyncResolution.KeepMine)
                changes[key] = conflict.Remote.Fields;
        }
        else if (resolution != SyncResolution.KeepMine && mine is not null)
        {
            if (resolution == SyncResolution.KeepBoth && key.Kind == SyncKey.Connection)
            {
                var copy = new Connection();
                SyncFieldMap.ApplyTo(copy, mine);
                copy.Name = copyName ?? copy.Name;
                copy.Credential = null; // a copy never shares a password (like Duplicate)
                library.Add(copy);
            }

            var theirs = new SyncFields(mine);
            foreach (string field in conflict.Fields)
                theirs.Put(field, conflict.Remote.Fields?.Get(field));
            changes[key] = theirs;
        }

        Apply(changes);
        state.Save(bases.Values, conflicts.Values);
    }

    /// <summary>Forgets bases and conflicts (another folder or passphrase).</summary>
    public void Reset() => state.Clear();

    private async Task<(SyncBlob? Main, SyncSnapshot Snapshot, SyncKdf Kdf)> ReadMainAsync(string passphrase, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(passphrase))
            throw new SyncException(SyncError.NoPassphrase, "No sync passphrase.");
        for (int attempt = 0; ; attempt++)
        {
            SyncBlob? main = await Task.Run(() => transport.ReadAsync(cancellationToken), cancellationToken);
            if (main is null)
                return (null, new SyncSnapshot(), SyncKdf.CreateDefault());
            try
            {
                SyncKdf kdf = null!;
                SyncSnapshot snapshot = await Task.Run(() => Open(main, passphrase, out kdf), cancellationToken);
                return (main, snapshot, kdf);
            }
            catch (SyncException ex) when (ex.Error == SyncError.Damaged && attempt < 3)
            {
                // Most likely a sync client is still writing it; never overwrite a file we could not read.
                await Task.Delay(DamagedRetryDelay, cancellationToken);
            }
        }
    }

    private static SyncSnapshot Open(SyncBlob blob, string passphrase, out SyncKdf kdf)
    {
        byte[] plaintext = SyncCrypto.Decrypt(blob.Data, passphrase, out kdf);
        SyncSnapshot snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<SyncSnapshot>(plaintext, SyncStateStore.Json) ?? throw new JsonException("empty");
        }
        catch (JsonException ex)
        {
            throw new SyncException(SyncError.Damaged, "The sync file content is unreadable.", ex);
        }

        if (snapshot.Format > SyncSnapshot.CurrentFormat)
            throw new SyncException(SyncError.NewerFormat, $"Sync content format {snapshot.Format}.");
        return snapshot;
    }

    private Dictionary<SyncKey, SyncFields> CaptureLocal()
    {
        var local = new Dictionary<SyncKey, SyncFields>();
        foreach (ConnectionGroup group in library.Groups)
            local[new SyncKey(SyncKey.Group, group.Id)] = SyncFieldMap.FromGroup(group);
        foreach (Connection connection in library.Connections)
            local[new SyncKey(SyncKey.Connection, connection.Id)] = SyncFieldMap.FromConnection(connection);
        return local;
    }

    private SyncFields? CaptureOne(SyncKey key) => key.Kind == SyncKey.Group
        ? library.FindGroup(key.Id) is { } group ? SyncFieldMap.FromGroup(group) : null
        : library.FindConnection(key.Id) is { } connection ? SyncFieldMap.FromConnection(connection) : null;

    /// <returns>How many records the library had to repair afterwards.</returns>
    private int Apply(IReadOnlyDictionary<SyncKey, SyncFields?> changes)
    {
        if (changes.Count == 0)
            return 0;
        List<ConnectionGroup> savedGroups = [];
        List<Connection> savedConnections = [];
        List<string> removedGroups = [], removedConnections = [];
        foreach ((SyncKey key, SyncFields? fields) in changes)
        {
            if (key.Kind == SyncKey.Group)
            {
                if (fields is null)
                    removedGroups.Add(key.Id);
                else
                {
                    ConnectionGroup group = library.FindGroup(key.Id) ?? new ConnectionGroup { Id = key.Id };
                    SyncFieldMap.ApplyTo(group, fields);
                    savedGroups.Add(group);
                }
            }
            else if (fields is null)
                removedConnections.Add(key.Id);
            else
            {
                Connection connection = library.FindConnection(key.Id) ?? new Connection { Id = key.Id };
                SyncFieldMap.ApplyTo(connection, fields);
                savedConnections.Add(connection);
            }
        }

        return library.ApplyExternalChanges(savedGroups, savedConnections, removedGroups, removedConnections);
    }

    private static void Overlay(Dictionary<SyncKey, SyncFields?> current, Dictionary<SyncKey, SyncFields?> changes)
    {
        foreach ((SyncKey key, SyncFields? fields) in changes)
            current[key] = fields;
    }

    private static Dictionary<SyncKey, SyncFields> Settled(Dictionary<SyncKey, SyncFields?> current) =>
        current.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!);
}
