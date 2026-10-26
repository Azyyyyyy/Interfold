using Interfold.Shared.Api.Json;
using Interfold.Shared.Domain;
using Interfold.Socket.Contracts;
using Interfold.Tags.Api.Json;
using Interfold.Tags.Contracts;
using Interfold.Tags.Domain;

namespace Interfold.Tags.Api.DependencyInjection;

/// <summary>Tags feature module — the DI equivalent of the seven tag-handler
/// <c>AddSingleton</c> calls previously living in <see cref="Interfold.Infrastructure.DependencyInjection.ServiceCollectionExtensions.AddInterfoldDomainHandlers"/>.
/// Consumed once from <c>Interfold.Api.Host/Program.cs</c> alongside the other
/// module extensions.</summary>
public static partial class TagsModuleServiceCollectionExtensions
{
    /// <summary>Registers everything the Tags feature owns: the seven tag command-handler
    /// singletons. <c>ITagRepository</c> is registered by the active persistence adapter
    /// (Sqlite / InMemory) via <c>AddInterfoldPersistence</c>, so no repository
    /// registration lives here.</summary>
    public static IServiceCollection AddTagsModule(this IServiceCollection services)
    {
        services
            .AddInterfoldJsonContext(TagsContractsJsonContext.Default)
            .AddInterfoldJsonContext(TagsApiJsonContext.Default);
        CommandSerialization.AddTypeInfoResolver(TagsCommandJsonContext.Default);
        SocketJson.AddTypeInfoResolver(TagsContractsJsonContext.Default);
        return services
            .AddSingleton<CreateTagCommandHandler>()
            .AddSingleton<UpdateTagCommandHandler>()
            .AddSingleton<DeleteTagCommandHandler>()
            .AddSingleton<AttachAlterToTagCommandHandler>()
            .AddSingleton<DetachAlterFromTagCommandHandler>()
            .AddSingleton<SetParentTagCommandHandler>()
            .AddSingleton<RemoveParentTagCommandHandler>();
    }
}
