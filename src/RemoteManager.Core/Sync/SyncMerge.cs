namespace RemoteManager.Core.Sync;

/// <summary>Who is merging: written into every record version this device creates.</summary>
public sealed record SyncDevice(string Id, string Name);

/// <summary>The outcome of <see cref="SyncMerge.Run"/>; every dictionary is the complete new state.</summary>
public sealed class SyncMergeResult
{
    /// <summary>Records to change on this device (null: delete).</summary>
    public Dictionary<SyncKey, SyncFields?> LocalChanges { get; } = [];
    public Dictionary<SyncKey, SyncRecord> Remote { get; init; } = [];
    public Dictionary<SyncKey, SyncRecord> Bases { get; init; } = [];
    public Dictionary<SyncKey, SyncConflict> Conflicts { get; init; } = [];

    /// <summary>Records this device wrote a new version of.</summary>
    public int Pushed { get; set; }
}

/// <summary>
/// Per-record three-way merge of this device's records, the sync file, and the base (what this device last
/// agreed with the sync file). Changes on one side win; edits to different fields merge; the same field
/// edited on both sides, or an edit against a delete, becomes a <see cref="SyncConflict"/> and is never
/// overwritten. A field in conflict is held back until the user decides: the file keeps the other value,
/// this device keeps its own. Pure: no I/O.
/// </summary>
public static class SyncMerge
{
    private enum Lineage { Same, RemoteNewer, RemoteOlder, Parallel, NoBase }

    public static SyncMergeResult Run(
        IReadOnlyDictionary<SyncKey, SyncFields> local,
        IReadOnlyDictionary<SyncKey, SyncRecord> bases,
        IReadOnlyDictionary<SyncKey, SyncRecord> remote,
        IReadOnlyDictionary<SyncKey, SyncConflict> conflicts,
        SyncDevice device,
        DateTime now)
    {
        var result = new SyncMergeResult
        {
            Remote = new(remote),
            Bases = new(bases),
            Conflicts = new(conflicts),
        };

        IEnumerable<SyncKey> keys = local.Keys.Concat(bases.Keys).Concat(remote.Keys).Concat(conflicts.Keys).Distinct();
        foreach (SyncKey key in keys)
        {
            var merge = new RecordMerge(key, local.GetValueOrDefault(key), bases.GetValueOrDefault(key), remote.GetValueOrDefault(key),
                conflicts.GetValueOrDefault(key), device, now, result);
            merge.Run();
        }

        return result;
    }

