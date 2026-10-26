using Interfold.Polls.Api.Json;
using Interfold.Polls.Contracts;
using Interfold.Polls.Domain;
using Interfold.Shared.Api.Json;
using Interfold.Shared.Domain;
using Interfold.Socket.Contracts;

namespace Interfold.Polls.Api.DependencyInjection;

/// <summary>Polls feature module — the DI equivalent of the three poll-handler
/// <c>AddSingleton</c> calls previously living in <see cref="Interfold.Infrastructure.DependencyInjection.ServiceCollectionExtensions.AddInterfoldDomainHandlers"/>.
/// Consumed once from <c>Interfold.Api.Host/Program.cs</c> alongside the other
/// module extensions.</summary>
public static partial class PollsModuleServiceCollectionExtensions
{
    /// <summary>Registers everything the Polls feature owns: the three poll command-handler
    /// singletons. <c>IPollRepository</c> is registered by the active persistence adapter
    /// (Sqlite / InMemory) via <c>AddInterfoldPersistence</c>, so no repository
    /// registration lives here.</summary>
    public static IServiceCollection AddPollsModule(this IServiceCollection services)
    {
        services
            .AddInterfoldJsonContext(PollsContractsJsonContext.Default)
            .AddInterfoldJsonContext(PollsApiJsonContext.Default);
        CommandSerialization.AddTypeInfoResolver(PollsCommandJsonContext.Default);
        SocketJson.AddTypeInfoResolver(PollsContractsJsonContext.Default);
        return services
            .AddSingleton<CreatePollCommandHandler>()
            .AddSingleton<UpdatePollCommandHandler>()
            .AddSingleton<DeletePollCommandHandler>();
    }
}
