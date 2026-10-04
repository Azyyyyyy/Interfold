namespace Interfold.Shared.Contracts.Configuration;

/// <summary>Well-known OCTOCON_* env-var names shared by Infrastructure binders and
/// Bootstrapper publish. External contract — changing any value here is a breaking change
/// for operators.</summary>
public static class OctoconEnvKeys
{
    public const string NodeGroup = "OCTOCON_NODE_GROUP";

    public const string Persistence = "OCTOCON_PERSISTENCE";
    public const string SqliteConnection = "OCTOCON_SQLITE_CONNECTION";
    public const string DbRetryAttempts = "OCTOCON_DB_RETRY_ATTEMPTS";
    public const string DbRetryInitialDelayMs = "OCTOCON_DB_RETRY_INITIAL_DELAY_MS";
    public const string DbRetryMaxDelayMs = "OCTOCON_DB_RETRY_MAX_DELAY_MS";
    public const string HydrationMaxConcurrency = "OCTOCON_HYDRATION_MAX_CONCURRENCY";

    // OCTOCON_INMEMORY_SECRETS_SEED__<SUFFIX> — `__` → `:` remap lands under
    // Octocon:InMemorySecretsSeed:* at read time.
    public const string InMemorySecretsSeedEncryptionPepper =
        "OCTOCON_INMEMORY_SECRETS_SEED:ENCRYPTION_PEPPER";
    public const string InMemorySecretsSeedAuthJwtEs256PrivatePem =
        "OCTOCON_INMEMORY_SECRETS_SEED:AUTH_JWT_ES256_PRIVATE_PEM";
    public const string InMemorySecretsSeedAuthDeepLinkSecret =
        "OCTOCON_INMEMORY_SECRETS_SEED:AUTH_DEEP_LINK_SECRET";
    public const string InMemorySecretsSeedAuthJwtRsa256PrivatePem =
        "OCTOCON_INMEMORY_SECRETS_SEED:AUTH_JWT_RSA256_PRIVATE_PEM";

    public const string AuthCallbackBaseUrl = "OCTOCON_AUTH_CALLBACK_BASE_URL";
    public const string JwtAuthority = "OCTOCON_JWT_AUTHORITY";
    public const string JwtAudience = "OCTOCON_JWT_AUDIENCE";

    public const string DiscordOAuthClientId = "OCTOCON_DISCORD_OAUTH_CLIENT_ID";
    public const string DiscordOAuthClientSecret = "OCTOCON_DISCORD_OAUTH_CLIENT_SECRET";
    public const string GoogleOAuthClientId = "OCTOCON_GOOGLE_OAUTH_CLIENT_ID";
    public const string GoogleOAuthClientSecret = "OCTOCON_GOOGLE_OAUTH_CLIENT_SECRET";
    public const string AppleOAuthClientId = "OCTOCON_APPLE_OAUTH_CLIENT_ID";
    public const string AppleOAuthClientSecret = "OCTOCON_APPLE_OAUTH_CLIENT_SECRET";

    public const string CfAccessTeamDomain = "OCTOCON_CF_ACCESS_TEAM_DOMAIN";
    public const string CfAccessAud = "OCTOCON_CF_ACCESS_AUD";
    public const string CfAccessDiscordIdpId = "OCTOCON_CF_ACCESS_DISCORD_IDP_ID";

    public const string CorsAllowedOrigins = "OCTOCON_CORS_ALLOWED_ORIGINS";

    public const string OtlpEndpoint = "OCTOCON_OTLP_ENDPOINT";
    public const string AdvertiseOtlpToClients = "OCTOCON_ADVERTISE_OTLP_TO_CLIENTS";
    public const string ClientOtlpHttpEndpoint = "OCTOCON_CLIENT_OTLP_HTTP_ENDPOINT";

    public const string TrustRootCaPath = "OCTOCON_TRUST_ROOT_CA_PATH";
    public const string TrustRootCaFingerprintPath = "OCTOCON_TRUST_ROOT_CA_FINGERPRINT_PATH";

    public const string AvatarStorageRoot = "OCTOCON_AVATAR_STORAGE_ROOT";
    public const string AvatarPublicBase = "OCTOCON_AVATAR_PUBLIC_BASE";

    public const string SocketBatchBytesThreshold = "OCTOCON_SOCKET_BATCH_BYTES_THRESHOLD";

    // Interfold.<Feature>.IntegrationTests / TestingConfiguration only.
    public const string RunApiIntegration = "OCTOCON_RUN_API_INTEGRATION";
    public const string RunLiveIntegration = "OCTOCON_RUN_LIVE_INTEGRATION";

    // Fly.io process group — read as NodeGroup fallback.
    public const string FlyProcessGroup = "FLY_PROCESS_GROUP";
}
