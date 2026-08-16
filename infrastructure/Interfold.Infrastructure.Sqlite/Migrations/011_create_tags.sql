-- Tags + alter↔tag membership.
-- Tag ids are TEXT ("N" hex UUIDs). system_id is scoped "{region}:{rawId}".

CREATE TABLE IF NOT EXISTS tags (
    system_id       TEXT    NOT NULL,
    id              TEXT    NOT NULL,
    parent_tag_id   TEXT,
    name            TEXT    NOT NULL,
    description     TEXT,
    color           TEXT,
    security_level  TEXT    NOT NULL,
    inserted_at     INTEGER NOT NULL,
    updated_at      INTEGER NOT NULL,
    PRIMARY KEY (system_id, id)
);

CREATE TABLE IF NOT EXISTS alter_tags (
    system_id   TEXT    NOT NULL,
    tag_id      TEXT    NOT NULL,
    alter_id    INTEGER NOT NULL,
    inserted_at INTEGER NOT NULL,
    updated_at  INTEGER NOT NULL,
    PRIMARY KEY (system_id, tag_id, alter_id)
);

CREATE INDEX IF NOT EXISTS idx_alter_tags_by_alter
    ON alter_tags(system_id, alter_id);
