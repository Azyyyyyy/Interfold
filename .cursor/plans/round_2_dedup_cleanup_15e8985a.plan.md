---
name: Round 2 dedup cleanup
overview: Round-2 codebase dedup and thin-wrapper cleanup on the Interfold C# solution. Closes the ~11 outstanding tails from Round 1 and lands ~25 new grep-verified findings that Round 1's helpers exposed. Includes three correctness fixes (InMemory front-delete parity, ScyllaJournal nested-retry, JWT dedupe) and ~1,500 LOC of dedup across Scylla, InMemory, API controllers, socket handlers, AppHost, Bootstrapper phases, and the four test projects.
todos:
  - id: wave-a
    content: "Wave A: correctness bugs — A1 InMemory Fronting delete parity, A2 ScyllaJournal nested-retry, A3 PkImportJobRunner error code decision"
    status: completed
  - id: b1-noredirect
    content: "B1: TestClient.NoRedirect sweep (42 sites, ~114 LOC)"
    status: completed
  - id: b2-dispatch
    content: "B2: Dispatch{NoContent,Accepted,Created}Async on InterfoldControllerBase (~54 sites)"
    status: completed
  - id: b3-socket-push
    content: "B3: SocketPushContext.SendIfJoinedAsync (26 sites, ~66 LOC)"
    status: completed
  - id: b4-global-scope
    content: "B4: extend IScyllaScopeResolver to global keyspace + close regional B1 miss (22 sites, ~50 LOC net)"
    status: completed
  - id: b5-row-mappers
    content: "B5: 4 row-mapper extractions — AlterReadModel / FrontHistory / AlterJournal / Tag (~115 LOC, 4 commits)"
    status: completed
  - id: b6-scratch-dir
    content: "B6: TestSupport.NewScratchDir + TryDeleteDir helper (~120 LOC)"
    status: completed
  - id: b7-jwt-validator
    content: "B7: shared JwtEs256Validator helper (security-critical, ~90 LOC, needs unit tests + second reviewer)"
    status: completed
  - id: b8-front-close-batch
    content: "B8: AddFrontCloseStatements batch builder (~30 LOC)"
    status: completed
  - id: c1-bootstrap-sweep
    content: "C1: sweep ~59 remaining RunBootstrapperAsync sites onto BootstrapAsync (2-3 commits)"
    status: completed
  - id: c2-identifier-collapse
    content: "C2: FindOrCreateByIdentifier<T> + UnlinkIdentifier<T> (close B7 tail; auth boundary)"
    status: completed
  - id: c3-ws-pair
    content: "C3: WebSocketHarness.ConnectPairAndJoinAsync (7 sites, ~42 LOC)"
    status: pending
  - id: c4-makeconfig
    content: "C4: TestSupport.MakeConfig(...) + sweep 5 file-local OptionsFor (~30-70 LOC)"
    status: pending
  - id: c5-postconfigure
    content: "C5: PostConfigureHarness.Apply(...) (8 sites, ~24 LOC)"
    status: pending
  - id: c6-reject-if-self
    content: "C6: RejectIfSelf on InterfoldControllerBase (8 sites, ~35 LOC)"
    status: pending
  - id: c7-system-must-exist
    content: "C7: [SystemMustExist] filter for PublicSystemsController (7 sites, ~14 LOC)"
    status: pending
  - id: c8-qualify-friendship
    content: "C8: AvatarUrlQualifier.QualifyFriendship (5 sites, ~25-35 LOC)"
    status: pending
  - id: c9-bare-prereqs
    content: "C9: BareDinDFixtureBase.InitializeAsync (Ubuntu+Fedora pair, ~16 LOC)"
    status: pending
  - id: c10-phase-loader
    content: "C10: PhaseArtifactLoader.LoadRequiredConfigAsync + RequireComposeFileOrFail (9 sites, ~65 LOC)"
    status: pending
  - id: c11-close-compose-migration
    content: "C11: close C6 DockerCompose.* migration — 10 bypass sites + missing helpers (~40 LOC)"
    status: pending
  - id: c12-up-checked
    content: "C12: DockerCompose.UpCheckedAsync (4 sites, ~24 LOC)"
    status: pending
  - id: c13-unix-permissions
    content: "C13: UnixFilePermissions chmod dedupe (~40 LOC)"
    status: pending
  - id: c14-spdump-di
    content: "C14: swap SPDump + SpImportTests onto AddSimplyPluralImport (~15 LOC)"
    status: pending
  - id: c15-timeprovider-tail
    content: "C15: thread TimeProvider into SimplyPluralImportService + FriendshipSocketEventHandlers"
    status: pending
  - id: c16-apphost
    content: "C16: AppHost consolidation — TCP probe, compose healthcheck, persistent volumes, ready/startup (~60 LOC)"
    status: pending
  - id: d1-inmemory-wrappers
    content: "D1: delete 8 dead 1-line InMemory GetSystemKey / Normalize wrappers"
    status: pending
  - id: d2-exists-inline
    content: "D2: inline 3 remaining private ExistsAsync/SystemExistsAsync wrappers"
    status: pending
  - id: d3-inmemory-fields
    content: "D3: inline 2 remaining InMemoryAlterRepository ResolveGuardedFields/ResolveVisibleDefinitionsAsync wrappers"
    status: pending
  - id: d4-postgres-core
    content: "D4: delete PostgresIdempotencyStore -Core forwarders (~24 LOC)"
    status: pending
  - id: d5-bootstrap-stack
    content: "D5: delete orphaned BootstrapStackAsync wrapper in DbInitSecurityInvariantsTests"
    status: pending
  - id: d6-secret-converter
    content: "D6: SecretStringJsonConverter<T> abstract base (6 sites, ~30 LOC)"
    status: pending
  - id: d7-guid-id-converter
    content: "D7: GuidIdJsonConverter<T> abstract base — option A only (5 sites, ~35 LOC)"
    status: pending
  - id: d8-visibility-level
    content: "D8: move VisibilityLevel out of Alter.cs + drop redundant TryGetValue in AlterFieldProjection"
    status: pending
  - id: d9-service-defaults
    content: "D9: ServiceDefaults ConfigureOpenTelemetry/AddDefaultHealthChecks visibility decision (one-liner)"
    status: pending
  - id: d10-dumponfail-base
    content: "D10: DumpOnFailure lift-to-base (deliberate re-evaluation of C1; 19 sites, ~38 LOC — may be no-op)"
    status: pending
  - id: d11-test-helpers
    content: "D11: bulk test-helper extractions — CapturingQueue, DotEnvParser, ScyllaDirectHarness, WebSocketHarness, PhxEndpointFrame, TestIds, IsolatedAvatarStorage, DinDFixtureBase.ReadSecretAsync, DinDScratch.SecretsJsonPath, EnsurePublicProfileAsync inline, RunFriendTrustUntrustFlow (one commit per helper)"
    status: pending
  - id: d12-scylla-probe
    content: "D12: add ScyllaReadinessProbe mirror + PostgresReadinessProbe RequiredConsecutiveSuccesses=1 overload (medium boot-time risk)"
    status: pending
  - id: d13-bundle-followups
    content: "D13: bundle SendAuthedDeleteAsync + reconcile AlterJournalsControllerTests.cs:17 empty-options quirk (in the same commit as B1)"
    status: pending
