using Aspire.Hosting.Docker.Resources.ComposeNodes;
using Interfold.AppHost.DevSeed;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Enums;
using Microsoft.Extensions.DependencyInjection;
// Aspire.Hosting.ApplicationModel also defines PersistenceMode; alias to disambiguate.
using PersistenceMode = Interfold.Shared.Contracts.PersistenceMode;

namespace Interfold.AppHost;

/// <summary>Interfold distributed-application resource graph. Consumed by the dev
/// <c>Interfold.AppHost</c> executable (`aspire run`) and by the self-hosting
/// <c>Interfold.Bootstrapper</c> which pre-populates <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>
/// with generated secrets before calling <see cref="Configure"/>.</summary>
public static class InterfoldAppHost
{
    // Health-check names must match between AddHealthChecks().AddCheck and WithHealthCheck
    // or the annotation silently never resolves.
    private const string MsgDbHealthCheckName = "msg-db-health";
    private const string ScyllaHealthCheckName = "scylla-health";

    /// <summary>Per-node CQL readiness gate name (<c>{resource}-cql</c>).</summary>
    private static string ScyllaCqlCheckName(string resourceName) => $"{resourceName}-cql";

    private const string PostgresEndpointName = "postgres";
    private const string CqlEndpointName = "cql";
    private const string HttpEndpointName = "http";
    private const string HttpsEndpointName = "https";

    /// <summary>Scheme for raw TCP endpoints (Postgres, CQL). HTTP/HTTPS go through the
    /// typed overloads.</summary>
    private const string TcpScheme = "tcp";

    private const string ComposeEnvironmentName = "docker-compose";

    /// <summary>Aspire's default dashboard service name (<c>{environment}-dashboard</c>).</summary>
    private const string DashboardComposeServiceName = ComposeEnvironmentName + "-dashboard";

    // Test-bench host port defaults (CLI overrides via Ports:postgres/scylla/cassandra).
    private const int DefaultPostgresPort = 4200;
    private const int DefaultScyllaPort = 9042;
    private const int DefaultCassandraPort = 9043;
    private const int DefaultApiContainerHttpPort = 5100;
    private const int DefaultEdgeHttpPort = 80;
    private const int DefaultEdgeHttpsPort = 443;

    /// <summary>CQL cluster-name fallback for dev `aspire run`; the bootstrapper always overrides.</summary>
    private const string DefaultClusterName = "InterfoldCluster";

