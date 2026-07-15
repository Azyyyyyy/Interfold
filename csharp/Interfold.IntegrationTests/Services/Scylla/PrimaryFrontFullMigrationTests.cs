using Cassandra;
using Interfold.Infrastructure.Scylla;
using Interfold.IntegrationTests.TestServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Interfold.IntegrationTests.Services.Scylla;

/// <summary>
/// Walks the full <c>users.primary_front</c> narrowing sequence — migration 006 (add
/// <c>primary_front_smallint</c>) → backfill → migration 007 (drop <c>primary_front</c>)
/// — against a scratch keyspace that starts in the legacy <c>int</c>-only shape. The
/// scratch keyspace deliberately does NOT contain <c>primary_front_smallint</c>, so this
/// test exercises the exact schema shape a first-boot production cluster would present
/// to <see cref="ScyllaMigrationService"/> in the same PR that ships phases 1 + 2.
///
/// <para>
/// The migrations live in <c>ScyllaMigrationService.StartingAsync</c> as an inline
/// sequence (006 → <c>BackfillPrimaryFrontAsync</c> → 007). This test drives the same
/// three steps directly via internal static helpers on the service so it can assert
/// intermediate schema and row state at each stage without contaminating the shared
/// <c>global.schema_migrations</c> ledger — the helpers skip the ledger, the production
/// path wraps them with the ledger guard.
/// </para>
///
/// <para>
/// Scylla-only, admin-session-only. Uses the same <see cref="TestDbCredentials"/>
/// pattern as <see cref="Migrations.MigrationLedgerTests"/> so schema-level DDL
/// against the scratch keyspace has the necessary role.
/// </para>
/// </summary>
[ClassDataSource<ScyllaWebFactoryFixture>(Shared = SharedType.PerTestSession)]
public sealed class PrimaryFrontFullMigrationTests(ScyllaWebFactoryFixture fixture) : BaseEndpointTest
{
    private SharedDbFixture SharedDb => fixture.Aspire;

