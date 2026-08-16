CREATE TABLE IF NOT EXISTS octocon_idempotency (
    principal_id    TEXT    NOT NULL,
    operation_id    TEXT    NOT NULL,
    idempotency_key TEXT    NOT NULL,
    payload_hash    TEXT    NOT NULL,
    outcome_hash    TEXT    NOT NULL,
    outcome_payload TEXT,
    created_at      INTEGER NOT NULL,
    PRIMARY KEY (principal_id, operation_id, idempotency_key)
);
