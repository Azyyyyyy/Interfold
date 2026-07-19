using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Alters;

public sealed class DeleteAlterCommandHandler : IdempotentCommandHandler<DeleteAlterCommand, AlterCommandResult>
{
    private readonly IAlterRepository _alterRepository;
    private readonly IJournalRepository _journalRepository;
    private readonly IClusterEventBus _eventBus;

    public DeleteAlterCommandHandler(
        IAlterRepository alterRepository,
        IJournalRepository journalRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _alterRepository = alterRepository;
        _journalRepository = journalRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.AlterDelete;

protected override async Task<CommandExecutionResult<AlterCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<DeleteAlterCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (RejectIfAlterIdOutOfRange(command, command.Payload.AlterId, EntityRefs.AlterId) is { } rangeReject)
            return rangeReject;

        var exists = await _alterRepository.ExistsAsync(command.PrincipalId, command.Payload.AlterId, cancellationToken);
        if (!exists)
            return RejectInvariant(command, EntityRefs.AlterNotFound);

        // Cascade BEFORE the alter row itself is removed so a journal-cleanup failure
        // leaves the alter intact (caller can retry); the inverse order would orphan the
        // journals if the alter delete succeeded but cleanup later threw. DeleteAllForAlterAsync
        // also detaches the alter from any global journals it was attached to without
        // deleting the global journal itself (multiple alters can share a group journal).
        await _journalRepository.DeleteAllForAlterAsync(command.PrincipalId, command.Payload.AlterId, cancellationToken);

        var deleted = await _alterRepository.DeleteAsync(command.PrincipalId, command.Payload.AlterId, cancellationToken);
        if (!deleted)
            return RejectInvariant(command, EntityRefs.AlterDeleteFailed);

        //TODO: Delete alter image if it exists

        var result = new AlterCommandResult(command.PrincipalId, command.Payload.AlterId, Replay: false);

        await _eventBus.PublishAsync(
            new AlterDeletedEvent(command.PrincipalId, command.Payload.AlterId),
            cancellationToken);

        return CommandExecutionResult<AlterCommandResult>.Success(result);
    }

}