    /// <summary>Registers the full Interfold resource graph. Does not call
    /// <c>Build()</c> or <c>Run()</c>.</summary>
    public static void Configure(IDistributedApplicationBuilder builder)
    {
        int Port(string key, int fallback) => int.TryParse(builder.Configuration[key], out var p) ? p : fallback;

        // ParamName keeps parameter declaration and IConfiguration override on the same spelling.
        static string ParamName(string key) => AppHostParameterKeys.ToParameterName(key);
        var postgresPort = Port(AppHostParameterKeys.PortsPostgres, DefaultPostgresPort);
        var scyllaPort = Port(AppHostParameterKeys.PortsScylla, DefaultScyllaPort);
        // Cassandra owns its own port so SharedDbFixture can publish both CQL backends
        // side-by-side; legacy Cassandra-only mode falls back to scyllaPort further down.
        var cassandraPort = Port(AppHostParameterKeys.PortsCassandra, DefaultCassandraPort);
        // Must match the API image's internal Kestrel listen port (5100). Override via
        // --Ports:api-container-http= when rebuilding the image with a different EXPOSE.
        var apiContainerHttpPort = Port(AppHostParameterKeys.PortsApiContainerHttp, DefaultApiContainerHttpPort);
        var edgeHttpPort = Port(AppHostParameterKeys.PortsEdgeHttp, DefaultEdgeHttpPort);
        var edgeHttpsPort = Port(AppHostParameterKeys.PortsEdgeHttps, DefaultEdgeHttpsPort);

        // Bench mode is the auto-managed cross-project DB fixture (see
        // TestBenchCoordinator + BenchSharedDb). When on it forces api/web/dashboard off,
        // switches on persistent-containers, pins stable container names on the three DB
        // resources, and — via TestBenchReadyEmitter — writes a machine-readable readiness
        // line to stdout after WaitForResourcesAsync so the launcher can exit cleanly.
        var testBenchMode = BoolWire.ParseToggle(builder.Configuration[AppHostParameterKeys.TestBenchMode], fallback: false);

        var includeEdge = !testBenchMode;
        var edgeTlsMode = builder.Configuration[AppHostParameterKeys.EdgeTlsMode] ?? "privateCa";
        var edgeUsesPlainHttp = string.Equals(edgeTlsMode, "none", StringComparison.OrdinalIgnoreCase);
        var edgeCloudflareTunnel = BoolWire.ParseToggle(
            builder.Configuration[AppHostParameterKeys.EdgeCloudflareTunnel], fallback: false);
        var hostPublishDbPorts = testBenchMode;

        var includeApi = testBenchMode
            ? false
            : BoolWire.ParseToggle(builder.Configuration[AppHostParameterKeys.IncludeApi], fallback: true);
        var persistentContainers = testBenchMode
            ? true
            : BoolWire.ParseToggle(builder.Configuration[AppHostParameterKeys.PersistentContainers], fallback: true);

        // Both CQL backends can be on simultaneously (SharedDbFixture uses this to
        // exercise Scylla + Cassandra under one Aspire host). scylla-topology only
        // affects layout when include-scylla=true.
        var includeScylla = BoolWire.ParseToggle(builder.Configuration[AppHostParameterKeys.IncludeScylla], fallback: true);
        var includeCassandra = BoolWire.ParseToggle(builder.Configuration[AppHostParameterKeys.IncludeCassandra], fallback: false);
        var scyllaTopology = ScyllaTopologyExtensions.Parse(builder.Configuration[AppHostParameterKeys.ScyllaTopology]);
        var isMultiScylla = scyllaTopology == ScyllaTopology.Multi;

        // Resolve regions ONCE so health-check registrations and node resources can't diverge.
        // Multi: one node per ScyllaKeyspace. Single: one NAM node under the un-suffixed name.
        ScyllaKeyspace[] scyllaRegions = isMultiScylla ? Enum.GetValues<ScyllaKeyspace>() : [ScyllaKeyspace.Nam];
        var isMultiScyllaNode = scyllaRegions.Length > 1;

        // CQL cluster identity — Cassandra reads CASSANDRA_CLUSTER_NAME, Scylla takes it
        // via --cluster-name (immune to image bake-time changes). Pure metadata.
        var clusterName = builder.Configuration[AppHostParameterKeys.ClusterName] ?? DefaultClusterName;

        // Off only for MultiNodeScyllaFixture (which shares SharedDbFixture's msg-db
        // instead of spinning up a redundant second Postgres).
        var includePostgres = BoolWire.ParseToggle(builder.Configuration[AppHostParameterKeys.IncludePostgres], fallback: true);

        if (includeApi && !includePostgres)
        {
            throw new InvalidOperationException(
                "Parameters:include-api=true requires Parameters:include-postgres=true (the API depends on msg-db).");
        }

        if (includeApi)
        {
            var hcBuilder = builder.Services.AddHealthChecks();
            if (hostPublishDbPorts)
            {
                hcBuilder.AddCheck(MsgDbHealthCheckName, HostPortTcpProbe.CreateCheck(postgresPort));
                // scylla-health only when Cassandra is the sole CQL backend; per-node {name}-cql
                // checks below cover include-scylla=true.
                hcBuilder.AddCheck(ScyllaHealthCheckName, HostPortTcpProbe.CreateCheck(scyllaPort));
            }
            else
            {
                hcBuilder.AddAsyncCheck(MsgDbHealthCheckName, async ct =>
                    await DockerExecPgIsReadyProbe.RunAsync(ComposeServices.Postgres, ct).ConfigureAwait(false));
                if (!includeScylla)
                {
                    hcBuilder.AddAsyncCheck(ScyllaHealthCheckName, async ct =>
                        await DockerExecCqlProbe.RunAsync(ComposeServices.Cassandra, ct).ConfigureAwait(false));
                }
            }
        }

        // Per-node CQL readiness gates (always on). Each node's WithHealthCheck below flips
        // WaitFor(previousNode) from "Running" to "Healthy", which serialises Raft joins —
        // Scylla 2026.1's topology coordinator admits one joiner at a time.
        // We poll `docker ps` per-probe (not via a BackgroundService watching
        // ResourceNotificationService) because Aspire.Hosting.Testing's in-process host
        // silently drops hosted services in multi-fixture sessions (multinode-stagger-debug.log).
        if (includeScylla)
        {
            var hcBuilder = builder.Services.AddHealthChecks();
            foreach (var region in scyllaRegions)
            {
                var resourceName = ComposeServices.ToScyllaNodeName(region, multiNode: isMultiScyllaNode);
                hcBuilder.AddAsyncCheck(ScyllaCqlCheckName(resourceName), async ct =>
                    await DockerExecCqlProbe.RunAsync(resourceName, ct).ConfigureAwait(false));
            }
        }

        // Bootstrapper sets include-dashboard=false so self-hosted production stacks
        // don't pull the nightly aspire-dashboard image; dev `aspire run` keeps it.
        // Bench mode also forces it off — the launcher spawns a short-lived AppHost that
        // exits after emitting readiness, so pulling the dashboard image is pure overhead.
        var includeDashboard = testBenchMode
            ? false
            : BoolWire.ParseToggle(builder.Configuration[AppHostParameterKeys.IncludeDashboard], fallback: true);
        builder.AddDockerComposeEnvironment(ComposeEnvironmentName)
            .WithDashboard(includeDashboard)
            .ConfigureComposeFile(compose =>
            {
                compose.AddNetwork(new Network { Name = ComposeNetworks.Scylla, Driver = "bridge" });
                compose.AddNetwork(new Network { Name = ComposeNetworks.Postgres, Driver = "bridge" });
                compose.AddNetwork(new Network { Name = ComposeNetworks.EdgeApi, Driver = "bridge" });
                compose.AddNetwork(new Network { Name = ComposeNetworks.EdgeWeb, Driver = "bridge" });

                if (compose.Services.TryGetValue(DashboardComposeServiceName, out var dashboard))
                {
                    dashboard.Networks.Add(ComposeNetworks.EdgeApi);
                }
            });

        var postgresUser = builder.AddParameter(ParamName(AppHostParameterKeys.PostgresUser));
        var postgresPassword = builder.AddParameter(ParamName(AppHostParameterKeys.PostgresPassword), secret: true);
        var postgresDb = builder.AddParameter(ParamName(AppHostParameterKeys.PostgresDb), "interfold", publishValueAsDefault: true);
        // Transient init credential. db_init is a disposable cluster-owner that
        // DatabaseInitPhase uses once to mint the real roles, then scrambles. The .env value
        // is intentionally stale by the time the API starts. Default via
        // GenerateParameterDefault so dev `aspire run` and TUnit.Aspire don't need a pre-seed;
        // publish mode overrides via PublishPhase.BuildEnvReplacements.
        var postgresInitPassword = builder.AddParameter(ParamName(AppHostParameterKeys.PostgresInitPassword),
            new GenerateParameterDefault { MinLength = 24 }, secret: true, persist: true);
        var scyllaUser = builder.AddParameter(ParamName(AppHostParameterKeys.ScyllaUser));
        var scyllaPassword = builder.AddParameter(ParamName(AppHostParameterKeys.ScyllaPassword), secret: true);
        // The <user>_admin superuser is minted by DatabaseInitPhase into internal.secrets and
        // is never surfaced here.
        // GenerateParameterDefault covers the dev `aspire run` case (bootstrapper always injects
        // a real PEM in publish mode, config wins over the default). The generated value is a
        // random alphanumeric string — RecoveryCodeResolver.TryLoadEncryptionPrivateKey checks
        // for "BEGIN" and short-circuits, so JWE-encrypted recovery codes silently 400 in dev.
        // That's the accepted trade-off for the no-config F5 target.
        var encryptionPrivateKey = builder.AddParameter(ParamName(AppHostParameterKeys.EncryptionPrivateKey),
            new GenerateParameterDefault { MinLength = 32 }, secret: true, persist: true);

        // Public OAuth client IDs — empty default disables the corresponding provider (see
        // OAuthChallengeServiceCollectionExtensions). Attached to the container path only so
        // the dev project path keeps reading them from launchSettings.json / user-secrets.
        var googleOAuthClientId = builder.AddParameter(ParamName(AppHostParameterKeys.GoogleOAuthClientId), "", publishValueAsDefault: true);
        var discordOAuthClientId = builder.AddParameter(ParamName(AppHostParameterKeys.DiscordOAuthClientId), "", publishValueAsDefault: true);
        var appleOAuthClientId = builder.AddParameter(ParamName(AppHostParameterKeys.AppleOAuthClientId), "", publishValueAsDefault: true);

        // API runtime parameters managed end-to-end by the bootstrapper (ConfigPhase prompts,
        // ConfigureApiSelfHostEnv pipes to the container as OCTOCON_*).
        var scyllaKeyspace = builder.AddParameter(ParamName(AppHostParameterKeys.ScyllaKeyspace), ScyllaKeyspace.Nam.ToWire(), publishValueAsDefault: true);
        var oauthCallbackBaseUrl = builder.AddParameter(ParamName(AppHostParameterKeys.OAuthCallbackBaseUrl), "", publishValueAsDefault: true);
        var jwtAuthority = builder.AddParameter(ParamName(AppHostParameterKeys.JwtAuthority), "", publishValueAsDefault: true);
        var jwtAudience = builder.AddParameter(ParamName(AppHostParameterKeys.JwtAudience), "octocon", publishValueAsDefault: true);
        var corsAllowedOrigins = builder.AddParameter(ParamName(AppHostParameterKeys.CorsAllowedOrigins), "", publishValueAsDefault: true);

        // Operator tuning — every default matches the API's compile-time fallback so a
        // fresh bootstrap reproduces "env var unset" behaviour. Empty for the nullable
        // knobs (avatars, OTLP, socket threshold) — ApplyStorage / ApplyObservability
        // normalise empty → null.
        var nodeGroup = builder.AddParameter(ParamName(AppHostParameterKeys.NodeGroup), "auxiliary", publishValueAsDefault: true);
        var avatarPublicBase = builder.AddParameter(ParamName(AppHostParameterKeys.AvatarPublicBase), "", publishValueAsDefault: true);
        var otlpEndpoint = builder.AddParameter(ParamName(AppHostParameterKeys.OtlpEndpoint), "", publishValueAsDefault: true);
        var socketBatchBytesThreshold = builder.AddParameter(ParamName(AppHostParameterKeys.SocketBatchBytesThreshold), "", publishValueAsDefault: true);
        var dbRetryAttempts = builder.AddParameter(ParamName(AppHostParameterKeys.DbRetryAttempts), "3", publishValueAsDefault: true);
        var dbRetryInitialDelayMs = builder.AddParameter(ParamName(AppHostParameterKeys.DbRetryInitialDelayMs), "100", publishValueAsDefault: true);
        var dbRetryMaxDelayMs = builder.AddParameter(ParamName(AppHostParameterKeys.DbRetryMaxDelayMs), "1500", publishValueAsDefault: true);
        var hydrationMaxConcurrency = builder.AddParameter(ParamName(AppHostParameterKeys.HydrationMaxConcurrency), "8", publishValueAsDefault: true);

        // Reject well-known default passwords in dev mode. Each check is gated on the
        // matching include-* toggle so opt-out fixtures don't need placeholder creds. Publish
        // mode relies on the operator's shell guard.
        if (!builder.ExecutionContext.IsPublishMode)
        {
            if (includeScylla)
            {
                var scyllaPasswordValue = builder.Configuration[AppHostParameterKeys.ScyllaPassword];
                if (string.IsNullOrEmpty(scyllaPasswordValue) || scyllaPasswordValue == "cassandra")
                {
                    throw new InvalidOperationException(
                        "SCYLLA_PASSWORD must be set to a non-default value. " +
                        "The well-known default 'cassandra' is not allowed.\n" +
                        "Fix: dotnet user-secrets set \"Parameters:scylla-password\" \"<your-secure-password>\" " +
                        "--project hosts/Interfold.AppHost");
                }

                var scyllaUserValue = builder.Configuration[AppHostParameterKeys.ScyllaUser];
                if (string.IsNullOrEmpty(scyllaUserValue) || scyllaUserValue == "cassandra")
                {
                    throw new InvalidOperationException(
                        "SCYLLA_USER must be set to a non-default value. " +
                        "The well-known default 'cassandra' is not allowed.\n" +
                        "Fix: dotnet user-secrets set \"Parameters:scylla-user\" \"<your-username>\" " +
                        "--project hosts/Interfold.AppHost");
                }
            }

            if (includePostgres)
            {
                var postgresPasswordValue = builder.Configuration[AppHostParameterKeys.PostgresPassword];
                if (string.IsNullOrEmpty(postgresPasswordValue) || postgresPasswordValue == "postgres")
                {
                    throw new InvalidOperationException(
                        "POSTGRES_PASSWORD must be set to a non-default value. " +
                        "The well-known default 'postgres' is not allowed.\n" +
                        "Fix: dotnet user-secrets set \"Parameters:postgres-password\" \"<your-secure-password>\" " +
                        "--project hosts/Interfold.AppHost");
                }

                var postgresUserValue = builder.Configuration[AppHostParameterKeys.PostgresUser];
                if (string.IsNullOrEmpty(postgresUserValue) || postgresUserValue == "postgres")
                {
                    throw new InvalidOperationException(
                        "POSTGRES_USER must be set to a non-default value. " +
                        "The well-known default 'postgres' is not allowed.\n" +
                        "Fix: dotnet user-secrets set \"Parameters:postgres-user\" \"<your-username>\" " +
                        "--project hosts/Interfold.AppHost");
                }
            }
        }

        // Postgres (TimescaleDB). Skipped when include-postgres=false. First-boot pattern:
        // POSTGRES_USER=db_init (disposable bootstrap superuser — can't demote OID 10 per
        // postgres' commands/user.c::AlterRole), POSTGRES_PASSWORD is the transient init
        // credential DatabaseInitPhase scrambles after seeding, no POSTGRES_DB (the app DB is
        // created owned by the admin role, not db_init, for consistent DDL gating).
        IResourceBuilder<ContainerResource>? msgDb = null;
        if (includePostgres)
        {
            msgDb = builder.AddContainer(ComposeServices.Postgres, "timescale/timescaledb", "latest-pg18")
                .WithContainerNetworkAlias(ComposeServices.Postgres)
                .WithEnvironment(ContainerEnvNames.PostgresUser, PostgresRoles.Init)
                .WithEnvironment(ContainerEnvNames.PostgresPassword, postgresInitPassword)
                .WithEnvironment(ContainerEnvNames.PgData, ContainerMountPaths.PostgresPgData)
                // Force scram-sha-256 for every host entry — the initdb default appends a
                // `host all all 127.0.0.1/32 trust` line first and pg_hba.conf is first-match-wins,
                // so without this loopback TCP connections silently bypass the db_init password
                // scramble. `local all all trust` still applies to Unix sockets, which is how
                // DatabaseInitPhase mints the app + admin roles pre-scramble.
                .WithEnvironment(ContainerEnvNames.PostgresInitDbArgs, "--auth-host=scram-sha-256")
                // pg_ctl's 60s default isn't enough on slow DinD disks — the timescaledb-tune
                // init script triggers a fast-shutdown checkpoint before postgres re-execs in
                // normal mode, and a slow checkpoint kills the container before WaitForPostgresAsync
                // sees normal mode. 300s buys headroom.
                .WithEnvironment(ContainerEnvNames.PgCtlTimeout, "300")
                // Bypass timescaledb-tune's cgroup-v2 memory autodetection (missing
                // /sys/fs/cgroup/memory.max on some Docker Desktop / restricted hosts panics
                // the init script). Conservative defaults; operators can override via
                // docker-compose.override.yaml.
                .WithEnvironment(ContainerEnvNames.TsTuneMemory, "1GB")
                .WithEnvironment(ContainerEnvNames.TsTuneNumCpus, "2");
            if (!hostPublishDbPorts)
            {
                msgDb = msgDb.WithEndpoint(targetPort: 5432, name: PostgresEndpointName, scheme: TcpScheme);
            }
            else
            {
                msgDb = msgDb.WithEndpoint(port: postgresPort, targetPort: 5432, name: PostgresEndpointName, scheme: TcpScheme);
            }

            msgDb = msgDb
                .PublishAsDockerComposeService((_, service) =>
                {
                    service.Networks = [ComposeNetworks.Postgres];
                    service.Healthcheck = ComposeHealthcheck.CmdShell(
                        $"pg_isready -U ${ContainerEnvNames.PostgresUser} -d postgres",
                        interval: "10s", timeout: "5s", retries: 10, startPeriod: "15s");
                });
            if (includeApi)
                msgDb.WithHealthCheck(MsgDbHealthCheckName);
            if (testBenchMode)
            {
                // Bench mode is deliberately volume-less: the container's own filesystem
                // holds transient DB state, ContainerLifetime.Persistent keeps it alive across
                // AppHost restarts (hot-attach path), and TestBenchIdleReaper's docker-rm-f
                // cleans everything up in one call. Sharing the ComposeVolumes.PostgresData
                // volume ("msg_pgdata") cross-contaminates with any local bootstrapper compose
                // stack and preserves stale db_init passwords across bench reboots.
                msgDb.WithLifetime(ContainerLifetime.Persistent);
                // Proxyless — docker binds host:14200 → container:5432 directly, no Aspire
                // reverse proxy. The launcher AppHost exits right after TestBenchReadyEmitter
                // signals ready; a proxied endpoint would die with it and leave the container
                // reachable only on a random docker-assigned ephemeral port. Aspire 13.4+ makes
                // persistent-resource endpoints proxyless by default, but we set it explicitly
                // for compatibility with older SDKs on the workstation.
                msgDb.WithEndpoint(PostgresEndpointName, e => e.IsProxied = false);
                // Legacy per-project fixtures shard tests across their own Postgres, so the
                // image default max_connections=100 was safe. Bench mode collapses every leaf
                // integration project onto ONE Postgres: SecretsPreBuildLoader pools up to 10
                // conns per test-host process, the app pool adds 5 more, and a solution-wide
                // `dotnet test` spawns many hosts concurrently. That trips PostgresErrorCode
                // 53300 ("too many clients already") during factory build. 500 buys headroom
                // with tiny shared-memory overhead (~50MB extra) and doesn't require touching
                // shared_buffers. Passed via `postgres -c`; docker-entrypoint.sh forwards CMD
                // args to the postgres binary verbatim.
                msgDb.WithArgs("-c", "max_connections=500");
            }
            else if (persistentContainers)
            {
                msgDb.AsPersistent(ComposeVolumes.PostgresData, ContainerMountPaths.PostgresData);
            }
            // Pin the docker container name in bench mode so subsequent AppHost launcher
            // processes reuse the same container instead of colliding on host port 14200.
            if (testBenchMode)
                msgDb.WithContainerName(TestBenchContainerNames.Postgres);
        }

        // API waits on each included CQL backend before starting.
        var cqlEndpointOwners = new List<IResourceBuilder<ContainerResource>>();

        if (includeScylla)
        {
            IResourceBuilder<ContainerResource>? previousNode = null;

            foreach (var region in scyllaRegions)
            {
                var regionWire = region.ToWire();
                var name = ComposeServices.ToScyllaNodeName(region, multiNode: isMultiScyllaNode);
                var seeds = previousNode is null
                    ? $"--seeds={name}"
                    : $"--seeds={ComposeServices.ToScyllaNodeName(scyllaRegions[0], multiNode: true)},{ComposeServices.ToScyllaNodeName(scyllaRegions[1], multiNode: true)}";

                var nodeArgs = new List<string>
                {
                    seeds, "--smp", "1", "--memory", "750M", "--overprovisioned", "1",
                    "--developer-mode", "1", "--authenticator", "PasswordAuthenticator",
                    "--authorizer", "CassandraAuthorizer",
                    "--endpoint-snitch", "GossipingPropertyFileSnitch",
                    "--api-address", "0.0.0.0", "--broadcast-address", name,
                    "--cluster-name", clusterName,
                };
                if (isMultiScylla)
                {
                    // Ring-delay 60s (vs 30s default) — extra settle time for the Raft
                    // topology coordinator between multi-DC joiners.
                    nodeArgs.Add("--ring-delay-ms");
                    nodeArgs.Add("60000");
                }

                var node = builder.AddContainer(name, "scylladb/scylla", "2026.1")
                    .WithContainerNetworkAlias(name)
                    .WithArgs([.. nodeArgs])
                    .WithBindMount($"../../db/scylla/cassandra-rackdc.{regionWire}.properties", "/etc/scylla/cassandra-rackdc.properties", isReadOnly: true)
                    .WithEnvironment(ContainerEnvNames.CqlshUser, scyllaUser)
                    .WithEnvironment(ContainerEnvNames.CqlshPassword, scyllaPassword)
                    .PublishAsDockerComposeService((_, service) =>
                    {
                        service.Networks = [ComposeNetworks.Scylla];
                        // Probe the actual CQL endpoint (not `nodetool status`) so
                        // depends_on:service_healthy really means "CQL reachable as the app user"
                        // — on slow DinD hosts the CQL listener can lag NORMAL mode by 60–90s.
                        // `$$VAR` is deliberate: docker-compose interpolates ${VAR} at parse time
                        // against the host shell, `$$VAR` escapes to a literal that the
                        // container's /bin/sh expands against the container env.
                        service.Healthcheck = ComposeHealthcheck.CmdShell(
                            $"cqlsh -u \"$${ContainerEnvNames.CqlshUser}\" -p \"$${ContainerEnvNames.CqlshPassword}\" -e 'DESCRIBE CLUSTER' >/dev/null 2>&1",
                            interval: "15s", timeout: "10s", retries: 20, startPeriod: "30s");
                    });

                if (testBenchMode && !isMultiScyllaNode)
                {
                    // Volume-less by design; see the Postgres branch above for rationale.
                    node.WithLifetime(ContainerLifetime.Persistent);
                }
                else if (persistentContainers)
                {
                    node.AsPersistent(
                        isMultiScyllaNode ? ComposeVolumes.ScyllaRegionData(regionWire) : ComposeVolumes.ScyllaData,
                        ContainerMountPaths.ScyllaData);
                }
                // Bench mode is single-node only (guarded above by testBenchMode forcing
                // includeApi/Web/Dashboard off; scylla-topology stays single). Pin the
                // container name so cross-process reuse works on the fixed 19042 port.
                if (testBenchMode && !isMultiScyllaNode)
                {
                    node.WithContainerName(TestBenchContainerNames.Scylla);
                    // Proxyless — see the Postgres branch above.
                    node.WithEndpoint(CqlEndpointName, e => e.IsProxied = false);
                }

                // HealthCheckAnnotation flips WaitFor(previousNode) from "Running" to
                // "Healthy" — serialises multi-DC joins so Raft doesn't ban concurrent joiners.
                node.WithHealthCheck(ScyllaCqlCheckName(name));

                if (previousNode is null)
                {
                    if (!hostPublishDbPorts)
                    {
                        node = node.WithEndpoint(targetPort: 9042, name: CqlEndpointName, scheme: TcpScheme);
                    }
                    else
                    {
                        node = node.WithEndpoint(port: scyllaPort, targetPort: 9042, name: CqlEndpointName, scheme: TcpScheme);
                    }
                    cqlEndpointOwners.Add(node);
                }
                else
                {
                    node.WaitFor(previousNode);
                }

                previousNode = node;
            }
        }

        if (includeCassandra)
        {
            // Built from db/cassandra/Dockerfile (FROM cassandra:5). The image bakes the
            // required cassandra.yaml overrides (materialized_views_enabled + Password
            // Authenticator/Authorizer) so we skip the runtime wrapper + chown that used to
            // break on GHA. Endpoint port: standalone Cassandra owns Ports:scylla (legacy),
            // co-hosted with Scylla it takes Ports:cassandra so the CQL listeners don't collide.
            var cassandraEndpointPort = includeScylla ? cassandraPort : scyllaPort;

            var cassandra = builder.AddDockerfile(ComposeServices.Cassandra, "../../db/cassandra")
                .WithContainerNetworkAlias(ComposeServices.Cassandra)
                .WithEnvironment(ContainerEnvNames.CassandraClusterName, clusterName)
                .WithEnvironment(ContainerEnvNames.CassandraListenAddress, ComposeServices.Cassandra)
                .WithEnvironment(ContainerEnvNames.CassandraBroadcastAddress, ComposeServices.Cassandra)
                .WithEnvironment(ContainerEnvNames.CassandraBroadcastRpcAddress, ComposeServices.Cassandra)
                .WithEnvironment(ContainerEnvNames.CassandraRpcAddress, "0.0.0.0")
                .WithEnvironment(ContainerEnvNames.CassandraEndpointSnitch, "GossipingPropertyFileSnitch")
                .WithEnvironment(ContainerEnvNames.CassandraNumTokens, "16")
                .WithEnvironment(ContainerEnvNames.CassandraDc, ScyllaKeyspace.Nam.ToWire())
                .WithEnvironment(ContainerEnvNames.CassandraRack, "rack1")
                .WithEnvironment(ContainerEnvNames.MaxHeapSize, "512M")
                .WithEnvironment(ContainerEnvNames.HeapNewSize, "256M")
                .WithEnvironment(ContainerEnvNames.CqlshUser, scyllaUser)
                .WithEnvironment(ContainerEnvNames.CqlshPassword, scyllaPassword);
            if (!hostPublishDbPorts)
            {
                cassandra = cassandra.WithEndpoint(targetPort: 9042, name: CqlEndpointName, scheme: TcpScheme);
            }
            else
            {
                cassandra = cassandra.WithEndpoint(port: cassandraEndpointPort, targetPort: 9042, name: CqlEndpointName, scheme: TcpScheme);
            }

            cassandra = cassandra
                .PublishAsDockerComposeService((_, service) =>
                {
                    service.Networks = [ComposeNetworks.Scylla];
                    service.Healthcheck = ComposeHealthcheck.CmdShell(
                        $"cqlsh -u \"$${ContainerEnvNames.CqlshUser}\" -p \"$${ContainerEnvNames.CqlshPassword}\" -e 'describe cluster' || nodetool status | grep -q '^UN'",
                        interval: "15s", timeout: "10s", retries: 20, startPeriod: "30s");
                });

            if (includeApi && !includeScylla)
            {
                // Attach scylla-health only when Cassandra owns Ports:scylla.
                cassandra.WithHealthCheck(ScyllaHealthCheckName);
            }
            if (testBenchMode)
            {
                // Volume-less by design; see the Postgres branch above for rationale.
                cassandra.WithLifetime(ContainerLifetime.Persistent);
            }
            else if (persistentContainers)
            {
                cassandra.AsPersistent(ComposeVolumes.CassandraData, ContainerMountPaths.CassandraData);
            }
            if (testBenchMode)
            {
                cassandra.WithContainerName(TestBenchContainerNames.Cassandra);
                // Proxyless — see the Postgres branch above.
                cassandra.WithEndpoint(CqlEndpointName, e => e.IsProxied = false);
            }

            cqlEndpointOwners.Add(cassandra);
        }

        // Seed ownership by mode:
        //  - Publish: DatabaseInitPhase in the bootstrapper (docker compose exec).
        //  - Tests (include-api=false): SharedDbFixture drives DbInitHelper directly.
        //  - RunMode dev (include-api=true): DevSeedHostedService, wired below.
        // First cqlEndpointOwners entry is scylla node 0 in the default flow, cassandra when
        // the user picked the cassandra launch profile; mixed scylla+cassandra is test-only
        // so we only ever seed one backend.
        IResourceBuilder<DevSeedResource>? devSeedResource = null;
        if (!builder.ExecutionContext.IsPublishMode && includeApi && includePostgres)
        {
            var firstCqlBackend = cqlEndpointOwners.Count > 0 ? cqlEndpointOwners[0] : null;
            // Aspire persists GenerateParameterDefault outputs to user-secrets, but only
            // after Build() completes — on first run IConfiguration doesn't yet see them.
            // Threading the ParameterResource lets the hosted service read the effective
            // value via GetValueAsync in both first-run and subsequent-run cases.
            devSeedResource = builder.AddDevSeedPipeline(
                msgDb!, firstCqlBackend,
                postgresInitPassword, postgresPassword, scyllaPassword);
        }

        // Interfold API: pre-built image for self-hosting (Parameters:api-image), csproj build
        // for dev (`aspire run`).
        if (includeApi)
        {
            // Guaranteed non-null by the includeApi && !includePostgres guard above.
            var msgDbResource = msgDb!;
            var pgEndpoint = msgDbResource.GetEndpoint(PostgresEndpointName);

            void ConfigureApiCommon(IResourceBuilder<IResourceWithEnvironment> api)
            {
                api.WithEnvironment(OctoconEnvKeys.Persistence, PersistenceMode.ScyllaPostgres.ToWire())
                   .WithEnvironment(OctoconEnvKeys.SingleScyllaInstance, BoolWire.ToWireValue(!isMultiScylla))
                   .WithEnvironment(OctoconEnvKeys.PostgresConnection,
                       ReferenceExpression.Create($"Host={pgEndpoint.Property(EndpointProperty.Host)};Port={pgEndpoint.Property(EndpointProperty.Port)};Database={postgresDb};Username={postgresUser};Password={postgresPassword}"))
                   .WithEnvironment(ContainerEnvNames.EncryptionPrivateKey, encryptionPrivateKey);
            }

            // Dev-project path only. ConfigureApiSelfHostEnv covers the container path via the
            // operator-provided Parameters:jwt-authority etc.; the dev path has no operator so
            // we stamp localhost-derived defaults driven by the AppHost's own port allocation.
            // Empty CORS falls back to "any origin" in the API — noisy in browser devtools but
            // functional; we still narrow it here so socket + fetch calls behave the same as prod.
            void ConfigureApiDevEnv(IResourceBuilder<IResourceWithEnvironment> api)
            {
                string publicOrigin;
                if (edgeUsesPlainHttp)
                {
                    var suffix = edgeHttpPort == DefaultEdgeHttpPort ? string.Empty : $":{edgeHttpPort}";
                    publicOrigin = $"http://localhost{suffix}";
                }
                else
                {
                    var suffix = edgeHttpsPort == DefaultEdgeHttpsPort ? string.Empty : $":{edgeHttpsPort}";
                    publicOrigin = $"https://localhost{suffix}";
                }

                api.WithEnvironment(OctoconEnvKeys.JwtAuthority, publicOrigin)
                   .WithEnvironment(OctoconEnvKeys.JwtAudience, "octocon")
                   .WithEnvironment(OctoconEnvKeys.AuthCallbackBaseUrl, publicOrigin)
                   .WithEnvironment(OctoconEnvKeys.CorsAllowedOrigins, publicOrigin)
                   .WithEnvironment(OctoconEnvKeys.ScyllaKeyspace, ScyllaKeyspace.Nam.ToWire());
            }

            // Self-hosting only. Avatars always bind-mounted; /certs when private CA material
            // exists for TrustController.
            void ConfigureApiSelfHostEnv(IResourceBuilder<ContainerResource> api)
            {
                api.WithBindMount(AvatarsPaths.HostDir, AvatarsPaths.ContainerDir, isReadOnly: false)
                   .WithEnvironment(ContainerEnvNames.AspNetCoreHttpPorts, apiContainerHttpPort.ToString())
                   .WithEnvironment(OctoconEnvKeys.GoogleOAuthClientId, googleOAuthClientId)
                   .WithEnvironment(OctoconEnvKeys.DiscordOAuthClientId, discordOAuthClientId)
                   .WithEnvironment(OctoconEnvKeys.AppleOAuthClientId, appleOAuthClientId)
                   .WithEnvironment(OctoconEnvKeys.ScyllaKeyspace, scyllaKeyspace)
                   .WithEnvironment(OctoconEnvKeys.AuthCallbackBaseUrl, oauthCallbackBaseUrl)
                   .WithEnvironment(OctoconEnvKeys.JwtAuthority, jwtAuthority)
                   .WithEnvironment(OctoconEnvKeys.JwtAudience, jwtAudience)
                   .WithEnvironment(OctoconEnvKeys.CorsAllowedOrigins, corsAllowedOrigins)
                   .WithEnvironment(OctoconEnvKeys.NodeGroup, nodeGroup)
                   .WithEnvironment(OctoconEnvKeys.AvatarStorageRoot, ContainerMountPaths.InterfoldAvatars)
                   .WithEnvironment(OctoconEnvKeys.AvatarPublicBase, avatarPublicBase)
                   .WithEnvironment(OctoconEnvKeys.OtlpEndpoint, otlpEndpoint)
                   .WithEnvironment(OctoconEnvKeys.SocketBatchBytesThreshold, socketBatchBytesThreshold)
                   .WithEnvironment(OctoconEnvKeys.DbRetryAttempts, dbRetryAttempts)
                   .WithEnvironment(OctoconEnvKeys.DbRetryInitialDelayMs, dbRetryInitialDelayMs)
                   .WithEnvironment(OctoconEnvKeys.DbRetryMaxDelayMs, dbRetryMaxDelayMs)
                   .WithEnvironment(OctoconEnvKeys.HydrationMaxConcurrency, hydrationMaxConcurrency);

                // Mount root CA for TrustController whenever edge uses private CA material.
                if (!edgeUsesPlainHttp && !edgeCloudflareTunnel)
                {
                    api.WithBindMount(CertsPaths.HostDir, CertsPaths.ContainerDir, isReadOnly: true)
                       .WithEnvironment(OctoconEnvKeys.TrustRootCaPath, CertsPaths.RootCaCrt)
                       .WithEnvironment(OctoconEnvKeys.TrustRootCaFingerprintPath, CertsPaths.RootCaFingerprint);
                }
            }

            var apiImageRef = builder.Configuration[AppHostParameterKeys.ApiImage];
            if (!string.IsNullOrEmpty(apiImageRef))
            {
                var apiImage = ImageRef.Parse(apiImageRef);
                var apiContainer = builder.AddContainer(ComposeServices.InterfoldApi, apiImage.Image, apiImage.Tag)
                    .WaitFor(msgDbResource)
                    .WithHttpEndpoint(targetPort: apiContainerHttpPort, name: HttpEndpointName)
                    .WithHttpHealthCheck(HealthEndpoints.Ready, endpointName: HttpEndpointName)
                    .PublishAsDockerComposeService(ApiComposeServicePublisher(apiContainerHttpPort));

                ConfigureApiCommon(apiContainer);
                ConfigureApiSelfHostEnv(apiContainer);
                foreach (var owner in cqlEndpointOwners)
                    apiContainer.WaitFor(owner);
                if (devSeedResource is not null)
                    apiContainer.WaitFor(devSeedResource);
            }
            else
            {
                var apiProject = builder.AddProject<Projects.Interfold_Api_Host>(ComposeServices.InterfoldApi)
                    .WithHttpEndpoint(targetPort: apiContainerHttpPort, name: HttpEndpointName)
                    .WithHttpHealthCheck(HealthEndpoints.Ready, endpointName: HttpEndpointName)
                    .WaitFor(msgDbResource)
                    .PublishAsDockerComposeService(ApiComposeServicePublisher(apiContainerHttpPort));
                ConfigureApiCommon(apiProject);
                ConfigureApiDevEnv(apiProject);
                foreach (var owner in cqlEndpointOwners)
                    apiProject.WaitFor(owner);
                if (devSeedResource is not null)
                    apiProject.WaitFor(devSeedResource);
            }
        }

        var includeWeb = testBenchMode
            ? false
            : BoolWire.ParseToggle(builder.Configuration[AppHostParameterKeys.IncludeWeb], fallback: true);
        if (includeWeb)
        {
            var web = builder.AddContainer(ComposeServices.OctoconWeb, "ghcr.io/azyyyyyy/octocon-wasm", "latest")
                .WithContainerNetworkAlias(ComposeServices.OctoconWeb)
                .WithHttpEndpoint(targetPort: 8080, name: HttpEndpointName)
                .WithHttpHealthCheck("/", endpointName: HttpEndpointName);

            web.PublishAsDockerComposeService((_, service) =>
            {
                service.Networks = [ComposeNetworks.EdgeWeb];
                service.Healthcheck = ComposeHealthcheck.Cmd(
                    interval: "15s", timeout: "5s", retries: 5, startPeriod: "10s",
                    command: ["curl", "-f", "http://localhost:8080/"]);
            });
        }

        if (includeEdge)
        {
            var edgeServerName = builder.Configuration[AppHostParameterKeys.EdgeServerName];
            if (string.IsNullOrWhiteSpace(edgeServerName)) edgeServerName = "_";
            var edgeApiHost = builder.Configuration[AppHostParameterKeys.EdgeApiHost] ?? edgeServerName;
            var edgeWebHost = builder.Configuration[AppHostParameterKeys.EdgeWebHost] ?? edgeServerName;
            var includeWebUpstream = BoolWire.ParseToggle(
                builder.Configuration[AppHostParameterKeys.EdgeIncludeWebUpstream], fallback: includeWeb);

            // Tunnel: private HTTP origin only (no host-published ports). Otherwise publish
            // edge HTTP, and HTTPS + leaf certs when tlsMode is privateCa.
            var edge = edgeCloudflareTunnel
                ? builder.AddContainer(ComposeServices.EdgeNginx, "nginx", "1.27-alpine")
                    .WithHttpEndpoint(targetPort: 80, name: HttpEndpointName)
                : builder.AddContainer(ComposeServices.EdgeNginx, "nginx", "1.27-alpine")
                    .WithHttpEndpoint(port: edgeHttpPort, targetPort: 80, name: HttpEndpointName);

            edge = edge
                .WithBindMount(
                    $"{EdgePaths.HostSupportDir}/default.conf.template",
                    EdgePaths.ContainerNginxTemplate,
                    isReadOnly: true)
                .WithBindMount(
                    $"{EdgePaths.HostSupportDir}/proxy_params.conf",
                    EdgePaths.ContainerProxyParams,
                    isReadOnly: true);

            if (!edgeCloudflareTunnel && !edgeUsesPlainHttp)
            {
                edge = edge
                    .WithHttpsEndpoint(port: edgeHttpsPort, targetPort: 443, name: HttpsEndpointName)
                    .WithBindMount(EdgePaths.HostCertsDir, EdgePaths.ContainerCertsDir, isReadOnly: true)
                    .WithEnvironment(ContainerEnvNames.NginxSslCertFile, EdgePaths.LeafCrt)
                    .WithEnvironment(ContainerEnvNames.NginxSslKeyFile, EdgePaths.LeafKey)
                    .WithEnvironment(
                        ContainerEnvNames.NginxHttpsPortSuffix,
                        edgeHttpsPort == 443 ? string.Empty : $":{edgeHttpsPort}");
            }

            edge = edge
                .WithEnvironment(ContainerEnvNames.NginxServerName, edgeServerName)
                .WithEnvironment(ContainerEnvNames.NginxApiServerName, edgeApiHost)
                .WithEnvironment(ContainerEnvNames.NginxWebServerName, edgeWebHost)
                .WithEnvironment(
                    ContainerEnvNames.NginxApiUpstream,
                    ComposeServices.InterfoldApi + ":" + apiContainerHttpPort.ToString())
                .WithEnvironment(
                    ContainerEnvNames.NginxWebUpstream,
                    ComposeServices.OctoconWeb + ":8080")
                .WithEnvironment(
                    ContainerEnvNames.NginxIncludeWeb,
                    includeWebUpstream ? "1" : string.Empty)
                .WithEnvironment(ContainerEnvNames.NginxEnvsubstFilter, "^NGINX_")
                .WithHttpHealthCheck("/nginx-health", endpointName: HttpEndpointName);

            if (!edgeCloudflareTunnel)
                edge = edge.WithExternalHttpEndpoints();

            edge = edge.PublishAsDockerComposeService((_, service) =>
            {
                service.Networks = includeWebUpstream
                    ? [ComposeNetworks.EdgeApi, ComposeNetworks.EdgeWeb]
                    : [ComposeNetworks.EdgeApi];
                service.Healthcheck = ComposeHealthcheck.Cmd(
                    interval: "15s", timeout: "5s", retries: 5, startPeriod: "10s",
                    command: ["wget", "-qO-", "http://127.0.0.1/nginx-health"]);
                // Private origin: no host port publish even if Aspire allocated an ephemeral mapping.
                if (edgeCloudflareTunnel)
                    service.Ports = [];
            });

            if (edgeCloudflareTunnel)
            {
                var tokenPath = builder.Configuration[AppHostParameterKeys.EdgeCloudflareTunnelTokenPath]
                    ?? throw new InvalidOperationException(
                        $"Missing configuration for '{AppHostParameterKeys.EdgeCloudflareTunnelTokenPath}'.");

                var cloudflared = builder.AddContainer(ComposeServices.Cloudflared, "cloudflare/cloudflared", "2025.2.1")
                    .WithArgs(
                        "tunnel", "--no-autoupdate", "run",
                        "--token-file", EdgePaths.ContainerCloudflareTunnelToken)
                    .WithBindMount(tokenPath, EdgePaths.ContainerCloudflareTunnelToken, isReadOnly: true)
                    .WaitFor(edge)
                    .PublishAsDockerComposeService((_, service) =>
                    {
                        service.Networks = includeWebUpstream
                            ? [ComposeNetworks.EdgeApi, ComposeNetworks.EdgeWeb]
                            : [ComposeNetworks.EdgeApi];
                        service.Restart = "unless-stopped";
                    });
                _ = cloudflared;
            }

            _ = edge;
        }

        // Bench mode: register a hosted service that waits for every DB resource to reach
        // Running, prints a machine-readable readiness line, then requests app shutdown so
        // the launcher's Process.WaitForExitAsync returns deterministically. Containers are
        // pinned to Persistent lifetime + stable names above so they survive this exit.
        if (testBenchMode)
        {
            builder.Services.AddSingleton(new TestBenchReadyEmitterOptions(
                PostgresPort: postgresPort,
                ScyllaPort: includeScylla ? scyllaPort : null,
                CassandraPort: includeCassandra ? (includeScylla ? cassandraPort : scyllaPort) : null,
                ScyllaResourceName: includeScylla
                    ? ComposeServices.ToScyllaNodeName(ScyllaKeyspace.Nam, multiNode: false)
                    : null,
                CassandraResourceName: includeCassandra ? ComposeServices.Cassandra : null,
                PostgresResourceName: ComposeServices.Postgres));
            builder.Services.AddHostedService<TestBenchReadyEmitter>();
        }
    }

