using Aspire.Hosting;
using Interfold.AppHost;
using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Util;
using Interfold.Shared.Contracts.Enums;
using Microsoft.Extensions.Configuration;
using Interfold.Shared.Contracts.Configuration;

namespace Interfold.Bootstrapper.Phases;

/// <summary>Phase 5 — runs the embedded <see cref="DistributedApplication"/> in publish mode
/// to emit <c>docker-compose.yaml</c> + <c>.env</c> from <c>InterfoldAppHost.Configure</c>.
/// Generated secrets flow via <see cref="IConfiguration"/> so they land in <c>.env</c>, not the
/// compose YAML.</summary>
internal static class PublishPhase
{
    // Aspire resolves some graph bind-mount placeholders against CWD during publish. Per-scratch
    // anchor under {outputDir} avoids parallel bootstraps racing on a shared {appDir}/_aspire_anchor
    // when several DinD sessions bind-mount the same published bootstrapper directory.
    private static readonly string[] AnchorSegments = ["_aspire_anchor", "inner"];

    public static async Task RunAsync(
        BootstrapOptions options,
        BootstrapConfig config,
        GeneratedSecrets secrets,
        PhaseLogger logger,
        CancellationToken ct)
    {
        const string Phase = "publish";
        logger.PhaseStart(Phase);

        Directory.CreateDirectory(options.OutputDir);

        var anchor = SetupAnchor(options.OutputDir);
        var previousCwd = Directory.GetCurrentDirectory();

        try
        {
            Directory.SetCurrentDirectory(anchor);

            await PublishInProcessAsync(options, config, secrets, logger, ct).ConfigureAwait(false);
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
        }

        var composePath = Path.Combine(options.OutputDir, BootstrapArtifactPaths.ComposeFileName);
        if (!File.Exists(composePath))
        {
            // Aspire >=13 sometimes emits into a subdirectory keyed by the environment name.
            var nested = BootstrapArtifactPaths.FindComposeFile(options.OutputDir);
            if (nested is null)
            {
                logger.PhaseFail(Phase, PhaseFailureReasons.ComposeNotEmitted);
                throw new InvalidOperationException(
                    $"Aspire publish completed but no {BootstrapArtifactPaths.ComposeFileName} was produced under {options.OutputDir}.");
            }
            logger.Info($"    compose emitted at {nested}");
            composePath = nested;
        }

        // Aspire 13.x emits .env with blank RHS for every parameter and bind-mount source,
        // expecting the operator to fill them. The bootstrapper IS the operator.
        var envPath = Path.Combine(Path.GetDirectoryName(composePath)!, ".env");
        FillEnvFile(envPath, AppContext.BaseDirectory, options.OutputDir, config, secrets, logger);
        EnsureAvatarHostDirectory(config, options.OutputDir, logger);

        // Cassandra mode: stamp `pull_policy: never` on the cassandra service. This is NOT the
        // update mechanism (CassandraImagePhase.EnsureBuiltAsync runs `docker build` before any
        // compose step); it prevents `docker compose pull` in update-images from erroring on
        // `interfold-cassandra:local` (no registry entry → "manifest not found" → non-zero exit
        // before the digest diff runs). With the stamp compose reports cassandra as Skipped on
        // pull (docker/compose#9376) and every other lifecycle step still targets the local build.
        if (CassandraImagePhase.IsCassandraDeployment(config))
        {
            StampCassandraPullPolicyNever(composePath, logger);
        }

        logger.PhaseDone(Phase);
    }

    /// <summary>Values to splice into the Aspire-emitted <c>.env</c>.
    /// <c>Parameters</c>: keyed by upper-snake env name (e.g. <c>POSTGRES_USER</c>).
    /// <c>BindMounts</c>: keyed by the <c>service:container-target</c> identifier from the
    /// <c># Bind mount source for ...</c> comment above each placeholder. Separated from
    /// <see cref="FillEnvFile"/> so unit tests can assert the key set without staging a real .env.</summary>
    internal sealed record EnvReplacements(
        IReadOnlyDictionary<string, string> Parameters,
        IReadOnlyDictionary<string, string> BindMounts);

