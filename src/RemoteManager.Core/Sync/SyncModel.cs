using System.Text.Json.Serialization;

namespace RemoteManager.Core.Sync;

/// <summary>A synced record: a group or a connection by id.</summary>
public readonly record struct SyncKey(string Kind, string Id)
{
    public const string Group = "group";
    public const string Connection = "connection";

    public override string ToString() => $"{Kind}:{Id}";
}

/// <summary>
/// The synced fields of one record, field name to its value as JSON text ("\"dc01\"", "3389", "[\"prod\"]").
/// Sorted, so the same content always serializes the same way.
/// </summary>
public sealed class SyncFields : SortedDictionary<string, string>
{
    public SyncFields() : base(StringComparer.Ordinal)
    {
    }

    public SyncFields(IDictionary<string, string> fields) : base(fields, StringComparer.Ordinal)
    {
    }

    public string? Get(string field) => TryGetValue(field, out string? value) ? value : null;

    /// <summary>Sets a field; null removes it.</summary>
    public void Put(string field, string? value)
    {
        if (value is null)
            Remove(field);
        else
            this[field] = value;
    }

    /// <summary>Same content; null (deleted) only equals null.</summary>
    public static bool Same(SyncFields? a, SyncFields? b) =>
        a is null || b is null ? a is null && b is null : a.Count == b.Count && a.All(f => b.Get(f.Key) == f.Value);
}

/// <summary>
/// One version of a record in the sync file. <see cref="Rev"/> is new for every written version and
/// <see cref="History"/> lists the revisions it was built on (newest first), so a device can tell whether
/// another version is newer, older, or was written in parallel (a Syncthing/OneDrive conflict copy).
/// </summary>
public sealed record SyncRecord
{
    /// <summary>How many ancestor revisions a record keeps. Older ones fall off; a fork that old shows as a conflict.</summary>
    public const int HistoryLimit = 32;

    public required string Kind { get; init; }
    public required string Id { get; init; }
    public required string Rev { get; init; }
    public List<string> History { get; init; } = [];

    /// <summary>Null: the record was deleted (tombstone).</summary>
    public SyncFields? Fields { get; init; }

    public DateTime Modified { get; init; }

    /// <summary>Id and name of the device that wrote this version.</summary>
    public string Device { get; init; } = "";
    public string DeviceName { get; init; } = "";

    /// <summary>
    /// Base records only (never in the file): the base this one replaced. A conflict copy written in parallel
    /// usually branched from it, which gives the three-way merge its common version.
    /// </summary>
    [JsonIgnore]
    public SyncAncestor? Previous { get; init; }

    [JsonIgnore]
    public SyncKey Key => new(Kind, Id);

    [JsonIgnore]
    public bool IsDeleted => Fields is null;

    public static string NewRev() => Guid.NewGuid().ToString("N")[..16];
}

/// <summary>An earlier version of a record: its revision and fields (null: deleted).</summary>
public sealed record SyncAncestor(string Rev, SyncFields? Fields);

/// <summary>
/// Both versions of a record that changed on this device and elsewhere. <see cref="Fields"/> are the
/// fields edited on both sides; empty when one side deleted the record and the other edited it.
/// </summary>
/// <param name="Local">This device's fields when the conflict was found; null when deleted here.</param>
/// <param name="Remote">The other version as it is in the sync file (a tombstone when deleted there).</param>
public sealed record SyncConflict(SyncKey Key, SyncFields? Local, SyncRecord Remote, IReadOnlyList<string> Fields, DateTime DetectedAt)
{
    public bool IsDeleteConflict => Fields.Count == 0;
}

/// <summary>The decrypted content of a sync file.</summary>
public sealed class SyncSnapshot
{
    public const int CurrentFormat = 1;

    public int Format { get; init; } = CurrentFormat;
    public List<SyncRecord> Records { get; init; } = [];
}

public enum SyncError
{
    /// <summary>The passphrase does not open the sync file.</summary>
    WrongPassphrase,
    /// <summary>The file is incomplete or damaged (also while a sync client is still writing it).</summary>
    Damaged,
    /// <summary>Written by a newer Remote-Manager.</summary>
    NewerFormat,
    FolderMissing,
    /// <summary>The file stayed locked (sync client, virus scanner) through all retries.</summary>
    Locked,
    /// <summary>Someone else replaced the file between read and write; the engine starts over.</summary>
    ChangedDuringWrite,
    NoPassphrase,
}

public sealed class SyncException(SyncError error, string message, Exception? inner = null) : Exception(message, inner)
{
    public SyncError Error { get; } = error;
}
