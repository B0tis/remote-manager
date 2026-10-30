-- Initial schema. Secrets never go into this database: credential_source/credential_key only
-- point at an entry in a credential source (Windows Credential Manager, later password managers).

CREATE TABLE connection_groups (
    id                TEXT PRIMARY KEY,
    parent_id         TEXT NULL REFERENCES connection_groups(id) ON DELETE SET NULL,
    name              TEXT NOT NULL,
    description       TEXT NOT NULL DEFAULT '',
    sort_order        INTEGER NOT NULL DEFAULT 0,
    is_collapsed      INTEGER NOT NULL DEFAULT 0,
    user_name         TEXT NOT NULL DEFAULT '',
    domain            TEXT NOT NULL DEFAULT '',
    credential_source TEXT NULL,
    credential_key    TEXT NULL,
    updated_at        TEXT NOT NULL
);

CREATE TABLE connections (
    id                TEXT PRIMARY KEY,
    name              TEXT NOT NULL,
    type              TEXT NOT NULL,          -- ConnectionType by name
    host              TEXT NOT NULL DEFAULT '',
    port              INTEGER NOT NULL DEFAULT 0,
    description       TEXT NOT NULL DEFAULT '',
    notes             TEXT NOT NULL DEFAULT '',
    is_favorite       INTEGER NOT NULL DEFAULT 0,
    group_id          TEXT NULL REFERENCES connection_groups(id) ON DELETE SET NULL,
    sort_order        INTEGER NOT NULL DEFAULT 0,
    last_connected_at TEXT NULL,              -- ISO 8601, UTC
    preview_path      TEXT NULL,
    user_name         TEXT NOT NULL DEFAULT '',
    domain            TEXT NOT NULL DEFAULT '',
    credential_source TEXT NULL,
    credential_key    TEXT NULL,
    settings_json     TEXT NOT NULL DEFAULT '{}', -- per-type record (RdpSettings, SshSettings, ...)
    updated_at        TEXT NOT NULL
);

CREATE INDEX ix_connections_group ON connections(group_id);

CREATE TABLE connection_tags (
    connection_id TEXT NOT NULL REFERENCES connections(id) ON DELETE CASCADE,
    tag           TEXT NOT NULL,
    position      INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (connection_id, tag)
);

CREATE INDEX ix_connection_tags_tag ON connection_tags(tag);

-- App-wide settings (tool paths, view state): key/value, never secrets.
CREATE TABLE app_settings (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
