using System.Text.Json.Serialization;
using Interfold.Settings.Contracts.Configuration;
using Interfold.Shared.Contracts.Enums;

namespace Interfold.Bootstrapper.Configuration;

/// <summary>Operator-supplied configuration for <c>interfold.bootstrap.json</c>.</summary>
public sealed class BootstrapConfig
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public const int CurrentSchemaVersion = 2;

    [JsonPropertyName("deployment")]
    public DeploymentSection Deployment { get; set; } = new();

    [JsonPropertyName("edge")]
    public EdgeSection Edge { get; set; } = new();

    [JsonPropertyName("datastores")]
    public DatastoresSection Datastores { get; set; } = new();

    [JsonPropertyName("api")]
    public ApiSection Api { get; set; } = new();

    [JsonPropertyName("observability")]
    public ObservabilitySection Observability { get; set; } = new();
}

public sealed class DeploymentSection
{
    [JsonPropertyName("outputDir")]
    public string OutputDir { get; set; } = "./deploy";

    [JsonPropertyName("includeWeb")]
    public bool IncludeWeb { get; set; }

    [JsonPropertyName("autostartServer")]
    public bool AutostartServer { get; set; }

    [JsonPropertyName("backup")]
    public BackupSection Backup { get; set; } = new();

    [JsonPropertyName("update")]
    public UpdateSection Update { get; set; } = new();
}

public sealed class EdgeSection
{
    [JsonPropertyName("hosts")]
    public List<string> Hosts { get; set; } = [];

    [JsonPropertyName("tlsMode")]
    public EdgeTlsMode TlsMode { get; set; } = EdgeTlsMode.PrivateCa;

    [JsonPropertyName("routing")]
    public EdgeRoutingSection Routing { get; set; } = new();

    [JsonPropertyName("ports")]
    public EdgePortsSection Ports { get; set; } = new();

    [JsonPropertyName("cloudflare")]
    public EdgeCloudflareSection Cloudflare { get; set; } = new();

    [JsonPropertyName("certificates")]
    public EdgeCertificatesSection Certificates { get; set; } = new();
}

public sealed class EdgeRoutingSection
{
    [JsonPropertyName("mode")]
    public EdgeRoutingMode Mode { get; set; } = EdgeRoutingMode.Path;

    [JsonPropertyName("apiHost")]
    public string ApiHost { get; set; } = string.Empty;

    [JsonPropertyName("webHost")]
    public string WebHost { get; set; } = string.Empty;
}

public sealed class EdgePortsSection
{
    [JsonPropertyName("http")]
    public int Http { get; set; } = 80;

    [JsonPropertyName("https")]
    public int Https { get; set; } = 443;
}

public sealed class EdgeCertificatesSection
{
    [JsonPropertyName("rootCaName")]
    public string RootCaName { get; set; } = "Interfold Root CA";

    [JsonPropertyName("certYears")]
    public int CertYears { get; set; } = 5;

    [JsonPropertyName("trustStoreInstall")]
    public bool TrustStoreInstall { get; set; } = true;
}

public sealed class EdgeCloudflareSection
{
    [JsonPropertyName("ipAllowlist")]
    public bool IpAllowlist { get; set; }

    [JsonPropertyName("dnsApiToken")]
    public string DnsApiToken { get; set; } = string.Empty;
}

public sealed class DatastoresSection
{
    [JsonPropertyName("postgres")]
    public PostgresDatastoreSection Postgres { get; set; } = new();

    [JsonPropertyName("cql")]
    public CqlDatastoreSection Cql { get; set; } = new();
}

public sealed class PostgresDatastoreSection
{
    [JsonPropertyName("database")]
    public string Database { get; set; } = "interfold";
}

public sealed class CqlDatastoreSection
{
    [JsonPropertyName("backend")]
    public CqlBackend Backend { get; set; } = CqlBackend.ScyllaSingle;

    [JsonPropertyName("clusterName")]
    public string ClusterName { get; set; } = "InterfoldCluster";

    [JsonPropertyName("keyspace")]
    public ScyllaKeyspace Keyspace { get; set; } = ScyllaKeyspace.Nam;
}

public sealed class ApiSection
{
    [JsonPropertyName("image")]
    public string Image { get; set; } = "ghcr.io/azyyyyyy/interfold-api:latest";

    [JsonPropertyName("nodeGroup")]
    public NodeGroup NodeGroup { get; set; } = NodeGroup.Auxiliary;

    [JsonPropertyName("oauth")]
    public ApiOAuthSection OAuth { get; set; } = new();

