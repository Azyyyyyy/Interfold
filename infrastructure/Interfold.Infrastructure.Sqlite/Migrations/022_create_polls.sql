-- Polls. user_id is the scoped "{region}:{rawId}" partition key.
-- type is PollType wire text; data is JSON TEXT; timestamps are unix-ms.

CREATE TABLE IF NOT EXISTS polls (
    user_id     TEXT    NOT NULL,
    id          TEXT    NOT NULL,
    title       TEXT    NOT NULL,
    description TEXT,
    type        TEXT    NOT NULL,
    data        TEXT    NOT NULL DEFAULT '{}',
    time_end    INTEGER,
    inserted_at INTEGER NOT NULL,
    updated_at  INTEGER NOT NULL,
    PRIMARY KEY (user_id, id)
);
