using Interfold.AppHost;
using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Configuration;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>Sub-second drift check on <see cref="PublishPhase.BuildEnvReplacements"/>. Integration
/// tests cover the emitted file end-to-end; these lock the pure key set.</summary>
public sealed class PublishEnvPostProcessingTests
{
    private static (BootstrapConfig Config, GeneratedSecrets Secrets) MakeInputs(
        string? apiImage = null,
        PersistenceMode persistence = PersistenceMode.Sqlite)
    {
        var config = new BootstrapConfig
        {
            Api = { Image = apiImage ?? DefaultContainerImages.Api },
            Datastores =
            {
                Persistence = persistence,
            },
        };
        // Edge.Hosts has no placeholder; without a seed ResolveDerivedDefaults has
        // nothing to feed CallbackBaseUrl / JwtAuthority / CorsAllowedOrigins from.
        config.Edge.Hosts = ["api.example.com"];
        // OAuth client secrets flow into internal.secrets, not env; setting exercises the
        // irrelevant path.
        config.Api.OAuth.GoogleClientSecret = "google-secret-from-config";
        config.Api.OAuth.DiscordClientSecret = "discord-secret-from-config";

        // Mirror ConfigPhase.Validate's post-derivation snapshot without invoking the full
        // Validate (publish tests deliberately allow shapes Validate would reject).
        ConfigPhase.ResolveDerivedDefaults(config);
        return (config, SecretsPhase.Generate());
    }

    [Test]
    public async Task BuildEnvReplacementsProducesAllRequiredParameterKeys()
    {
        var (config, secrets) = MakeInputs();
        // Pin all three OAuth IDs so IsNotEmpty is meaningful; empty-still-emits behaviour
        // has its own test below.
        config.Api.OAuth.GoogleClientId = "google-client-id";
        config.Api.OAuth.DiscordClientId = "discord-client-id";
        config.Api.OAuth.AppleClientId = "apple-client-id";
        // Pin the three nullable tuning fields that still flow as Aspire parameters.
        // AvatarStorageRoot is a host bind-mount path (not an env parameter); see
        // BindMountPathsResolveToAbsoluteUnderOutputDir / ResolveAvatarHostRoot.
        config.Api.Storage.AvatarStorageRoot = "/var/lib/interfold/avatars";
        config.Api.Storage.AvatarPublicBase = "https://cdn.example.com/avatars/";
        config.Observability.OtlpEndpoint = "http://localhost:4317";
        config.Observability.ClientOtlpHttpEndpoint = "http://localhost:4318";
        config.Api.BatchBytesThreshold = 65536;
        const string baseDir = "/var/lib/interfold";
        const string outputDir = "/srv/interfold/deploy";

        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, baseDir, outputDir);

        string[] required =
        [
            "ENCRYPTION_PRIVATE_KEY",
            "GOOGLE_OAUTH_CLIENT_ID",
            "DISCORD_OAUTH_CLIENT_ID",
            "APPLE_OAUTH_CLIENT_ID",
            "OAUTH_CALLBACK_BASE_URL",
            "JWT_AUTHORITY",
            "JWT_AUDIENCE",
            "CORS_ALLOWED_ORIGINS",
            "NODE_GROUP",
            "AVATAR_PUBLIC_BASE",
            "OTLP_ENDPOINT",
            "ADVERTISE_OTLP_TO_CLIENTS",
            "CLIENT_OTLP_HTTP_ENDPOINT",
            "SOCKET_BATCH_BYTES_THRESHOLD",
            "DB_RETRY_ATTEMPTS",
            "DB_RETRY_INITIAL_DELAY_MS",
            "DB_RETRY_MAX_DELAY_MS",
            "HYDRATION_MAX_CONCURRENCY",
            "CF_ACCESS_TEAM_DOMAIN",
            "CF_ACCESS_AUD",
            "CF_ACCESS_DISCORD_IDP_ID",
        ];
        foreach (var key in required)
        {
            await Assert.That(replacements.Parameters.ContainsKey(key)).IsTrue()
                .Because($"missing parameter key '{key}' in env replacements");
            if (!key.StartsWith("CF_ACCESS_", StringComparison.Ordinal))
            {
                await Assert.That(replacements.Parameters[key]).IsNotEmpty()
                    .Because($"parameter '{key}' must be non-empty");
            }
        }

