using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Journals;

public sealed class SetGlobalJournalPinnedCommandHandler : IdempotentCommandHandler<SetGlobalJournalPinnedCommand, GlobalJournalCommandResult>
{
    private readonly IJournalRepository _journalRepository;
    private readonly IClusterEventBus _eventBus;

    public SetGlobalJournalPinnedCommandHandler(
        IJournalRepository journalRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _journalRepository = journalRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.JournalGlobalSetPinned;

protected override async Task<CommandExecutionResult<GlobalJournalCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<SetGlobalJournalPinnedCommand> command,
        CancellationToken cancellationToken = default)
    {

        var exists = await _journalRepository.ExistsGlobalAsync(command.PrincipalId, command.Payload.EntryId, cancellationToken);
        if (!exists)
            return RejectInvariant(command, EntityRefs.JournalNotFound);

        var updated = await _journalRepository.SetGlobalPinnedAsync(
            command.PrincipalId, command.Payload.EntryId, command.Payload.Pinned, cancellationToken);
        if (!updated)
            return RejectInvariant(command, EntityRefs.JournalUpdateFailed);

        var result = new GlobalJournalCommandResult(command.PrincipalId, command.Payload.EntryId, Replay: false);

        await _eventBus.PublishAsync(new GlobalJournalEntryUpdatedEvent(command.PrincipalId, command.Payload.EntryId), cancellationToken);
        return CommandExecutionResult<GlobalJournalCommandResult>.Success(result);
    }

}
