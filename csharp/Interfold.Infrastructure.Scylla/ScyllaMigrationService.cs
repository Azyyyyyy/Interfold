using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Cassandra;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Secrets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.Scylla;

/// <summary>
/// Applies embedded CQL migrations at startup using admin credentials from ISecretsStore.
/// Creates keyspaces for all regions, applies schema, and grants DML permissions to the app user.
/// Supports both single-node (SimpleStrategy) and multi-DC (NetworkTopologyStrategy) deployments.
/// Runs before the app accepts traffic (IHostedLifecycleService.StartingAsync).
/// </summary>
/// <remarks>
/// Each migration is tracked in <c>global.schema_migrations</c> by <c>(scope, version)</c>
/// + SHA-256 checksum so previously-applied files are skipped on subsequent runs and
/// post-deploy edits are detected (fail-fast). The cluster-wide singleton keyspaces
/// (<c>global</c>, <c>nam_nt</c>, <c>dummy</c>) are bootstrapped from
/// <c>000_create_singleton_keyspaces.cql</c> unconditionally — they're a precondition for
/// the ledger itself and every statement is idempotent. Templated per-region files
/// (<c>001_create_interfold_keyspaces.cql</c>, <c>002_create_interfold_schema.templated.cql</c>)
/// are tracked once per regional keyspace, so adding a new region later only re-applies the
/// rendered template to the new keyspace.
/// </remarks>
public sealed partial class ScyllaMigrationService(
    IOptions<PersistenceConfiguration> options,
    ISecretsStore secretsStore,
    IScyllaConfigResolver configResolver,
    ILogger<ScyllaMigrationService> logger) : IHostedLifecycleService
{
    // Derived from the ScyllaKeyspace enum so the regional list can't drift from the type
    // that the resolution APIs (IRegionContext) hand around.
    private static readonly string[] RegionalKeyspaces =
        Enum.GetValues<Contracts.Enums.ScyllaKeyspace>()
            .Select(Contracts.Enums.EnumWire<Contracts.Enums.ScyllaKeyspace>.ToWire)
            .ToArray();

    // Singleton keyspaces created once during bootstrap from 000_create_singleton_keyspaces.cql.
    private static readonly string[] SingletonKeyspaces = ["global", "nam_nt", "dummy"];

    // Ledger constants.
    private const string LedgerKeyspace = "global";
    private const string LedgerTable = "schema_migrations";
    private const string LedgerTableFqn = $"{LedgerKeyspace}.{LedgerTable}";
    private const string SingletonsMigration = "000_create_singleton_keyspaces.cql";
    private const string KeyspacesMigration = "001_create_interfold_keyspaces.cql";
    private const string SchemaMigration = "002_create_interfold_schema.templated.cql";
    private const string FieldTimestampsMigration = "003_field_udt_timestamps.templated.cql";
    private const string ImportOperationsMigration = "004_import_operations.templated.cql";
    private const string ColorFixupMarkerMigration = "005_marker_color_fixup.templated.cql";
    private const string PrimaryFrontAddNewMigration = "006_add_primary_front_new.templated.cql";
    private const string PrimaryFrontSwapTypeMigration = "007_swap_primary_front_type.templated.cql";
    private const string PrimaryFrontDropNewMigration = "008_drop_primary_front_new.templated.cql";
    private const string GrantsVersion = "grants_v1";

    // Ledger constants for the two inline backfill passes of the primary_front int -> smallint
    // narrowing. Both passes have to be sequenced against the ALTER migrations that bracket
    // them (see 006 for the full sequence), so they can't be a separate hosted service and
    // instead live inline in StartingAsync. Same ledger shape as any tracked migration:
    // (scope, version, checksum) — bump the checksum on either to force a per-keyspace re-run.
    private const string PrimaryFrontBackfillVersion = "v1";
    private const string PrimaryFrontIntToNewChecksum = "primary_front_int_to_new_v1";
    private const string PrimaryFrontNewToPrimaryChecksum = "primary_front_new_to_primary_v1";

    // Stable template hashed for grant tracking. Bump GrantsVersion whenever this string
    // changes so existing rows mismatch and grants get re-applied across all scopes.
    private const string GrantTemplate = "GRANT SELECT ON KEYSPACE {ks} TO {user};GRANT MODIFY ON KEYSPACE {ks} TO {user}";

    private string? _adminUsername;
    private string? _adminPassword;
    private string[]? _contactPoints;
    private string? _datacenter;
    private string? _appUsername;
    private string? _keyspace;
    private int _port;

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        // Read admin credentials from secrets store
        _adminUsername = await secretsStore.GetAsync(SecretsStoreKeys.ScyllaAdminUsername, cancellationToken);
        _adminPassword = await secretsStore.GetAsync(SecretsStoreKeys.ScyllaAdminPassword, cancellationToken);

        if (string.IsNullOrWhiteSpace(_adminUsername) ||
            string.IsNullOrWhiteSpace(_adminPassword))
        {
            logger.LogInformation("[scylla-migrate] No admin credentials in secrets store — skipping migrations.");
            return;
        }

        // Read connection details via unified resolver
        _contactPoints = await configResolver.GetContactPointsAsync(cancellationToken);
        _datacenter = await configResolver.GetDatacenterAsync(cancellationToken);
        _appUsername = await configResolver.GetUsernameAsync(cancellationToken);
        _port = await configResolver.GetPortAsync(cancellationToken);

        // Keyspace is the per-node region identity — env-only, never store-shared.
        _keyspace = configResolver.GetKeyspace();

        logger.LogInformation("[scylla-migrate] Applying ScyllaDB schema migrations...");

        // Ensure we get an authenticated connection. Cassandra's PasswordAuthenticator
        // may not be enforcing auth immediately after startup, causing the driver to
        // connect anonymously. We retry until LIST ROLES succeeds (requires auth).
        var cluster = BuildCluster();
        var session = await cluster.ConnectAsync();

        for (var authAttempt = 0; authAttempt < 10; authAttempt++)
        {
            try
            {
                await session.ExecuteAsync(new SimpleStatement("LIST ROLES"));
                logger.LogInformation("[scylla-migrate] Authenticated as '{User}'.", _adminUsername);
                break;
            }
            catch (UnauthorizedException)
            {
                if (authAttempt >= 9) throw;
                logger.LogWarning("[scylla-migrate] Connection is anonymous — auth not ready, retrying in 3s (attempt {Attempt}/10)...",
                    authAttempt + 1);
                session.Dispose();
                await cluster.ShutdownAsync();
                await Task.Delay(3000);
                cluster = BuildCluster();
                session = await cluster.ConnectAsync();
            }
            catch (AuthenticationException)
            {
                if (authAttempt >= 9) throw;
                logger.LogWarning("[scylla-migrate] Auth rejected — admin role may not exist yet, retrying in 3s (attempt {Attempt}/10)...",
                    authAttempt + 1);
                session.Dispose();
                await cluster.ShutdownAsync();
                await Task.Delay(3000);
                cluster = BuildCluster();
                session = await cluster.ConnectAsync();
            }
        }

        try
        {
            var visibleDcs = await DiscoverDatacenters(session);
            var isScylla = await DetectScyllaDb(session);
            logger.LogInformation("[scylla-migrate] Visible datacenters: {DCs}, database: {Db}",
                string.Join(", ", visibleDcs), isScylla ? "ScyllaDB" : "Cassandra");

            // Bootstrap: create the singleton keyspaces (global, nam_nt, dummy) unconditionally
            // so the ledger table can live in `global` and the rest of the run can be tracked.
            await EnsureSingletonKeyspacesAsync(session, visibleDcs, isScylla);
            await EnsureLedgerAsync(session);
            var applied = await LoadAppliedAsync(session);

            await ApplyKeyspaces(session, visibleDcs, isScylla, applied);
            await ApplySchema(session, applied);
            await ApplyTemplatedMigrationPerKeyspace(session, applied, FieldTimestampsMigration);
            await ApplyTemplatedMigrationPerKeyspace(session, applied, ImportOperationsMigration);
            await ApplyTemplatedMigrationPerKeyspace(session, applied, ColorFixupMarkerMigration);

            // users.primary_front int -> smallint narrowing. Three ALTERs with two inline
            // backfills between them; sequencing is load-bearing (see migration 006's
            // header). All five steps have to finish inside this StartingAsync pass because
            // repositories bind to the final primary_front (smallint) shape as soon as
            // traffic starts.
            await ApplyTemplatedMigrationPerKeyspace(session, applied, PrimaryFrontAddNewMigration);
            await BackfillPrimaryFrontIntToNewAsync(session, applied, cancellationToken);
            await ApplyTemplatedMigrationPerKeyspace(session, applied, PrimaryFrontSwapTypeMigration);
            await BackfillPrimaryFrontNewToPrimaryAsync(session, applied, cancellationToken);
            await ApplyTemplatedMigrationPerKeyspace(session, applied, PrimaryFrontDropNewMigration);

            await GrantPermissions(session, applied);
        }
        finally
        {
            session.Dispose();
            await cluster.ShutdownAsync();
        }

        logger.LogInformation("[scylla-migrate] All migrations applied.");

        // Clear admin credentials from memory.
        _adminUsername = null;
        _adminPassword = null;
        logger.LogInformation("[scylla-migrate] Admin credentials cleared from memory.");
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Externally invocable entry point that applies the embedded CQL migrations using admin
    /// credentials from <paramref name="secretsStore"/>. The integration test
    /// <c>SharedDbFixture</c> calls this once per test session against each enabled CQL backend
    /// (Scylla and/or Cassandra) so the per-test <c>InterfoldWebApplicationFactory</c> doesn't
    /// need to rebuild migrations on every host. Idempotent: every CREATE statement uses
    /// <c>IF NOT EXISTS</c> and <see cref="AlreadyExistsException"/> is caught.
    /// </summary>
    public static Task MigrateAsync(
        PersistenceConfiguration options,
        ISecretsStore secretsStore,
        IScyllaConfigResolver configResolver,
        ILogger<ScyllaMigrationService> logger,
        CancellationToken cancellationToken)
    {
        // Wrap the caller-supplied snapshot so the primary constructor's
        // IOptions<PersistenceConfiguration> contract is honoured without spreading
        // Options.Create noise across every test call site.
        var service = new ScyllaMigrationService(Options.Create(options), secretsStore, configResolver, logger);
        return service.StartingAsync(cancellationToken);
    }

    private Cluster BuildCluster() =>
        Cluster.Builder()
            .AddContactPoints(_contactPoints!)
            .WithPort(_port)
            .WithLoadBalancingPolicy(new DCAwareRoundRobinPolicy(_datacenter!))
            .WithCredentials(_adminUsername, _adminPassword)
            .WithQueryTimeout(30000)
            .WithSocketOptions(new SocketOptions()
                .SetConnectTimeoutMillis(15000)
                // Default SocketOptions.ReadTimeoutMillis is 12s, which is too short for the
                // migration path where Scylla can spend a few seconds per CREATE TABLE on a
                // contended host (e.g. self-hosted DinD on slow disks). Bumping to 60s matches
                // the per-host budget of a single migration statement; the per-query budget is
                // still bounded by WithQueryTimeout above.
                .SetReadTimeoutMillis(60000)
                .SetKeepAlive(true))
            .Build();

    // --- DC Discovery ---

    private async Task<HashSet<string>> DiscoverDatacenters(ISession session)
    {
        var dcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var localRows = await session.ExecuteAsync(new SimpleStatement("SELECT data_center FROM system.local"));
        foreach (var row in localRows)
        {
            var dc = row.GetValue<string>("data_center");
            if (!string.IsNullOrWhiteSpace(dc)) dcs.Add(dc.ToLowerInvariant());
        }

        var peerRows = await session.ExecuteAsync(new SimpleStatement("SELECT data_center FROM system.peers"));
        foreach (var row in peerRows)
        {
            var dc = row.GetValue<string>("data_center");
            if (!string.IsNullOrWhiteSpace(dc)) dcs.Add(dc.ToLowerInvariant());
        }

        // Only keep known regional DCs
        dcs.IntersectWith(RegionalKeyspaces);
        return dcs;
    }

    // --- Replication Strategies (mirrors scylla-load-keyspaces.sh) ---

    private static bool IsMultiDc(HashSet<string> dcs) => dcs.Count > 1;

    private static string SimpleReplication() =>
        "{'class': 'SimpleStrategy', 'replication_factor': '1'}";

    private static string NtsReplication(HashSet<string> visibleDcs, params string[] preferredDcs)
    {
        // Only include DCs that actually exist in the cluster
        var actualDcs = preferredDcs.Where(visibleDcs.Contains).ToArray();
        if (actualDcs.Length == 0) actualDcs = [visibleDcs.First()];
        var pairs = string.Join(", ", actualDcs.Select(dc => $"'{dc}': '1'"));
        return $"{{'class': 'NetworkTopologyStrategy', {pairs}}}";
    }

    private static string RegionalReplicationFor(string keyspace, HashSet<string> dcs)
    {
        if (!IsMultiDc(dcs)) return SimpleReplication();

        return keyspace switch
        {
            "nam" => NtsReplication(dcs, "nam", "eur", "sam"),
            "eur" => dcs.Contains("eur") ? NtsReplication(dcs, "nam", "eur", "sas") : NtsReplication(dcs, "nam", "sam", "sas"),
            "sam" => NtsReplication(dcs, "nam", "sam", "eas"),
            "sas" => dcs.Contains("sas") ? NtsReplication(dcs, "nam", "sas", "ocn") : NtsReplication(dcs, "nam", "sas", "gdpr"),
            "eas" => dcs.Contains("eas") ? NtsReplication(dcs, "nam", "eas", "ocn") : NtsReplication(dcs, "nam", "eas", "gdpr"),
            "ocn" => dcs.Contains("ocn") ? NtsReplication(dcs, "nam", "ocn", "gdpr") : NtsReplication(dcs, "nam", "ocn", "sas"),
            "gdpr" => dcs.Contains("gdpr") ? NtsReplication(dcs, "nam", "gdpr", "eur") : NtsReplication(dcs, "nam", "eur", "ocn"),
            _ => NtsReplication(dcs, "nam", "eur", "sam")
        };
    }

    private static string GlobalReplication(HashSet<string> dcs)
    {
        if (!IsMultiDc(dcs)) return SimpleReplication();

        // Global keyspace replicates to all available DCs
        var allDcs = new[] { "nam", "eur", "sam", "sas", "eas", "ocn", "gdpr" }
            .Where(dcs.Contains)
            .ToArray();
        return NtsReplication(dcs, allDcs);
    }

    private static string NamNtReplication(HashSet<string> dcs)
    {
        if (!IsMultiDc(dcs)) return SimpleReplication();
        return NtsReplication(dcs, "nam");
    }

    // --- Database Detection ---

    private static async Task<bool> DetectScyllaDb(ISession session)
    {
        try
        {
            var rs = await session.ExecuteAsync(new SimpleStatement("SELECT cluster_name FROM system.local"));
            // ScyllaDB clusters have system_schema.scylla_tables; try a lightweight probe
            await session.ExecuteAsync(new SimpleStatement(
                "SELECT keyspace_name FROM system_schema.scylla_tables LIMIT 1"));
            return true;
        }
        catch
        {
            return false;
        }
    }

    // --- Singleton Keyspace Bootstrap (untracked) ---

    /// <summary>
    /// Renders and applies <see cref="SingletonsMigration"/> to create the cluster-wide
    /// singleton keyspaces (<c>global</c>, <c>nam_nt</c>, <c>dummy</c>). This step is
    /// intentionally not recorded in the ledger: <c>global</c> must exist before the ledger
    /// table can be created and the statements are cheap idempotent CREATE/ALTERs anyway.
    /// </summary>
    private async Task EnsureSingletonKeyspacesAsync(ISession session, HashSet<string> dcs, bool isScylla)
    {
        var cqlTemplate = GetEmbeddedResource(SingletonsMigration);
        var (tabletsClause, tabletsClauseDisabled) = ComputeTabletsClauses(dcs, isScylla);
        var rendered = cqlTemplate
            .Replace("{{GLOBAL_REPLICATION}}", GlobalReplication(dcs))
            .Replace("{{NAM_NT_REPLICATION}}", NamNtReplication(dcs))
            .Replace("{{TABLETS_CLAUSE_DISABLED}}", tabletsClauseDisabled)
            .Replace("{{TABLETS_CLAUSE}}", tabletsClause);

        logger.LogInformation("[scylla-migrate] Bootstrapping singleton keyspaces ({Singletons})...",
            string.Join(", ", SingletonKeyspaces));
        await ExecuteStatements(session, rendered);
    }

    // --- Keyspace Creation ---

    private async Task ApplyKeyspaces(
        ISession session,
        HashSet<string> dcs,
        bool isScylla,
        Dictionary<(string Scope, string Version), string> applied)
    {
        var cqlTemplate = GetEmbeddedResource(KeyspacesMigration);
        var checksum = ComputeChecksum(cqlTemplate);
        var (tabletsClause, _) = ComputeTabletsClauses(dcs, isScylla);

        var keyspaces = TargetKeyspaces();

        foreach (var keyspace in keyspaces)
        {
            if (ShouldSkip(applied, keyspace, KeyspacesMigration, checksum))
            {
                logger.LogDebug("[scylla-migrate] Skipping {Migration} for '{Keyspace}', already applied.",
                    KeyspacesMigration, keyspace);
                continue;
            }

            var regionalRepl = RegionalReplicationFor(keyspace, dcs);
            var rendered = cqlTemplate
                .Replace("{{KEYSPACE}}", keyspace)
                .Replace("{{KEYSPACE_REPLICATION}}", regionalRepl)
                .Replace("{{TABLETS_CLAUSE}}", tabletsClause);

            logger.LogInformation("[scylla-migrate] Creating keyspace '{Region}' (replication={Repl})...",
                keyspace, regionalRepl);
            var stopwatch = Stopwatch.StartNew();
            await ExecuteStatements(session, rendered);
            stopwatch.Stop();

            await RecordMigrationAsync(session, keyspace, KeyspacesMigration, checksum,
                (int)stopwatch.ElapsedMilliseconds);
        }
    }

    // --- Schema Application ---

    private async Task ApplySchema(
        ISession session,
        Dictionary<(string Scope, string Version), string> applied)
    {
        var cqlTemplate = GetEmbeddedResource(SchemaMigration);
        var checksum = ComputeChecksum(cqlTemplate);

        var keyspaces = TargetKeyspaces();

        foreach (var keyspace in keyspaces)
        {
            if (ShouldSkip(applied, keyspace, SchemaMigration, checksum))
            {
                logger.LogDebug("[scylla-migrate] Skipping {Migration} for '{Keyspace}', already applied.",
                    SchemaMigration, keyspace);
                continue;
            }

            var rendered = cqlTemplate.Replace("{{KEYSPACE}}", keyspace);
            logger.LogInformation("[scylla-migrate] Applying schema to keyspace '{Keyspace}'...", keyspace);
            var stopwatch = Stopwatch.StartNew();
            await ExecuteStatements(session, rendered);
            stopwatch.Stop();

            await RecordMigrationAsync(session, keyspace, SchemaMigration, checksum,
                (int)stopwatch.ElapsedMilliseconds);
        }
    }

    // --- Templated Per-Keyspace Migrations ---

    /// <summary>
    /// Applies a templated <c>.cql</c> migration to every regional keyspace, tracked individually
    /// in the ledger so it runs exactly once per keyspace. Used for post-002 schema additions
    /// such as UDT extensions where re-running the bootstrap schema isn't acceptable.
    /// </summary>
    private async Task ApplyTemplatedMigrationPerKeyspace(
        ISession session,
        Dictionary<(string Scope, string Version), string> applied,
        string migrationFilename)
    {
        var cqlTemplate = GetEmbeddedResource(migrationFilename);
        var checksum = ComputeChecksum(cqlTemplate);

        var keyspaces = TargetKeyspaces();

        foreach (var keyspace in keyspaces)
        {
            if (ShouldSkip(applied, keyspace, migrationFilename, checksum))
            {
                logger.LogDebug("[scylla-migrate] Skipping {Migration} for '{Keyspace}', already applied.",
                    migrationFilename, keyspace);
                continue;
            }

            var rendered = cqlTemplate.Replace("{{KEYSPACE}}", keyspace);
            logger.LogInformation("[scylla-migrate] Applying {Migration} to keyspace '{Keyspace}'...",
                migrationFilename, keyspace);
            var stopwatch = Stopwatch.StartNew();
            await ExecuteStatements(session, rendered);
            stopwatch.Stop();

            await RecordMigrationAsync(session, keyspace, migrationFilename, checksum,
                (int)stopwatch.ElapsedMilliseconds);
        }
    }

    // --- Primary Front Backfill ---

    /// <summary>
    /// Pass 1 of the users.primary_front int -> smallint narrowing: copies every non-null
    /// <c>primary_front</c> (int) value into the sibling <c>primary_front_new</c> (smallint)
    /// column added by migration 006. Runs before migration 007 drops the int column.
    /// Ledger-guarded so the scan runs at most once per keyspace even under crash-restart
    /// churn.
    /// </summary>
    private async Task BackfillPrimaryFrontIntToNewAsync(
        ISession session,
        Dictionary<(string Scope, string Version), string> applied,
        CancellationToken cancellationToken)
        => await RunPrimaryFrontBackfillAsync(
            session,
            applied,
            "primary_front_backfill_int_to_new",
            PrimaryFrontIntToNewChecksum,
            keyspace => new SimpleStatement(
                $"SELECT id, primary_front, primary_front_new FROM {keyspace}.users"),
            static row => (row.GetValue<int?>("primary_front"), row.GetValue<short?>("primary_front_new")),
            static intValue =>
            {
                if (intValue is < short.MinValue or > short.MaxValue)
                {
                    throw new InvalidDataException(
                        $"users.primary_front value {intValue} is outside " +
                        $"[{short.MinValue}, {short.MaxValue}]; a legacy row escaped the " +
                        $"smallint invariant. Investigate before re-running the migration.");
                }
                return (short)intValue;
            },
            (keyspace, userId, smallintValue) => new SimpleStatement(
                $"UPDATE {keyspace}.users SET primary_front_new = ? WHERE id = ?",
                smallintValue,
                userId),
            cancellationToken);

    /// <summary>
    /// Pass 2 of the users.primary_front int -> smallint narrowing: copies the smallint
    /// values from <c>primary_front_new</c> back into the re-added <c>primary_front</c>
    /// (smallint) column. Runs after migration 007 has dropped the legacy int and re-added
    /// primary_front as smallint, and before migration 008 drops the temp column.
    /// </summary>
    private async Task BackfillPrimaryFrontNewToPrimaryAsync(
        ISession session,
        Dictionary<(string Scope, string Version), string> applied,
        CancellationToken cancellationToken)
        => await RunPrimaryFrontBackfillAsync(
            session,
            applied,
            "primary_front_backfill_new_to_primary",
            PrimaryFrontNewToPrimaryChecksum,
            keyspace => new SimpleStatement(
                $"SELECT id, primary_front, primary_front_new FROM {keyspace}.users"),
            static row => (row.GetValue<short?>("primary_front_new"), row.GetValue<short?>("primary_front")),
            static shortValue => (short)shortValue,
            (keyspace, userId, smallintValue) => new SimpleStatement(
                $"UPDATE {keyspace}.users SET primary_front = ? WHERE id = ?",
                smallintValue,
                userId),
            cancellationToken);

    /// <summary>
    /// Shared per-keyspace scan/update loop used by both narrowing backfills. Ledgered with
    /// <c>(scope:{keyspace}, PrimaryFrontBackfillVersion, checksum)</c> so a completed pass
    /// short-circuits on the next boot. Rows where the destination is already populated are
    /// skipped, so a crash-mid-backfill just resumes and only touches the still-null tail.
    /// </summary>
    private async Task RunPrimaryFrontBackfillAsync(
        ISession session,
        Dictionary<(string Scope, string Version), string> applied,
        string scopePrefix,
        string checksum,
        Func<string, SimpleStatement> buildSelect,
        Func<Row, (int? Source, short? Destination)> readColumns,
        Func<int, short> convertSource,
        Func<string, string, short, SimpleStatement> buildUpdate,
        CancellationToken cancellationToken)
    {
        foreach (var keyspace in TargetKeyspaces())
        {
            var scope = $"{scopePrefix}:{keyspace}";
            if (ShouldSkip(applied, scope, PrimaryFrontBackfillVersion, checksum))
            {
                logger.LogDebug("[scylla-migrate] Skipping {Scope}, already applied.", scope);
                continue;
            }

            var stopwatch = Stopwatch.StartNew();
            var selectStmt = buildSelect(keyspace);
            selectStmt.SetPageSize(500);
            var rows = await session.ExecuteAsync(selectStmt);

            var copied = 0;
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (source, destination) = readColumns(row);
                if (destination is not null)
                    continue;
                if (source is null)
                    continue;

                var smallintValue = convertSource(source.Value);
                var userId = row.GetValue<string>("id");
                await session.ExecuteAsync(buildUpdate(keyspace, userId, smallintValue));
                copied++;
            }

            stopwatch.Stop();
            logger.LogInformation("[scylla-migrate] {Scope}: copied {Copied} row(s).", scope, copied);

            await RecordMigrationAsync(session, scope, PrimaryFrontBackfillVersion, checksum,
                (int)stopwatch.ElapsedMilliseconds);
        }
    }

    // --- Permission Grants ---

    private async Task GrantPermissions(
        ISession session,
        Dictionary<(string Scope, string Version), string> applied)
    {
        var appUser = _appUsername;
        if (string.IsNullOrWhiteSpace(appUser))
        {
            logger.LogWarning("[scylla-migrate] No app user configured — skipping permission grants.");
            return;
        }

        // Skip grants when app user is the same as admin (e.g. default Cassandra superuser)
        if (string.Equals(appUser, _adminUsername, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("[scylla-migrate] App user is the admin user — skipping permission grants.");
            return;
        }

        logger.LogInformation("[scylla-migrate] Granting DML permissions to '{AppUser}'...", appUser);

        var grantChecksum = ComputeChecksum(GrantTemplate);
        var keyspaces = TargetKeyspaces()
            .Concat(SingletonKeyspaces)
            .ToArray();

        foreach (var keyspace in keyspaces)
        {
            var scope = $"grants:{keyspace}";
            if (ShouldSkip(applied, scope, GrantsVersion, grantChecksum))
            {
                logger.LogDebug("[scylla-migrate] Skipping grants for '{Keyspace}', already applied.", keyspace);
                continue;
            }

            var stopwatch = Stopwatch.StartNew();
            await GrantKeyspacePermissions(session, keyspace, appUser);
            stopwatch.Stop();

            await RecordMigrationAsync(session, scope, GrantsVersion, grantChecksum,
                (int)stopwatch.ElapsedMilliseconds);
        }
    }

    private async Task GrantKeyspacePermissions(ISession session, string keyspace, string user)
    {
        var grants = new[]
        {
            $"GRANT SELECT ON KEYSPACE {keyspace} TO '{user}'",
            $"GRANT MODIFY ON KEYSPACE {keyspace} TO '{user}'"
        };

        foreach (var stmt in grants)
        {
            try
            {
                await session.ExecuteAsync(new SimpleStatement(stmt));
            }
            catch (InvalidQueryException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                // Permission already granted — idempotent
            }
        }
    }

    // --- Ledger Helpers ---

    /// <summary>
    /// Ensures the <c>global.schema_migrations</c> ledger table exists. Assumes the
    /// <c>global</c> keyspace has already been created by <see cref="EnsureSingletonKeyspacesAsync"/>.
    /// </summary>
    private static async Task EnsureLedgerAsync(ISession session)
    {
        var ddl = $$"""
                    CREATE TABLE IF NOT EXISTS {{LedgerTableFqn}} (
                        scope        text,
                        version      text,
                        checksum     text,
                        applied_at   timestamp,
                        duration_ms  int,
                        applied_by   text,
                        PRIMARY KEY (scope, version)
                    )
                    """;
        await session.ExecuteAsync(new SimpleStatement(ddl));
    }

    private static async Task<Dictionary<(string Scope, string Version), string>> LoadAppliedAsync(ISession session)
    {
        var applied = new Dictionary<(string, string), string>();
        var rs = await session.ExecuteAsync(new SimpleStatement(
            $"SELECT scope, version, checksum FROM {LedgerTableFqn}"));
        foreach (var row in rs)
        {
            var scope = row.GetValue<string>("scope");
            var version = row.GetValue<string>("version");
            var checksum = row.GetValue<string>("checksum");
            applied[(scope, version)] = checksum;
        }
        return applied;
    }

    /// <summary>
    /// Returns true if the migration is already recorded with a matching checksum. Throws
    /// <see cref="InvalidOperationException"/> if a row exists with a different checksum
    /// (fail-fast on post-deploy file drift).
    /// </summary>
    private static bool ShouldSkip(
        Dictionary<(string Scope, string Version), string> applied,
        string scope,
        string version,
        string checksum)
    {
        if (!applied.TryGetValue((scope, version), out var existing)) return false;
        if (string.Equals(existing, checksum, StringComparison.Ordinal)) return true;

        throw new InvalidOperationException(
            $"[scylla-migrate] Checksum mismatch for migration '{version}' (scope='{scope}'): " +
            $"recorded={existing}, actual={checksum}. Migration files must not be edited after " +
            $"they have been applied. Restore the original file or, if the change is intentional, " +
            $"manually update {LedgerTableFqn}.checksum for this row.");
    }

    private async Task RecordMigrationAsync(
        ISession session,
        string scope,
        string version,
        string checksum,
        int durationMs)
    {
        var appliedBy = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        var stmt = new SimpleStatement(
            $"INSERT INTO {LedgerTableFqn} (scope, version, checksum, applied_at, duration_ms, applied_by) " +
            "VALUES (?, ?, ?, ?, ?, ?) IF NOT EXISTS",
            scope, version, checksum, DateTimeOffset.UtcNow, durationMs, appliedBy);

        var rs = await session.ExecuteAsync(stmt);
        var row = rs.FirstOrDefault();
        // [applied]=false means a concurrent migrator inserted the same row first. Safe to ignore:
        // if the existing checksum differs we'll catch it on the next startup via ShouldSkip.
        if (row is not null && !row.GetValue<bool>("[applied]"))
        {
            logger.LogDebug("[scylla-migrate] Ledger row for ({Scope}, {Version}) already inserted by concurrent run.",
                scope, version);
        }
    }

    internal static string ComputeChecksum(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private string[] TargetKeyspaces() =>
        options.Value.IsSingleScyllaInstance
            ? [_keyspace!]
            : RegionalKeyspaces;

    private static (string TabletsClause, string TabletsClauseDisabled) ComputeTabletsClauses(
        HashSet<string> dcs, bool isScylla)
    {
        var tabletsClause = isScylla && IsMultiDc(dcs)
            ? " AND tablets = {'enabled': true}"
            : "";
        var tabletsClauseDisabled = isScylla
            ? " AND tablets = {'enabled': false}"
            : "";
        return (tabletsClause, tabletsClauseDisabled);
    }

    // --- CQL Execution ---

    private Task ExecuteStatements(ISession session, string cql)
        => ExecuteStatementsStatic(session, cql, logger);

    /// <summary>
    /// CQL execution helper. Swallows two idempotence-critical exception classes:
    ///
    /// <list type="bullet">
    ///   <item>
    ///     <see cref="AlreadyExistsException"/> — CREATE-side migrations all use
    ///     <c>IF NOT EXISTS</c> but Scylla still raises this for a small window of
    ///     edge cases (concurrent CREATE INDEX, older server builds).
    ///   </item>
    ///   <item>
    ///     <see cref="InvalidQueryException"/> where the message signals "column not
    ///     found" / "column does not exist" / "column already exists" — kept as a
    ///     forward-compat safety net so that a boot which crashes between an ALTER
    ///     landing and its ledger row being written can re-run without throwing on
    ///     the second boot.
    ///   </item>
    /// </list>
    /// </summary>
    private static async Task ExecuteStatementsStatic(ISession session, string cql, ILogger logger)
    {
        var statements = SplitCqlStatements(cql);
        foreach (var stmt in statements)
        {
            logger.LogDebug("[scylla-migrate] Executing: {Stmt}", stmt[..Math.Min(stmt.Length, 80)]);
            try
            {
                await session.ExecuteAsync(new SimpleStatement(stmt));
            }
            catch (AlreadyExistsException)
            {
                // Idempotent — keyspace/table/type/index already exists
            }
            catch (InvalidQueryException ex) when (IsColumnNotFound(ex))
            {
                // Idempotent — DROP {column} already applied on an earlier boot.
                logger.LogDebug("[scylla-migrate] Skipping DROP for missing column: {Message}", ex.Message);
            }
            catch (InvalidQueryException ex) when (IsColumnAlreadyExists(ex))
            {
                // Idempotent — ADD {column} already applied on an earlier boot. The
                // target ScyllaDB version rejects `ADD IF NOT EXISTS` at parse time
                // (SyntaxError on the `IF` token), so we lean on this handler to make
                // any future ALTER ... ADD migrations re-runnable after a crash between
                // the ALTER landing and the ledger row being written.
                logger.LogDebug("[scylla-migrate] Skipping ADD for existing column: {Message}", ex.Message);
            }
        }
    }

    private static bool IsColumnNotFound(InvalidQueryException ex)
    {
        var msg = ex.Message;
        return msg.Contains("column", StringComparison.OrdinalIgnoreCase)
               && (msg.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
                   || msg.Contains("not found", StringComparison.OrdinalIgnoreCase)
                   || msg.Contains("undefined", StringComparison.OrdinalIgnoreCase)
                   || msg.Contains("unknown", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsColumnAlreadyExists(InvalidQueryException ex)
    {
        var msg = ex.Message;
        // Scylla surfaces this as "Invalid column name ... conflicts with an existing column"
        // or "Column X of type Y already exists" depending on version; match on the intent
        // rather than the exact wording.
        return (msg.Contains("column", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("conflicts with", StringComparison.OrdinalIgnoreCase))
               && (msg.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                   || msg.Contains("conflicts with an existing", StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> SplitCqlStatements(string cql)
    {
        return StatementSplitter().Split(cql)
            .Select(StripCommentLines)
            .Where(s => s.Length > 0)
            .ToList();
    }

    private static string StripCommentLines(string chunk)
    {
        var lines = chunk.Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal) && l.Length > 0);
        return string.Join('\n', lines).Trim();
    }

    [GeneratedRegex(@";\s*$", RegexOptions.Multiline)]
    private static partial Regex StatementSplitter();

    private static string GetEmbeddedResource(string filename)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"Interfold.Infrastructure.Scylla.Migrations.{filename}";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