isProject: false
---

# Codebase deduplication & thin-wrapper cleanup — Round 2

Scope: the full `csharp/` solution. Round 1 landed 22 of 24 planned bullets. Four parallel scans (infra/domain/contracts, API/AppHost/ServiceDefaults, tests, bootstrapper) identified 11 tail items and 25 new opportunities exposed by Round 1's helpers. Every citation below is grep-verified against the current tree.

The prior in-flight change set from Round 1 (`IScyllaScopeResolver`, `BuildEnvelope<T>`, `AlterRowMappers.MapBareAlter`, `ScyllaExistsQueries.RowExistsAsync`, `PostgresReadinessProbe.WaitAsync`, `DockerCompose[Exec]`, `PersistenceRegistration`, `TestConfigPaths`, `DinDFixtureBase`, `TestJson.SendAuthedGetAsync`, `SimplyPluralImportExtensions`) is assumed present.

## Progress (as of 2026-07-19)

**Waves A + B are complete; C1 + C2 done.** All 17 commits are on branch `dedup`:

- A1 `37f4ce6` `fix(infra): fix InMemoryFrontingRepository delete parity`
- A2 `d78b5f1` `fix(scylla): eliminate nested retry on ScyllaJournalRepository alter writers`
- A3 `7eef6d3` `refactor(contracts): add ImportErrorCode.Unimplemented; switch PkImportJobRunner`
- B1 `c2848db` `refactor(test): add TestClient.NoRedirect + sweep 42 AllowAutoRedirect sites`
- B2 `61fbb47` `refactor(api): add Dispatch*Async helpers to InterfoldControllerBase; sweep 9 controllers`
- B3 `6fee24b` `refactor(socket): add SendIfJoinedAsync to SocketPushContext; sweep 19 handler sites`
- B4 `74af853` `refactor(infra): introduce IScyllaScopeResolver.ExecuteGlobalAsync + sweep 22 sites`
- B5.a `405ae6a` `refactor(rowmapper): extract AlterRowMappers.MapAlterReadModel`
- B5.b `8bd08ee` `refactor(rowmapper): extract FrontingRowMappers.MapFrontHistoryReadModel`
- B5.c `9d52eb0` `refactor(rowmapper): extract JournalRowMappers`
- B5.d `ffa2286` `refactor(rowmapper): extract TagRowMappers`
- B6 `5aa7951` `refactor(test): extract TestSupport scratch dir helper`
- B7 `9cc2202` + `dc985bf` `refactor(auth): add JwtEs256Validator + PemUtil + unit tests` / `sweep Program.cs and WebSocketHandler.cs`
- B8 `7c039ed` `refactor(scylla): extract ScyllaFrontingDenormalizedTable batch builder`
- C1 `d863855` `refactor(bootstrapper-it): add RunOnScratchAsync helper and sweep ~40 sites`
- C2 `4cd69ec` `refactor(inmemory-accounts): extract FindOrCreateIdentifier + UnlinkIdentifier generics`

**Remaining work: Wave C (14 bullets) + Wave D (13 bullets).** Wave E stays as guardrails throughout. Wave A, Wave B, C1, and C2 sections below are kept for historical reference — do NOT re-execute them.

### Wave C grep re-verification (2026-07-19)

Counts have drifted since the plan was drafted — all favourable (smaller, not larger). Adjust expectations before executing:

- **C2**: Scylla `FindOrCreate*` variants were already collapsed onto `ProviderIdentity` (see `ScyllaAccountRepository.cs:180-186`). Only the 3 Scylla `Unlink*Async` methods (:331, 334, 337) + the InMemory 6 methods (:191, 217, 240, 263, 274, 285) need the sweep. Half the scope.
- **C6**: `RejectIfSelf` has ~4–6 real sites, not 8. `FriendsController.SetTrustInternal` already centralises the trust/untrust self-check; the remaining hand-rolled sites are 4 in `FriendRequestsController` (Send / Cancel / Accept / Reject) + 2 in `FriendsController.Show/Delete` (:49, :79).
- **C7**: 6 `SystemExistsAsync` call sites in `PublicSystemsController`, not 7. Bullet still valid.
- **C10**: `LoadRequiredConfigAsync` has 2 real sites (`LaunchPhase`, `ConfigPhase`), not 4 — `RestorePhase` / `BackupPhase` / `UpdateImagesPhase` don't currently load a config file. `RequireComposeFileOrFail` unchanged at 5 sites. Halve the LOC estimate.
- **C11**: ~7 `docker compose` bypass sites in production, not 10. RestorePhase has 5 (stop/start pairs), `UpdateImagesPhase.down` has 1, `BackupPhase.ps -q` has 1. Missing helpers reduce to `DownAsync`, `PsAsync`, `StartAsync` (drop `ImagesAsync` — only 1 site and it wants JSON parsing anyway).

Rule of thumb: **do not re-audit before starting** — the counts above are the actual ones. But if a count drifts again by the time you land the commit (e.g. someone else touches the file), pause and re-verify.

## Ground rules for the executing agent

