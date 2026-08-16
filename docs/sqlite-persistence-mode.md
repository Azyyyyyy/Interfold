# `PersistenceMode.Sqlite` — full-stack SQLite mode for small self-hosts

Last updated: 2026-08-13
Status: **Scaffolding landed on 2026-08-13 — follow-on PRs enumerated below.**

Adds a third `PersistenceMode` alongside `ScyllaPostgres` and `InMemory` that
runs the entire persistence surface — domain repositories, idempotency store,
auth-token revocation, notification-token store, secrets store — from a
single SQLite file. Target audience: Raspberry Pi Zero 2W / Pi 3 /
low-memory-x86 self-hosts where the current Cassandra + Postgres compose
stack is too heavy, and any operator who wants a single-file, no-Docker
install.

---

## Motivation

The current low-powered-device path lives in
[`tools/Interfold.Bootstrapper`](../tools/Interfold.Bootstrapper/): swap
**Cassandra** in for Scylla (arm64 base image built at compose-up time by
`CassandraImagePhase`), keep Postgres alongside for the auth / idempotency /
secrets surfaces that Scylla is the wrong tool for. On a Pi 4 (4 GB) or Pi 5
(8 GB) this is tight but workable; on smaller hardware it doesn't fit. A
SQLite mode collapses the stack.

Rough baseline resource comparison:

| Mode | RSS at rest | Startup | Docker | ARM support |
| --- | --- | --- | --- | --- |
| `ScyllaPostgres` (Scylla + Postgres) | ~3 GB | ~60 s | Yes | x86 only (Scylla) |
| `ScyllaPostgres` (Cassandra + Postgres) | ~2 GB | ~60 s | Yes | arm64 via custom image |
| `Sqlite` (proposed) | ~10–20 MB | < 1 s | No | Native everywhere |

---

## Decision