    [Test]
    public async Task FullSequence_FromLegacyShape_LandsSmallintOnlyPostBackfill()
    {
        await Assert.That(SharedDb.ScyllaPort).IsNotNull()
            .Because("Scylla must be provisioned by the shared fixture for the full-migration test.");

        // Fresh scratch keyspace per run so a failing assertion never leaks state into
        // the shared cluster. `test_pfmig_<guid>` keeps the name uniquely traceable if
        // teardown ever regresses.
        var keyspace = $"test_pfmig_{Guid.NewGuid():N}"[..24];
        using var session = await OpenScyllaSessionAsync();
        var logger = NullLoggerFactory.Instance.CreateLogger<ScyllaMigrationService>();

        try
        {
            await CreateScratchKeyspaceAsync(session, keyspace);
            await CreateLegacyUsersTableAsync(session, keyspace);
            await SeedLegacyRowsAsync(session, keyspace);

            // Stage 1 — verify the pre-state matches what a real legacy cluster looks
            // like. Without this the "006 added the column" assertion would be vacuous
            // (the column might already exist for unrelated reasons).
            var preSchemaColumns = await ReadUsersColumnsAsync(session, keyspace);
            using (Assert.Multiple())
            {
                await Assert.That(preSchemaColumns.TryGetValue("primary_front", out var preIntType)).IsTrue()
                    .Because("Legacy users table must start with a primary_front int column.");
                await Assert.That(preIntType).IsEqualTo("int");
                await Assert.That(preSchemaColumns.ContainsKey("primary_front_smallint")).IsFalse()
                    .Because("Pre-migration schema must NOT already contain primary_front_smallint — otherwise migration 006 isn't being exercised.");
            }

            // Stage 2 — apply migration 006 (add primary_front_smallint).
            await ScyllaMigrationService.ApplyTemplatedMigrationToKeyspaceAsync(
                session,
                "006_primary_front_smallint.templated.cql",
                keyspace,
                logger);

            var midSchemaColumns = await ReadUsersColumnsAsync(session, keyspace);
            var midRows = await ReadPrimaryFrontRowsAsync(session, keyspace);
            using (Assert.Multiple())
            {
                await Assert.That(midSchemaColumns.TryGetValue("primary_front", out var midIntType)).IsTrue();
                await Assert.That(midIntType).IsEqualTo("int");
                await Assert.That(midSchemaColumns.TryGetValue("primary_front_smallint", out var midShortType)).IsTrue()
                    .Because("Migration 006 must add primary_front_smallint.");
                await Assert.That(midShortType).IsEqualTo("smallint");

                foreach (var (id, intVal, shortVal) in midRows)
                {
                    await Assert.That(shortVal).IsNull()
                        .Because($"Post-006 / pre-backfill: primary_front_smallint must still be null for '{id}' (int={intVal}).");
                }
            }

            // Stage 3 — run the backfill against the scratch keyspace directly. The
            // helper is ledger-bypassing so we can drive it repeatedly without touching
            // global.schema_migrations.
            var copied = await ScyllaMigrationService.BackfillPrimaryFrontForKeyspaceAsync(
                session,
                keyspace,
                logger,
                CancellationToken.None);

            var postBackfillRows = await ReadPrimaryFrontRowsAsync(session, keyspace);
            using (Assert.Multiple())
            {
                // Seeded rows: 5, 32767, null. Two non-null values → two copies.
                await Assert.That(copied).IsEqualTo(2)
                    .Because("Backfill must report a copy count matching the number of non-null legacy rows.");

                foreach (var (id, intVal, shortVal) in postBackfillRows)
                {
                    if (intVal is null)
                    {
                        await Assert.That(shortVal).IsNull()
                            .Because($"Null legacy value stays null after backfill for '{id}'.");
                    }
                    else
                    {
                        await Assert.That(shortVal).IsEqualTo((short?)intVal)
                            .Because($"Row '{id}': backfill must copy int primary_front verbatim into primary_front_smallint.");
                    }
                }
            }

            // Stage 4 — apply migration 007 (drop primary_front). Post-drop the column
            // is gone from the schema and reads through primary_front_smallint keep
            // returning the same values.
            await ScyllaMigrationService.ApplyTemplatedMigrationToKeyspaceAsync(
                session,
                "007_drop_primary_front_int.templated.cql",
                keyspace,
                logger);

            var finalSchemaColumns = await ReadUsersColumnsAsync(session, keyspace);
            var finalRows = await ReadSmallintOnlyRowsAsync(session, keyspace);
            using (Assert.Multiple())
            {
                await Assert.That(finalSchemaColumns.ContainsKey("primary_front")).IsFalse()
                    .Because("Migration 007 must drop the legacy int column.");
                await Assert.That(finalSchemaColumns.TryGetValue("primary_front_smallint", out var finalShortType)).IsTrue()
                    .Because("primary_front_smallint must survive the drop.");
                await Assert.That(finalShortType).IsEqualTo("smallint");

                await Assert.That(finalRows.Count).IsEqualTo(3)
                    .Because("All three seeded rows must survive the migration.");
                await Assert.That(finalRows["u-small"]).IsEqualTo((short?)5);
                await Assert.That(finalRows["u-max"]).IsEqualTo(short.MaxValue);
                await Assert.That(finalRows["u-null"]).IsNull();

                // Re-applying 007 on top of the dropped column must be idempotent —
                // that's the whole point of the InvalidQueryException / "column not
                // found" handler in ExecuteStatements.
                await ScyllaMigrationService.ApplyTemplatedMigrationToKeyspaceAsync(
                    session,
                    "007_drop_primary_front_int.templated.cql",
                    keyspace,
                    logger);
            }
        }
        finally
        {
            // Best-effort teardown so a failed assertion never leaks a scratch keyspace
            // into the shared cluster. DROP KEYSPACE with IF EXISTS is safe even if the
            // CREATE above threw before the keyspace materialised.
            await session.ExecuteAsync(new SimpleStatement(
                $"DROP KEYSPACE IF EXISTS {keyspace}"));
        }
    }

