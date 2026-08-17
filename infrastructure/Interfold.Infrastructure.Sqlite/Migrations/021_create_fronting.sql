-- Fronting: active fronts, history, and primary alter.
-- user_id is the scoped "{region}:{rawId}" partition key (same as accounts / journals).

CREATE TABLE IF NOT EXISTS current_fronts (
    user_id     TEXT    NOT NULL,
    alter_id    INTEGER NOT NULL,
    id          TEXT    NOT NULL,
    comment     TEXT,
    time_start  INTEGER NOT NULL,
    PRIMARY KEY (user_id, alter_id)
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_current_fronts_id ON current_fronts(user_id, id);

CREATE TABLE IF NOT EXISTS fronts (
    user_id     TEXT    NOT NULL,
    id          TEXT    NOT NULL,
    alter_id    INTEGER NOT NULL,
    comment     TEXT,
    time_start  INTEGER NOT NULL,
    time_end    INTEGER,
    PRIMARY KEY (user_id, id)
);

CREATE INDEX IF NOT EXISTS idx_fronts_time_start ON fronts(user_id, time_start);

CREATE TABLE IF NOT EXISTS front_primary (
    user_id   TEXT    PRIMARY KEY,
    alter_id  INTEGER
);
