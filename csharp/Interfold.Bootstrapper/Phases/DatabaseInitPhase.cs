using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Util;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Interfold.DatabaseBootstrap;

namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// Phase 5.5 — owns the database admin work that previously lived in the
/// <c>pg-bootstrap-auth</c> / <c>scylla-bootstrap-auth</c> init containers. Brings up the
/// stateful services in isolation, then hands off seed orchestration to
/// <see cref="PostgresSeeder"/> / <see cref="ScyllaSeeder"/> via the compose-exec executors
/// in this folder. After this phase returns the cluster is ready for
/// <see cref="LaunchPhase"/> to <c>up -d</c> the rest of the graph.
/// </summary>
/// <remarks>
/// <para>
/// All admin/seed operations are driven via <c>docker compose exec</c> against the running
/// containers — no admin password ever appears in <c>docker-compose.yaml</c>, <c>.env</c>,
/// or AppHost parameters. State is detected from inside the cluster (the <c>&lt;user&gt;_admin</c>
/// role's existence, plus the app user's <c>rolsuper</c> flag), so a rerun against a
/// populated data volume short-circuits cleanly.
/// </para>
/// <para>
/// The actual SQL/CQL bodies live in <c>Interfold.DatabaseBootstrap</c> so the same logic
/// runs against the in-process test fixtures via <c>DbInitHelper</c> + the Npgsql / DataStax
/// driver adapters. This file is intentionally lean: it owns only the docker-compose
/// orchestration, the cold-start wait loops, and the executor wire-up.
/// </para>
/// </remarks>
internal static class DatabaseInitPhase
{
    private static readonly string Phase = BootstrapPhase.DbInit.ToWireName();
    private const string PostgresService = ComposeServices.Postgres;

    public static async Task RunAsync(
        BootstrapOptions options,
        BootstrapConfig config,
        GeneratedSecrets secrets,
        FirebaseSeedInputs firebase,
        PhaseLogger logger,
        CancellationToken ct)
    {
        logger.PhaseStart(Phase);

        var composeFile = PhaseArtifactLoader.RequireComposeFileOrFail(options, logger, Phase);

        // Scylla service name depends on launch profile. The 'cassandra' fallback and
        // multi-region scylla deployments use a different container alias - we drive the
        // first node only because all admin operations propagate via gossip.
        var scyllaService = ResolveScyllaServiceName(config);
        var scyllaPort = ResolveScyllaPort();

        if (CassandraImagePhase.IsCassandraDeployment(config))
        {
            await CassandraImagePhase.EnsureBuiltAsync(logger, ct).ConfigureAwait(false);
        }

        // Bring up only the stateful services first. We deliberately don't start the API or
        // any other services so the API doesn't race against an unconfigured Postgres.
        await DockerComposeStartAsync(composeFile, [PostgresService, scyllaService], logger, ct).ConfigureAwait(false);

        var seederLogger = new PhaseLoggerAdapter(logger);
        var pgExecutor = new ComposeExecPostgresExecutor(composeFile, PostgresService, seederLogger);
        var scExecutor = new ComposeExecScyllaExecutor(composeFile, scyllaService, seederLogger);
        var pgOptions = BuildPostgresSeedOptions(config, secrets, firebase, scyllaService, scyllaPort);
        var scOptions = new ScyllaSeedOptions(
            AppUser: secrets.ScyllaUser,
            AppPassword: secrets.ScyllaPassword,
            AdminUser: $"{secrets.ScyllaUser}_admin",
            AdminPassword: secrets.ScyllaAdminPassword,
            LockDefaultCassandra: true);

        await WaitForPostgresAsync(composeFile, logger, ct).ConfigureAwait(false);
        await PostgresSeeder.BootstrapAsync(pgExecutor, pgOptions, seederLogger, ct).ConfigureAwait(false);

        // Hidden testability hook: halt the phase between Postgres and Scylla so
        // DbInitFaultRecoveryTests can confirm a rerun resumes cleanly. We throw rather than
        // return so the Orchestrator's try/catch surfaces a non-zero exit and skips the
        // Launch phase (which would otherwise try to `compose up` an un-initialised stack).
        if (string.Equals(options.FaultInject, BootstrapPhase.DbPostgres.ToFaultInjectToken(), StringComparison.OrdinalIgnoreCase))
        {
            logger.Warn("--fault-inject=after-db-postgres triggered; halting before scylla init.");
            throw new InvalidOperationException("fault-inject:after-db-postgres");
        }

        await WaitForScyllaAsync(composeFile, scyllaService, scExecutor, scOptions, logger, ct).ConfigureAwait(false);
        await ScyllaSeeder.BootstrapAsync(scExecutor, scOptions, seederLogger, ct).ConfigureAwait(false);

        logger.PhaseDone(Phase);
    }