    private async Task<ISession> OpenScyllaSessionAsync()
    {
        var cluster = Cluster.Builder()
            .AddContactPoint("127.0.0.1")
            .WithPort(SharedDb.ScyllaPort!.Value)
            .WithLoadBalancingPolicy(new DCAwareRoundRobinPolicy("nam"))
            .WithCredentials(TestDbCredentials.ScyllaAdminUser, TestDbCredentials.ScyllaAdminPassword)
            .WithQueryTimeout(30000)
            .Build();
        return await cluster.ConnectAsync();
    }

    private static async Task CreateScratchKeyspaceAsync(ISession session, string keyspace)
    {
        await session.ExecuteAsync(new SimpleStatement(
            $"CREATE KEYSPACE IF NOT EXISTS {keyspace} " +
            "WITH replication = {'class': 'SimpleStrategy', 'replication_factor': 1}"));
    }

    private static async Task CreateLegacyUsersTableAsync(ISession session, string keyspace)
    {
        // Deliberately drops primary_front_smallint from the CREATE — this is the
        // pre-006 schema shape that migration 006 has to add the column to.
        await session.ExecuteAsync(new SimpleStatement(
            $"CREATE TABLE {keyspace}.users (" +
            "  id text, " +
            "  primary_front int, " +
            "  updated_at timestamp, " +
            "  PRIMARY KEY (id)" +
            ")"));
    }

    private static async Task SeedLegacyRowsAsync(ISession session, string keyspace)
    {
        // Three seeded rows exercise: (a) a small in-range int, (b) short.MaxValue at
        // the top of the range, and (c) null-primary steady state. That's the full
        // sweep the backfill has to handle correctly.
        await session.ExecuteAsync(new SimpleStatement(
            $"INSERT INTO {keyspace}.users (id, primary_front, updated_at) VALUES (?, ?, toTimestamp(now()))",
            "u-small", 5));
        await session.ExecuteAsync(new SimpleStatement(
            $"INSERT INTO {keyspace}.users (id, primary_front, updated_at) VALUES (?, ?, toTimestamp(now()))",
            "u-max", (int)short.MaxValue));
        await session.ExecuteAsync(new SimpleStatement(
            $"INSERT INTO {keyspace}.users (id, primary_front, updated_at) VALUES (?, ?, toTimestamp(now()))",
            "u-null", (int?)null));
    }

    private static async Task<Dictionary<string, string>> ReadUsersColumnsAsync(ISession session, string keyspace)
    {
        var rs = await session.ExecuteAsync(new SimpleStatement(
            "SELECT column_name, type FROM system_schema.columns WHERE keyspace_name = ? AND table_name = ?",
            keyspace, "users"));
        var cols = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rs)
        {
            cols[row.GetValue<string>("column_name")] = row.GetValue<string>("type");
        }
        return cols;
    }

    private static async Task<List<(string Id, int? IntVal, short? ShortVal)>> ReadPrimaryFrontRowsAsync(
        ISession session,
        string keyspace)
    {
        var rs = await session.ExecuteAsync(new SimpleStatement(
            $"SELECT id, primary_front, primary_front_smallint FROM {keyspace}.users"));
        var rows = new List<(string, int?, short?)>();
        foreach (var row in rs)
        {
            rows.Add((
                row.GetValue<string>("id"),
                row.GetValue<int?>("primary_front"),
                row.GetValue<short?>("primary_front_smallint")));
        }
        return rows;
    }

    private static async Task<Dictionary<string, short?>> ReadSmallintOnlyRowsAsync(
        ISession session,
        string keyspace)
    {
        var rs = await session.ExecuteAsync(new SimpleStatement(
            $"SELECT id, primary_front_smallint FROM {keyspace}.users"));
        var rows = new Dictionary<string, short?>(StringComparer.Ordinal);
        foreach (var row in rs)
        {
            rows[row.GetValue<string>("id")] = row.GetValue<short?>("primary_front_smallint");
        }
        return rows;
    }
}
