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
            var store = new SqliteSecretsStore(new SqliteConnectionFactory(cs));
            await store.UpsertAsync(SecretsStoreKeys.EncryptionPepper, "sqlite-pepper");
            await store.UpsertAsync(SecretsStoreKeys.AuthDeepLinkSecret, "sqlite-deeplink");

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
}
