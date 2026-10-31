-- Device sync (Phase 4). Groups and connections stay in their tables; these only hold what the merge needs.
-- Never secrets: synced fields carry credential references, not passwords.

-- Per record, the version this device last agreed with the sync file: the base of the three-way merge.
-- revision is new for every written version; history_json lists the revisions it was built on, so a
-- version from a conflict copy can be told apart as newer, older, or parallel. fields_json NULL = deleted (tombstone).
-- previous_json is the base this one replaced (revision and fields): the common version for merging a conflict copy.
CREATE TABLE sync_base (
    kind         TEXT NOT NULL,           -- 'group' or 'connection'
    id           TEXT NOT NULL,
    revision     TEXT NOT NULL,
    history_json TEXT NOT NULL DEFAULT '[]',
    fields_json  TEXT NULL,
    modified_at  TEXT NOT NULL,           -- ISO 8601, UTC
    device_id    TEXT NOT NULL,           -- device that wrote this version
    previous_json TEXT NULL,
    PRIMARY KEY (kind, id)
);

-- Open conflicts, kept until the user picks a version. local_json NULL = deleted here; remote_json is the other
-- version as in the sync file (with its device name); fields_json lists the fields edited on both sides
-- (empty for an edit against a delete).
CREATE TABLE sync_conflicts (
    kind        TEXT NOT NULL,
    id          TEXT NOT NULL,
    local_json  TEXT NULL,
    remote_json TEXT NOT NULL,
    fields_json TEXT NOT NULL DEFAULT '[]',
    detected_at TEXT NOT NULL,            -- ISO 8601, UTC
    PRIMARY KEY (kind, id)
);
