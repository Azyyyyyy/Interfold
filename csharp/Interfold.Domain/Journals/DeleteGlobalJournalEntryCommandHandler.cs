using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Journals;

public sealed class DeleteGlobalJournalEntryCommandHandler : IdempotentCommandHandler<DeleteGlobalJournalEntryCommand, GlobalJournalCommandResult>
{
    private readonly IJournalRepository _journalRepository;
    private readonly IClusterEventBus _eventBus;

    public DeleteGlobalJournalEntryCommandHandler(
        IJournalRepository journalRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _journalRepository = journalRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.JournalGlobalDelete;

    protected override GlobalJournalCommandResult CreateReplayResult(GlobalJournalCommandResult originalResult) =>
        originalResult with { Replay = true };
protected override async Task<CommandExecutionResult<GlobalJournalCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<DeleteGlobalJournalEntryCommand> command,
        CancellationToken cancellationToken = default)
    {

        var exists = await _journalRepository.ExistsGlobalAsync(command.PrincipalId, command.Payload.EntryId, cancellationToken);
        if (!exists)
            return RejectInvariant(command, EntityRefs.JournalNotFound);
        
        var deleted = await _journalRepository.DeleteGlobalAsync(command.PrincipalId, command.Payload.EntryId, cancellationToken);
        if (!deleted)
            return RejectInvariant(command, EntityRefs.JournalDeleteFailed);

        var result = new GlobalJournalCommandResult(command.PrincipalId, command.Payload.EntryId, Replay: false);

        await _eventBus.PublishAsync(new GlobalJournalEntryDeletedEvent(command.PrincipalId, command.Payload.EntryId), cancellationToken);
        return CommandExecutionResult<GlobalJournalCommandResult>.Success(result);
    }

}
