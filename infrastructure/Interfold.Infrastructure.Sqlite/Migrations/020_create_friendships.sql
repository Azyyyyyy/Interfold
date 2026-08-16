-- Friendship graph + pending requests (global — normalized system ids, no region prefix).
-- level is FriendshipLevel wire text ("friend" / "trusted_friend"); timestamps are unix-ms.

CREATE TABLE IF NOT EXISTS friendships (
    user_id    TEXT    NOT NULL,
    friend_id  TEXT    NOT NULL,
    level      TEXT    NOT NULL,
    since      INTEGER NOT NULL,
    PRIMARY KEY (user_id, friend_id)
);

CREATE INDEX IF NOT EXISTS idx_friendships_friend_id ON friendships(friend_id);

CREATE TABLE IF NOT EXISTS friend_requests (
    from_user_id TEXT    NOT NULL,
    to_user_id   TEXT    NOT NULL,
    date_sent    INTEGER NOT NULL,
    PRIMARY KEY (from_user_id, to_user_id)
);

CREATE INDEX IF NOT EXISTS idx_friend_requests_to_user_id ON friend_requests(to_user_id);
