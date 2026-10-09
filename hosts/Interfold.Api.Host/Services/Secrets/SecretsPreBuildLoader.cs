using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Secrets;
using Interfold.Infrastructure.Sqlite;

namespace Interfold.Api.Host.Services.Secrets;

/// <summary>Populates the API's <see cref="SecretsSnapshot"/> before
/// <c>WebApplicationBuilder.Build()</c>. Sqlite branch: batched read on a bare
/// connection (ISecretsStore isn't built yet). InMemory branch: reads
/// <c>OCTOCON_INMEMORY_SECRETS_SEED:*</c> env vars.</summary>
internal static class SecretsPreBuildLoader
{
    private static readonly SecretsStoreKey[] SnapshotKeys =
    [
        SecretsStoreKeys.OAuthGoogleClientSecret,
        SecretsStoreKeys.OAuthDiscordClientSecret,
        SecretsStoreKeys.OAuthAppleClientSecret,
        SecretsStoreKeys.EncryptionPepper,
        SecretsStoreKeys.AuthDeepLinkSecret,
        SecretsStoreKeys.AuthJwtRsa256PrivatePem,
        SecretsStoreKeys.AuthJwtEs256PrivatePem,

        SecretsStoreKeys.FirebaseClientAndroid,
        SecretsStoreKeys.FirebaseClientIos,
        SecretsStoreKeys.FirebaseClientWeb,

        SecretsStoreKeys.FcmServiceAccountJson,
    ];

    public static SecretsSnapshot Load(IConfigurationBuilder cfg)
    {
        var config = cfg.Build();
        var snapshot = new SecretsSnapshot();
        var mode = EnumWireExtensions.ParsePersistenceMode(config[OctoconEnvKeys.Persistence]);

        if (mode == PersistenceMode.Sqlite)
        {
            var sqliteConn = config[OctoconEnvKeys.SqliteConnection];
            if (string.IsNullOrWhiteSpace(sqliteConn))
            {
                throw new InvalidOperationException("SQLite connection string is not configured.");
            }

            var keys = new string[SnapshotKeys.Length + 1];
            for (var i = 0; i < SnapshotKeys.Length; i++)
            {
                keys[i] = SnapshotKeys[i].Value;
            }
            keys[^1] = SecretsStoreKeys.CertsLeafPfxPassword.Value;

            var rows = SqliteSecretsPreload.Fetch(sqliteConn, keys);
            snapshot.Populate(BuildSnapshotBuffer(rows));
            var leafPfxPassword = rows.GetValueOrDefault(SecretsStoreKeys.CertsLeafPfxPassword.Value);
            ApplyLeafPfxPasswordIfNeeded(cfg, config, secretsBackendPresent: true, leafPfxPassword);
        }
        else
        {
            snapshot.Populate(BuildInMemorySeedBuffer(config));
            ApplyLeafPfxPasswordIfNeeded(cfg, config, secretsBackendPresent: false, leafPfxPassword: null);
        }

        return snapshot;
    }

    private static Dictionary<SecretsStoreKey, string?> BuildSnapshotBuffer(Dictionary<string, string?> rows)
    {
        var buffer = new Dictionary<SecretsStoreKey, string?>(SnapshotKeys.Length);
        foreach (var key in SnapshotKeys)
        {
            buffer[key] = rows.GetValueOrDefault(key.Value);
        }
        return buffer;
    }

    private static Dictionary<SecretsStoreKey, string?> BuildInMemorySeedBuffer(IConfigurationRoot config) => new()
    {
        [SecretsStoreKeys.EncryptionPepper] = config[OctoconEnvKeys.InMemorySecretsSeedEncryptionPepper],
        [SecretsStoreKeys.AuthJwtEs256PrivatePem] = config[OctoconEnvKeys.InMemorySecretsSeedAuthJwtEs256PrivatePem],
        [SecretsStoreKeys.AuthDeepLinkSecret] = config[OctoconEnvKeys.InMemorySecretsSeedAuthDeepLinkSecret],
        [SecretsStoreKeys.AuthJwtRsa256PrivatePem] = config[OctoconEnvKeys.InMemorySecretsSeedAuthJwtRsa256PrivatePem],
    };

    private static void ApplyLeafPfxPasswordIfNeeded(
        IConfigurationBuilder cfg,
        IConfigurationRoot config,
        bool secretsBackendPresent,
        string? leafPfxPassword)
    {
        var pfxPath = config["Kestrel:Certificates:Default:Path"]
                      ?? Environment.GetEnvironmentVariable("ASPNETCORE_Kestrel__Certificates__Default__Path");
        if (string.IsNullOrWhiteSpace(pfxPath)) return;

        var existingPassword = config["Kestrel:Certificates:Default:Password"];
        if (!string.IsNullOrWhiteSpace(existingPassword)) return;

        if (!secretsBackendPresent)
        {
            throw new InvalidOperationException(
                "Kestrel default-cert path is set but no durable secrets backend is configured " +
                "(OCTOCON_SQLITE_CONNECTION); " +
                "cannot fetch certs:leaf_pfx_password.");
        }

        if (string.IsNullOrEmpty(leafPfxPassword))
        {
            throw new InvalidOperationException(
                "Row secrets[certs:leaf_pfx_password] is missing or empty; " +
                "re-run the bootstrapper so the cert password is seeded.");
        }

        cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kestrel:Certificates:Default:Password"] = leafPfxPassword,
        });
    }
}
