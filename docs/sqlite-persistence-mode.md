# `PersistenceMode.Sqlite` — full-stack SQLite mode

Status: **Active**, alongside `PersistenceMode.InMemory`.

`PersistenceMode.Sqlite` runs the entire persistence surface — domain repositories, idempotency store,
auth-token revocation, notification-token store, secrets store — from a single SQLite file. Target audience:
single-file, zero-external-dependency self-hosts and local development.

---

## Architecture

- **One mode, `PersistenceMode.Sqlite`, all-in-one file.**
- **SQLite absorbs every persistence surface:** `IIdempotencyStore`, `IAuthTokenRevocationRepository`, `INotificationTokenRepository`, `ISecretsStore`, in addition to every domain repository.
- **No multi-node SQLite.** SQLite mode is single-machine by construction.
- **Avatars stay on `LocalAvatarStorage`.** No BLOB-in-SQLite for avatars.

---

## Locked-in decisions

- **Package**: `Microsoft.Data.Sqlite` (AOT-safe, no EF Core, no reflection JSON).
- **AOT**: `Interfold.Infrastructure.Sqlite` sets `<IsAotCompatible>true</IsAotCompatible>`; all JSON goes through `System.Text.Json` source-generated contexts.
- **KMP-shareable schema**: schema DDL lives in embedded `.sql` resource files under `infrastructure/Interfold.Infrastructure.Sqlite/Migrations/` (kept dialect-neutral SQLite — no `WITHOUT ROWID`, no `STRICT` for now, no `AUTOINCREMENT`). Type conventions: `TEXT` for UUIDs / JSON / emails / Discord snowflakes / import-enum wires; `INTEGER` for booleans (0/1), unix-ms timestamps, and short-backed enum ordinals; no `BLOB` (avatars stay on `LocalAvatarStorage`). Discord ids stay `TEXT` because SQLite `INTEGER` is signed 64-bit and snowflakes overflow `long.MaxValue` around 2084.
- **Connection pragmas** applied at every open by `SqliteConnectionFactory`: `PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;`.

---

## Integration test matrix

Integration test suites run against `SqliteWebFactoryFixture` and `InMemoryWebFactoryFixture`:

| Suite | Status |
| --- | --- |
| AltersControllerTests | green |
| AuthControllerTests / AuthLinkControllerTests | green |
| Friends* / SendFriendRequestPrefixTests | green |
| FrontingControllerTests | green |
| Journals* | green |
| NodeRoleControllerTests | green |
| PollsControllerTests | green |
| Settings* / AvatarSourceTests | green |
| WebSocket* / ReplayParityTests | green |
| PublicSystemsControllerTests / TagsControllerTests | green |

---

## Implementation status

The SQLite implementation is complete. Per-feature repositories and cross-cutting
stores use `SqliteConnectionFactory`; migrations and the SQLite health check are
registered by the API host. The schema remains available as embedded SQL resources
for clients that need to consume the same local-store format.

---

## Project layout

Project: `Interfold.Infrastructure.Sqlite`.

- References every per-feature `Interfold.<Feature>.Domain` project.
- Package: `Microsoft.Data.Sqlite`.
- `SqliteServiceCollectionExtensions.Register()` registers the complete SQLite
  persistence graph.
- `SqliteConnectionFactory` applies the configured connection pragmas for every
  repository and store.
- `Migrations/` contains the embedded schema and migration ledger resources.

---

## Host and test integration

- The API registers the SQLite health check and the SQLite persistence graph when
  `OCTOCON_PERSISTENCE=sqlite` is selected.
- `SecretsPreBuildLoader` supports the two active modes: SQLite and InMemory.
- AppHost runs `SqliteDevSeed`, sets `OCTOCON_PERSISTENCE` and
  `OCTOCON_SQLITE_CONNECTION`, and stores the development database below
  `hosts/Interfold.AppHost/.data/sqlite/`.
- The bootstrapper uses `datastores.persistence: sqlite`, initializes the data directory,
  migrates and seeds it, and routes backup/restore to the SQLite database.