    /// <summary>Translates the operator-facing <see cref="CqlBackend"/> value
    /// into the orthogonal AppHost toggles <c>InterfoldAppHost</c> reads. The throw guards
    /// internal callers (unit tests) that bypass <see cref="ConfigPhase.Validate"/>.</summary>
    internal static (bool IncludeScylla, bool IncludeCassandra, ScyllaTopology ScyllaTopology) TranslateDatabaseMode(
        DatabaseMode databaseMode) => databaseMode switch
        {
            DatabaseMode.Single => (true, false, ScyllaTopology.Single),
            DatabaseMode.Multi => (true, false, ScyllaTopology.Multi),
            DatabaseMode.Cassandra => (false, true, ScyllaTopology.Single),
            _ => throw new InvalidOperationException(
                $"Unhandled databaseMode '{databaseMode}'. Expected: single | multi | cassandra."),
        };

    /// <summary>Single source of truth for every operator-tunable Aspire parameter that must
    /// appear in BOTH the <c>.env</c> replacement dictionary (<see cref="BuildEnvReplacements"/>)
    /// AND the <c>Parameters:*</c> injection dictionary (<c>PublishInProcessAsync</c>).
    /// <c>ConfigKey</c> is the <see cref="AppHostParameterKeys"/> IConfiguration key.
    /// <c>EnvKey</c> is the upper-snake spelling Aspire emits into .env; explicit (not derived) so
    /// an Aspire rename is a one-line edit — round-trip asserted in
    /// <c>PublishSharedAspireParametersTests</c>. Nullable inputs collapse to empty to reproduce
    /// the API's "unset env var" branch. Absent by design: graph-only knobs
    /// (cluster/topology/image/dashboard/web/ports) that don't round-trip through .env, plus
    /// <c>CASSANDRA_IMAGE</c> which flows in via <see cref="CassandraImagePhase"/>.</summary>
    internal static IEnumerable<(string ConfigKey, string EnvKey, string Value)>
        EnumerateSharedAspireParameters(BootstrapConfig config, GeneratedSecrets secrets)
    {
        // POSTGRES_INIT_PASSWORD carries the *initial* db_init password so operators who nuke
        // pgdata can rerun the bootstrap; DatabaseInitPhase scrambles it again in-cluster.
        yield return (AppHostParameterKeys.PostgresUser, "POSTGRES_USER", secrets.PostgresUser);
        yield return (AppHostParameterKeys.PostgresPassword, "POSTGRES_PASSWORD", secrets.PostgresPassword);
        yield return (AppHostParameterKeys.PostgresDb, "POSTGRES_DB", config.Datastores.Postgres.Database);
        yield return (AppHostParameterKeys.PostgresInitPassword, "POSTGRES_INIT_PASSWORD", secrets.PostgresInitPassword);

        // Scylla admin password is intentionally absent — that role is created by
        // DatabaseInitPhase and its password only lives in internal.secrets.
        yield return (AppHostParameterKeys.ScyllaUser, "SCYLLA_USER", secrets.ScyllaUser);
        yield return (AppHostParameterKeys.ScyllaPassword, "SCYLLA_PASSWORD", secrets.ScyllaPassword);

        // Bootstrap encryption key only. All other secrets (OAuth client secrets, JWT signing keys,
        // deep-link HMAC, leaf PFX password, encryption pepper) live in internal.secrets, seeded
        // by DatabaseInitPhase and read via SecretsBootstrapService at startup.
        yield return (AppHostParameterKeys.EncryptionPrivateKey, "ENCRYPTION_PRIVATE_KEY", secrets.EncryptionPrivateKeyB64);

        // OAuth client IDs are public identifiers (not secrets); empty is a valid
        // "provider disabled" signal — the API's scheme registrar skips empty IDs.
        yield return (AppHostParameterKeys.GoogleOAuthClientId, "GOOGLE_OAUTH_CLIENT_ID", config.Api.OAuth.GoogleClientId ?? string.Empty);
        yield return (AppHostParameterKeys.DiscordOAuthClientId, "DISCORD_OAUTH_CLIENT_ID", config.Api.OAuth.DiscordClientId ?? string.Empty);
        yield return (AppHostParameterKeys.AppleOAuthClientId, "APPLE_OAUTH_CLIENT_ID", config.Api.OAuth.AppleClientId ?? string.Empty);

        // API runtime config → OCTOCON_* env vars. ConfigPhase.ResolveDerivedDefaults fills
        // empties before Validate; CORS list joined with commas to match the wire format.
        yield return (AppHostParameterKeys.ScyllaKeyspace, "SCYLLA_KEYSPACE", config.Datastores.Cql.Keyspace.ToWire());
        yield return (AppHostParameterKeys.OAuthCallbackBaseUrl, "OAUTH_CALLBACK_BASE_URL", config.Api.OAuth.CallbackBaseUrl);
        yield return (AppHostParameterKeys.JwtAuthority, "JWT_AUTHORITY", config.Api.OAuth.JwtAuthority);
        yield return (AppHostParameterKeys.JwtAudience, "JWT_AUDIENCE", config.Api.OAuth.JwtAudience);
        yield return (AppHostParameterKeys.CorsAllowedOrigins, "CORS_ALLOWED_ORIGINS", string.Join(",", config.Api.CorsAllowedOrigins));

        // Non-secret operator tuning knobs. Nullable/disabled-when-empty fields serialise as
        // "" — the API's binders normalise empty → null, matching the "env var unset" branch.
        // AvatarStorageRoot is intentionally absent: the container always sees
        // /app/data/avatars (baked via WithEnvironment); the host path is a bind-mount
        // source filled by BuildEnvReplacements, not an Aspire parameter.
        yield return (AppHostParameterKeys.NodeGroup, "NODE_GROUP", config.Api.NodeGroup.ToWire());
        yield return (AppHostParameterKeys.AvatarPublicBase, "AVATAR_PUBLIC_BASE", config.Api.Storage.AvatarPublicBase ?? string.Empty);
        yield return (AppHostParameterKeys.OtlpEndpoint, "OTLP_ENDPOINT", config.Observability.OtlpEndpoint ?? string.Empty);
        yield return (AppHostParameterKeys.SocketBatchBytesThreshold, "SOCKET_BATCH_BYTES_THRESHOLD", config.Api.BatchBytesThreshold?.ToString() ?? string.Empty);
        yield return (AppHostParameterKeys.DbRetryAttempts, "DB_RETRY_ATTEMPTS", config.Api.Resilience.DbRetryAttempts.ToString());
        yield return (AppHostParameterKeys.DbRetryInitialDelayMs, "DB_RETRY_INITIAL_DELAY_MS", config.Api.Resilience.DbRetryInitialDelayMs.ToString());
        yield return (AppHostParameterKeys.DbRetryMaxDelayMs, "DB_RETRY_MAX_DELAY_MS", config.Api.Resilience.DbRetryMaxDelayMs.ToString());
        yield return (AppHostParameterKeys.HydrationMaxConcurrency, "HYDRATION_MAX_CONCURRENCY", config.Api.Resilience.HydrationMaxConcurrency.ToString());
    }

