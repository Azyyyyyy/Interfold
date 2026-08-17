-- Alters + per-alter custom field values.
-- system_id is the scoped "{region}:{rawId}" key (SqliteStorageKeys.ForSystem).
-- Enums (security_level, avatar_source) stored as TEXT wire values; booleans as INTEGER 0/1;
-- timestamps as unix-ms INTEGER. Alter ids are INTEGER (Scylla smallint).

CREATE TABLE IF NOT EXISTS alters (
    system_id       TEXT    NOT NULL,
    id              INTEGER NOT NULL,
    alias           TEXT,
    name            TEXT    NOT NULL,
    pronouns        TEXT,
    description     TEXT,
    avatar_url      TEXT,
    avatar_source   TEXT,
    security_level  TEXT    NOT NULL,
    color           TEXT,
    proxy_name      TEXT,
    untracked       INTEGER NOT NULL DEFAULT 0,
    archived        INTEGER NOT NULL DEFAULT 0,
    pinned          INTEGER NOT NULL DEFAULT 0,
    inserted_at     INTEGER NOT NULL,
    updated_at      INTEGER NOT NULL,
    PRIMARY KEY (system_id, id)
);

CREATE INDEX IF NOT EXISTS idx_alters_alias
    ON alters(system_id, alias COLLATE NOCASE)
    WHERE alias IS NOT NULL;

CREATE TABLE IF NOT EXISTS alter_fields (
    system_id  TEXT    NOT NULL,
    alter_id   INTEGER NOT NULL,
    field_id   TEXT    NOT NULL,
    value      TEXT,
    PRIMARY KEY (system_id, alter_id, field_id)
);