- Integration suites use `SqliteWebFactoryFixture` and
  `InMemoryWebFactoryFixture` only. All feature suites have green SQLite and
  InMemory legs.

---

## Workload characteristics

SQLite is intended for a single host, family deployment, or small community service.
Its single-writer design provides ample capacity for ordinary API traffic, while
large imports should use bounded transactions so interactive requests continue to
make progress. Use the bootstrapper backup command rather than copying a live file.

---

## Why not DuckDB (evaluation record)

Recorded 2026-07-22 so the question doesn't get reopened. DuckDB.NET
reached full Native-AOT compatibility in v1.5.0 (March 2026,
`LibraryImport` migration, custom string marshallers, `SuppressGCTransition`
on trivial calls), and DuckDB themselves publish `DuckDB.ExtensionKit`
explicitly requiring AOT — so the mechanism is there. Rejected on
workload-fit grounds for both the local-store client and this API mode:

- DuckDB is OLAP-oriented; Interfold's persistence surface is entirely OLTP
  (small point reads, small single-row writes, one command per user action).
- Global single writer with no WAL — a single POST holding a write
  transaction stalls every concurrent GET. The API's WebSocket cluster
  event bus writes on every relevant domain event, which under DuckDB
  would block every socket-relevant read on the same process.
- Per-row insert overhead is 10–50 ms (transaction machinery is bulk-oriented,
  wants `COPY` not row-at-a-time). SQLite is sub-millisecond in the same
  path.
- SQLite: ~400 K inserts/s WAL. DuckDB: ~200 K/s single-writer, no reader
  concurrency during writes.
- No shipped mobile RIDs (`ios-*` / `android-*`) — irrelevant for a server
  but a maturity signal about the platform's target audience.
- Adds 30–50 MB to an iOS binary (relevant for the client, less so here)
  vs 0 MB for the OS-provided SQLite.

SQLite is the correct primary store for both scenarios. DuckDB may re-enter
the picture later as an **optional read-only analytics layer** that
`ATTACH`es the primary SQLite file — well-established pattern, zero impact
on the write path — but only when a concrete analytical use case appears
(offline dashboards during a large import, "your fronting history over
the last year" style aggregations). Captured under Open questions.

---

## Non-goals

- Multi-machine or multi-writer SQLite clustering.
- Automatic migration between unrelated persistence engines.
- Storing avatar binaries in SQLite.
- Replacing InMemory for fast, isolated tests.
- Adding another durable persistence engine as part of the SQLite implementation.

---

## Operational follow-up

- Keep the migration ledger and embedded migration files immutable after release;
  schema changes should use a new migration.
- Protect SQLite backups because they include the durable secrets store.
- Consider filesystem or volume encryption when the deployment's threat model
  requires encryption at rest.
- Keep both active modes: InMemory is deliberately optimized for isolated tests,
  while SQLite provides durable state.

---

## Progress

- `PersistenceMode.Sqlite` and `PersistenceMode.InMemory` are the active wire values.
- SQLite domain repositories, cross-cutting stores, migrations, and health checks are
  implemented and registered by the host.
- AppHost and the bootstrapper initialize SQLite, seed secrets, and expose SQLite
  backup/restore operations.
- Integration suites are green against the SQLite and InMemory fixtures.

## Integration scoreboard

All feature suites use the two active fixture types and are verified green locally:

| Suite | Sqlite ClassDataSource |
| --- | --- |
| AltersControllerTests | green |
| AuthControllerTests / AuthLinkControllerTests | green |
| Friends* / SendFriendRequestPrefixTests | green |
| FrontingControllerTests | green |
| Journals* | green |
| PollsControllerTests | green |
| SettingsControllerTests / AvatarSourceTests | green |
| TagsControllerTests | green |
| PublicSystemsControllerTests | green (same intentional skip as InMemory) |
| WebSocket* | green |
| NodeRoleControllerTests | green |
| ReplayParityTests | green |

Parity is covered by the same controller and socket suites for both active modes.
