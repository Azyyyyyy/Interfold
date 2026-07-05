using Interfold.Contracts;
using Interfold.Contracts.Configuration;
using Interfold.Contracts.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.DependencyInjection;

/// <summary>
/// Extension methods for registering strongly-typed configuration from environment variables.
/// Uses .NET configuration binding with custom providers to ensure compatibility with
/// existing OCTOCON_* and platform-specific (FLY_*) environment variables.
/// </summary>
public static class ConfigurationServiceCollectionExtensions
{
    /// <summary>
    /// Registers all Interfold configuration classes with the DI container using the
    /// <see cref="IOptions{TOptions}"/> / <see cref="IOptionsMonitor{TOptions}"/> pattern.
    /// <para>
    /// Call this once in Program.cs instead of manually binding and registering singletons.
    /// Configuration is read from the live <see cref="IConfiguration"/> on each access, so
    /// values backed by <c>appsettings.json</c> (with <c>reloadOnChange: true</c>) will update
    /// without a restart when consumed via <see cref="IOptionsMonitor{TOptions}"/>.
    /// Environment-variable-backed values are fixed at process start.
    /// </para>
    /// Usage in services:
    /// <list type="bullet">
    ///   <item><see cref="IOptions{TOptions}"/> — startup-only, single snapshot (Persistence, Cluster)</item>
    ///   <item><see cref="IOptionsMonitor{TOptions}"/> — live reload, safe for singletons (Auth, Storage, Socket)</item>
    ///   <item><see cref="IOptionsSnapshot{TOptions}"/> — per-request reload, safe for scoped services</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddInterfoldOptions(this IServiceCollection services)
    {
        // Startup-only: node role cannot change while the process is running.
        services.AddOptions<ClusterConfiguration>()
            .Configure<IConfiguration>(ApplyCluster)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Startup-only: database connection pools are created once; reconnection requires restart.
        // Validate ranges + the cross-field max >= initial constraint (IValidatableObject on
        // PersistenceConfiguration) at DI-container build time so malformed retry knobs or
        // an empty Postgres connection string fail with a clear boot-time error instead of
        // manifesting as a mysterious first-query hang.
        services.AddOptions<PersistenceConfiguration>()
            .Configure<IConfiguration>(ApplyPersistence)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Startup-baked: AuthenticationConfiguration is a hybrid of env-bound values (OAuth
        // client IDs, callback base URL, JWT authority) and secret-store-bound values
        // (RSA/ES256 signing material, deep-link secret, encryption pepper, OAuth client
        // secrets). SecretsBootstrapService patches the secret fields directly into the
        // IOptionsMonitor.CurrentValue snapshot at startup. Wiring an
        // IOptionsChangeTokenSource here would cause every IConfiguration reload to
        // re-run ApplyAuthentication and overwrite the patched secrets with the empty
        // initial values, breaking JWT verification and the encryption pepper guard.
        // Treat auth as startup-only until the secret bootstrap moves to
        // IPostConfigureOptions or a dedicated reload-aware patcher.
        services.AddOptions<AuthenticationConfiguration>()
            .Configure<IConfiguration>(ApplyAuthentication);

        // Startup-only: FirebaseClientConfiguration is populated entirely from
        // internal.secrets (firebase:client:{android,ios,web}) by SecretsBootstrapService
        // ahead of any request-time consumer. The initial ApplyFirebaseClient callback
        // just leaves the platform variants null so a bad seed manifests as a 503 rather
        // than as a config-binding error; wiring an IOptionsChangeTokenSource here would
        // clobber the patched values on any IConfiguration reload (see the equivalent
        // AuthenticationConfiguration note above).
        services.AddOptions<FirebaseClientConfiguration>()
            .Configure<IConfiguration>(ApplyFirebaseClient);

        // Registered for completeness; OTLP exporters are wired at startup so runtime changes
        // to OtlpEndpoint only take effect after a restart. ValidateOnStart runs
        // [AbsoluteHttpUri] on the (optional) endpoint so a garbled env var trips at boot,
        // not on the first exporter connection attempt.
        services.AddOptions<ObservabilityConfiguration>()
            .Configure<IConfiguration>(ApplyObservability)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Trust-distribution paths read by TrustController. The values are filesystem paths
        // pointing into the read-only /certs bind mount; they change only on
        // bootstrap --rotate-certs (which restarts the API container), so an
        // IOptions<T> snapshot taken at startup is correct. Validation is opted-in here so
        // an operator error like a relative path lands as a boot failure with the offending
        // env var named — matches the strictness the bootstrapper's config gate applies on
        // config.trust.rootCaPath / rootCaFingerprintPath.
        services.AddOptions<TrustOptions>()
            .Configure<IConfiguration>(ApplyTrust)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Live-reloadable: avatar storage paths can be updated via appsettings.json.
        // ValidateOnStart still fires at the initial bind — reloads are eventual-consistency,
        // not fail-fast (see the live-reload gotcha in the Slice 3 plan).
        AddLiveReloadable<StorageConfiguration>(services, ApplyStorage)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Live-reloadable: batch tuning can be adjusted without restart.
        AddLiveReloadable<SocketConfiguration>(services, ApplySocket)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Startup-only: CORS origins baked into the CorsPolicy at builder-time. Live-reload
        // would require rebuilding the CorsPolicy, which ASP.NET Core's default
        // CorsPolicyProvider does not do. Per-entry [AbsoluteHttpUri] validation lives on
        // the options class (IValidatableObject) so an operator that pushes a non-http
        // origin trips at boot with the offending entry called out.
        services.AddOptions<CorsOptions>()
            .Configure<IConfiguration>(ApplyCors)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Startup-only: Scylla host-port + contact-point overrides for integration tests.
        // Production stacks leave both null and the client falls through to the secrets
        // store. No numeric-range validation on the port here — ScyllaConfigResolver's
        // consumer surfaces a friendlier "no override, fall back" branch that we don't
        // want fail-fast validation to short-circuit.
        services.AddOptions<ScyllaOverrideOptions>()
            .Configure<IConfiguration>(ApplyScyllaOverride)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Startup-only: in-memory secrets seed. Blank values are legal and skipped by the
        // consumer (SecretsBootstrapService is the sole fail-fast for the mandatory rows).
        services.AddOptions<InMemorySecretsSeedOptions>()
            .Configure<IConfiguration>(ApplyInMemorySecretsSeed)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return services;
    }

    /// <summary>
    /// Registers a live-reloadable strongly-typed options instance. Calling
    /// <c>AddOptions&lt;T&gt;().Configure&lt;IConfiguration&gt;(...)</c> on its own only registers an
    /// <see cref="IConfigureOptions{TOptions}"/> — that wires the apply callback into the snapshot
    /// pipeline, but it does NOT subscribe <see cref="IOptionsMonitor{TOptions}"/> to configuration
    /// reload events. Without an <see cref="IOptionsChangeTokenSource{TOptions}"/> the first access
    /// to <c>CurrentValue</c> caches whatever the apply callback produced and ignores subsequent
    /// <see cref="IConfigurationRoot.Reload"/> calls. <see cref="ConfigurationChangeTokenSource{TOptions}"/>
    /// bridges the configuration's reload token into the options pipeline so consumers actually see
    /// updates, which is what the integration tests' <c>WithConfiguration</c> live-reload contract
    /// depends on.
    /// </summary>
    private static OptionsBuilder<TOptions> AddLiveReloadable<TOptions>(
        IServiceCollection services,
        Action<TOptions, IConfiguration> apply)
        where TOptions : class
    {
        var builder = services.AddOptions<TOptions>().Configure<IConfiguration>(apply);
        services.AddSingleton<IOptionsChangeTokenSource<TOptions>>(sp =>
            new ConfigurationChangeTokenSource<TOptions>(
                Options.DefaultName, sp.GetRequiredService<IConfiguration>()));
        return builder;
    }

    // --- Bind helpers (thin wrappers used by CLI and other non-DI callers) ---

    /// <summary>
    /// Binds environment variables to ClusterConfiguration.
    /// Maps FLY_PROCESS_GROUP and OCTOCON_NODE_GROUP to the NodeGroup property.
    /// </summary>
    public static ClusterConfiguration BindClusterConfiguration(this IConfiguration config)
    {
        var opts = new ClusterConfiguration();
        ApplyCluster(opts, config);
        return opts;
    }

    /// <summary>
    /// Binds environment variables to PersistenceConfiguration.
    /// Maps OCTOCON_* variables to properties with camelCase names.
    /// </summary>
    public static PersistenceConfiguration BindPersistenceConfiguration(this IConfiguration config)
    {
        var opts = new PersistenceConfiguration();
        ApplyPersistence(opts, config);
        return opts;
    }

    /// <summary>
    /// Binds environment variables to AuthenticationConfiguration.
    /// Maps OCTOCON_AUTH_* and GUARDIAN_* variables to properties.
    /// </summary>
    public static AuthenticationConfiguration BindAuthenticationConfiguration(this IConfiguration config)
    {
        var opts = new AuthenticationConfiguration();
        ApplyAuthentication(opts, config);
        return opts;
    }

    /// <summary>
    /// Binds environment variables to TestingConfiguration.
    /// Maps OCTOCON_RUN_*, OCTOCON_TEST_* variables.
    /// </summary>
    public static TestingConfiguration BindTestingConfiguration(this IConfiguration config)
    {
        var runApi = bool.TryParse(config[OctoconEnvKeys.RunApiIntegration], out var resultApi) && resultApi;
        var runLive = bool.TryParse(config[OctoconEnvKeys.RunLiveIntegration], out var resultLive) && resultLive;

        return new TestingConfiguration
        {
            RunApiIntegration = runApi,
            RunLiveIntegration = runLive,
            TestScyllaContactPoints = config[OctoconEnvKeys.TestScyllaContactPoints] ?? "127.0.0.1",
            TestScyllaUsername = config[OctoconEnvKeys.TestScyllaUsername] ?? "cassandra",
            TestScyllaPassword = config[OctoconEnvKeys.TestScyllaPassword] ?? "cassandra",
            TestRegion = config[OctoconEnvKeys.TestRegion] ?? "nam",
        };
    }

    // --- Apply methods: single source of truth for each configuration mapping ---

    internal static void ApplyCluster(ClusterConfiguration opts, IConfiguration config)
    {
        // .NET's EnvironmentVariablesConfigurationProvider surfaces an env var set to ""
        // as a configuration key with value "" (not null), which breaks the ?? fall-through
        // pattern below. The bootstrapper now always emits OCTOCON_NODE_GROUP (even when the
        // operator leaves the cluster section at its defaults), so guard against the empty-
        // string case explicitly via NullIfEmpty — otherwise Fly stacks that also have
        // OCTOCON_NODE_GROUP="" would short-circuit before reading FLY_PROCESS_GROUP.
        opts.NodeGroup = EnumWireExtensions.ParseNodeGroup(
            NullIfEmpty(config[OctoconEnvKeys.FlyProcessGroup])
            ?? NullIfEmpty(config[OctoconEnvKeys.NodeGroup]));
    }

    internal static void ApplyPersistence(PersistenceConfiguration opts, IConfiguration config)
    {
        // Fail-fast on operator typos: ParseScyllaKeyspace throws for unknown region spellings.
        var keyspace = Interfold.Contracts.Enums.EnumWireExtensions.ParseScyllaKeyspace(
            config[OctoconEnvKeys.ScyllaKeyspace] ?? "nam");
        opts.Mode = PersistenceModeExtensions.Parse(config[OctoconEnvKeys.Persistence]);
        opts.ScyllaKeyspace = keyspace;
        opts.PostgresConnectionString = config[OctoconEnvKeys.PostgresConnection]
            ?? "Host=localhost;Port=5432;Database=interfold;Username=interfold;Password=interfold";
        opts.IsSingleScyllaInstance = bool.TryParse(config[OctoconEnvKeys.SingleScyllaInstance], out var singleKs) && singleKs;
        opts.DbRetryAttempts = TryParseInt(config[OctoconEnvKeys.DbRetryAttempts]) ?? 3;
        // Wire form is integer milliseconds (external contract); convert once to TimeSpan
        // here so DatabaseTransientRetry and other consumers work in strongly-typed
        // durations without re-parsing ms at every call site.
        opts.DbRetryInitialDelay = TimeSpan.FromMilliseconds(
            TryParseInt(config[OctoconEnvKeys.DbRetryInitialDelayMs]) ?? 100);
        opts.DbRetryMaxDelay = TimeSpan.FromMilliseconds(
            TryParseInt(config[OctoconEnvKeys.DbRetryMaxDelayMs]) ?? 1500);
        opts.HydrationMaxConcurrency = TryParseInt(config[OctoconEnvKeys.HydrationMaxConcurrency]) ?? 8;
    }

    /// <summary>
    /// Initial bind of <see cref="AuthenticationConfiguration"/> from env. JWT signing keys
    /// (RSA + ES256), the deep-link HMAC secret, and the encryption pepper override get
    /// patched in over the top by <c>SecretsBootstrapService</c> on startup from
    /// <c>internal.secrets</c>; the rest of the fields stay env-bound. OAuth client IDs are
    /// public values and remain env-only; the matching client secrets live in the store and
    /// are also overridden by <c>SecretsBootstrapService</c>.
    /// </summary>
    private static void ApplyAuthentication(AuthenticationConfiguration opts, IConfiguration config)
    {
        opts.CallbackBaseUrl = config[OctoconEnvKeys.AuthCallbackBaseUrl];
        opts.JwtAuthority = config[OctoconEnvKeys.JwtAuthority] ?? "octocon-local";
        // OCTOCON_JWT_AUDIENCE was documented but never bound — bind it now so the
        // bootstrapper's value flows through. Falls back to the property-initialiser
        // default ("octocon") when the env var is unset so existing callers keep working.
        opts.JwtAudience = config[OctoconEnvKeys.JwtAudience] ?? opts.JwtAudience;

        // OAuth client IDs are public values (they appear in OAuth redirect URLs); keep them
        // env-bound. The matching secrets are placeholders here and get overwritten by
        // SecretsBootstrapService from the store before they're consumed.
        opts.DiscordOAuthClientId = config[OctoconEnvKeys.DiscordOAuthClientId];
        opts.DiscordOAuthClientSecret = config[OctoconEnvKeys.DiscordOAuthClientSecret];
        opts.GoogleOAuthClientId = config[OctoconEnvKeys.GoogleOAuthClientId];
        opts.GoogleOAuthClientSecret = config[OctoconEnvKeys.GoogleOAuthClientSecret];
        opts.AppleOAuthClientId = config[OctoconEnvKeys.AppleOAuthClientId];
        opts.AppleOAuthClientSecret = config[OctoconEnvKeys.AppleOAuthClientSecret];

        // JWT signing material, deep-link secret, and the encryption pepper are intentionally
        // left null/empty here. SecretsBootstrapService.StartingAsync runs before any consumer
        // touches these fields (its registration order in Program.cs sits ahead of every
        // migration service and request-time handler) and fills them from
        // `auth:jwt_rsa256_private_pem`, `auth:jwt_es256_private_pem`, `auth:deep_link_secret`,
        // and `encryption:pepper` respectively. The pepper row is enforced as required
        // inside SecretsBootstrapService — if it's missing the API refuses to boot. The
        // JWT and deep-link rows fail at first signing/verification (visible in startup
        // logs) rather than at boot, matching the pattern established for those fields.
        opts.Rsa256PublicKey = string.Empty;
        opts.Rsa256PrivateKey = string.Empty;
        opts.JwtEs256PrivateKeyPem = null;
        opts.JwtEs256VerificationKeyPems = null;
        opts.DeepLinkSecret = null;
        // Left empty for the SecretsBootstrapService to overwrite. The service throws if
        // the internal.secrets:encryption:pepper row is missing, so the empty default
        // never survives past startup in a well-seeded deployment.
        opts.EncryptionPepper = string.Empty;

        // The OAuth challenge query parameters (scopes / response_type / response_mode) plus
        // each provider's scheme name + authorization endpoint are constants in
        // OAuthChallengeServiceCollectionExtensions — the scopes are functionally tied to
        // the data the callback handlers read, so changing them requires a code change. The
        // only per-deployment value (client_id) is injected directly during scheme
        // registration from the OAuthClientId fields above.
    }

    /// <summary>
    /// Initial bind of <see cref="FirebaseClientConfiguration"/>. Every platform variant
    /// is intentionally left <c>null</c> here — <c>SecretsBootstrapService</c> patches
    /// them in from <c>internal.secrets:firebase:client:{android,ios,web}</c> before any
    /// request-time consumer runs. A missing row is a supported state (returns 503 for
    /// that platform) so there is nothing to bind from env vars.
    /// </summary>
    private static void ApplyFirebaseClient(FirebaseClientConfiguration opts, IConfiguration config)
    {
        opts.Android = null;
        opts.Ios = null;
        opts.Web = null;
    }

    private static void ApplyObservability(ObservabilityConfiguration opts, IConfiguration config)
    {
        // The OTLP endpoint is optional — null means "skip exporter registration entirely".
        // The bootstrapper now always emits OCTOCON_OTLP_ENDPOINT (even when blank), so an
        // empty env var would otherwise land here as the literal string "" and confuse the
        // OTLP exporter SDK (it would attempt to connect to ""). Normalise to null so the
        // not-configured branch in the telemetry registration still fires.
        opts.OtlpEndpoint = NullIfEmpty(config[OctoconEnvKeys.OtlpEndpoint]);
    }

    /// <summary>
    /// Binds the OCTOCON_TRUST_* env vars onto <see cref="TrustOptions"/>. Empty strings
    /// (the bootstrapper always emits these env vars, even in dev where the values are
    /// blank) are normalised to <c>null</c> so TrustController's "trust artefacts not
    /// configured" 404 branch fires correctly instead of treating "" as a usable path.
    /// </summary>
    private static void ApplyTrust(TrustOptions opts, IConfiguration config)
    {
        opts.RootCaPath = NullIfEmpty(config[OctoconEnvKeys.TrustRootCaPath]);
        opts.RootCaFingerprintPath = NullIfEmpty(config[OctoconEnvKeys.TrustRootCaFingerprintPath]);
    }

    private static void ApplyStorage(StorageConfiguration opts, IConfiguration config)
    {
        // Both avatar fields are optional — null on each disables the corresponding API
        // surface. The bootstrapper emits empty strings for the unset case (its always-
        // emit-every-parameter contract), so normalise empty → null here to preserve the
        // pre-bootstrapper behaviour where an unset env var produced a null on read.
        opts.AvatarStorageRoot = NullIfEmpty(config[OctoconEnvKeys.AvatarStorageRoot]);
        opts.AvatarPublicBase = NullIfEmpty(config[OctoconEnvKeys.AvatarPublicBase]);
    }

    private static void ApplySocket(SocketConfiguration opts, IConfiguration config)
    {
        opts.BatchBytesThreshold = TryParseInt(config[OctoconEnvKeys.SocketBatchBytesThreshold]);
    }

    /// <summary>
    /// Parses <c>OCTOCON_CORS_ALLOWED_ORIGINS</c> into <see cref="CorsOptions.AllowedOrigins"/>.
    /// Trailing slashes are stripped and comparison is case-insensitive for parity with the
    /// ASP.NET Core CORS matcher, which does exact-string matching against the resulting list.
    /// Blank env-var → empty list (caller is responsible for the "empty means allow-any"
    /// dev-only fallback).
    /// </summary>
    private static void ApplyCors(CorsOptions opts, IConfiguration config)
    {
        opts.AllowedOrigins = (config[OctoconEnvKeys.CorsAllowedOrigins] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static origin => origin.TrimEnd('/'))
            .Where(static origin => !string.IsNullOrWhiteSpace(origin))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Parses the two test-only Scylla override env vars onto <see cref="ScyllaOverrideOptions"/>.
    /// Blank/missing values leave the properties null so <c>ScyllaConfigResolver</c> can
    /// distinguish "no override — use the secrets-store row" from "operator forced a value".
    /// </summary>
    private static void ApplyScyllaOverride(ScyllaOverrideOptions opts, IConfiguration config)
    {
        var contactPointsRaw = NullIfEmpty(config[OctoconEnvKeys.ScyllaContactPoints]);
        opts.ContactPoints = contactPointsRaw is null
            ? null
            : contactPointsRaw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        opts.Port = TryParseInt(config[OctoconEnvKeys.ScyllaPort]);
    }

    /// <summary>
    /// Copies the four <c>OCTOCON_INMEMORY_SECRETS_SEED:*</c> configuration values onto
    /// <see cref="InMemorySecretsSeedOptions"/>. Blank/missing values remain null so the
    /// consumer's "skip silently" contract stays intact — SecretsBootstrapService is the
    /// sole fail-fast for the mandatory rows.
    /// </summary>
    private static void ApplyInMemorySecretsSeed(InMemorySecretsSeedOptions opts, IConfiguration config)
    {
        opts.EncryptionPepper = NullIfEmpty(config[OctoconEnvKeys.InMemorySecretsSeedEncryptionPepper]);
        opts.AuthJwtEs256PrivatePem = NullIfEmpty(config[OctoconEnvKeys.InMemorySecretsSeedAuthJwtEs256PrivatePem]);
        opts.AuthDeepLinkSecret = NullIfEmpty(config[OctoconEnvKeys.InMemorySecretsSeedAuthDeepLinkSecret]);
        opts.AuthJwtRsa256PrivatePem = NullIfEmpty(config[OctoconEnvKeys.InMemorySecretsSeedAuthJwtRsa256PrivatePem]);
    }

    // --- Helpers ---

    private static int? TryParseInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return int.TryParse(value, out var result) ? result : null;
    }

    /// <summary>
    /// Returns <c>null</c> for null, empty, or whitespace-only inputs; otherwise the input
    /// unchanged. Used to bridge the gap between the .NET configuration system (which
    /// surfaces env-var-set-to-"" as a key with empty-string value) and binders whose
    /// "feature disabled" signal is <c>null</c>.
    /// </summary>
    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
