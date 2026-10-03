# SQLite migrations (KMP-shareable)

Schema DDL lives here as embedded `.sql` resources. The API host applies them via
a future `SqliteMigrationService`; a separate Kotlin Multiplatform client may
consume the same bytes through SQLDelight or Room-KMP.

## Conventions

- Dialect-neutral SQLite only (no `WITHOUT ROWID`, no `STRICT`, no `AUTOINCREMENT` for now).
- Types: `TEXT` for UUIDs / JSON / emails / Discord snowflakes / import-enum wires; `INTEGER` for booleans (0/1), unix-ms timestamps, and short-backed enum ordinals (same codes as Scylla `smallint`); no `BLOB` (avatars stay on `LocalAvatarStorage`).
- Connection pragmas (applied by `SqliteConnectionFactory` on every open):
  `journal_mode=DELETE`, `synchronous=FULL`, `foreign_keys=ON`, `busy_timeout=5000`.
  Pooling stays at the Microsoft.Data.Sqlite default (on).

See `docs/sqlite-persistence-mode.md` for the full locked-in decisions and follow-on work list.
