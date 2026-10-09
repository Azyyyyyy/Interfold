using Interfold.Alters.Contracts.Abstractions;
using Interfold.Alters.Domain.Abstractions.Repository;
using Interfold.Infrastructure.DependencyInjection;
using Interfold.Infrastructure.InMemory;
using Interfold.Infrastructure.Sqlite;
using Interfold.Settings.Contracts.Configuration;
using Interfold.Settings.Domain;
using Interfold.Shared.Contracts.Configuration;
using Interfold.Shared.Contracts.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core.Interfaces;

namespace Interfold.Api.UnitTests.Coordination;

// TUnit fixture for FCMServiceFactoryTests. Each CreateScope yields a fresh SP because
// the IFCMService factory snapshots secrets at singleton-construction time — sharing
// containers across scenarios would cross-contaminate routing decisions.
public sealed class FCMServiceFactoryFixture : IAsyncInitializer
{
    public Task InitializeAsync() => Task.CompletedTask;

    public FCMFactoryScope CreateScope(NodeGroup role, bool seedServiceAccount)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        // FirebaseFCMService's concrete type is DI-resolvable even in "not selected"
        // scenarios; wire its transitive InMemory-repo chain so construction never
        // throws unexpectedly.
        services.AddSqlitePersistence(new PersistenceConfiguration
        {
            SqliteConnectionString = InMemoryServiceCollectionExtensions.DefaultConnectionStringBuilder.ConnectionString
        });
        services.RemoveAll<ISqliteConnectionFactory>();
        services.AddSingleton<ISqliteConnectionFactory>(_ => InMemoryServiceCollectionExtensions.CreateConnectionFactory());
        services.AddSingleton<IAlterFieldDefinitions, AlterFieldDefinitionsAdapter>();

        services.Configure<FcmConfiguration>(o =>
            o.ServiceAccountJson = seedServiceAccount 
                ? "{\"type\":\"service_account\",\"project_id\":\"test\"}" 
                : null);

        services.AddInterfoldCluster(role);
        return new FCMFactoryScope(services.BuildServiceProvider());
    }
}

// Owns a single scenario's ServiceProvider; async disposal routes IDisposable and
// IAsyncDisposable singletons through the same walker.
public sealed class FCMFactoryScope(ServiceProvider provider) : IAsyncDisposable
{
    public IServiceProvider Services => provider;

    public ValueTask DisposeAsync() => provider.DisposeAsync();
}