    internal static EnvReplacements BuildEnvReplacements(
        BootstrapConfig config,
        GeneratedSecrets secrets,
        string baseDir,
        string outputDir)
    {
        // Seeded from the shared enumerator so every operator-tunable value is emitted here AND
        // injected as an AppHost Parameter from one source of truth.
        var parameters = EnumerateSharedAspireParameters(config, secrets)
            .ToDictionary(p => p.EnvKey, p => p.Value, StringComparer.Ordinal);

        if (CassandraImagePhase.IsCassandraDeployment(config))
        {
            // Aspire emits `image: "${CASSANDRA_IMAGE}"` for AddDockerfile resources;
            // without this entry compose starts the service with an empty image ref.
            parameters["CASSANDRA_IMAGE"] = CassandraImagePhase.LocalImageTag;
        }

        // Bind-mount lookup. Aspire emits `# Bind mount source for <service>:<target>` above each
        // placeholder; we key on "service:target" to resolve the host path. A rename or reorder
        // in a future Aspire release surfaces via the unit tests.
        var bindMountLookup = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"{ComposeServices.InterfoldApi}:{ContainerMountPaths.InterfoldAvatars}"] =
                ResolveAvatarHostRoot(config, outputDir),
        };

        if (config.Edge.TlsMode != EdgeTlsMode.LetsEncrypt
            && config.Edge.TlsMode != EdgeTlsMode.None)
        {
            bindMountLookup[$"{ComposeServices.InterfoldApi}:/certs"] = Path.Combine(outputDir, "certs");
        }

        var edgeSupport = Path.Combine(EmbeddedSupportFiles.SupportRoot(outputDir), "edge", "nginx");
        bindMountLookup[$"{ComposeServices.EdgeNginx}:{EdgePaths.ContainerNginxTemplate}"] =
            Path.Combine(edgeSupport, "default.conf.template");
        bindMountLookup[$"{ComposeServices.EdgeNginx}:{EdgePaths.ContainerProxyParams}"] =
            Path.Combine(edgeSupport, "proxy_params.conf");
        bindMountLookup[$"{ComposeServices.EdgeNginx}:{EdgePaths.ContainerCloudflareIps}"] =
            Path.Combine(edgeSupport, "cloudflare-ips.conf");
        if (config.Edge.TlsMode != EdgeTlsMode.None)
        {
            bindMountLookup[$"{ComposeServices.EdgeNginx}:{EdgePaths.ContainerCertsDir}"] =
                Path.Combine(outputDir, "certs");
            if (config.Edge.TlsMode == EdgeTlsMode.LetsEncrypt)
            {
                bindMountLookup[$"{ComposeServices.EdgeNginx}:{EdgePaths.ContainerAcmeWebroot}"] =
                    Path.Combine(outputDir, "certs", "certbot-www");
            }
        }

        // Region-keyed rackdc mount; single mode → one "scylla" node in "nam", multi mode → one
        // node per region. Derived from ScyllaKeyspace so the list can't drift.
        var databaseMode = CqlBackendMapping.ToDatabaseMode(config.Datastores.Cql.Backend);
        if (databaseMode != DatabaseMode.Cassandra)
        {
            foreach (var region in ResolveScyllaRegions(config))
            {
                var nodeName = ComposeServices.ToScyllaNodeName(region, multiNode: databaseMode == DatabaseMode.Multi);
                bindMountLookup[$"{nodeName}:/etc/scylla/cassandra-rackdc.properties"] =
                    EmbeddedSupportFiles.SupportFilePath(outputDir, EmbeddedSupportFiles.RackDcRelative(region));
            }
        }

        return new EnvReplacements(parameters, bindMountLookup);
    }

    internal static string[] ResolveScyllaRegions(BootstrapConfig config) =>
        CqlBackendMapping.ToDatabaseMode(config.Datastores.Cql.Backend) == DatabaseMode.Multi
            ? Enum.GetValues<ScyllaKeyspace>().Select(k => k.ToWire()).ToArray()
            : [ScyllaKeyspace.Nam.ToWire()];

    /// <summary>Materializes embedded bind-mount sources under <c>{outputDir}/support</c>.</summary>
    internal static void StagePublishSupportFiles(BootstrapConfig config, string outputDir, PhaseLogger logger)
    {
        var materialized = 0;

        if (CqlBackendMapping.ToDatabaseMode(config.Datastores.Cql.Backend) != DatabaseMode.Cassandra)
        {
            foreach (var region in ResolveScyllaRegions(config))
            {
                var relative = EmbeddedSupportFiles.RackDcRelative(region);
                var target = EmbeddedSupportFiles.SupportFilePath(outputDir, relative);
                if (EmbeddedSupportFiles.Materialize(relative, target, logger))
                    materialized++;
            }
        }

        StageEdgeSupportFiles(config, outputDir, logger, ref materialized);

        if (materialized > 0)
        {
            logger.Info(
                $"    staged {materialized} support file(s) under {EmbeddedSupportFiles.SupportRoot(outputDir)} (existing files preserved)");
        }
    }

    internal static void StageEdgeSupportFiles(
        BootstrapConfig config, string outputDir, PhaseLogger logger, ref int materialized)
    {
        var edgeDir = Path.Combine(EmbeddedSupportFiles.SupportRoot(outputDir), "edge", "nginx");
        Directory.CreateDirectory(edgeDir);

        var templateRelative = EmbeddedSupportFiles.EdgeTemplateRelative(
            config.Edge.Routing.Mode == EdgeRoutingMode.Subdomain,
            config.Edge.TlsMode == EdgeTlsMode.None);
        var templateTarget = Path.Combine(edgeDir, "default.conf.template");
        if (!File.Exists(templateTarget))
        {
            EmbeddedSupportFiles.Materialize(templateRelative, templateTarget, logger);
            materialized++;
        }

        var proxyTarget = Path.Combine(edgeDir, "proxy_params.conf");
        if (EmbeddedSupportFiles.Materialize(EmbeddedSupportFiles.EdgeProxyParamsRelative, proxyTarget, logger))
            materialized++;

        var cfTarget = Path.Combine(edgeDir, "cloudflare-ips.conf");
        // Always refresh Cloudflare ranges when allowlist is on; otherwise write a no-op stub.
        // Synchronous wait keeps StagePublishSupportFiles sync for existing callers.
        if (config.Edge.Cloudflare.IpAllowlist)
        {
            CloudflareIpAllowlist.WriteAllowlistAsync(cfTarget, logger).GetAwaiter().GetResult();
        }
        else
        {
            CloudflareIpAllowlist.WriteDisabled(cfTarget);
        }

        Directory.CreateDirectory(Path.Combine(outputDir, "certs", "certbot-www"));

        if (config.Edge.TlsMode == EdgeTlsMode.LetsEncrypt)
        {
            LetsEncryptStaging.EnsureCredentialsAndPlaceholderCerts(config, outputDir, logger);
        }
    }

    /// <summary>Host directory bind-mounted at <see cref="ContainerMountPaths.InterfoldAvatars"/>.
    /// Blank config → <c>{outputDir}/data/avatars</c> (same layout as certs under outputDir).</summary>
    internal static string ResolveAvatarHostRoot(BootstrapConfig config, string outputDir)
    {
        if (!string.IsNullOrWhiteSpace(config.Api.Storage.AvatarStorageRoot))
            return Path.GetFullPath(config.Api.Storage.AvatarStorageRoot);

        return Path.GetFullPath(Path.Combine(outputDir, "data", "avatars"));
    }

    /// <summary>Creates the host avatar directory and relaxes permissions so the non-root
    /// API container can write uploads.</summary>
    internal static void EnsureAvatarHostDirectory(BootstrapConfig config, string outputDir, PhaseLogger logger)
    {
        var hostRoot = ResolveAvatarHostRoot(config, outputDir);
        Directory.CreateDirectory(hostRoot);
        UnixFilePermissions.SetWorldWritable(hostRoot, logger, "avatar storage");
        logger.Info($"    avatar host storage: {hostRoot}");
    }

    /// <summary>Rewrites Aspire's blank <c>.env</c> RHSes with concrete secret/parameter/bind-mount
    /// values. Unknown keys are left untouched so future <see cref="InterfoldAppHost.Configure"/>
    /// additions degrade gracefully (blank key surfaces to the operator).</summary>
    private static void FillEnvFile(
        string envPath,
        string baseDir,
        string outputDir,
        BootstrapConfig config,
        GeneratedSecrets secrets,
        PhaseLogger logger)
    {
        if (!File.Exists(envPath))
        {
            logger.Warn($".env not found at {envPath}; skipping value rewrite");
            return;
        }

        StagePublishSupportFiles(config, outputDir, logger);
        var replacements = BuildEnvReplacements(config, secrets, baseDir, outputDir);
        var (rewritten, skipped) = ApplyReplacementsToEnvFile(envPath, replacements);

        logger.Info($"    .env: filled {rewritten} value(s)");
        if (skipped.Count > 0)
        {
            logger.Warn($".env: {skipped.Count} key(s) left blank: {string.Join(", ", skipped)}");
        }
    }

    /// <summary>Reads <paramref name="envPath"/>, applies <paramref name="replacements"/> in
    /// place, and returns (rewrittenCount, unmatchedBlankKeys). Internal so unit tests can drive
    /// the comment-pair logic directly against an in-memory .env.</summary>
    internal static (int Rewritten, IReadOnlyList<string> Skipped) ApplyReplacementsToEnvFile(
        string envPath, EnvReplacements replacements)
    {
        var lines = File.ReadAllLines(envPath);
        string? pendingBindMountKey = null;
        var rewritten = 0;
        var skipped = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Comment ↔ following KEY= line pair.
            if (line.StartsWith("# Bind mount source for ", StringComparison.Ordinal))
            {
                pendingBindMountKey = line["# Bind mount source for ".Length..].Trim();
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                pendingBindMountKey = null;
                continue;
            }

            var key = line[..eq];
            string? value = null;

            if (pendingBindMountKey is not null && replacements.BindMounts.TryGetValue(pendingBindMountKey, out var src))
            {
                value = src;
            }
            else if (replacements.Parameters.TryGetValue(key, out var paramVal))
            {
                value = paramVal;
            }

            if (value is not null)
            {
                lines[i] = $"{key}={value}";
                rewritten++;
            }
            else if (line.EndsWith('='))
            {
                // Unrecognised blank RHS; operator may need to fix it before `docker compose up`.
                skipped.Add(key);
            }

            pendingBindMountKey = null;
        }

        File.WriteAllLines(envPath, lines);
        return (rewritten, skipped);
    }

    /// <summary>Inserts <c>pull_policy: never</c> after <c>image: "${CASSANDRA_IMAGE}"</c> on
    /// the cassandra service; idempotent. Uses the placeholder token as the anchor (unique to
    /// the cassandra service in Aspire's <c>AddDockerfile("cassandra", ...)</c> emission) so we
    /// never stamp another service. Targeted find+insert avoids a full YAML round-trip that
    /// could reorder Aspire's other keys or reflow compose anchors.</summary>
    internal static void StampCassandraPullPolicyNever(string composePath, PhaseLogger? logger = null)
    {
        var lines = File.ReadAllLines(composePath).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (!line.Contains("${CASSANDRA_IMAGE}", StringComparison.Ordinal)) continue;

            // Idempotency guard so re-runs of `bootstrap publish` don't double-stamp.
            var next = lines.Skip(i + 1).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
            if (string.Equals(next?.Trim(), "pull_policy: never", StringComparison.Ordinal))
            {
                logger?.Info("    compose: cassandra service already has pull_policy: never");
                return;
            }

            var indent = line[..(line.Length - line.TrimStart().Length)];
            lines.Insert(i + 1, $"{indent}pull_policy: never");
            File.WriteAllLines(composePath, lines);
            logger?.Info("    compose: stamped pull_policy: never on cassandra service");
            return;
        }
        // No cassandra service in the compose file — older cassandra-mode setups that skip
        // AddDockerfile don't need the stamp. No-op rather than throw.
        logger?.Warn("compose: no ${CASSANDRA_IMAGE} anchor found; pull_policy stamp skipped");
    }

    private static string SetupAnchor(string outputDir)
    {
        var anchor = Path.Combine(outputDir, Path.Combine(AnchorSegments));
        Directory.CreateDirectory(anchor);
        return anchor;
    }

    private static async Task PublishInProcessAsync(
        BootstrapOptions options,
        BootstrapConfig config,
        GeneratedSecrets secrets,
        PhaseLogger logger,
        CancellationToken ct)
    {
        // Aspire 13.x publish requires `--operation publish --step publish`. Without --step
        // the AppHost emits only aspire-manifest.json then blocks on the CLI backchannel
        // (we're not under the CLI). The docker-compose publisher is selected by the
        // AddDockerComposeEnvironment registration in InterfoldAppHost.Configure, not by --publisher.
        var publishArgs = new[]
        {
            "--operation", "publish",
            "--step", "publish",
            "--output-path", options.OutputDir,
        };

        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = publishArgs,
            DisableDashboard = true,
        });

        var (includeScylla, includeCassandra, scyllaTopology) =
            TranslateDatabaseMode(CqlBackendMapping.ToDatabaseMode(config.Datastores.Cql.Backend));

        // Parameters:* injected via IConfiguration; Aspire writes secret parameter values into
        // the .env file rather than the compose YAML at publish time. Seeded from the shared
        // enumerator; extras below are graph-only knobs (topology, image, ports, cluster name)
        // that never round-trip through .env.
        var injected = EnumerateSharedAspireParameters(config, secrets)
            .ToDictionary(p => p.ConfigKey, p => (string?)p.Value, StringComparer.Ordinal);

        // ClusterName is IConfiguration-read (like include-scylla / scylla-topology) rather than
        // an Aspire Parameter — it also feeds Scylla's WithArgs list and that overload takes
        // plain strings. Baked into compose as both CASSANDRA_CLUSTER_NAME and --cluster-name.
        injected[AppHostParameterKeys.ClusterName] = config.Datastores.Cql.ClusterName;
        injected[AppHostParameterKeys.IncludeScylla] = BoolWire.ToWireValue(includeScylla);
        injected[AppHostParameterKeys.IncludeCassandra] = BoolWire.ToWireValue(includeCassandra);
        injected[AppHostParameterKeys.ScyllaTopology] = scyllaTopology.ToWireValue();
        // Bootstrapper always uses a pre-built API image; this switches off the AddProject<> path.
        injected[AppHostParameterKeys.ApiImage] = config.Api.Image;
        // Aspire dev dashboard would pull an MCR-nightly image at compose-up — unwanted in prod.
        injected[AppHostParameterKeys.IncludeDashboard] = BoolWire.FalseValue;
        injected[AppHostParameterKeys.IncludeWeb] = BoolWire.ToWireValue(config.Deployment.IncludeWeb);
        injected[AppHostParameterKeys.EdgeTlsMode] = config.Edge.TlsMode.ToWire();
        injected[AppHostParameterKeys.EdgeRouting] = config.Edge.Routing.Mode.ToWire();
        injected[AppHostParameterKeys.EdgeApiHost] = config.Edge.Routing.ApiHost;
        injected[AppHostParameterKeys.EdgeWebHost] = config.Edge.Routing.WebHost;
        injected[AppHostParameterKeys.EdgeCloudflareAllowlist] =
            BoolWire.ToWireValue(config.Edge.Cloudflare.IpAllowlist);
        injected[AppHostParameterKeys.EdgeIncludeWebUpstream] =
            BoolWire.ToWireValue(config.Deployment.IncludeWeb);
        injected[AppHostParameterKeys.EdgeServerName] = PickServerName(config.Edge.Hosts);
        injected[AppHostParameterKeys.PortsEdgeHttp] = config.Edge.Ports.Http.ToString();
        injected[AppHostParameterKeys.PortsEdgeHttps] = config.Edge.Ports.Https.ToString();

        builder.Configuration.AddInMemoryCollection(injected);

        InterfoldAppHost.Configure(builder);

        logger.Info("    invoking Aspire publish...");
        await using var app = builder.Build();
        await app.RunAsync(ct).ConfigureAwait(false);
    }

    /// <summary>First parseable, non-CIDR entry wins; falls back to nginx's <c>_</c> catch-all
    /// for the bypass-validation dev path (<see cref="ConfigPhase.Validate"/> otherwise rejects
    /// an all-CIDR/empty list before this runs).</summary>
    internal static string PickServerName(IReadOnlyList<string> hosts)
    {
        foreach (var raw in hosts)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            HostEntry entry;
            try
            {
                entry = HostParser.Parse(raw);
            }
            catch (FormatException)
            {
                continue;
            }
            if (!entry.IsLeafEligible) continue;
            return entry.Kind switch
            {
                HostKind.Dns => entry.DnsName!,
                _ => entry.Ip!.ToString(),
            };
        }
        return "_";
    }
}
