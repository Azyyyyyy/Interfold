namespace Interfold.Contracts.Secrets;

/// <summary>
/// Well-known row keys stored in <c>internal.secrets</c> and read back through
/// <see cref="ISecretsStore"/>. Centralising the strings here keeps the seeding side
/// (<c>Interfold.DatabaseBootstrap.SeedKeys</c>) and the runtime consumers (Infrastructure
/// DI wiring, <c>SecretsBootstrapService</c>, <c>FirebaseFCMService</c>) from drifting.
/// The values are the persisted wire format — CHANGING ANY VALUE HERE IS A BREAKING
/// SCHEMA CHANGE for existing self-hosted deployments.
/// </summary>
/// <remarks>
/// The file is included in <c>Interfold.DatabaseBootstrap</c> as a linked
/// <c>&lt;Compile&gt;</c> item so both projects share the same source of truth without
/// forcing DatabaseBootstrap to take a project reference on Contracts (which would blow
/// the trimmed bootstrapper binary size guardrail).
/// </remarks>
public static class SecretsStoreKeys
{
    public const string OAuthGoogleClientSecret = "oauth:google:client_secret";
    public const string OAuthDiscordClientSecret = "oauth:discord:client_secret";
    public const string OAuthAppleClientSecret = "oauth:apple:client_secret";

    public const string EncryptionPepper = "encryption:pepper";

    public const string PostgresAdminUsername = "postgres:admin_username";
    public const string PostgresAdminPassword = "postgres:admin_password";

    public const string ScyllaAdminUsername = "scylla:admin_username";
    public const string ScyllaAdminPassword = "scylla:admin_password";
    public const string ScyllaContactPoints = "scylla:contact_points";
    public const string ScyllaLocalDatacenter = "scylla:local_datacenter";
    public const string ScyllaUsername = "scylla:username";
    public const string ScyllaPassword = "scylla:password";
    public const string ScyllaPort = "scylla:port";

    public const string AuthJwtRsa256PrivatePem = "auth:jwt_rsa256_private_pem";
    public const string AuthJwtEs256PrivatePem = "auth:jwt_es256_private_pem";
    public const string AuthDeepLinkSecret = "auth:deep_link_secret";

    public const string CertsLeafPfxPassword = "certs:leaf_pfx_password";

    public const string FirebaseClientAndroid = "firebase:client:android";
    public const string FirebaseClientIos = "firebase:client:ios";
    public const string FirebaseClientWeb = "firebase:client:web";

    /// <summary>
    /// FCM v1 service-account credential (PRIVATE — authenticates the API to Google FCM).
    /// Read directly by the <c>IFCMService</c> DI factory; absent row → NullFCMService
    /// fallback so deployments without Firebase silently no-op the push flow.
    /// </summary>
    public const string FcmServiceAccountJson = "fcm:service_account_json";
}