    [JsonPropertyName("corsAllowedOrigins")]
    public List<string> CorsAllowedOrigins { get; set; } = [];

    [JsonPropertyName("resilience")]
    public ApiResilienceSection Resilience { get; set; } = new();

    [JsonPropertyName("batchBytesThreshold")]
    public int? BatchBytesThreshold { get; set; }

    [JsonPropertyName("storage")]
    public StorageSection Storage { get; set; } = new();

    [JsonPropertyName("firebase")]
    public FirebaseSection Firebase { get; set; } = new();
}

public sealed class ApiOAuthSection
{
    [JsonPropertyName("googleClientId")]
    public string GoogleClientId { get; set; } = string.Empty;

    [JsonPropertyName("googleClientSecret")]
    public string GoogleClientSecret { get; set; } = string.Empty;

    [JsonPropertyName("discordClientId")]
    public string DiscordClientId { get; set; } = string.Empty;

    [JsonPropertyName("discordClientSecret")]
    public string DiscordClientSecret { get; set; } = string.Empty;

    [JsonPropertyName("appleClientId")]
    public string AppleClientId { get; set; } = string.Empty;

    [JsonPropertyName("appleClientSecret")]
    public string AppleClientSecret { get; set; } = string.Empty;

    [JsonPropertyName("callbackBaseUrl")]
    public string CallbackBaseUrl { get; set; } = string.Empty;

    [JsonPropertyName("jwtAuthority")]
    public string JwtAuthority { get; set; } = string.Empty;

    [JsonPropertyName("jwtAudience")]
    public string JwtAudience { get; set; } = "octocon";
}

public sealed class ApiResilienceSection
{
    [JsonPropertyName("dbRetryAttempts")]
    public int DbRetryAttempts { get; set; } = 3;

    [JsonPropertyName("dbRetryInitialDelayMs")]
    public int DbRetryInitialDelayMs { get; set; } = 100;

    [JsonPropertyName("dbRetryMaxDelayMs")]
    public int DbRetryMaxDelayMs { get; set; } = 1500;

    [JsonPropertyName("hydrationMaxConcurrency")]
    public int HydrationMaxConcurrency { get; set; } = 8;
}

public sealed class StorageSection
{
    [JsonPropertyName("avatarStorageRoot")]
    public string AvatarStorageRoot { get; set; } = string.Empty;

    [JsonPropertyName("avatarPublicBase")]
    public string AvatarPublicBase { get; set; } = string.Empty;
}

public sealed class ObservabilitySection
{
    [JsonPropertyName("otlpEndpoint")]
    public string OtlpEndpoint { get; set; } = string.Empty;
}

public sealed class BackupSection
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("schedule")]
    public string Schedule { get; set; } = "daily";

    [JsonPropertyName("retainCount")]
    public int RetainCount { get; set; } = 14;

    [JsonPropertyName("directory")]
    public string Directory { get; set; } = string.Empty;
}

public sealed class UpdateSection
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("bootstrapper")]
    public UpdateBootstrapperSection Bootstrapper { get; set; } = new();

    [JsonPropertyName("healthCheckTimeoutSeconds")]
    public int HealthCheckTimeoutSeconds { get; set; } = 180;

    [JsonPropertyName("autoRestoreOnFailure")]
    public bool AutoRestoreOnFailure { get; set; }

    [JsonPropertyName("recreateOnUpdate")]
    public bool RecreateOnUpdate { get; set; } = true;

    [JsonPropertyName("services")]
    public string[] Services { get; set; } = [];
}

public sealed class UpdateBootstrapperSection
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("channel")]
    public BootstrapperReleaseChannel Channel { get; set; } = BootstrapperReleaseChannel.Stable;

    [JsonPropertyName("updateOnBootstrap")]
    public bool? UpdateOnBootstrap { get; set; }

    [JsonPropertyName("autoRollbackOnFailure")]
    public bool AutoRollbackOnFailure { get; set; }

    internal bool ResolveUpdateOnBootstrap()
        => UpdateOnBootstrap ?? Enabled;
}

public sealed class FirebaseSection
{
    [JsonPropertyName("androidConfigPath")]
    public string AndroidConfigPath { get; set; } = string.Empty;

    [JsonPropertyName("iosConfigPath")]
    public string IosConfigPath { get; set; } = string.Empty;

    [JsonPropertyName("webConfigPath")]
    public string WebConfigPath { get; set; } = string.Empty;

    [JsonPropertyName("serviceAccountPath")]
    public string ServiceAccountPath { get; set; } = string.Empty;
}
