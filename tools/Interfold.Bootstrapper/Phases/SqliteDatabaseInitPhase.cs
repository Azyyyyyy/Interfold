using Interfold.Bootstrapper.Cli;
using Interfold.Bootstrapper.Configuration;
using Interfold.Infrastructure.Sqlite;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Secrets;
using Microsoft.Extensions.Logging;

namespace Interfold.Bootstrapper.Phases;

/// <summary>
/// Sqlite substitute for <see cref="DatabaseInitPhase"/>: creates the host data dir, runs
/// embedded schema migrations, and upserts secrets the API's preload requires. No compose
/// services are started — the API container bind-mounts this directory at publish time.
/// </summary>
internal static class SqliteDatabaseInitPhase
{
    private static readonly string Phase = BootstrapPhase.DbInit.ToWireName();

    public static async Task RunAsync(
        BootstrapOptions options,
        BootstrapConfig config,
        GeneratedSecrets secrets,
        FirebaseSeedInputs firebase,
        PhaseLogger logger,
        CancellationToken ct)
    {
        logger.PhaseStart(Phase);

        var hostDir = PublishPhase.ResolveSqliteDataHostDir(options.OutputDir);
        var dbPath = Path.Combine(hostDir, ContainerMountPaths.InterfoldSqliteDbFileName);
        Directory.CreateDirectory(hostDir);

        var phaseLogger = new SqlitePhaseLoggerAdapter(logger);
        await MigrateAndSeedAsync(dbPath, config, secrets, firebase, phaseLogger, ct).ConfigureAwait(false);

        logger.PhaseDone(Phase);
    }

    /// <summary>Testable core: migrate + idempotent secret upsert against an absolute .db path.</summary>
    internal static async Task MigrateAndSeedAsync(
        string dbPath,
        BootstrapConfig config,
        GeneratedSecrets secrets,
        FirebaseSeedInputs firebase,
        ILogger logger,
        CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var connectionString = $"Data Source={dbPath}";
        logger.LogInformation("Sqlite db-init: migrating {Path}", dbPath);
        await SqliteMigrationService.MigrateAsync(connectionString, logger, ct).ConfigureAwait(false);

        var store = new SqliteSecretsStore(new SqliteConnectionFactory(connectionString));
        var existingPepper = await store.GetAsync(SecretsStoreKeys.EncryptionPepper, ct).ConfigureAwait(false);
        if (existingPepper is not null)
        {
            logger.LogInformation("Sqlite db-init: secrets already present; skipping upsert");
            return;
        }

        await UpsertIfPresentAsync(store, SecretsStoreKeys.EncryptionPepper, secrets.EncryptionPepper, ct).ConfigureAwait(false);
        await UpsertIfPresentAsync(store, SecretsStoreKeys.AuthDeepLinkSecret, secrets.DeepLinkSecret, ct).ConfigureAwait(false);
        await UpsertIfPresentAsync(store, SecretsStoreKeys.AuthJwtRsa256PrivatePem, secrets.JwtRsa256PrivateKeyPem, ct).ConfigureAwait(false);
        await UpsertIfPresentAsync(store, SecretsStoreKeys.AuthJwtEs256PrivatePem, secrets.JwtEs256PrivateKeyPem, ct).ConfigureAwait(false);
        await UpsertIfPresentAsync(store, SecretsStoreKeys.CertsLeafPfxPassword, secrets.LeafPfxPassword, ct).ConfigureAwait(false);

        await UpsertIfPresentAsync(store, SecretsStoreKeys.OAuthGoogleClientSecret, config.OAuth.GoogleClientSecret, ct).ConfigureAwait(false);
        await UpsertIfPresentAsync(store, SecretsStoreKeys.OAuthDiscordClientSecret, config.OAuth.DiscordClientSecret, ct).ConfigureAwait(false);
        await UpsertIfPresentAsync(store, SecretsStoreKeys.OAuthAppleClientSecret, config.OAuth.AppleClientSecret, ct).ConfigureAwait(false);

        await UpsertIfPresentAsync(store, SecretsStoreKeys.FirebaseClientAndroid, firebase.AndroidClientJson, ct).ConfigureAwait(false);
        await UpsertIfPresentAsync(store, SecretsStoreKeys.FirebaseClientIos, firebase.IosClientJson, ct).ConfigureAwait(false);
        await UpsertIfPresentAsync(store, SecretsStoreKeys.FirebaseClientWeb, firebase.WebClientJson, ct).ConfigureAwait(false);
        await UpsertIfPresentAsync(store, SecretsStoreKeys.FcmServiceAccountJson, firebase.ServiceAccountJson, ct).ConfigureAwait(false);

        logger.LogInformation("Sqlite db-init: wrote initial secrets");
    }

    private static async Task UpsertIfPresentAsync(
        SqliteSecretsStore store,
        SecretsStoreKey key,
        string? value,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(value)) return;
        await store.UpsertAsync(key, value, cancellationToken: ct).ConfigureAwait(false);
    }

    private sealed class SqlitePhaseLoggerAdapter(PhaseLogger inner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            switch (logLevel)
            {
                case LogLevel.Warning:
                    inner.Warn(message);
                    break;
                case LogLevel.Error:
                case LogLevel.Critical:
                    inner.Error(message);
                    break;
                default:
                    inner.Info($"    {message}");
                    break;
            }
        }
    }
}
