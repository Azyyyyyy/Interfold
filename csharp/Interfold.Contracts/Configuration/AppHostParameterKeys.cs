namespace Interfold.Contracts.Configuration;

/// <summary>
/// The <c>IConfiguration</c> keys the bootstrapper's <c>PublishPhase</c> injects and
/// <c>InterfoldAppHost.Configure</c> reads back. Centralising them keeps the writer and
/// reader from drifting (a typo used to surface as a silently-defaulted parameter at
/// compose-publish time). Key spellings are internal to the publish pipeline but must
/// match Aspire's <c>AddParameter(name)</c> calls, so treat them as frozen.
/// </summary>
public static class AppHostParameterKeys
{
    // --- Toggles / topology ---
    public const string IncludeApi = "Parameters:include-api";
    public const string IncludePostgres = "Parameters:include-postgres";
    public const string IncludeScylla = "Parameters:include-scylla";
    public const string IncludeCassandra = "Parameters:include-cassandra";
    public const string IncludeDashboard = "Parameters:include-dashboard";
    public const string IncludeWeb = "Parameters:include-web";
    public const string PersistentContainers = "Parameters:persistent-containers";
    public const string ScyllaTopology = "Parameters:scylla-topology";
    public const string ClusterName = "Parameters:cluster-name";
    public const string WebTls = "Parameters:web-tls";
    public const string WebServerName = "Parameters:web-server-name";
    public const string ApiImage = "Parameters:api-image";

    // --- Credentials / secrets ---
    public const string PostgresUser = "Parameters:postgres-user";
    public const string PostgresPassword = "Parameters:postgres-password";
    public const string PostgresInitPassword = "Parameters:postgres-init-password";
    public const string PostgresDb = "Parameters:postgres-db";
    public const string ScyllaUser = "Parameters:scylla-user";
    public const string ScyllaPassword = "Parameters:scylla-password";
    public const string EncryptionPrivateKey = "Parameters:encryption-private-key";

    // --- OAuth ---
    public const string GoogleOAuthClientId = "Parameters:google-oauth-client-id";
    public const string DiscordOAuthClientId = "Parameters:discord-oauth-client-id";
    public const string AppleOAuthClientId = "Parameters:apple-oauth-client-id";

    // --- API runtime ---
    public const string ScyllaKeyspace = "Parameters:scylla-keyspace";
    public const string OAuthCallbackBaseUrl = "Parameters:oauth-callback-base-url";
    public const string JwtAuthority = "Parameters:jwt-authority";
    public const string JwtAudience = "Parameters:jwt-audience";
    public const string CorsAllowedOrigins = "Parameters:cors-allowed-origins";
    public const string NodeGroup = "Parameters:node-group";
    public const string AvatarStorageRoot = "Parameters:avatar-storage-root";
    public const string AvatarPublicBase = "Parameters:avatar-public-base";
    public const string OtlpEndpoint = "Parameters:otlp-endpoint";
    public const string SocketBatchBytesThreshold = "Parameters:socket-batch-bytes-threshold";
    public const string DbRetryAttempts = "Parameters:db-retry-attempts";
    public const string DbRetryInitialDelayMs = "Parameters:db-retry-initial-delay-ms";
    public const string DbRetryMaxDelayMs = "Parameters:db-retry-max-delay-ms";
    public const string HydrationMaxConcurrency = "Parameters:hydration-max-concurrency";

    // --- Host port mappings ---
    public const string PortsPostgres = "Ports:postgres";
    public const string PortsScylla = "Ports:scylla";
    public const string PortsApiHttp = "Ports:api-http";
    public const string PortsApiHttps = "Ports:api-https";
    public const string PortsWebHttp = "Ports:web-http";
    public const string PortsWebHttps = "Ports:web-https";
}
