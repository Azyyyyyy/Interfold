using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Journals;

public sealed class DeleteAlterJournalEntryCommandHandler : IdempotentCommandHandler<DeleteAlterJournalEntryCommand, AlterJournalCommandResult>
{
    private readonly IJournalRepository _journalRepository;
    private readonly IClusterEventBus _eventBus;

    public DeleteAlterJournalEntryCommandHandler(
        IJournalRepository journalRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _journalRepository = journalRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.JournalAlterDelete;

    protected override AlterJournalCommandResult CreateReplayResult(AlterJournalCommandResult originalResult) =>
        originalResult with { Replay = true };
protected override async Task<CommandExecutionResult<AlterJournalCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<DeleteAlterJournalEntryCommand> command,
        CancellationToken cancellationToken = default)
    {

        var alterRef = await _journalRepository.GetAlterRefAsync(command.PrincipalId, command.Payload.EntryId, cancellationToken);
        if (alterRef is null)
            return RejectInvariant(command, EntityRefs.JournalNotFound);

        var deleted = await _journalRepository.DeleteAlterAsync(command.PrincipalId, command.Payload.EntryId, cancellationToken);
        if (!deleted)
            return RejectInvariant(command, EntityRefs.JournalDeleteFailed);

        var result = new AlterJournalCommandResult(command.PrincipalId, command.Payload.EntryId, alterRef.AlterId, Replay: false);

        await _eventBus.PublishAsync(new AlterJournalEntryDeletedEvent(command.PrincipalId, command.Payload.EntryId), cancellationToken);
        return CommandExecutionResult<AlterJournalCommandResult>.Success(result);
    }

}
