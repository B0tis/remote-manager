-- Connection history: when a connection was opened, how it went, and when the session ended.
-- Feeds "Verlauf" in the detail panel and the usage ranking of the quick launcher.

CREATE TABLE connection_history (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    connection_id TEXT NOT NULL REFERENCES connections(id) ON DELETE CASCADE,
    started_at    TEXT NOT NULL,              -- ISO 8601, UTC
    ended_at      TEXT NULL,                  -- ISO 8601, UTC; null while open or unknown
    result        TEXT NOT NULL               -- HistoryResult by name
);

CREATE INDEX ix_connection_history_connection ON connection_history(connection_id, started_at);
