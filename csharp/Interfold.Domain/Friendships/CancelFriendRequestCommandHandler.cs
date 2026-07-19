using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Friendships;

public sealed class CancelFriendRequestCommandHandler : IdempotentCommandHandler<CancelFriendRequestCommand, FriendshipCommandResult>
{
    private readonly IFriendshipRepository _repository;
    private readonly IClusterEventBus _eventBus;

    public CancelFriendRequestCommandHandler(
        IFriendshipRepository repository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _repository = repository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.FriendRequestCancel;

protected override async Task<CommandExecutionResult<FriendshipCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<CancelFriendRequestCommand> command,
        CancellationToken cancellationToken = default)
    {
        
        var canonicalTargetSystemId = ScopedSystemId.Compose(
            command.PrincipalId.Region,
            command.Payload.TargetSystemId);

        var canonicalPrincipalId = FriendshipIdNormalization.CanonicalizeForPrincipal(
            command.Payload.TargetSystemId,
            command.PrincipalId);

        var outcome = await _repository.CancelRequestAsync(
            command.PrincipalId,
            canonicalTargetSystemId,
            cancellationToken);

        if (outcome.ToRejectionEntityRef() is { } er)
        {
            return RejectInvariant(command, er);
        }

        var result = new FriendshipCommandResult(
            command.PrincipalId,
            canonicalTargetSystemId,
            FriendshipAction.Cancelled,
            Replay: false);

        await _eventBus.PublishAsync(new FriendRequestRemovedToEvent(
            canonicalPrincipalId,
            canonicalTargetSystemId), cancellationToken);

        await _eventBus.PublishAsync(new FriendRequestRemovedFromEvent(
            canonicalTargetSystemId,
            canonicalPrincipalId), cancellationToken);

        return CommandExecutionResult<FriendshipCommandResult>.Success(result);
    }

}
