using Interfold.Alters.Domain.Abstractions.Repository;
using Interfold.Friendships.Domain.Abstractions.Repository;
using Interfold.Fronting.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Secrets;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Infrastructure.DependencyInjection;
using Interfold.Infrastructure.Sqlite.Repository;
using Interfold.Journals.Domain.Abstractions.Repository;
using Interfold.Polls.Domain.Abstractions.Repository;
using Interfold.Settings.Domain.Abstractions.Repository;
using Interfold.Tags.Domain.Abstractions.Repository;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Interfold.Infrastructure.Sqlite;

public static class SqliteServiceCollectionExtensions
{
    private static readonly Action Registration = PersistenceRegistration.Create(PersistenceMode.Sqlite, AddSqlitePersistence);

    public static void Register() => Registration();

    private static IServiceCollection AddSqlitePersistence(
        IServiceCollection services,
        PersistenceConfiguration options)
    {
        services.TryAddSingleton(TimeProvider.System);

        return services
            .AddSingleton<ISqliteConnectionFactory, SqliteConnectionFactory>()
            .AddSingleton<ISecretsStore, SqliteSecretsStore>()
            .AddSingleton<IIdempotencyStore, SqliteIdempotencyStore>()
            .AddSingleton<IAuthTokenRevocationRepository, SqliteAuthTokenRevocationRepository>()
            .AddHostedService<SqliteMigrationService>()
            .AddSingleton<IRegionContext>(_ => new SqliteRegionContext(options.ScyllaKeyspace))
            .AddSingleton<IEncryptionStateRepository, SqliteEncryptionStateRepository>()
            .AddSingleton<IAccountRepository, SqliteAccountRepository>()
            .AddSingleton<INotificationTokenRepository, SqliteNotificationTokenRepository>()
            .AddSingleton<IAlterRepository, SqliteAlterRepository>()
            .AddSingleton<IFriendshipRepository, SqliteFriendshipRepository>()
            .AddSingleton<IFrontingRepository, SqliteFrontingRepository>()
            .AddSingleton<IImportOperationRepository, SqliteImportOperationRepository>()
            .AddSingleton<IJournalRepository, SqliteJournalRepository>()
            .AddSingleton<IPollRepository, SqlitePollRepository>()
            .AddSingleton<ISettingsFieldRepository, SqliteSettingsFieldRepository>()
            .AddSingleton<ITagRepository, SqliteTagRepository>();
    }
}