1. **Work in small commits, one PR.** Each Wave-B/C/D bullet is a discrete commit on the same feature branch; commit messages reference the plan section (e.g. `refactor(scylla): extend scope resolver to global keyspace [R2-B4]`) so the mapping stays obvious in review.
2. **Round-1 Wave E still applies verbatim.** Do NOT collapse `AlterReadModel`'s two ctors, do NOT replace `FromNullable` factories with a generic wrap, do NOT remove `InProcessEventBus.SubscribeAsync()` no-target overload, do NOT change `SuccessResponse<T>.StatusCode` / `ErrorResponse.StatusCode` `[JsonIgnore]` on ctor parameters, do NOT force `ClusterRoleAndOAuthRegistrationTests` into a `[ClassDataSource]` base class, do NOT "simplify" the `ApplyAuthentication` OAuth-client-ID read pattern in `Program.cs`, keep single-impl interfaces (`IClusterEventBus`, `IImportJobQueue`, `IFCMService`, `ISingletonTaskOwner`, `IPostgresExecutor`, `IScyllaExecutor`, `IDatabaseInitLogger`, `INodeRoleContext`).
3. **New Wave E items in this plan** (§ Wave E) — do NOT touch them either.
4. **JWT dedupe is security-critical.** [B7](csharp/Interfold.Api/Program.cs) requires unit tests on the shared validator before the sweep, and the diff must be reviewed by someone other than the author before merge.
5. **Do not squash the branch.** Preserve the commit-per-bullet story.

## Wave A — correctness bugs (must fix, do first)

- **A1. `InMemoryFrontingRepository.DeleteFrontByIdAsync` clears the wrong active front.** [csharp/Interfold.Infrastructure.InMemory/Repository/InMemoryFrontingRepository.cs](csharp/Interfold.Infrastructure.InMemory/Repository/InMemoryFrontingRepository.cs) lines 290-315: the `active.ContainsKey(entry.AlterId)` guard clears any active front on the alter, not just the one being deleted. Scylla's counterpart at [ScyllaFrontingRepository.cs:509-518](csharp/Interfold.Infrastructure.Scylla/Repository/ScyllaFrontingRepository.cs) checks `currentRow.FrontId == frontGuid`. Trigger: alter X starts F1, ends F1, starts F2; deleting F1 wipes F2 in InMemory. Fix + add a `ReplayParityTests` case.
- **A2. `ScyllaJournalRepository` nested-retry envelope on 4 alter-scoped writers.** [csharp/Interfold.Infrastructure.Scylla/Repository/ScyllaJournalRepository.cs](csharp/Interfold.Infrastructure.Scylla/Repository/ScyllaJournalRepository.cs) lines 333 (`UpdateAlterAsync`), 390 (`DeleteAlterAsync`), 423 (`SetAlterLockedAsync`), 450 (`SetAlterPinnedAsync`) wrap in `DatabaseTransientRetry.ExecuteScyllaAsync(...)` then call `GetAlterRefAsync` — which itself uses `_scopeResolver.ExecuteAsync` (another retry envelope). Transient errors currently retry 5×5. Rewrite each to use `_scopeResolver.ExecuteAsync` with an internal `GetAlterRefCore(scope, entryId)` variant that skips the envelope. Threads `_logger` into all 4 sites in the same commit.
- **A3. `PkImportJobRunner` — resolve the plan wording vs implementation gap.** [csharp/Interfold.Api/Services/ImportJobs/PkImportJobRunner.cs:26-27](csharp/Interfold.Api/Services/ImportJobs/PkImportJobRunner.cs) now returns `Success: false, AlterCount: 0, ErrorCode: ImportFailed`. Round 1 called for an `ImportErrorCode.Unimplemented`. One-line decision commit: add `Unimplemented` to `ImportErrorCode` (Contracts) and switch to it, OR document a code-comment stating `ImportFailed` was the intentional pick.

## Wave B — top-value dedupe (highest ROI, low-to-medium risk)

Ordered by (call-site reduction × safety). Land as separate commits.

### B1. `TestClient.NoRedirect` sweep — 42 sites
[csharp/Interfold.IntegrationTests/](csharp/Interfold.IntegrationTests/) — 42 identical `new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }` blocks across 14 files (biggest: `PollsControllerTests=6`, `PublicSystemsControllerTests=6`, `FrontingControllerTests=5`, `SettingsControllerImportSpTests=4`, `AvatarSourceTests=4`). Add `TestClient.NoRedirect(IWebFactoryFixture fixture)` plus an overload accepting the raw `InterfoldWebApplicationFactory` for the 3 sites that build the factory in-body (`TrustControllerTests.cs:206`, `ClusterRoleAndOAuthRegistrationTests.cs:82`, `ReplayParityTests.cs:57`). ~114 LOC removed.

### B2. `Dispatch{NoContentAsync,AcceptedAsync,CreatedAsync}` on `InterfoldControllerBase` — ~54 sites
Natural next step after Round-1 B4. Add three helpers that compose `BuildEnvelope<T>` + `ExecuteCommandAsync` + `CommandNoContent`/`CommandAccepted`/`CommandCreated` in one call. Grep count: 40 core `ExecuteCommandAsync(BuildEnvelope(...))` triads + 8 embedded + 6 created variants across [csharp/Interfold.Api/Controllers/](csharp/Interfold.Api/Controllers/) 8 controllers. ~80–100 LOC. Bundle-blocker: verify each site's `principal` argument is `PrincipalId` (i.e. not a synthesized principal); [AuthController.cs:116, 178](csharp/Interfold.Api/Controllers/AuthController.cs) are exceptions and stay hand-rolled.

### B3. `SocketPushContext.SendIfJoinedAsync` — 26 sites
[csharp/Interfold.Api/Sockets/](csharp/Interfold.Api/Sockets/) — 26 handler methods across 8 files begin with the same 5-line early-return: `if (!TryGetSystemTopic(systemId, out var topic)) return; ... await SendAsync(topic, payload, ct);`. Extract `SocketPushContext.SendIfJoinedAsync(systemId, payload, ct)` that folds the topic-resolve + push. ~66 LOC.

### B4. Extend `IScyllaScopeResolver` to global keyspace + close the regional B1 miss — 22 sites
Round-1 B1 handled the *regional-keyspace + normalizedSystemId* case only. Four Scylla repos still hand-roll `DatabaseTransientRetry.ExecuteScyllaAsync(...)`:

- `ScyllaFriendshipRepository.cs:63, 82, 118, 178, 202, 375, 481, 543, 555, 631, 691, 700` (12, global keyspace)
- `ScyllaImportOperationRepository.cs:64, 119, 146, 174, 198, 221, 241` (7, regional — pure B1 miss)
- `ScyllaNotificationTokenRepository.cs:31, 49, 90` (3, global)
- `ScyllaAccountRepository.cs:418, 452, 527, 591` (4, global-only subset)