    private sealed class RecordMerge(SyncKey key, SyncFields? local, SyncRecord? baseRecord, SyncRecord? remote, SyncConflict? conflict,
        SyncDevice device, DateTime now, SyncMergeResult result)
    {
        public void Run()
        {
            if (conflict is { IsDeleteConflict: true })
            {
                HoldDeleteConflict(conflict);
                return;
            }

            if (conflict is not null && local is null)
            {
                // Deleted here while fields were in conflict: merge the delete like any other change.
                result.Conflicts.Remove(key);
                conflict = null;
            }

            if (conflict is null)
            {
                Outcome outcome = Merge(local);
                if (outcome.Changed)
                    result.LocalChanges[key] = outcome.Fields;
                if (outcome.Conflicts.Count > 0)
                    result.Conflicts[key] = new SyncConflict(key, local, OtherVersion(), outcome.Conflicts, now);
                return;
            }

            // A field conflict holds its fields at the base (which carries the other side's value) until resolved.
            var held = new SyncFields(local!);
            foreach (string field in conflict.Fields)
                held.Put(field, baseRecord?.Fields?.Get(field));
            Outcome merged = Merge(held);

            if (result.Conflicts.GetValueOrDefault(key) is { IsDeleteConflict: true } deleteConflict)
            {
                result.Conflicts[key] = deleteConflict with { Local = local };
                return;
            }

            if (merged.Changed && merged.Fields is null)
            {
                // Deleted elsewhere while fields are in conflict here: an edit against a delete, nothing moves.
                if (baseRecord is not null)
                    result.Bases[key] = baseRecord;
                result.Conflicts[key] = new SyncConflict(key, local, remote!, [], now);
                return;
            }

            // Keep this device's values for the held fields; drop the ones the other side now agrees with.
            var kept = new SyncFields(merged.Changed ? merged.Fields! : held);
            SyncFields? other = result.Remote.GetValueOrDefault(key)?.Fields;
            List<string> fields = merged.Conflicts;
            foreach (string field in conflict.Fields)
            {
                kept.Put(field, local!.Get(field));
                if (local.Get(field) != other?.Get(field) && !fields.Contains(field))
                    fields.Add(field);
            }

            if (!SyncFields.Same(kept, local))
                result.LocalChanges[key] = kept;
            if (fields.Count > 0)
                result.Conflicts[key] = new SyncConflict(key, local, remote is not null && remote.Device != device.Id ? remote : conflict.Remote, fields, conflict.DetectedAt);
            else
                result.Conflicts.Remove(key);
        }

        /// <summary>The record stays as it is on both sides; only the other version is kept current.</summary>
        private void HoldDeleteConflict(SyncConflict open)
        {
            if (remote is not null && Classify(open.Remote, remote) == Lineage.RemoteNewer)
                open = open with { Remote = remote };
            if (SyncFields.Same(local, open.Remote.Fields))
            {
                // Both sides agree again (both deleted, or the same content).
                result.Conflicts.Remove(key);
                SetBase(open.Remote);
                return;
            }

            result.Conflicts[key] = open;
        }

        private Outcome Merge(SyncFields? mine)
        {
            if (remote is null)
            {
                // The file does not have this record (new file, or never pushed): this device's state goes in.
                if (mine is not null || baseRecord is { IsDeleted: false })
                    Push(mine);
                return Outcome.Unchanged;
            }

            Lineage lineage = Classify(baseRecord, remote);
            SyncFields? theirs = remote.Fields;
            SyncFields? common = baseRecord?.Fields;
            bool known = lineage is Lineage.Same or Lineage.RemoteNewer or Lineage.RemoteOlder;
            if (lineage == Lineage.Parallel && baseRecord!.Previous is { } previous && remote.History.Contains(previous.Rev))
            {
                // A parallel version (conflict copy) that branched from the version before our base.
                known = true;
                common = previous.Fields;
            }

            if (known)
            {
                bool force = lineage == Lineage.RemoteOlder;
                if (force)
                    theirs = common; // the file went back to an older version: ours is newer
                bool mineChanged = !SyncFields.Same(mine, common);
                bool theirsChanged = !SyncFields.Same(theirs, common);

                if (!mineChanged && !theirsChanged)
                {
                    if (force)
                        Push(mine);
                    else
                        SetBase(remote);
                    return Outcome.Unchanged;
                }

                if (mineChanged && !theirsChanged)
                {
                    Push(mine);
                    return Outcome.Unchanged;
                }

                if (!mineChanged)
                {
                    SetBase(remote);
                    return Outcome.Take(theirs);
                }

                return Combine(mine, theirs, common);
            }

            // No usable base: never synced this record, or the file holds a parallel version (conflict copy).
            if (SyncFields.Same(mine, theirs) || (mine is null && baseRecord is null))
            {
                SetBase(remote);
                return SyncFields.Same(mine, theirs) ? Outcome.Unchanged : Outcome.Take(theirs);
            }

            return Combine(mine, theirs, null);
        }

        /// <summary>Both sides changed the record: merge field by field against <paramref name="common"/> (null: unknown).</summary>
        private Outcome Combine(SyncFields? mine, SyncFields? theirs, SyncFields? common)
        {
            if (SyncFields.Same(mine, theirs))
            {
                SetBase(remote!);
                return Outcome.Unchanged;
            }

            if (mine is null || theirs is null)
            {
                // Edit against delete: nothing moves until the user decides. The base stays.
                result.Conflicts[key] = new SyncConflict(key, mine, remote!, [], now);
                return Outcome.Unchanged;
            }

            var pushed = new SyncFields();
            var kept = new SyncFields();
            List<string> conflicts = [];
            foreach (string field in mine.Keys.Concat(theirs.Keys).Distinct())
            {
                string? m = mine.Get(field), t = theirs.Get(field);
                string? merged;
                if (m == t)
                    merged = m;
                else if (common is not null && m == common.Get(field))
                    merged = t;
                else if (common is not null && t == common.Get(field))
                    merged = m;
                else
                {
                    conflicts.Add(field);
                    pushed.Put(field, t);
                    kept.Put(field, m);
                    continue;
                }

                pushed.Put(field, merged);
                kept.Put(field, merged);
            }

            if (SyncFields.Same(pushed, theirs))
                SetBase(remote!);
            else
                Push(pushed);
            return new Outcome(!SyncFields.Same(kept, mine), kept, conflicts);
        }

        /// <summary>Writes a new version of the record (a tombstone for null) built on the file's and the base's versions.</summary>
        private void Push(SyncFields? fields)
        {
            if (fields is null && remote is null && baseRecord is null)
                return;
            var record = new SyncRecord
            {
                Kind = key.Kind,
                Id = key.Id,
                Rev = SyncRecord.NewRev(),
                History = Ancestry(remote).Concat(Ancestry(baseRecord)).Distinct().Take(SyncRecord.HistoryLimit).ToList(),
                Fields = fields is null ? null : new SyncFields(fields),
                Modified = now,
                Device = device.Id,
                DeviceName = device.Name,
            };
            result.Remote[key] = record;
            SetBase(record);
            result.Pushed++;
        }

        /// <summary>
        /// The version a field conflict shows as "the other one": the other device's record (its conflicting
        /// values are also what the file now holds), not a merged version this device just wrote.
        /// </summary>
        private SyncRecord OtherVersion() => remote is not null && remote.Device != device.Id ? remote : result.Remote[key];

        /// <summary>Makes <paramref name="record"/> the new base, remembering the base it replaces.</summary>
        private void SetBase(SyncRecord record) =>
            result.Bases[key] = record with
            {
                Previous = baseRecord is null ? null : baseRecord.Rev == record.Rev ? baseRecord.Previous : new SyncAncestor(baseRecord.Rev, baseRecord.Fields),
            };

        private static IEnumerable<string> Ancestry(SyncRecord? record) => record is null ? [] : [record.Rev, .. record.History];

    }