    private static void PromoteDependenciesToHealthy(Service service, params string[] dependencyNames)
    {
        foreach (var dep in dependencyNames)
        {
            if (service.DependsOn.TryGetValue(dep, out var composeDep))
                composeDep.Condition = ComposeDependencyCondition.ServiceHealthy;
        }
    }

    private static Action<Aspire.Hosting.Docker.DockerComposeServiceResource, Aspire.Hosting.Docker.Resources.ComposeNodes.Service> ApiComposeServicePublisher(int apiContainerHttpPort)
    {
        return (_, service) =>
        {
            service.Networks = [ComposeNetworks.Scylla, ComposeNetworks.Postgres, ComposeNetworks.EdgeApi];
            service.Healthcheck = ComposeHealthcheck.Cmd(
                interval: "15s", timeout: "5s", retries: 10, startPeriod: "20s",
                command: ["curl", "-f", $"http://localhost:{apiContainerHttpPort}{HealthEndpoints.Ready}"]);
            // Gate on msg-db + scylla being CQL-ready (not just Running). Without this the
            // bootstrapper's `up` command races the API into scylla before gossip-bootstrap
            // finishes and ScyllaMigrationService.StartingAsync crashes with
            // "connection refused on 9042".
            PromoteDependenciesToHealthy(service, ComposeServices.Postgres, ComposeServices.ScyllaSingle, ComposeServices.Cassandra);
        };
    }
}