Introduce a sibling `IScyllaGlobalScopeResolver` (or a `record ScyllaGlobalScope(ISession Session, string GlobalKeyspace)` + `ExecuteGlobalAsync(...)` overload). Sweep the 15 pure-global sites onto it, sweep the 7 `ImportOperationRepository` sites onto the existing regional resolver. Fixes the missing `_logger` argument on 19 of these sites. ~50 LOC net. Anti-goals: `ScyllaAccountRepository.LinkIdentityAsync`/`UnlinkIdentityAsync` write both global registry and regional rows in one batch — keep those two hand-rolled; also `NotifyFriendsAsync` fans out N reads keyed by each friend and needs a bespoke helper, not a bare scope wrapper.

### B5. Row-mapper extractions — mirror the D5 precedent (4 read models)
`AlterRowMappers.MapBareAlter` (Round 1 D5) demonstrated the pattern. Apply to four more read models:

- **B5.a `AlterRowMappers.MapAlterReadModel`** — 2 verbatim 14-field projections at [ScyllaAlterRepository.cs:427-442, 496-511](csharp/Interfold.Infrastructure.Scylla/Repository/ScyllaAlterRepository.cs). Add matching `AlterReadModel.From(...)` for InMemory mirrors ([InMemoryAlterRepository.cs:206-220, 267-281](csharp/Interfold.Infrastructure.InMemory/Repository/InMemoryAlterRepository.cs)). Must call the existing bool-nullable ctor — do NOT invent a third ctor (Round-1 Wave E rule 4). ~40 LOC.
- **B5.b `FrontingRowMappers.MapFrontHistoryReadModel`** — 4 identical 6-arg shapes at `ScyllaFrontingRepository.cs:247-253, 322-328, 392-398, 420-426` (differ only in `time_end` `null` vs row value; take an `override` param). ~15 LOC net.
- **B5.c `JournalRowMappers.MapAlterJournalReadModel` / `MapJournalReadModel`** — 4 verbatim 10-arg sites at `ScyllaJournalRepository.cs:491-501, 524-534, 564-574, 611-621`. ~30 LOC net.
- **B5.d `TagRowMappers.MapTagReadModel` / `MapTagPublicReadModel`** — 4 verbatim 10-arg sites at `ScyllaTagRepository.cs:401-411, 461-471, 499-509, 555-565`. ~30 LOC net.

### B6. Temp-scratch-dir + best-effort delete helper for `Bootstrapper.UnitTests`
Add `TestSupport.NewScratchDir(prefix)` (`IDisposable`-returning) + `TestSupport.TryDeleteDir(path)`. Grep-verified 18 mint sites (`Path.Combine(Path.GetTempPath(), "prefix-" + Guid.NewGuid().ToString("N"))`) + ~30 cleanup sites across 12 files. `EmbeddedSupportFilesTests.cs:170-181` even factored its own `TryDelete` — telegraphing the missing helper. ~120 LOC. Anti-goal: `fixtures/BootstrapperBuild.cs:28-30, 67-70` uses a per-process-id + timestamp scratch layout for session-shared publish output caching — do NOT fold that in.

### B7. Shared `JwtEs256Validator` helper (security-critical dedupe)
[csharp/Interfold.Api/Program.cs:578-656](csharp/Interfold.Api/Program.cs) and [csharp/Interfold.Api/Sockets/WebSocketHandler.cs:792-899](csharp/Interfold.Api/Sockets/WebSocketHandler.cs) hold near-duplicate ES256 verification + `NormalizePem` blocks. Extract `Interfold.Api.Auth.JwtEs256Validator` with `ValidateAsync(token, keyPem, expectedAudience, ct): Task<ValidationResult>` and a shared `PemUtil.NormalizePem`. **Mandatory** — write unit tests on the extracted validator BEFORE the sweep; require a second reviewer on the diff. ~90 LOC.