        // The encryption pepper, OAuth client secrets, JWT material, deep-link secret, and
        // the leaf PFX password must NOT appear here any more — they all live inside
        // internal.secrets and are loaded at startup (SecretsBootstrapService / Program.cs
        // Kestrel loader).
        await Assert.That(replacements.Parameters.ContainsKey("ENCRYPTION_PEPPER")).IsFalse();
        await Assert.That(replacements.Parameters.ContainsKey("GOOGLE_OAUTH_CLIENT_SECRET")).IsFalse();
        await Assert.That(replacements.Parameters.ContainsKey("DISCORD_OAUTH_CLIENT_SECRET")).IsFalse();
        await Assert.That(replacements.Parameters.ContainsKey("APPLE_OAUTH_CLIENT_SECRET")).IsFalse();
        await Assert.That(replacements.Parameters.ContainsKey("LEAF_PFX_PASSWORD")).IsFalse();
    }

    [Test]
    public async Task BuildEnvReplacementsCarriesOAuthClientIdsFromConfig()
    {
        // Contract: OAuth client IDs copy verbatim into the env dict; a rename either side
        // shows up as a missing key or value mismatch here.
        var (config, secrets) = MakeInputs();
        config.Api.OAuth.GoogleClientId = "google-client-from-config.apps.googleusercontent.com";
        config.Api.OAuth.DiscordClientId = "1234567890";
        config.Api.OAuth.AppleClientId = "com.example.interfold.signin";

        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, "/base", "/out");

        await Assert.That(replacements.Parameters["GOOGLE_OAUTH_CLIENT_ID"])
            .IsEqualTo("google-client-from-config.apps.googleusercontent.com");
        await Assert.That(replacements.Parameters["DISCORD_OAUTH_CLIENT_ID"]).IsEqualTo("1234567890");
        await Assert.That(replacements.Parameters["APPLE_OAUTH_CLIENT_ID"])
            .IsEqualTo("com.example.interfold.signin");
    }

    [Test]
    public async Task BuildEnvReplacementsEmitsEmptyOAuthClientIdsWhenNotConfigured()
    {
        // Empty client IDs mean "provider disabled"; keys MUST still emit as `KEY=` so
        // ApplyReplacementsToEnvFile doesn't warn on unfilled blanks.
        var (config, secrets) = MakeInputs();

        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, "/base", "/out");

        await Assert.That(replacements.Parameters.ContainsKey("GOOGLE_OAUTH_CLIENT_ID")).IsTrue();
        await Assert.That(replacements.Parameters.ContainsKey("DISCORD_OAUTH_CLIENT_ID")).IsTrue();
        await Assert.That(replacements.Parameters.ContainsKey("APPLE_OAUTH_CLIENT_ID")).IsTrue();
        await Assert.That(replacements.Parameters["GOOGLE_OAUTH_CLIENT_ID"]).IsEqualTo(string.Empty);
        await Assert.That(replacements.Parameters["DISCORD_OAUTH_CLIENT_ID"]).IsEqualTo(string.Empty);
        await Assert.That(replacements.Parameters["APPLE_OAUTH_CLIENT_ID"]).IsEqualTo(string.Empty);
    }




    [Test]
    public async Task BuildEnvReplacementsReadsCloudflareAccessState()
    {
        using var scratch = TestSupport.NewScratchDir("interfold-cf-access-env");
        var (config, secrets) = MakeInputs();
        Directory.CreateDirectory(scratch.Path);
        await File.WriteAllTextAsync(
            Path.Combine(scratch.Path, ".cloudflare-access.json"),
            """{"teamDomain":"team.cloudflareaccess.com","aud":"aud-from-state","identityProviderId":"idp-1","discordIdentityProviderId":"idp-discord","appIds":{}}""");

        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, "/base", scratch.Path);

        await Assert.That(replacements.Parameters["CF_ACCESS_TEAM_DOMAIN"]).IsEqualTo("team.cloudflareaccess.com");
        await Assert.That(replacements.Parameters["CF_ACCESS_AUD"]).IsEqualTo("aud-from-state");
        await Assert.That(replacements.Parameters["CF_ACCESS_DISCORD_IDP_ID"]).IsEqualTo("idp-discord");
        await Assert.That(replacements.Parameters["CORS_ALLOWED_ORIGINS"])
            .IsEqualTo("https://api.example.com,https://team.cloudflareaccess.com");

        config.Api.CorsAllowedOrigins.Add("https://team.cloudflareaccess.com/");
        var again = PublishPhase.BuildEnvReplacements(config, secrets, "/base", scratch.Path);
        await Assert.That(again.Parameters["CORS_ALLOWED_ORIGINS"])
            .IsEqualTo("https://api.example.com,https://team.cloudflareaccess.com/");
    }

    [Test]
    public async Task BuildEnvReplacementsDerivesApiRuntimeFromDeploymentWhenUnset()
    {
        var config = new BootstrapConfig
        {
            Edge =
            {
                Hosts = ["api.example.com", "admin.example.com"],
                TlsMode = EdgeTlsMode.PrivateCa,
            },
        };
        config.Edge.Ports.Https = 443;
        ConfigPhase.ResolveDerivedDefaults(config);
        var secrets = SecretsPhase.Generate();

        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, "/base", "/out");

        await Assert.That(replacements.Parameters["OAUTH_CALLBACK_BASE_URL"])
            .IsEqualTo("https://api.example.com");
        await Assert.That(replacements.Parameters["JWT_AUTHORITY"])
            .IsEqualTo("https://api.example.com");
        await Assert.That(replacements.Parameters["JWT_AUDIENCE"]).IsEqualTo("octocon");
        await Assert.That(replacements.Parameters["CORS_ALLOWED_ORIGINS"])
            .IsEqualTo("https://api.example.com,https://admin.example.com");
    }

    [Test]
    public async Task BuildEnvReplacementsCarriesTuningFromConfig()
    {
        // Every operator tuning field must round-trip verbatim.
        var (config, secrets) = MakeInputs();
        config.Api.NodeGroup = NodeGroup.Primary;
        config.Api.Storage.AvatarStorageRoot = "/srv/avatars";
        config.Api.Storage.AvatarPublicBase = "https://cdn.example.com/a/";
        config.Observability.OtlpEndpoint = "http://otel-collector:4317";
        config.Observability.AdvertiseOtlpToClients = true;
        config.Observability.ClientOtlpHttpEndpoint = "http://otel-collector:4318";
        config.Api.BatchBytesThreshold = 131_072;
        config.Api.Resilience.DbRetryAttempts = 5;
        config.Api.Resilience.DbRetryInitialDelayMs = 250;
        config.Api.Resilience.DbRetryMaxDelayMs = 3_000;
        config.Api.Resilience.HydrationMaxConcurrency = 16;

        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, "/base", "/out");

        await Assert.That(replacements.Parameters["NODE_GROUP"]).IsEqualTo("primary");
        await Assert.That(replacements.Parameters.ContainsKey("AVATAR_STORAGE_ROOT")).IsFalse()
            .Because("Avatar host path is a bind-mount source, not an Aspire parameter");
        await Assert.That(replacements.BindMounts["interfold-api:/app/data/avatars"]).IsEqualTo(
            Path.GetFullPath("/srv/avatars"));
        await Assert.That(replacements.Parameters["AVATAR_PUBLIC_BASE"])
            .IsEqualTo("https://cdn.example.com/a/");
        await Assert.That(replacements.Parameters["OTLP_ENDPOINT"])
            .IsEqualTo("http://otel-collector:4317");
        await Assert.That(replacements.Parameters["ADVERTISE_OTLP_TO_CLIENTS"]).IsEqualTo("true");
        await Assert.That(replacements.Parameters["CLIENT_OTLP_HTTP_ENDPOINT"])
            .IsEqualTo("http://otel-collector:4318");
        await Assert.That(replacements.Parameters["SOCKET_BATCH_BYTES_THRESHOLD"]).IsEqualTo("131072");
        await Assert.That(replacements.Parameters["DB_RETRY_ATTEMPTS"]).IsEqualTo("5");
        await Assert.That(replacements.Parameters["DB_RETRY_INITIAL_DELAY_MS"]).IsEqualTo("250");
        await Assert.That(replacements.Parameters["DB_RETRY_MAX_DELAY_MS"]).IsEqualTo("3000");
        await Assert.That(replacements.Parameters["HYDRATION_MAX_CONCURRENCY"]).IsEqualTo("16");
    }

    [Test]
    public async Task BuildEnvReplacementsDefaultsAvatarBindMountUnderOutputDir()
    {
        // Blank avatarStorageRoot → {outputDir}/data/avatars bind-mounted at /app/data/avatars.
        var (config, secrets) = MakeInputs();
        await Assert.That(config.Api.Storage.AvatarStorageRoot).IsEqualTo(string.Empty)
            .Because("Pre-condition: this test only makes sense when the config-side default is blank.");

        var outputDir = Path.Combine(Path.GetTempPath(), "interfold-avatar-default");
        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, "/base", outputDir);

        await Assert.That(replacements.Parameters.ContainsKey("AVATAR_STORAGE_ROOT")).IsFalse()
            .Because("Container path is baked into compose via WithEnvironment; host path is the bind mount.");
        var expected = Path.GetFullPath(Path.Combine(outputDir, "data", "avatars"));
        await Assert.That(replacements.BindMounts["interfold-api:/app/data/avatars"]).IsEqualTo(expected);
    }

    [Test]
    public async Task BuildEnvReplacementsEmitsEmptyTuningSlotsForNullables()
    {
        // "Disabled when empty" tuning fields must emit `KEY=` (not be omitted); API binders
        // normalise the empty vars back to null so the not-configured branches still fire.
        var (config, secrets) = MakeInputs();

        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, "/base", "/out");

        await Assert.That(replacements.Parameters.ContainsKey("AVATAR_STORAGE_ROOT")).IsFalse();
        await Assert.That(replacements.Parameters.ContainsKey("AVATAR_PUBLIC_BASE")).IsTrue();
        await Assert.That(replacements.Parameters.ContainsKey("OTLP_ENDPOINT")).IsTrue();
        await Assert.That(replacements.Parameters.ContainsKey("ADVERTISE_OTLP_TO_CLIENTS")).IsTrue();
        await Assert.That(replacements.Parameters.ContainsKey("CLIENT_OTLP_HTTP_ENDPOINT")).IsTrue();
        await Assert.That(replacements.Parameters.ContainsKey("SOCKET_BATCH_BYTES_THRESHOLD")).IsTrue();
        await Assert.That(replacements.Parameters["AVATAR_PUBLIC_BASE"]).IsEqualTo(string.Empty);
        await Assert.That(replacements.Parameters["OTLP_ENDPOINT"]).IsEqualTo(string.Empty);
        await Assert.That(replacements.Parameters["ADVERTISE_OTLP_TO_CLIENTS"]).IsEqualTo("false");
        await Assert.That(replacements.Parameters["CLIENT_OTLP_HTTP_ENDPOINT"]).IsEqualTo(string.Empty);
        await Assert.That(replacements.Parameters["SOCKET_BATCH_BYTES_THRESHOLD"]).IsEqualTo(string.Empty);

        // Non-nullable tuning fields fall back to their property-initialiser defaults.
        await Assert.That(replacements.Parameters["NODE_GROUP"]).IsEqualTo("auxiliary");
        await Assert.That(replacements.Parameters["DB_RETRY_ATTEMPTS"]).IsEqualTo("3");
        await Assert.That(replacements.Parameters["DB_RETRY_INITIAL_DELAY_MS"]).IsEqualTo("100");
        await Assert.That(replacements.Parameters["DB_RETRY_MAX_DELAY_MS"]).IsEqualTo("1500");
        await Assert.That(replacements.Parameters["HYDRATION_MAX_CONCURRENCY"]).IsEqualTo("8");
    }


    [Test]
    public async Task SqliteModeAddsApiSqliteDataBindMount()
    {
        var (config, secrets) = MakeInputs(persistence: PersistenceMode.Sqlite);
        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, "/base", "/out/deploy");

        var sqliteKey = $"{ComposeServices.InterfoldApi}:{ContainerMountPaths.InterfoldSqliteData}";
        await Assert.That(replacements.BindMounts.ContainsKey(sqliteKey)).IsTrue();
        await Assert.That(replacements.BindMounts[sqliteKey])
            .IsEqualTo(PublishPhase.ResolveSqliteDataHostDir("/out/deploy"));

    }


    [Test]
    public async Task AlwaysOnEdgeAddsEdgeNginxBindMounts()
    {
        var (config, secrets) = MakeInputs();
        config.Deployment.IncludeWeb = true;
        var outputDir = Path.Combine(Path.GetTempPath(), "interfold-edge-outdir-" + Guid.NewGuid().ToString("N"));

        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, "/base", outputDir);

        await Assert.That(replacements.BindMounts.ContainsKey(
            "edge-nginx:/etc/nginx/templates/default.conf.template")).IsTrue();
        await Assert.That(replacements.BindMounts.ContainsKey(
            "edge-nginx:/etc/nginx/proxy_params_interfold.conf")).IsTrue();
        await Assert.That(replacements.BindMounts.ContainsKey(
            "edge-nginx:/etc/nginx/cloudflare-ips.conf")).IsFalse();
        await Assert.That(replacements.BindMounts.ContainsKey("edge-nginx:/certs")).IsTrue();
        await Assert.That(replacements.BindMounts.ContainsKey("interfold-api:/certs")).IsTrue()
            .Because("privateCa edge still mounts certs on the API for TrustController");
    }

    [Test]
    public async Task CloudflareTunnelAddsCloudflaredTokenMountAndOmitsCerts()
    {
        var (config, secrets) = MakeInputs();
        config.Edge.Cloudflare.Enabled = true;
        config.Edge.Cloudflare.ApiToken = "token";
        config.Edge.TlsMode = EdgeTlsMode.None;
        var outputDir = Path.Combine(Path.GetTempPath(), "interfold-edge-tunnel-" + Guid.NewGuid().ToString("N"));

        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, "/base", outputDir);

        await Assert.That(replacements.BindMounts.ContainsKey(
            $"cloudflared:{EdgePaths.ContainerCloudflareTunnelToken}")).IsTrue();
        await Assert.That(replacements.BindMounts.ContainsKey("edge-nginx:/certs")).IsFalse();
        await Assert.That(replacements.BindMounts.ContainsKey("interfold-api:/certs")).IsFalse();
    }

    [Test]
    public async Task PlaintextEdgeOmitsCertBindMounts()
    {
        var (config, secrets) = MakeInputs();
        config.Edge.TlsMode = EdgeTlsMode.None;
        var outputDir = Path.Combine(Path.GetTempPath(), "interfold-edge-plain-" + Guid.NewGuid().ToString("N"));

        var replacements = PublishPhase.BuildEnvReplacements(config, secrets, "/base", outputDir);

        await Assert.That(replacements.BindMounts.ContainsKey("edge-nginx:/certs")).IsFalse();
        await Assert.That(replacements.BindMounts.ContainsKey("interfold-api:/certs")).IsFalse();
    }

    [Test]
    public async Task ApiImageOverrideDoesNotLeakIntoEnvReplacements()
    {
        // ApiImage flows via Aspire Parameters:api-image into compose YAML, not .env.
        var (configA, secretsA) = MakeInputs(apiImage: "ghcr.io/azyyyyyy/interfold-api:v1.2.3");
        var (configB, secretsB) = MakeInputs(apiImage: "private-registry.example.com/api:custom-tag");
        secretsB.EncryptionPrivateKeyB64 = secretsA.EncryptionPrivateKeyB64;

        var a = PublishPhase.BuildEnvReplacements(configA, secretsA, "/base", "/out");
        var b = PublishPhase.BuildEnvReplacements(configB, secretsB, "/base", "/out");

        await Assert.That(a.Parameters.Count).IsEqualTo(b.Parameters.Count);
        foreach (var kv in a.Parameters)
        {
            await Assert.That(b.Parameters.ContainsKey(kv.Key)).IsTrue();
            await Assert.That(b.Parameters[kv.Key]).IsEqualTo(kv.Value);
        }

        foreach (var key in a.Parameters.Keys)
        {
            await Assert.That(key.Contains("IMAGE", StringComparison.OrdinalIgnoreCase)).IsFalse()
                .Because($"unexpected image-related key '{key}' leaked into env replacements");
        }
    }

    [Test]
    public async Task WebImageOverrideDoesNotLeakIntoEnvReplacements()
    {
        var (configA, secretsA) = MakeInputs();
        configA.Deployment.WebImage = "ghcr.io/azyyyyyy/interfold-web:v1.2.3";
        var (configB, secretsB) = MakeInputs();
        configB.Deployment.WebImage = "private-registry.example.com/web:custom-tag";
        secretsB.EncryptionPrivateKeyB64 = secretsA.EncryptionPrivateKeyB64;

        var a = PublishPhase.BuildEnvReplacements(configA, secretsA, "/base", "/out");
        var b = PublishPhase.BuildEnvReplacements(configB, secretsB, "/base", "/out");

        await Assert.That(a.Parameters.Count).IsEqualTo(b.Parameters.Count);
        foreach (var kv in a.Parameters)
        {
            await Assert.That(b.Parameters.ContainsKey(kv.Key)).IsTrue();
            await Assert.That(b.Parameters[kv.Key]).IsEqualTo(kv.Value);
        }

        foreach (var key in a.Parameters.Keys)
        {
            await Assert.That(key.Contains("IMAGE", StringComparison.OrdinalIgnoreCase)).IsFalse()
                .Because($"unexpected image-related key '{key}' leaked into env replacements");
        }
    }

    [Test]
    public async Task WebServerNameSkipsCidrEntries()
    {
        // nginx server_name accepts DNS and IPs but not CIDRs; PickServerName must skip.
        var serverName = PublishPhase.PickServerName(["192.168.1.0/24", "api.example.com"]);
        await Assert.That(serverName).IsEqualTo("api.example.com");
    }

    [Test]
    public async Task WebServerNameFallsBackToCatchAllWhenAllCidr()
    {
        // Direct InterfoldAppHost.Configure callers skip Validate; `_` is nginx's catch-all.
        var serverName = PublishPhase.PickServerName(["10.0.0.0/8", "fe80::/64"]);
        await Assert.That(serverName).IsEqualTo("_");
    }

    [Test]
    public async Task WebServerNamePicksIpLiteralAsServerName()
    {
        // LAN-only deployments (no DNS) get the bare IP; nginx accepts dotted-quad + v6.
        var serverName = PublishPhase.PickServerName(["192.168.1.42"]);
        await Assert.That(serverName).IsEqualTo("192.168.1.42");
    }


    [Test]
    public async Task EnumerateSharedAspireParametersConfigKeyMatchesEnvKeyKebabToUpperSnake()
    {
        // EnvKey must equal upper-snake(kebab parameter name); a hand-typed EnvKey would
        // otherwise silently blank the corresponding OCTOCON_* env var.
        var (config, secrets) = MakeInputs();

        var seenConfigKeys = new HashSet<string>(StringComparer.Ordinal);
        var seenEnvKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (configKey, envKey, _) in PublishPhase.EnumerateSharedAspireParameters(config, secrets))
        {
            await Assert.That(seenConfigKeys.Add(configKey)).IsTrue()
                .Because($"config-key '{configKey}' appears twice in EnumerateSharedAspireParameters");
            await Assert.That(seenEnvKeys.Add(envKey)).IsTrue()
                .Because($"env-key '{envKey}' appears twice in EnumerateSharedAspireParameters");

            // ToParameterName also enforces the "Parameters:*" namespace by throwing for
            // graph-only Ports:* entries.
            var bareName = AppHostParameterKeys.ToParameterName(configKey);
            var expectedEnvKey = bareName.Replace('-', '_').ToUpperInvariant();
            await Assert.That(envKey).IsEqualTo(expectedEnvKey)
                .Because($"env-key '{envKey}' must be the upper-snake-cased form of the kebab-cased Aspire parameter '{bareName}' (config-key '{configKey}')");
        }

        // Spec-frozen at 21 — bump this AND the enumerator together.
        await Assert.That(seenConfigKeys.Count).IsEqualTo(21)
            .Because("shared-parameter count is spec-frozen at 21; update BOTH the enumerator AND this assertion together");
    }
}
