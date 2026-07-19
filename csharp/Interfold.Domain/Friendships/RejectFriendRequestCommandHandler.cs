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

public sealed class RejectFriendRequestCommandHandler : IdempotentCommandHandler<RejectFriendRequestCommand, FriendshipCommandResult>
{
    private readonly IFriendshipRepository _repository;
    private readonly IClusterEventBus _eventBus;

    public RejectFriendRequestCommandHandler(
        IFriendshipRepository repository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _repository = repository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.FriendRequestReject;

protected override async Task<CommandExecutionResult<FriendshipCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<RejectFriendRequestCommand> command,
        CancellationToken cancellationToken = default)
    {

        var canonicalSourceSystemId = ScopedSystemId.Compose(
            command.PrincipalId.Region,
            command.Payload.SourceSystemId);
        var canonicalPrincipalId = FriendshipIdNormalization.CanonicalizeForPrincipal(
            canonicalSourceSystemId,
            command.PrincipalId);

        var outcome = await _repository.RejectRequestAsync(
            command.PrincipalId,
            canonicalSourceSystemId,
            cancellationToken);

        if (outcome.ToRejectionEntityRef() is { } er)
        {
            return RejectInvariant(command, er);
        }

        var result = new FriendshipCommandResult(
            command.PrincipalId,
            canonicalSourceSystemId,
            FriendshipAction.Rejected,
            Replay: false);

        await _eventBus.PublishAsync(new FriendRequestRemovedFromEvent(
            canonicalPrincipalId,
            canonicalSourceSystemId), cancellationToken);

        await _eventBus.PublishAsync(new FriendRequestRemovedToEvent(
            canonicalSourceSystemId,
            canonicalPrincipalId), cancellationToken);

        return CommandExecutionResult<FriendshipCommandResult>.Success(result);
    }

}