### B8. `AddFrontCloseStatements` batch builder — 30-line 5-statement close batch
Byte-identical 5-statement batch (`fronts` UPDATE + `current_fronts` DELETE + `fronts_by_alter` UPDATE + `fronts_by_time` INSERT + `fronts_by_end_time` INSERT) plus optional `primary_front_alter = null` clear at [ScyllaFrontingRepository.cs:133-163 and 448-478](csharp/Interfold.Infrastructure.Scylla/Repository/ScyllaFrontingRepository.cs). Extract into `Interfold.Infrastructure.Scylla/Repository/ScyllaFrontingDenormalizedTable.cs` (sibling to Round-1's `ScyllaFriendshipDenormalizedTable`). ~30 LOC net. Cover with `FrontingControllerTests` idempotency assertion.

## Wave C — bulk consolidation

### C1. Sweep ~59 remaining `RunBootstrapperAsync` sites onto `DinDFixtureBase.BootstrapAsync`
Round-1 C1 shipped `BootstrapAsync`/`PublishAsync` but only ~25 of ~85 call sites adopted them. The `CreateScratchAsync + RunBootstrapperAsync + Assert(ExitCode==0)` triplet still lives verbatim at ~59 sites across 16 files. Biggest offenders: `UbuntuBootstrapTests.cs` (10), `SystemdInstallTests.cs` (8), `RestorePhaseTests.cs` (8), `BackupPhaseTests.cs` (5). Sweep the "single-invocation, standard args" majority; leave multi-invocation / custom-args tests as-is.

**DONE (commit `d863855`, 15 files, +136 / -192 LOC).** The 59-site figure was the raw `RunBootstrapperAsync` count, not the sweep-able subset. `BootstrapAsync` / `PublishAsync` only fit the "single-invocation, standard `bootstrap`/`publish` command" shape which turned out to be just 3 sites (`GeneratedLeafCertHasCorrectSans`, `SecretsFileHasRestrictedPermissions`, `TrustStoreInstallFalseLeavesSystemStoreClean`). To unlock the rest, the commit adds a second helper `DinDFixtureBase.RunOnScratchAsync(scratch, testName, command, extraArgs)` that pre-populates `--config` / `--output-dir` / `--non-interactive` from an existing scratch but does NOT assert exit code (so exit-non-zero misuse tests can adopt it too). That opened up sweeps in `SystemdInstallTests` (7 `install-service` sites), `RestorePhaseTests` (6 `backup`/`restore` combos), `BackupPhaseTests` (5), `UbuntuBootstrapTests` (5 rotate/idempotence/recovery sites), `BootstrapIdempotenceTests` (4), `UpdateImagesPhaseTests` (4), `LaunchPhaseTests` (2), `UpdateImagesCassandraModeTests` (2), `DbInitFaultRecoveryTests` (2), `MdnsGateTests` (1), `UnsupportedOsTests` (1). Left as-is: `LaunchPhaseTests`'s `up` invocation (deliberately omits `--config`) and all 4 `PrereqsPhaseTests` sites (deliberately omit `--config` / `--output-dir` to exercise the no-config path). Two previously-migrated sites (`TrustDownloadTests`, `WebHttpsTests`) from a prior working session were folded in for consistency.

### C2. `FindOrCreateByIdentifier<T>` + `UnlinkIdentifier<T>` — close B7 tail (both backends)
Round-1 B7 shipped `LinkIdentifier<T>` on InMemoryAccountRepository but left `FindOrCreateSystemIdBy{Discord,Email,Apple}` (6 near-duplicate bodies) and `Unlink{Discord,Email,Apple}Async` (6 more) unfactored at [InMemoryAccountRepository.cs:191-294](csharp/Interfold.Infrastructure.InMemory/Repository/InMemoryAccountRepository.cs). Do the extractions on both InMemory and Scylla ([ScyllaAccountRepository.cs:316-329](csharp/Interfold.Infrastructure.Scylla/Repository/ScyllaAccountRepository.cs) and sibling Unlink block). Medium risk — auth boundary — cover with unit tests.

**Done 2026-07-19 · commit `4cd69ec`.** Shape landed:
- InMemory: added `FindOrCreateIdentifier<TIdentity>` (generic new-user path with reverse-map miss auto-provisioning + encryption-salt seed) and `UnlinkIdentifier<TIdentity>` (generic forward/reverse dict scrub) next to the existing `LinkIdentifier<TIdentity>`. All three private `FindOrCreateSystemIdBy*` methods removed; all three public `Unlink*Async` methods collapsed to one-line dispatchers. `DeleteAsync`'s three identity cleanup blocks also collapsed onto `UnlinkIdentifier`. Removed the now-unused `GetSystemKey` private helper.
- Scylla: **no change needed** — the plan's grep re-verification note was correct that Scylla was already factored via `UnlinkIdentityAsync(systemId, ProviderColumn)` and `FindOrCreateSystemIdByRegistryColumnAsync(column, value, ct)`; the three public `Unlink*Async` methods there were already one-line dispatchers.
- Unit coverage: added `InMemoryAccountRepositoryIdentityRegressionTests` with 8 tests — one auto-provision-idempotency test per provider (Discord/Email/Apple), email case-insensitivity (guards the `StringComparer.OrdinalIgnoreCase` invariant through the generic dispatch), Unlink round-trip via `TryFindSystemIdByDiscordIdAsync`, Unlink idempotency on empty user, **dict-pair isolation** (UnlinkDiscord must not touch email/apple maps — this is the specific refactor risk the pin catches), and DeleteAsync scrubs-all-three. `ProvisionAllThreeIdentitiesAsync` test helper documents the pre-existing constraint that `LinkIdentityToUserAsync` requires a username/description/avatar/linkToken to be set before it will accept a second identity (auto-provisioned OAuth-only users are treated as `UserNotFound` by the link path).
- Net: −42 LOC on InMemoryAccountRepository.cs (79 insertions, 121 deletions). 373/373 unit tests still green.

### C3. `WebSocketHarness.ConnectPairAndJoinAsync` — 7 sites (two-party sister of D7)
7 verbatim 8-line sender/recipient connect+join blocks in [csharp/Interfold.IntegrationTests/Endpoints/WebSocketTests.cs](csharp/Interfold.IntegrationTests/Endpoints/WebSocketTests.cs) at lines 297-309, 352-363, 443-454, 511-522, 582-593, 689-700, and one two-system variant near 815. Return a tuple `(senderWs, recipientWs, senderToken, recipientToken)` — the names are load-bearing for assertion readability. ~42 LOC.

### C4. `TestSupport.MakeConfig(...)` — close C4 tail
Round-1 C4 promoted `MakeOptions` but the generic `MakeConfig(DatabaseMode mode = DatabaseMode.Scylla, Action<BootstrapConfig>? tweak = null)` was never added. Add it and sweep the 5 file-local `OptionsFor` copies (`EmbeddedSupportFilesTests.cs:25-34`, `CertificateGenerationTests.cs:20-29`, `SecretsBackfillTests.cs:18-27`, `ConfigMdnsGateTests.cs:35-44`, `ConfigPreFillMdnsCheckTests.cs:31-40` — the last two are byte-identical). Also sweep the untouched `BackupCommandBuildingTests` / `SystemdUnitRenderingTests` helpers the Round-1 plan listed. ~30–70 LOC.

### C5. `PostConfigureHarness.Apply(...)` — 8 sites in `Api.UnitTests/Options/`
Add an `ApplyPostConfigure<TPost, TOptions>(this Mock<ISecretsSnapshot> mock, string? name = null)` extension on `SecretsSnapshotMock`. Sweep [AuthenticationSecretsPostConfigureTests.cs](csharp/Interfold.Api.UnitTests/Options/AuthenticationSecretsPostConfigureTests.cs) (3), [FirebaseClientSecretsPostConfigureTests.cs](csharp/Interfold.Api.UnitTests/Options/FirebaseClientSecretsPostConfigureTests.cs) (3), [FcmSecretsPostConfigureTests.cs](csharp/Interfold.Api.UnitTests/Options/FcmSecretsPostConfigureTests.cs) (2). Helper MUST accept a name override — `FcmSecretsPostConfigureTests.cs:55` deliberately calls `PostConfigure("other-name", ...)`. ~24 LOC.

### C6. `RejectIfSelf(SystemId principal, SystemId target)` on `InterfoldControllerBase` — 8 sites
Repeated `if (principal == target) return BadRequest(...)` self-check across 3 controllers (Friend* + Trust). ~35 LOC.

### C7. `[SystemMustExist]` action filter for `PublicSystemsController` — 7 sites
Attribute filter that hydrates `SystemId` from the route and returns `NotFound` if the account lookup returns null. Sweep 7 sites in [PublicSystemsController.cs](csharp/Interfold.Api/Controllers/PublicSystemsController.cs). ~14 LOC.

### C8. `AvatarUrlQualifier.QualifyFriendship` extraction — 3+2 sites
3 direct sites + 2 sibling `FriendRequest` sites; the qualifier shape is duplicated. ~25–35 LOC.

### C9. `BareDinDFixtureBase.InitializeAsync` — collapse Ubuntu/Fedora bare-prereqs pair
[UbuntuBarePrereqsDinDFixture.cs:36-47](csharp/Interfold.Bootstrapper.IntegrationTests/fixtures/UbuntuBarePrereqsDinDFixture.cs) and [FedoraBarePrereqsDinDFixture.cs:21-32](csharp/Interfold.Bootstrapper.IntegrationTests/fixtures/FedoraBarePrereqsDinDFixture.cs) hold byte-identical 12-line `InitializeAsync` overrides (the second file's comment even declares the duplication). Extract an abstract base with `protected abstract string DistroLabel`. Keep both concrete subclasses (TUnit `[ClassDataSource]` needs concrete non-abstract types). ~16 LOC.

### C10. `PhaseArtifactLoader.LoadRequiredConfigAsync` + `RequireComposeFileOrFail` — 4+5 sites
Extract into `csharp/Interfold.Bootstrapper/Util/PhaseArtifactLoader.cs`. Sweep `BackupPhase`, `RestorePhase`, `UpdateImagesPhase`, `SystemdInstallPhase` for the config load; add `DatabaseInitPhase` to the compose-file-or-fail set. The compose-file error message text is identical across all 5 sites. ~65 LOC.

### C11. Close the C6 `DockerCompose.*` migration — 10 remaining bypass sites
Round-1 C6 shipped `DockerCompose.UpAsync/PullAsync/LogsAsync/StopAsync/ExecAsync` but 10 sites still call `docker compose` via `ProcessRunner` directly (missing helpers: `DownAsync`, `PsAsync`, `ImagesAsync`, `StartAsync`). Add the missing helpers, sweep the 10 sites. ~40 LOC.

### C12. `DockerCompose.UpCheckedAsync` — 4 sites
The "log + UpAsync + checkExit + logStdout" trio repeats verbatim in `DatabaseInitPhase`, `LaunchPhase`, `RestorePhase`, `UpdateImagesPhase`. Fold into a single `UpCheckedAsync(composeFile, service, timeout, logger, ct)`. ~24 LOC.

### C13. `Util/UnixFilePermissions.{SetOwnerOnly, SetWorldReadable}` — chmod P/Invoke dedupe
`CertificatePhase` and `SecretsPhase` both hold the same P/Invoke `chmod` block; the second file's doc-comment self-declares "Mirrors SecretsPhase.ChmodReadable". ~40 LOC.

### C14. Swap `Interfold.SPDump` and `SpImportTests` onto `AddSimplyPluralImport` — close C10 tail
[Interfold.SPDump/Program.cs:41-47](csharp/Interfold.SPDump/Program.cs) and [Interfold.IntegrationTests/SimplyPluralImport/SpImportTests.cs:1159-1167](csharp/Interfold.IntegrationTests/SimplyPluralImport/SpImportTests.cs) still hand-roll the DI registration Round-1 C10 extracted. Split `AddSimplyPluralImport` into a `core` + optional `AddPkImportJobRunner` overload so SPDump can consume the core only. ~15 LOC.

### C15. Thread `TimeProvider` into `SimplyPluralImportService` + `FriendshipSocketEventHandlers` — close `600fdc7` sweep
Commit `600fdc7` (TimeProvider adoption) missed [SimplyPluralImportService.cs](csharp/Interfold.Api/SimplyPlural/SimplyPluralImportService.cs) (4 `DateTimeOffset.UtcNow` + 1 `DateTime.UtcNow`) and [FriendshipSocketEventHandlers.cs](csharp/Interfold.Api/Sockets/FriendshipSocketEventHandlers.cs) (2 sites). Thread the injected `TimeProvider` through. No LOC saved; testability + consistency win only.

### C16. AppHost consolidation — F7/F8/F9/F10
Small mechanical AppHost cleanups:
- `HostPortTcpProbe.CreateCheck(port)` for the 2 dashboard TCP checks (~14 LOC).
- `ComposeHealthcheck.CmdShell(...)` for the 6 compose `Healthcheck` object literals; preserve the `$${}` escape trick (~30 LOC).
- `AsPersistent(volume, path)` extension for the 3 `if (persistentContainers)` blocks (~9 LOC).
- `AddReadyAndStartup<T>` in Program.cs → 4 sites collapse to 2 (~8 LOC).

## Wave D — cleanup (dead code, tiny wrappers, polish)

### D1. Delete 8 dead 1-line InMemory wrappers — close B9 tail
`GetSystemKey` wrappers at [InMemoryAlterRepository.cs:362](csharp/Interfold.Infrastructure.InMemory/Repository/InMemoryAlterRepository.cs), `InMemoryAccountRepository.cs:355`, `InMemoryFrontingRepository.cs:346`, `InMemoryPollRepository.cs:147`, `InMemoryTagRepository.cs:269`, `InMemorySettingsFieldRepository.cs:162`, `InMemoryJournalRepository.cs:367`, plus `Normalize` at `InMemoryFriendshipRepository.cs:343`. Every caller migrated to direct `InMemoryStorageKeys.*` in Round 1 but the wrappers were not deleted. Zero in-file callers remain (verified).

### D2. Inline 3 remaining private `ExistsAsync`/`SystemExistsAsync` — B6 polish
`ScyllaAlterRepository.cs:576`, `ScyllaPollRepository.cs:200`, `ScyllaFriendshipRepository.cs:433` — cosmetic 1-line wrappers around `ScyllaExistsQueries.RowExistsAsync`.

### D3. Inline 2 remaining InMemory field wrappers — C5 tail
`InMemoryAlterRepository.ResolveGuardedFields:371-374` and `ResolveVisibleDefinitionsAsync:378-384`. If the current justification comments are load-bearing, upgrade them to a `<remarks>` cref pointing at `AlterFieldProjection`.

### D4. Delete `PostgresIdempotencyStore.{Find,Save}Async` `-Core` forwarders
[csharp/Interfold.Infrastructure.Postgres/PostgresIdempotencyStore.cs:25-48](csharp/Interfold.Infrastructure.Postgres/PostgresIdempotencyStore.cs) — pure 1-expression forwarders to `FindCoreAsync`/`SaveCoreAsync`. Rename the private bodies and promote to public (already the interface signature). ~24 LOC.

### D5. Delete orphaned `BootstrapStackAsync` wrapper
[csharp/Interfold.Bootstrapper.IntegrationTests/DbInitSecurityInvariantsTests.cs:50-55](csharp/Interfold.Bootstrapper.IntegrationTests/DbInitSecurityInvariantsTests.cs) — dead thin wrapper left over from C1 sweep; every `[Test]` in the file calls `dinD.BootstrapAsync(...)` directly. 6 LOC.

### D6. `SecretStringJsonConverter<T>` abstract base — 6 sites
[csharp/Interfold.Contracts/Ids/SecretTokens.cs](csharp/Interfold.Contracts/Ids/SecretTokens.cs) — 6 `JsonConverter<T>` classes with byte-identical bodies at lines 54-61, 89-96, 123-130, 157-164, 208-215, 242-249. Add an abstract base with a `protected abstract T Create(string)` factory + `Func<T, string> Selector`; sealed subclasses shrink to 3-line stubs. Concrete stubs required — `[JsonConverter(typeof(<X>JsonConverter))]` needs a concrete class name. ~30 LOC net.

### D7. `GuidIdJsonConverter<T>` abstract base — 5 sites (option-A only)
[csharp/Interfold.Contracts/Ids/StringBackedIds.cs:158-211](csharp/Interfold.Contracts/Ids/StringBackedIds.cs) — 5 `TagId`/`PollId`/`EntryId`/`FrontId`/`FieldId` converters. Wire form MUST stay `Guid.ToString("N")` — the class-level comment (lines 8-13) says this is load-bearing for idempotency-payload hashes. **DO NOT do option-B** (static-abstract `IGuidId<TSelf>` collapsing the struct body) — Wave E. ~35 LOC net.

### D8. Move `VisibilityLevel` + drop redundant `TryGetValue` — close D8 tail
Move `VisibilityLevel` out of [csharp/Interfold.Contracts/Models/Alter.cs:107](csharp/Interfold.Contracts/Models/Alter.cs) into `csharp/Interfold.Contracts/Enums/VisibilityLevel.cs`. Drop the redundant `TryGetValue` after `ContainsKey` in [csharp/Interfold.Domain/Alters/AlterFieldProjection.cs:60](csharp/Interfold.Domain/Alters/AlterFieldProjection.cs).

### D9. ServiceDefaults hygiene decision — close D9
[csharp/Interfold.ServiceDefaults/Extensions.cs](csharp/Interfold.ServiceDefaults/Extensions.cs) lines 101 (`ConfigureOpenTelemetry`) and 143 (`AddDefaultHealthChecks`). Pick "keep public" (Aspire convention) or "demote to private static". One-line decision commit either way. Stop having the discussion.

### D10. `DumpOnFailure` lift-to-base — 19 sites (deliberate re-evaluation of C1)
Round-1 C1 explicitly picked "1-line hook per class" over the base class because the fixture primary-ctor pattern (`(UbuntuDinDFixture dinD)`) complicates inheritance. Re-evaluate: has the 1-line-per-class shape drifted since C1 landed? If yes, extract `DinDIntegrationTestBase<TFixture>` with `protected virtual bool Teardown => true` for the 2 sites that pass `teardown: false` (`UnsupportedOsTests.cs`, `PrereqsPhaseTests.cs`). If no drift, leave the hook and delete this bullet. ~38 LOC.

### D11. Test-only helper extractions — close D7 (mostly untouched)
Bulk extractions, one commit each:
- `csharp/Interfold.Api.UnitTests/ImportJobs/CapturingQueue.cs` — dup at `ImportSpCommandHandlerDispatchTests.cs:132-158` and `ImportPkCommandHandlerDispatchTests.cs:151-177`.
- `csharp/Interfold.Bootstrapper.IntegrationTests/TestServices/DotEnvParser.cs` (`ParseEnv`) — dup at `PublishIntegrationTests.cs:128` and `WebHttpsTests.cs:241`.
- `csharp/Interfold.Api.IntegrationTests/Scylla/ScyllaDirectHarness.cs` (rename existing test-project location as appropriate) — 8-line preamble at `ScyllaAlterRepositoryUdtNullTests.cs:52-58` and `ScyllaFrontingRepositoryNullTimeStartTests.cs:28-48`.
- `csharp/Interfold.IntegrationTests/TestServices/WebSocketHarness.ConnectAndJoinAsync` (single-party — D7 original ask) — ~10 inline 4-line blocks in `WebSocketTests.cs`.
- `csharp/Interfold.IntegrationTests/TestServices/PhxEndpointFrame.Build(topic, method, path, body, @ref, joinRef)` — ~20 verbatim shapes across `WebSocketTests.cs`.
- `csharp/Interfold.IntegrationTests/TestServices/TestIds.NewSystemId(prefix, maxLen = 32)` — consolidates the scattered `UniqueId` shape.
- `csharp/Interfold.IntegrationTests/TestServices/IsolatedAvatarStorage.RunAsync(fixture, Func<HttpClient, string publicBasePath, Task>)` — 17-line scaffold at `SettingsControllerTests.cs:116-183` and `189-259`.
- `DinDFixtureBase.ReadSecretAsync(scratch, secretKey)` + `DinDScratch.SecretsJsonPath` — 18 inline `secrets/secrets.json` fragments across `RestorePhaseTests`, `UbuntuBootstrapTests`, `DbInitSecurityInvariantsTests`, `BackupPhaseTests`, `DbInitFaultRecoveryTests`.
- Inline `EnsurePublicProfileAsync` (`PublicSystemsControllerTests.cs:324`) into `BaseEndpointTest.EnsureUserExistsAsync`.
- Extract `RunFriendTrustUntrustFlowAsync` from `WebSocketThreadStarvationTests.cs:183` — shared with `WebSocketTests`.

### D12. Add `ScyllaReadinessProbe` mirror + `PostgresReadinessProbe.RequiredConsecutiveSuccesses = 1` overload
Scylla-mode bootstraps still hand-roll a readiness check (no Round-1 equivalent to `PostgresReadinessProbe`); the `DatabaseInitPhase` Scylla path relies on `docker compose ps` polling. Extract `ScyllaReadinessProbe.WaitAsync` (same option record shape as `PostgresReadinessProbe`). Also add a `RequiredConsecutiveSuccesses = 1` overload to the Postgres probe for `RestorePhase` (whose 5-minute deadline predates the 3-in-a-row check that landed in `DatabaseInitPhase`). Medium boot-time risk; cover with `BootstrapIdempotenceTests`.

### D13. Bundle `SendAuthedDeleteAsync` + `AlterJournalsControllerTests.cs:17` follow-ups
Below-threshold cleanup: (a) 4 remaining raw `HttpMethod.Delete` sites (`AlterJournalsControllerTests.cs:72,97`, `AltersControllerTests.cs:212`, `SettingsControllerTests.cs:240`); (b) reconcile [AlterJournalsControllerTests.cs:17](csharp/Interfold.IntegrationTests/Controllers/AlterJournalsControllerTests.cs) which uses `WebApplicationFactoryClientOptions()` (empty) while every sibling uses `{ AllowAutoRedirect = false }` — decide intentional or bug. Include in the same commit as B1.

## Wave E — DO NOT DO (Round-2 additions to Round-1 Wave E)

Carries forward every Round-1 Wave-E item (see Ground rules #2). Additional Round-2 exclusions:

- **`SocketEventPumpRunner.RunAllAsync`** — the 45-line subscription manifest looks like boilerplate but is intentionally verbose. Do NOT extract into a metadata-driven loop.
- **`StringBackedIds` "option B"** — do NOT collapse the 5 Guid-Id struct bodies onto a static-abstract `IGuidId<TSelf>`. Breaks grep-ability of `TagId.Parse` etc. Only D7's option-A converter dedupe is in scope.
- **`ScrambleAlphabet` duplication in DatabaseBootstrap** — 2 sites, 2 LOC each. Below threshold. If password entropy shape ever intentionally diverges, a shared constant becomes wrong. Add a cross-reference doc-comment only.
- **`FriendProfileReadModel.Bare(SystemId)` factory** — 5 sites, ~5 LOC saved. Below threshold. Fold into any incidental touch; do NOT ship as a dedicated commit.
- **`BackupCommandBuildingTests` / `SystemdUnitRenderingTests` scratch dir** — those tests use a per-process-id + timestamp scratch layout for session-shared publish output caching (see `fixtures/BootstrapperBuild.cs:28-30, 67-70`). Do NOT sweep into B6.
- **`FcmSecretsPostConfigureTests.cs:55`** — deliberately calls `PostConfigure("other-name", ...)`. C5's helper MUST accept a name override.
- **`ClusterRoleAndOAuthRegistrationTests.cs:82`** — bespoke in-body `factory` construction. Keep the `NoRedirect(InterfoldWebApplicationFactory)` overload for it.
- **`RestorePhase` `alpine:3.20` dependency** — flag as a separate DR-hardening issue (not part of this plan). `RestorePhase.cs:337` uses `docker run --rm --volumes-from … alpine:3.20` for the wipe step and only warns on failure. Air-gapped DR restore risk. File a ticket, do NOT touch here.

## Handoff checklist for the executing agent

Before touching any file, verify these grep counts match the plan (or the plan is stale). This repo runs on **Windows PowerShell** — patterns below use single-quoted outer quoting so PowerShell doesn't mangle them. Alternatively (and preferably for an agent), run these via the Cursor `Grep` tool with `output_mode: "count"`, which is shell-agnostic.

Add `--stats` to any `rg -c` invocation to see the aggregate `matched lines` total at the bottom, since `-c` alone prints per-file counts.

| # | Pattern (ripgrep regex, PowerShell-safe) | Search path | Expect |
|---|---|---|---|
| B1 | `'AllowAutoRedirect = false'` | `csharp/Interfold.IntegrationTests` | **42** matches |
| B3 | `'TryGetSystemTopic\('` | `csharp/Interfold.Api/Sockets` | **26** matches |
| B4 | `'DatabaseTransientRetry\.ExecuteScyllaAsync'` | `csharp/Interfold.Infrastructure.Scylla/Repository` | **26** across the four B4 files |
| B5.a | `'new AlterReadModel\('` | `csharp/Interfold.Infrastructure.Scylla` | **2** |
| B6 | `'Path\.Combine\(Path\.GetTempPath\(\), "'` | `csharp/Interfold.Bootstrapper.UnitTests` | **18** |
| D10 | `'DumpOnFailureAsync'` | `csharp/Interfold.Bootstrapper.IntegrationTests` | **19** |
| C1 | `'RunBootstrapperAsync'` | `csharp/Interfold.Bootstrapper.IntegrationTests` | **59** |
| C5 | `'PostConfigure\(Microsoft\.Extensions\.Options\.Options\.DefaultName'` | `csharp/Interfold.Api.UnitTests/Options` | **~8** |

Reference PowerShell invocation (safe against all patterns above):

```powershell
rg -c --stats 'AllowAutoRedirect = false' csharp/Interfold.IntegrationTests
```

Note the outer **single quotes** — do NOT switch to double quotes, or the `\(`, `\.`, and embedded `"` will break on PowerShell (`\"` inside `"..."` prematurely terminates the string in PowerShell; `""` is the correct escape but ugly). If you must use double quotes, escape inner double quotes as `""` and prefix regex metacharacters with `\`.

If counts drift by more than 10%, pause and re-audit the affected bullet before proceeding.

## Suggested commit ordering

One feature branch, one PR at the end. Land the commits in this order — each builds on the previous.

1. **Wave A** — three small correctness commits (A1, A2, A3). Ship first, before any dedupe.
2. **Wave B** in the listed order. B7 (JWT dedupe) needs unit tests on the validator before its sweep commit — that's 2 commits, not 1. B1 (`NoRedirect` sweep) and B4 (scope resolver global) are the biggest LOC wins.
3. **Wave C** in the listed order. C1 (bootstrap sweep) may need 2-3 commits because 59 sites is too many for one screen. C2 (auth-boundary refactor) needs its own reviewer sign-off.
4. **Wave D** last. D11 (test-helper extractions) is one commit per helper, not one commit for the whole bullet.
5. Wave E — leave alone.

Rule of thumb per commit: diff fits on one screen for a reviewer, tests green, commit message names the plan section (e.g. `refactor(api): add Dispatch*Async helpers [R2-B2]`).

**Total estimated LOC removed across A+B+C+D: ~1,400–1,700 lines** (350–450 infra/domain, 370–410 API/AppHost/ServiceDefaults, 390 tests, 170 bootstrapper phases, ~50 assorted). Plus three correctness fixes (A1 parity bug, A2 nested retry, B7 shared JWT validator).