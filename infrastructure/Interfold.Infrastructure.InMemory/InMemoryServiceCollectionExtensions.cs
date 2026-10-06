using System.Collections.Concurrent;
using Interfold.Infrastructure.DependencyInjection;
using Interfold.Infrastructure.Sqlite;
using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Secrets;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Interfold.Infrastructure.InMemory;

public static class InMemoryServiceCollectionExtensions
{
    public const string DefaultConnectionString = "Data Source=interfold_inmemory;Mode=Memory;Cache=Shared";

    private static readonly ConcurrentDictionary<string, SqliteConnection> PersistentConnections = new(StringComparer.Ordinal);
    private static readonly Lock InitLock = new();

    private static readonly Action Registration = PersistenceRegistration.Create(PersistenceMode.InMemory, AddInMemoryPersistence);
    public static void Register() => Registration();

    private static IServiceCollection AddInMemoryPersistence(
        IServiceCollection services,
        PersistenceConfiguration options)
    {
        services.AddSqlitePersistence(options);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = "interfold_inmemory",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared
        };

        var connectionString = builder.ToString();
        options.SqliteConnectionString = connectionString;
        services.Configure<PersistenceConfiguration>(cfg => cfg.SqliteConnectionString = connectionString);
        services.PostConfigure<PersistenceConfiguration>(cfg => cfg.SqliteConnectionString = connectionString);
        
        services.RemoveAll<ISqliteConnectionFactory>();
        services.AddSingleton<ISqliteConnectionFactory>(sp =>
        {
            var persistentConn = EnsurePersistentConnection(connectionString);
            var seed = sp.GetService<IOptions<InMemorySecretsSeedOptions>>()?.Value;
            if (seed is not null)
            {
                SeedSecrets(persistentConn, seed);
            }

            return new SqliteConnectionFactory(connectionString);
        });

        return services;
    }

    public static SqliteConnection EnsurePersistentConnection(string connectionString = DefaultConnectionString)
    {
        lock (InitLock)
        {
            if (PersistentConnections.TryGetValue(connectionString, out var existing))
            {
                return existing;
            }

            var conn = new SqliteConnection(connectionString);
            conn.Open();

            SqliteMigrationService.MigrateAsync(connectionString, NullLogger.Instance, CancellationToken.None)
                .GetAwaiter().GetResult();

            PersistentConnections[connectionString] = conn;
            return conn;
        }
    }

    public static ISqliteConnectionFactory CreateConnectionFactory(string connectionString = DefaultConnectionString)
    {
        EnsurePersistentConnection(connectionString);
        return new SqliteConnectionFactory(connectionString);
    }

    public static ISqliteConnectionFactory CreateIsolatedConnectionFactory()
    {
        var cs = $"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        EnsurePersistentConnection(cs);
        return new SqliteConnectionFactory(cs);
    }

    private static void SeedSecrets(SqliteConnection connection, InMemorySecretsSeedOptions seed)
    {
        lock (connection)
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Seed(SecretsStoreKeys.EncryptionPepper, seed.EncryptionPepper);
            Seed(SecretsStoreKeys.AuthJwtEs256PrivatePem, seed.AuthJwtEs256PrivatePem);
            Seed(SecretsStoreKeys.AuthDeepLinkSecret, seed.AuthDeepLinkSecret);
            Seed(SecretsStoreKeys.AuthJwtRsa256PrivatePem, seed.AuthJwtRsa256PrivatePem);
            return;

            void Seed(SecretsStoreKey key, string? value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO secrets (key, value, created_by, created_at, updated_at, expires_at, rotated_from)
                    VALUES ($key, $value, 'inmemory-seed', $now, $now, NULL, NULL)
                    ON CONFLICT(key) DO UPDATE SET
                        value = excluded.value,
                        updated_at = excluded.updated_at;
                    """;
                cmd.Parameters.AddWithValue("$key", key.Value);
                cmd.Parameters.AddWithValue("$value", value);
                cmd.Parameters.AddWithValue("$now", nowMs);
                cmd.ExecuteNonQuery();
            }
        }
    }
}