    /// <summary>What happens to this device's record: <see cref="Changed"/> with the new fields (null: delete), and the fields in conflict.</summary>
    private sealed record Outcome(bool Changed, SyncFields? Fields, List<string> Conflicts)
    {
        public static Outcome Unchanged => new(false, null, []);

        public static Outcome Take(SyncFields? fields) => new(true, fields is null ? null : new SyncFields(fields), []);
    }

    private static Lineage Classify(SyncRecord? baseRecord, SyncRecord remote)
    {
        if (baseRecord is null)
            return Lineage.NoBase;
        if (remote.Rev == baseRecord.Rev)
            return Lineage.Same;
        if (remote.History.Contains(baseRecord.Rev))
            return Lineage.RemoteNewer;
        return baseRecord.History.Contains(remote.Rev) ? Lineage.RemoteOlder : Lineage.Parallel;
    }

    /// <summary>
    /// True when <paramref name="candidate"/> (from a conflict copy) should be merged over <paramref name="current"/>
    /// (from the main file): it is newer, or was written in parallel. Older or identical versions add nothing.
    /// </summary>
    public static bool AddsTo(SyncRecord? current, SyncRecord candidate) =>
        Classify(current, candidate) is Lineage.NoBase or Lineage.RemoteNewer or Lineage.Parallel;
}
