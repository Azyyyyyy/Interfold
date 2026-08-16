-- Async-import job state. Dialect-neutral SQLite.
-- active_import_by_system is the per-(system, kind) mutex (INSERT OR IGNORE claim).
-- Timestamps are unix-ms INTEGER; enum columns store EnumWire text.

CREATE TABLE IF NOT EXISTS import_operations (
    system_id       TEXT    NOT NULL,
    operation_id    TEXT    NOT NULL,
    kind            TEXT    NOT NULL,
    status          TEXT    NOT NULL,
    started_at      INTEGER NOT NULL,
    finished_at     INTEGER,
    alter_count     INTEGER,
    error_code      TEXT,
    error_message   TEXT,
    idempotency_key TEXT    NOT NULL,
    PRIMARY KEY (system_id, operation_id)
);

CREATE INDEX IF NOT EXISTS idx_import_operations_status_started
    ON import_operations(status, started_at);

CREATE TABLE IF NOT EXISTS active_import_by_system (
    system_id    TEXT    NOT NULL,
    kind         TEXT    NOT NULL,
    operation_id TEXT    NOT NULL,
    started_at   INTEGER NOT NULL,
    PRIMARY KEY (system_id, kind)
);
