-- Per-system custom field definitions (settings fields).
-- idx is the display ordinal (0-based). type / security_level are smallint ordinals.

CREATE TABLE IF NOT EXISTS settings_fields (
    system_id       TEXT    NOT NULL,
    id              TEXT    NOT NULL,
    name            TEXT    NOT NULL,
    type            INTEGER NOT NULL,
    security_level  INTEGER NOT NULL,
    locked          INTEGER NOT NULL DEFAULT 0,
    idx             INTEGER NOT NULL,
    inserted_at     INTEGER NOT NULL,
    updated_at      INTEGER NOT NULL,
    PRIMARY KEY (system_id, id)
);

CREATE INDEX IF NOT EXISTS idx_settings_fields_order
    ON settings_fields(system_id, idx);
