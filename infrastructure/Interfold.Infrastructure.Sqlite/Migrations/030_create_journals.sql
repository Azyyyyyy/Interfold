-- Journal tables (global + per-alter). Dialect-neutral SQLite.
-- Timestamps are unix-ms INTEGER; booleans are INTEGER 0/1; UUIDs are TEXT ("N" hex).

CREATE TABLE IF NOT EXISTS global_journals (
    user_id     TEXT    NOT NULL,
    id          TEXT    NOT NULL,
    title       TEXT    NOT NULL,
    content     TEXT,
    color       TEXT,
    pinned      INTEGER NOT NULL DEFAULT 0,
    locked      INTEGER NOT NULL DEFAULT 0,
    inserted_at INTEGER NOT NULL,
    updated_at  INTEGER NOT NULL,
    PRIMARY KEY (user_id, id)
);

CREATE TABLE IF NOT EXISTS global_journal_alters (
    user_id           TEXT    NOT NULL,
    global_journal_id TEXT    NOT NULL,
    alter_id          INTEGER NOT NULL,
    inserted_at       INTEGER NOT NULL,
    updated_at        INTEGER NOT NULL,
    PRIMARY KEY (user_id, global_journal_id, alter_id),
    FOREIGN KEY (user_id, global_journal_id)
        REFERENCES global_journals(user_id, id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS alter_journals (
    user_id     TEXT    NOT NULL,
    id          TEXT    NOT NULL,
    alter_id    INTEGER NOT NULL,
    title       TEXT    NOT NULL,
    content     TEXT,
    color       TEXT,
    pinned      INTEGER NOT NULL DEFAULT 0,
    locked      INTEGER NOT NULL DEFAULT 0,
    inserted_at INTEGER NOT NULL,
    updated_at  INTEGER NOT NULL,
    PRIMARY KEY (user_id, id)
);

CREATE INDEX IF NOT EXISTS idx_alter_journals_by_alter
    ON alter_journals(user_id, alter_id, inserted_at);