    private static PostgresSeedOptions BuildPostgresSeedOptions(
        BootstrapConfig config,
        GeneratedSecrets secrets,
        FirebaseSeedInputs firebase,
        string scyllaService,
        int scyllaPort)
    {
        return new PostgresSeedOptions(
            InitUser: PostgresRoles.Init,
            InitPassword: secrets.PostgresInitPassword,
            AppUser: secrets.PostgresUser,
            AppPassword: secrets.PostgresPassword,
            AdminUser: $"{secrets.PostgresUser}_admin",
            AdminPassword: secrets.PostgresAdminPassword,
            DefaultDatabase: config.PostgresDatabase,
            GoogleOAuthClientSecret: config.OAuth.GoogleClientSecret ?? string.Empty,
            DiscordOAuthClientSecret: config.OAuth.DiscordClientSecret ?? string.Empty,
            AppleOAuthClientSecret: config.OAuth.AppleClientSecret ?? string.Empty,
            EncryptionPepper: secrets.EncryptionPepper,
            // Self-hosted deployments reach scylla via the docker network using its container
            // alias. The matching DataStax driver inside the API resolves it inside the same
            // network so the alias is sufficient.
            ScyllaContactPoints: scyllaService,
            ScyllaLocalDatacenter: "nam",
            ScyllaAppUser: secrets.ScyllaUser,
            ScyllaAppPassword: secrets.ScyllaPassword,
            ScyllaPort: scyllaPort,
            ScyllaAdminUser: $"{secrets.ScyllaUser}_admin",
            ScyllaAdminPassword: secrets.ScyllaAdminPassword,
            JwtRsa256PrivateKeyPem: secrets.JwtRsa256PrivateKeyPem,
            JwtEs256PrivateKeyPem: secrets.JwtEs256PrivateKeyPem,
            DeepLinkSecret: secrets.DeepLinkSecret,
            LeafPfxPassword: secrets.LeafPfxPassword,
            // Production callers always finish by scrambling the init credential in-cluster
            // so the .env value sitting in the operator's deployment dir is intentionally
            // stale by the time the API starts.
            ScrambleInitUserPassword: true,
            FirebaseAndroidClientJson: firebase.AndroidClientJson,
            FirebaseIosClientJson: firebase.IosClientJson,
            FirebaseWebClientJson: firebase.WebClientJson,
            FcmServiceAccountJson: firebase.ServiceAccountJson);
    }

    private static string ResolveScyllaServiceName(BootstrapConfig config)
    {
        // 'cassandra' mode uses an entirely different image / container alias (see
        // InterfoldAppHost Cassandra branch). Multi-region scylla deployments name the
        // first node 'scylla-nam'. We always target one seed node — all admin operations
        // propagate via CQL gossip.
        return config.DatabaseMode switch
        {
            DatabaseMode.Cassandra => ComposeServices.Cassandra,
            DatabaseMode.Multi => ComposeServices.ScyllaNam,
            _ => ComposeServices.ScyllaSingle,
        };
    }

    private static int ResolveScyllaPort()
    {
        // CQL port the API uses to reach Scylla *over the compose docker network* (resolving the
        // scylla service name via docker DNS). That target is always the container's listening
        // port — 9042 — irrespective of whatever host port the operator (or test fixture) chose
        // for external access via `config.ports.scylla`. The host-port choice flows into the
        // compose YAML via PublishPhase; here we deliberately stay on the in-network port.
        return 9042;
    }

    // -------- Bring-up --------

    private static async Task DockerComposeStartAsync(
        string composeFile, IReadOnlyList<string> services, PhaseLogger logger, CancellationToken ct)
    {
        logger.Info($"    docker compose up -d {string.Join(' ', services)}");
        var run = await Util.DockerCompose.UpAsync(composeFile, services, detach: true, build: false, ct: ct).ConfigureAwait(false);
        if (run.ExitCode != 0)
        {
            logger.Error(run.StdErr.Trim());
            throw new InvalidOperationException(
                $"docker compose up -d for [{string.Join(", ", services)}] exited with code {run.ExitCode}.");
        }
        if (!string.IsNullOrWhiteSpace(run.StdOut)) logger.Info(run.StdOut.Trim());
    }

    // -------- Wait loops (transport-specific) --------

    private static Task WaitForPostgresAsync(string composeFile, PhaseLogger logger, CancellationToken ct)
    {
        return Util.PostgresReadinessProbe.WaitAsync(
            composeFile,
            PostgresService,
            new Interfold.DatabaseBootstrap.PostgresReadinessOptions(TimeSpan.FromMinutes(10), 3, PostgresRoles.Init),
            logger,
            ct);
    }

    private static async Task WaitForScyllaAsync(
        string composeFile, string scyllaService,
        IScyllaExecutor executor, ScyllaSeedOptions options,
        PhaseLogger logger, CancellationToken ct)
    {
        // CQL connectivity from inside the container — try app creds first, then fall back
        // to the built-in cassandra/cassandra. Either successful response means the node has
        // finished gossip-bootstrap and is accepting auth.
        _ = composeFile;
        _ = scyllaService;
        var deadline = DateTime.UtcNow.AddMinutes(5);
        var attempt = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;
            var asApp = await executor.TryExecCqlAsync(
                options.AppUser, options.AppPassword, "DESCRIBE CLUSTER", ct).ConfigureAwait(false);
            if (asApp.Succeeded)
            {
                logger.Info($"    scylla ready (as app user) after {attempt} attempt(s)");
                return;
            }
            var asDefault = await executor.TryExecCqlAsync(
                ScyllaCqlTemplates.DefaultUser, ScyllaCqlTemplates.DefaultPassword,
                "DESCRIBE CLUSTER", ct).ConfigureAwait(false);
            if (asDefault.Succeeded)
            {
                logger.Info($"    scylla ready (as cassandra default) after {attempt} attempt(s)");
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
        throw new TimeoutException($"scylla did not become ready within 5 minutes.");
    }
}

