using Interfold.Alters.Contracts;
using Interfold.Alters.Contracts.Events;
using Interfold.Alters.Contracts.Models.Commands;
using Interfold.Alters.Domain.Abstractions.Repository;
using Interfold.Fronting.Contracts.Events;
using Interfold.Journals.Contracts.Events;
using Interfold.Polls.Contracts.Events;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Tags.Contracts.Events;

namespace Interfold.Alters.Domain;

public sealed class DeleteAlterCommandHandler : IdempotentCommandHandler<DeleteAlterCommand, AlterCommandResult>
{
    private readonly IAlterRepository _alterRepository;
    private readonly IAlterDeletion _deletion;
    private readonly IClusterEventBus _eventBus;

    public DeleteAlterCommandHandler(
        IAlterRepository alterRepository,
        IAlterDeletion deletion,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
        : base(idempotencyStore)
    {
        _alterRepository = alterRepository;
        _deletion = deletion;
        _eventBus = eventBus;
    }

    protected override EntityRef DuplicateEntityRef => EntityRefs.AlterDelete;

    protected override async Task<CommandExecutionResult<AlterCommandResult>> ExecuteCoreAsync(
        CommandEnvelope<DeleteAlterCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (AlterCommandFlow.RejectIfInvalidAlterId(command, command.Payload.AlterId, EntityRefs.AlterId) is { } rangeReject)
            return rangeReject;

        if (await AlterCommandFlow.RejectIfAlterNotFoundAsync(command, _alterRepository, command.Payload.AlterId, EntityRefs.AlterNotFound, cancellationToken) is { } notFoundReject)
            return notFoundReject;

        //TODO: Delete alter image if it exists

        var deletion = await _deletion.DeleteAsync(command.PrincipalId, command.Payload.AlterId, cancellationToken);
        if (deletion is null)
            return AlterCommandFlow.RejectIfMutationFailed(command, succeeded: false, EntityRefs.AlterDeleteFailed)!;

        await PublishCascadeAsync(command.PrincipalId, deletion, cancellationToken);
        await _eventBus.PublishAsync(new AlterDeletedEvent(command.PrincipalId, command.Payload.AlterId), cancellationToken);
        return AlterCommandFlow.Success(command.PrincipalId, command.Payload.AlterId);
    }

    private async ValueTask PublishCascadeAsync(
        ScopedSystemId systemId,
        AlterDeletionResult deletion,
        CancellationToken cancellationToken)
    {
        if (deletion.Fronts.HadActiveFront)
            await _eventBus.PublishAsync(new FrontingStateChangedEvent(systemId), cancellationToken);

        foreach (var frontId in deletion.Fronts.DeletedFrontIds)
            await _eventBus.PublishAsync(new FrontDeletedEvent(systemId, frontId), cancellationToken);

        if (deletion.Fronts.PrimaryCleared)
            await _eventBus.PublishAsync(new FrontingPrimaryChangedEvent(systemId, null), cancellationToken);

        foreach (var tagId in deletion.DetachedTagIds)
            await _eventBus.PublishAsync(new TagUpdatedEvent(systemId, tagId), cancellationToken);

        foreach (var entryId in deletion.Journals.DeletedEntryIds)
            await _eventBus.PublishAsync(new AlterJournalEntryDeletedEvent(systemId, entryId), cancellationToken);

        foreach (var entryId in deletion.Journals.DetachedGlobalJournalIds)
            await _eventBus.PublishAsync(new GlobalJournalEntryUpdatedEvent(systemId, entryId), cancellationToken);

        foreach (var pollId in deletion.UpdatedPollIds)
            await _eventBus.PublishAsync(new PollUpdatedEvent(systemId, pollId), cancellationToken);
    }
}
