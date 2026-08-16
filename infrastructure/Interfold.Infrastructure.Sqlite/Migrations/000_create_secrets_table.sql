-- Secrets table for OAuth credentials, encryption keys, etc.
-- Dialect-neutral SQLite (no schema qualifier — KMP-shareable).
-- Timestamps are unix-ms INTEGER per locked-in conventions.

CREATE TABLE IF NOT EXISTS secrets (
    key          TEXT    PRIMARY KEY,
    value        TEXT    NOT NULL,
    created_by   TEXT    NOT NULL DEFAULT 'bootstrap',
    created_at   INTEGER NOT NULL,
    updated_at   INTEGER NOT NULL,
    expires_at   INTEGER,
    rotated_from TEXT
);
