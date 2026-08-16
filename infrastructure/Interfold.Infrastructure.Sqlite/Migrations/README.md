# SQLite migrations (KMP-shareable)

Schema DDL lives here as embedded `.sql` resources. The API host applies them via
a future `SqliteMigrationService`; a separate Kotlin Multiplatform client may
consume the same bytes through SQLDelight or Room-KMP.

## Conventions

- Dialect-neutral SQLite only (no `WITHOUT ROWID`, no `STRICT`, no `AUTOINCREMENT` for now).
- Types: `TEXT` for UUIDs / enum wire values / JSON / ISO-8601; `INTEGER` for booleans (0/1) and unix-ms timestamps; no `BLOB` (avatars stay on `LocalAvatarStorage`).
- Connection pragmas (applied by `SqliteConnectionFactory` on every open):
  `journal_mode=WAL`, `synchronous=NORMAL`, `foreign_keys=ON`, `busy_timeout=5000`.

See `docs/sqlite-persistence-mode.md` for the full locked-in decisions and follow-on work list.
