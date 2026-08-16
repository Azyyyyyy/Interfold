-- Account / encryption / push-token tables for PersistenceMode.Sqlite.
-- system_id on accounts is the scoped "{region}:{rawId}" key (InMemory ForSystem).
-- encryption_states / notification_tokens use normalized (prefix-stripped) system_id.

CREATE TABLE IF NOT EXISTS accounts (
    system_id              TEXT    PRIMARY KEY,
    username               TEXT,
    description            TEXT,
    avatar_url             TEXT,
    avatar_source          TEXT,
    discord_id             TEXT,
    email                  TEXT,
    apple_id               TEXT,
    link_token             TEXT,
    link_token_expires_at  INTEGER,
    created_at             INTEGER NOT NULL,
    updated_at             INTEGER NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_accounts_discord_id
    ON accounts(discord_id COLLATE NOCASE)
    WHERE discord_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS idx_accounts_email
    ON accounts(email COLLATE NOCASE)
    WHERE email IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS idx_accounts_apple_id
    ON accounts(apple_id COLLATE NOCASE)
    WHERE apple_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS idx_accounts_link_token
    ON accounts(link_token)
    WHERE link_token IS NOT NULL;

CREATE TABLE IF NOT EXISTS encryption_states (
    system_id                 TEXT    PRIMARY KEY,
    encryption_initialized    INTEGER NOT NULL DEFAULT 0,
    encryption_key_checksum   TEXT,
    salt                      TEXT,
    updated_at                INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS notification_tokens (
    system_id    TEXT    NOT NULL,
    push_token   TEXT    NOT NULL,
    inserted_at  INTEGER NOT NULL,
    updated_at   INTEGER NOT NULL,
    PRIMARY KEY (system_id, push_token)
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_notification_tokens_push_token
    ON notification_tokens(push_token);
