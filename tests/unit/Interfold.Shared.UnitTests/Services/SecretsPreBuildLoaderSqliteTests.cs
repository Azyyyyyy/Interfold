using Interfold.Api.Host.Services.Secrets;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Secrets;
using Interfold.Infrastructure.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Interfold.Api.UnitTests.Services;

public sealed class SecretsPreBuildLoaderSqliteTests
{
    [Test]
    public async Task Load_Sqlite_ReadsSecretsFromMigratedDatabase()
    {
        var path = Path.Combine(Path.GetTempPath(), $"interfold-sqlite-preload-{Guid.NewGuid():N}.db");
        var cs = $"Data Source={path}";
        try
        {
            await SqliteMigrationService.MigrateAsync(cs, NullLogger.Instance, CancellationToken.None);
            var store = new SqliteSecretsStore(new SqliteConnectionFactory(cs), TimeProvider.System);
            await store.UpsertAsync(SecretsStoreKeys.EncryptionPepper, "sqlite-pepper", "bootstrap");
            await store.UpsertAsync(SecretsStoreKeys.AuthDeepLinkSecret, "sqlite-deeplink", "bootstrap");

            var builder = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [OctoconEnvKeys.Persistence] = "sqlite",
                    [OctoconEnvKeys.SqliteConnection] = cs,
                });

            var snapshot = SecretsPreBuildLoader.Load(builder);

            await Assert.That(snapshot.Get(SecretsStoreKeys.EncryptionPepper)).IsEqualTo("sqlite-pepper");
            await Assert.That(snapshot.Get(SecretsStoreKeys.AuthDeepLinkSecret)).IsEqualTo("sqlite-deeplink");
        }
        finally
        {
            foreach (var p in new[] { path, path + "-wal", path + "-shm" })
            {
                try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort */ }
            }
        }
    }

    [Test]
    public async Task Load_Sqlite_MissingConnection_Throws()
    {
        var builder = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [OctoconEnvKeys.Persistence] = "sqlite",
            });

        var ex = Assert.Throws<InvalidOperationException>(() => SecretsPreBuildLoader.Load(builder));
        await Assert.That(ex!.Message).Contains("SQLite connection string");
    }

    [Test]
    public async Task Load_InMemory_ReadsConfiguredSeedSecrets()
    {
        var builder = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [OctoconEnvKeys.Persistence] = "inmemory",
                [OctoconEnvKeys.InMemorySecretsSeedEncryptionPepper] = "mem-pepper",
                [OctoconEnvKeys.InMemorySecretsSeedAuthDeepLinkSecret] = "mem-deeplink",
                [OctoconEnvKeys.InMemorySecretsSeedAuthJwtEs256PrivatePem] = "mem-es256",
                [OctoconEnvKeys.InMemorySecretsSeedAuthJwtRsa256PrivatePem] = "mem-rsa256",
            });

        var snapshot = SecretsPreBuildLoader.Load(builder);

        await Assert.That(snapshot.Get(SecretsStoreKeys.EncryptionPepper)).IsEqualTo("mem-pepper");
        await Assert.That(snapshot.Get(SecretsStoreKeys.AuthDeepLinkSecret)).IsEqualTo("mem-deeplink");
        await Assert.That(snapshot.Get(SecretsStoreKeys.AuthJwtEs256PrivatePem)).IsEqualTo("mem-es256");
        await Assert.That(snapshot.Get(SecretsStoreKeys.AuthJwtRsa256PrivatePem)).IsEqualTo("mem-rsa256");
    }

    [Test]
    public async Task Load_Sqlite_AppliesLeafPfxPasswordToKestrelConfig()
    {
        var path = Path.Combine(Path.GetTempPath(), $"interfold-sqlite-pfx-{Guid.NewGuid():N}.db");
        var cs = $"Data Source={path}";
        try
        {
            await SqliteMigrationService.MigrateAsync(cs, NullLogger.Instance, CancellationToken.None);
            var store = new SqliteSecretsStore(new SqliteConnectionFactory(cs), TimeProvider.System);
            await store.UpsertAsync(SecretsStoreKeys.CertsLeafPfxPassword, "leaf-secret-password", "bootstrap");

            var builder = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [OctoconEnvKeys.Persistence] = "sqlite",
                    [OctoconEnvKeys.SqliteConnection] = cs,
                    ["Kestrel:Certificates:Default:Path"] = "/path/to/cert.pfx",
                });

            var snapshot = SecretsPreBuildLoader.Load(builder);
            var builtConfig = builder.Build();

            await Assert.That(builtConfig["Kestrel:Certificates:Default:Password"]).IsEqualTo("leaf-secret-password");
        }
        finally
        {
            foreach (var p in new[] { path, path + "-wal", path + "-shm" })
            {
                try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort */ }
            }
        }
    }

    [Test]
    public async Task Load_InMemory_WithKestrelCertPathWithoutPassword_Throws()
    {
        var builder = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [OctoconEnvKeys.Persistence] = "inmemory",
                ["Kestrel:Certificates:Default:Path"] = "/path/to/cert.pfx",
            });

        var ex = Assert.Throws<InvalidOperationException>(() => SecretsPreBuildLoader.Load(builder));
        await Assert.That(ex!.Message).Contains("Kestrel default-cert path is set but no durable secrets backend is configured");
    }
}