- **One mode, `PersistenceMode.Sqlite`, all-in-one file.** No mixed
  configurations (no "Cassandra domain + SQLite auth", no "SQLite domain +
  Postgres auth"). The combinatorial explosion isn't worth it for the target
  audience.
- **SQLite absorbs every persistence surface currently in `Interfold.Infrastructure.Postgres`:**
  `IIdempotencyStore`, `IAuthTokenRevocationRepository`,
  `INotificationTokenRepository`, secrets store — in addition to every
  domain repository. The whole point is to remove the Postgres dependency
  entirely, not just replace CQL.
- **No automated migration between modes.** If a `ScyllaPostgres` operator
  wants to switch to `Sqlite`, they export via API and re-import into a
  fresh deployment. Direct data-file migration is out of scope.
- **No multi-node SQLite.** No rqlite, no LiteFS. This mode is
  single-machine by construction.
- **Avatars stay on `LocalAvatarStorage`.** No BLOB-in-SQLite for avatars —
  the current `LocalAvatarStorage` disk pattern already works and there's
  no reason to change it for this mode.

---

## Locked-in decisions

Recorded with the scaffolding slice so subsequent PRs do not re-litigate them:

- **Package**: `Microsoft.Data.Sqlite` (AOT-safe, no EF Core, no reflection JSON).
- **AOT**: `Interfold.Infrastructure.Sqlite` sets
  `<IsAotCompatible>true</IsAotCompatible>`; all JSON goes through
  `System.Text.Json` source-generated contexts (mirrors the Postgres project's
  approach).
- **KMP-shareable schema**: schema DDL lives in embedded `.sql` resource files
  under `infrastructure/Interfold.Infrastructure.Sqlite/Migrations/` (kept
  dialect-neutral SQLite — no `WITHOUT ROWID`, no `STRICT` for now, no
  `AUTOINCREMENT`). A future Kotlin Multiplatform client (via SQLDelight or
  Room-KMP) reads the same `.sql` bytes. Type conventions: `TEXT` for UUIDs /
  enum wire values / JSON / ISO-8601; `INTEGER` for booleans (0/1) and unix-ms
  timestamps; no `BLOB` (avatars stay on `LocalAvatarStorage`).
- **Connection pragmas** applied at every open by `SqliteConnectionFactory`:
  `PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;
  PRAGMA busy_timeout=5000;`.

---

## Prerequisites

1. **Feature-module split through Phase 4+.** Landed — per-feature `.Domain`
   projects give SQLite repository implementations clean seams. The scaffolding
   project already references the same `.Domain` set as
   `Interfold.Infrastructure.Scylla`.
2. **KMP client is a consumer, not a blocker.** A future Kotlin Multiplatform
   client (separate repo) will consume the `Migrations/*.sql` bytes published
   from this workstream. Client-store SQLite is no longer a prerequisite for
   API-side SQLite work; schemas stay identical by construction so the client
   can attach later via SQLDelight / Room-KMP.

The three stores that are **new** to the API SQLite mode (not needed by the
client) are `SqliteIdempotencyStore`, `SqliteAuthTokenRevocationRepository`,
and a SQLite-backed secrets store. Each is a mechanical port of its
Postgres counterpart in `Interfold.Infrastructure.Postgres`.

---

## Target project layout

Project: `Interfold.Infrastructure.Sqlite` (scaffolded).

- References every per-feature `Interfold.<Feature>.Domain` project (mirrors
  `Interfold.Infrastructure.Scylla`).
- Package: `Microsoft.Data.Sqlite`.
- `SqliteServiceCollectionExtensions.Register()` self-registers into the
  persistence dispatcher and currently throws
  `NotImplementedException` so `OCTOCON_PERSISTENCE=sqlite` fails fast with a
  clear message.
- `SqliteConnectionFactory` applies the locked-in pragmas; repositories in
  follow-on slices consume it.
- Placeholder `Migrations/000_placeholder.sql` + README describing KMP-shareable
  schema conventions.

---

## Scope of what changes elsewhere

The following are all small, targeted changes rather than restructures:

- **`shared/Interfold.Shared.Contracts/PersistenceMode.cs`** — enum member
  `Sqlite` with wire value `"sqlite"` (landed in scaffolding).
- **`shared/Interfold.Shared.Contracts/Enums/EnumWireExtensions.cs`** — error
  message includes `sqlite` (landed).
- **`SqliteServiceCollectionExtensions.Register()`** + host `Program.cs`
  registration call (landed; develop uses self-registration rather than a
  switch inside `ServiceCollectionExtensions`).
- **`hosts/Interfold.Api.Host/Program.cs` health-check block** — new branch
  registering `AddReadyAndStartup<SqliteHealthChecker>("sqlite")` when `Sqlite`
  mode is active (follow-on).
- **`hosts/Interfold.Api.Host/Services/Secrets/SecretsPreBuildLoader.cs`** —
  three-way switch: `InMemory` / `Postgres (ScyllaPostgres)` / `Sqlite`
  (follow-on).
- **`hosts/Interfold.AppHost/InterfoldAppHost.cs`** — Sqlite-mode bootstrap
  path that skips CQL and Postgres compose resources; bind-mounted `.db`
  volume (follow-on).
- **`tools/Interfold.Bootstrapper`** — Sqlite bootstrap variant: skip
  Cassandra/Postgres phases; create data dir, run migrations, seed; wire
  `backup` / `restore` to SQLite online-backup API (follow-on).
- **Integration tests** — `SqliteWebFactoryFixture`; suite runs against
  InMemory + Sqlite by default (follow-on).

---

## Follow-on work

Each item is its own PR:

1. **Cross-cutting stores** — `SqliteIdempotencyStore`,
   `SqliteAuthTokenRevocationRepository`, `SqliteSecretsStore` +
   `SqliteMigrationService` + `SqliteHealthChecker`. Mechanical ports of the
   Postgres equivalents under
   [`infrastructure/Interfold.Infrastructure.Postgres/`](../infrastructure/Interfold.Infrastructure.Postgres/).
2. **Domain repositories (11)**, one feature per PR, mirroring InMemory +
   Scylla: Account · Alter · EncryptionState · Friendship · Fronting ·
   ImportOperation · Journal · NotificationToken · Poll · SettingsField · Tag.
   Each consumes `SqliteConnectionFactory`.
3. **Program.cs wiring** — health-check branch registering
   `AddReadyAndStartup<SqliteHealthChecker>("sqlite")`; three-way switch in
   `SecretsPreBuildLoader.cs`.
4. **AppHost** — Sqlite bootstrap path in `InterfoldAppHost.cs` that skips
   Scylla/Postgres compose resources and bind-mounts the `.db` volume onto the
   API resource.
5. **Bootstrapper** — Sqlite variant in `tools/Interfold.Bootstrapper/Phases/`:
   skips `CassandraImagePhase` + Postgres phases; new phase creates data dir +
   runs schema migrations + seeds. `backup` / `restore` wired to SQLite
   online-backup API.
6. **Tests** — `SqliteWebFactoryFixture` alongside `InMemoryWebFactoryFixture`
   under `tests/integration/Interfold.IntegrationTests.Shared/`; integration
   suite runs against InMemory + Sqlite by default.
7. **KMP client integration** — separate repo; publish the `Migrations/*.sql`
   bytes as an artefact the KMP client can consume via SQLDelight/Room-KMP
   against an identical schema.

---

## Workload sanity check

Family-scale self-host (5–20 users) baseline:

- Peak writes: < 10/s (domain mutations + idempotency + occasional auth
  event). SQLite WAL sustains ~400 K writes/s on modest hardware; three
  orders of magnitude of headroom.
- Peak concurrent reads: dozens (WebSocket clients + REST calls). WAL mode
  does not serialise readers against a single writer.
- Bulk imports (SP / PK) are the shape to watch — implement as batched
  transactions with periodic commits so live user traffic gets writer
  slots between batches. Same pattern the current Postgres path uses.

Above the target audience (~100+ concurrent write-heavy users), single-writer
serialisation starts to bite; that deployment should stay on
`ScyllaPostgres`. This is documented as a non-goal below.

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

- **Mixed modes** (`CassandraSqlite`, `SqlitePostgres`, etc.). Two modes:
  full CQL+Pg or full SQLite.
- **Multi-node SQLite.** SQLite mode is single-machine.
- **Automated data migration** between `ScyllaPostgres` and `Sqlite`.
  Operators export/import via API.
- **Enterprise / large-team deploys.** SQLite mode targets personal,
  family, and small-community self-hosts; larger deployments stay on
  `ScyllaPostgres`.
- **Retiring `InMemory` mode.** InMemory stays for unit tests — it does no
  I/O and is fastest for isolated repo behaviour tests. Sqlite mode
  complements it rather than replacing it.
- **Retiring `ScyllaPostgres` mode.** The high-end path stays exactly as
  it is.

---

## Open questions

- **First-boot schema strategy.** Migration ledger like Postgres has
  (rows in `internal.migration_history`), or one-shot `CREATE TABLE IF
  NOT EXISTS` at boot? Provisional call: migration ledger for symmetry
  with the Postgres path.
- **Backup story.** SQLite online backup API + `sqlite3_backup_step`
  triggered from a systemd timer, or leave `cp interfold.db backup.db`
  (with a WAL checkpoint step) to the operator? Provisional call: bootstrap
  an operator-friendly `interfold-bootstrap backup` command that does the
  right thing (checkpoint + online backup + WAL rotate).
- **Encrypted at rest?** SQLite has SEE (commercial), SQLCipher (LGPL),
  or the "encrypt the whole disk" option. For a personal-device
  self-host, disk-level encryption is usually the operator's answer.
  Decide when the mode is closer to landing.
- **DuckDB analytics attach — worth it, and when?** Provisional call:
  add nothing now. Revisit if a concrete analytical use case appears
  (large-import dashboards, historical aggregations). The attach model
  means it would be additive — a new `Interfold.<Feature>.LocalStore.Analytics`
  project that references DuckDB.NET, opens the SQLite file read-only,
  runs the query. Zero impact on the write path.
- **InMemory idempotency / revocation fakes vs `:memory:` SQLite.** The
  `InMemory` mode has fake implementations of `IIdempotencyStore` and
  `IAuthTokenRevocationRepository`. Once Sqlite mode exists, we could
  point InMemory at `:memory:` SQLite and delete the duplicate fakes.
  Provisional call: keep both. The InMemory fakes are trivial and fast;
  a `:memory:` SQLite pays real I/O overhead. Revisit when the fakes
  drift out of parity.
- **Test-suite runtime cost.** Adding a third backend to the integration
  suite (or a fourth counting Cassandra) grows CI. Provisional call: run
  the full suite against InMemory + Sqlite by default; run against
  Scylla + Cassandra nightly / on release branches.

---

## Progress

- 2026-07-22 — Roadmap entry captured. No code work started; awaiting
  feature-module split completion + client-store SQLite work.
- 2026-08-13 — Scaffolding slice landed: `PersistenceMode.Sqlite` wire value,
  DI self-registration stub, `Interfold.Infrastructure.Sqlite` project with
  pragma helper + placeholder migration, design doc moved to
  `docs/sqlite-persistence-mode.md` with locked-in decisions and follow-on
  work list. KMP client reframed as consumer of schemas, not a prerequisite.
- 2026-08-13 — Cross-cutting stores landed (secrets / idempotency / auth
  tokens / migration ledger / health checker) with unit tests; domain
  repositories registered as NotImplemented stubs.
- 2026-08-13 — Host wiring: `SecretsPreBuildLoader` three-way switch +
  `SqliteHealthChecker` readiness registration.
- 2026-08-13 — `SqliteWebFactoryFixture` attached alongside InMemory on the
  default multi-backend ClassDataSource suites (scoreboard below). Expect
  red until domain repositories land.

## Integration scoreboard

Suites with `[ClassDataSource<SqliteWebFactoryFixture>]` (additive to
InMemory / Scylla / Cassandra):

| Suite | Notes |
| --- | --- |
| AltersControllerTests | ClassDataSource includes Sqlite |
| AuthControllerTests / AuthLinkControllerTests | ClassDataSource includes Sqlite |
| Friends* / SendFriendRequestPrefixTests | ClassDataSource includes Sqlite |
| FrontingControllerTests | ClassDataSource includes Sqlite |
| Journals* | ClassDataSource includes Sqlite |
| PollsControllerTests | ClassDataSource includes Sqlite |
| SettingsControllerTests / AvatarSourceTests | ClassDataSource includes Sqlite |
| TagsControllerTests | ClassDataSource includes Sqlite |
| PublicSystemsControllerTests | ClassDataSource includes Sqlite |
| WebSocket* | ClassDataSource includes Sqlite |
| NodeRoleControllerTests | ClassDataSource includes Sqlite |
| ReplayParityTests | ClassDataSource includes Sqlite |

Integration ClassDataSource legs may still fail on behavioural parity gaps —
they are the progress tracker, not a release gate yet.

Domain repository status:

| Slice | Status |
| --- | --- |
| Account | implemented |
| EncryptionState | implemented |
| SettingsField | implemented |
| Alter | implemented |
| Tag | implemented |
| Fronting | implemented |
| Friendship | implemented |
| Journal | implemented |
| Poll | implemented |
| NotificationToken | implemented |
| ImportOperation | implemented |

- 2026-08-13 — Domain repositories (11) ported from InMemory behavioural contracts onto
  SQLite tables + `ISqliteConnectionFactory`. NotImplemented stubs removed. Integration
  ClassDataSource legs remain the progress tracker for behavioural parity.
