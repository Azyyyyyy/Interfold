using Interfold.Domain.Alters;

namespace Interfold.Alters.Api.DependencyInjection;

/// <summary>Alters feature module — the DI equivalent of the three alter-handler
/// <c>AddSingleton</c> calls previously living in <see cref="Interfold.Infrastructure.DependencyInjection.ServiceCollectionExtensions.AddInterfoldDomainHandlers"/>.
/// Consumed once from <c>Interfold.Api.Host/Program.cs</c> alongside the other
/// module extensions.</summary>
public static class AltersModuleServiceCollectionExtensions
{
    /// <summary>Registers everything the Alters feature owns: the three alter
    /// command-handler singletons (Create/Update/Delete). <c>IAlterRepository</c>
    /// is registered by the active persistence adapter (Scylla / Postgres / InMemory)
    /// via <c>AddInterfoldPersistence</c>, so no repository registration lives here.</summary>
    public static IServiceCollection AddAltersModule(this IServiceCollection services) =>
        services
            .AddSingleton<CreateAlterCommandHandler>()
            .AddSingleton<UpdateAlterCommandHandler>()
            .AddSingleton<DeleteAlterCommandHandler>();
}
