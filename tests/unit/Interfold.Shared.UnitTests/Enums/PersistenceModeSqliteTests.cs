using Interfold.Shared.Contracts;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Secrets;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.Repository;
using Interfold.Infrastructure.DependencyInjection;
using Interfold.Infrastructure.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Interfold.Api.UnitTests.Enums;

public sealed class PersistenceModeSqliteTests
{
    [Test]
    public async Task ParsePersistenceMode_Sqlite_ReturnsSqlite()
    {
        var mode = EnumWireExtensions.ParsePersistenceMode("sqlite");

        await Assert.That(mode).IsEqualTo(PersistenceMode.Sqlite)
            .Because("OCTOCON_PERSISTENCE=sqlite must round-trip through EnumWire to PersistenceMode.Sqlite.");
    }

    [Test]
    public async Task ParsePersistenceMode_Unknown_ErrorMessageMentionsSqlite()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => EnumWireExtensions.ParsePersistenceMode("nope"));

        await Assert.That(ex!.Message).Contains("sqlite");
        await Assert.That(ex.Message).Contains("scylla-postgres");
        await Assert.That(ex.Message).Contains("inmemory");
    }

    [Test]
    public async Task AddInterfoldPersistence_Sqlite_RegistersCrossCuttingStores()
    {
        SqliteServiceCollectionExtensions.Register();

        var dbPath = Path.Combine(Path.GetTempPath(), $"interfold-sqlite-di-{Guid.NewGuid():N}.db");
        try
        {
            var cfg = new PersistenceConfiguration
            {
                Mode = PersistenceMode.Sqlite,
                SqliteConnectionString = $"Data Source={dbPath}",
            };

            var services = new ServiceCollection();
            services.AddSingleton(Microsoft.Extensions.Options.Options.Create(cfg));
            services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(
                Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
            services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
            services.AddInterfoldPersistence(PersistenceMode.Sqlite, c =>
            {
                c.Mode = PersistenceMode.Sqlite;
                c.SqliteConnectionString = cfg.SqliteConnectionString;
            });

            await using var provider = services.BuildServiceProvider();

            await Assert.That(provider.GetService<ISqliteConnectionFactory>()).IsNotNull();
            await Assert.That(provider.GetService<ISecretsStore>()).IsNotNull();
            await Assert.That(provider.GetService<IIdempotencyStore>()).IsNotNull();
            await Assert.That(provider.GetService<IAuthTokenRevocationRepository>()).IsNotNull();
            await Assert.That(provider.GetServices<IHostedService>().OfType<SqliteMigrationService>().Any()).IsTrue()
                .Because("SqliteMigrationService must be registered as a hosted service.");
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
        try { if (File.Exists(path + "-wal")) File.Delete(path + "-wal"); } catch { /* best-effort */ }
        try { if (File.Exists(path + "-shm")) File.Delete(path + "-shm"); } catch { /* best-effort */ }
    }
}
