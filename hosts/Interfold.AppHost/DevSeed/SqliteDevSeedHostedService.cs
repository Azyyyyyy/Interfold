using Aspire.Hosting.ApplicationModel;
using Interfold.Infrastructure.Sqlite;
using Interfold.Shared.Contracts.Secrets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Interfold.AppHost.DevSeed;

/// <summary>
/// Migrates the host-side SQLite file and upserts required secrets before the API's
/// secrets preload, then flips the seed marker resource to Running. Registered only for
/// RunMode + <c>persistence=sqlite</c>.
/// </summary>
internal sealed class SqliteDevSeedHostedService(
    SqliteDevSeedContext context,
    ResourceNotificationService notifications,
    ILogger<SqliteDevSeedHostedService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await notifications.PublishUpdateAsync(context.SeedResource,
                snap => snap with { State = new ResourceStateSnapshot(KnownResourceStates.Starting, "") });

            var dir = Path.GetDirectoryName(context.HostDbPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var connectionString = $"Data Source={context.HostDbPath}";
            logger.LogInformation("Sqlite dev seed: migrating {Path}", context.HostDbPath);
            await SqliteMigrationService.MigrateAsync(connectionString, logger, stoppingToken);

            var store = new SqliteSecretsStore(new SqliteConnectionFactory(connectionString));
            var existingPepper = await store.GetAsync(SecretsStoreKeys.EncryptionPepper, stoppingToken);
            if (existingPepper is null)
            {
                var pepper = await context.EncryptionPepper.GetValueAsync(stoppingToken)
                    ?? throw new InvalidOperationException("Encryption pepper parameter resolved to null.");
                var deepLink = await context.DeepLinkSecret.GetValueAsync(stoppingToken)
                    ?? throw new InvalidOperationException("Deep-link secret parameter resolved to null.");
                var (rsaPem, es256Pem) = GenerateJwtPems();

                await store.UpsertAsync(SecretsStoreKeys.EncryptionPepper, pepper, cancellationToken: stoppingToken);
                await store.UpsertAsync(SecretsStoreKeys.AuthDeepLinkSecret, deepLink, cancellationToken: stoppingToken);
                await store.UpsertAsync(SecretsStoreKeys.AuthJwtRsa256PrivatePem, rsaPem, cancellationToken: stoppingToken);
                await store.UpsertAsync(SecretsStoreKeys.AuthJwtEs256PrivatePem, es256Pem, cancellationToken: stoppingToken);
                logger.LogInformation("Sqlite dev seed: wrote initial secrets");
            }
            else
            {
                logger.LogInformation("Sqlite dev seed: secrets already present; skipping upsert");
            }

            await notifications.PublishUpdateAsync(context.SeedResource,
                snap => snap with { State = new ResourceStateSnapshot(KnownResourceStates.Running, "Success") });
            logger.LogInformation("Sqlite dev seed: complete");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await notifications.PublishUpdateAsync(context.SeedResource,
                snap => snap with { State = new ResourceStateSnapshot(KnownResourceStates.Exited, "Cancelled") });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sqlite dev seed failed; the API resource's WaitFor(db-seed) will not unblock.");
            await notifications.PublishUpdateAsync(context.SeedResource,
                snap => snap with { State = new ResourceStateSnapshot(KnownResourceStates.FailedToStart, "Error") });
            throw;
        }
    }

    private static (string Rsa256Pem, string Es256Pem) GenerateJwtPems()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var rsaPem = rsa.ExportPkcs8PrivateKeyPem();
        using var ecdsa = System.Security.Cryptography.ECDsa.Create(
            System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var esPem = ecdsa.ExportPkcs8PrivateKeyPem();
        return (rsaPem, esPem);
    }
}
