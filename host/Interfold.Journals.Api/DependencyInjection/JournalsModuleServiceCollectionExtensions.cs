using Interfold.Domain.Journals;

namespace Interfold.Journals.Api.DependencyInjection;

/// <summary>Journals feature module — the DI equivalent of the twelve journal-handler
/// <c>AddSingleton</c> calls previously living in <see cref="Interfold.Infrastructure.DependencyInjection.ServiceCollectionExtensions.AddInterfoldDomainHandlers"/>.
/// Consumed once from <c>Interfold.Api.Host/Program.cs</c> alongside the other
/// module extensions.</summary>
public static class JournalsModuleServiceCollectionExtensions
{
    /// <summary>Registers everything the Journals feature owns: the twelve journal
    /// command-handler singletons (seven global + five alter). <c>IJournalRepository</c>
    /// is registered by the active persistence adapter (Scylla / Postgres / InMemory)
    /// via <c>AddInterfoldPersistence</c>, so no repository registration lives here.</summary>
    public static IServiceCollection AddJournalsModule(this IServiceCollection services) =>
        services
            .AddSingleton<CreateGlobalJournalEntryCommandHandler>()
            .AddSingleton<UpdateGlobalJournalEntryCommandHandler>()
            .AddSingleton<DeleteGlobalJournalEntryCommandHandler>()
            .AddSingleton<SetGlobalJournalLockedCommandHandler>()
            .AddSingleton<SetGlobalJournalPinnedCommandHandler>()
            .AddSingleton<AttachAlterToGlobalJournalCommandHandler>()
            .AddSingleton<DetachAlterFromGlobalJournalCommandHandler>()
            .AddSingleton<CreateAlterJournalEntryCommandHandler>()
            .AddSingleton<UpdateAlterJournalEntryCommandHandler>()
            .AddSingleton<DeleteAlterJournalEntryCommandHandler>()
            .AddSingleton<SetAlterJournalLockedCommandHandler>()
            .AddSingleton<SetAlterJournalPinnedCommandHandler>();
}
