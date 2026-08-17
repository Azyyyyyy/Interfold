using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using Interfold.Infrastructure.Sqlite;
using Interfold.Shared.Contracts.Secrets;
using Microsoft.Extensions.Logging.Abstractions;

namespace Interfold.Bootstrapper.UnitTests;

public sealed class SqliteDatabaseInitPhaseTests
{
    [Test]
    public async Task MigrateAndSeedIsIdempotent()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"interfold-sqlite-init-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "interfold.db");

        try
        {
            var config = new BootstrapConfig();
            config.OAuth.GoogleClientSecret = "google-secret";
            var secrets = SecretsPhase.Generate();

            await SqliteDatabaseInitPhase.MigrateAndSeedAsync(
                dbPath, config, secrets, FirebaseSeedInputs.Empty, NullLogger.Instance, CancellationToken.None);

            var store = new SqliteSecretsStore(new SqliteConnectionFactory($"Data Source={dbPath}"));
            var pepper1 = await store.GetAsync(SecretsStoreKeys.EncryptionPepper);
            await Assert.That(pepper1).IsEqualTo(secrets.EncryptionPepper);
            await Assert.That(await store.GetAsync(SecretsStoreKeys.OAuthGoogleClientSecret)).IsEqualTo("google-secret");

            var mutated = SecretsPhase.Generate();
            await SqliteDatabaseInitPhase.MigrateAndSeedAsync(
                dbPath, config, mutated, FirebaseSeedInputs.Empty, NullLogger.Instance, CancellationToken.None);

            var pepper2 = await store.GetAsync(SecretsStoreKeys.EncryptionPepper);
            await Assert.That(pepper2).IsEqualTo(secrets.EncryptionPepper);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
