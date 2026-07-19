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

public sealed class AcceptFriendRequestCommandHandler : IdempotentCommandHandler<AcceptFriendRequestCommand, FriendshipCommandResult>
{
    private readonly IFriendshipRepository _repository;
    private readonly IClusterEventBus _eventBus;

    public AcceptFriendRequestCommandHandler(
        IFriendshipRepository repository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _repository = repository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.FriendRequestAccept;

    protected override FriendshipCommandResult CreateReplayResult(FriendshipCommandResult originalResult) =>
        originalResult with { Replay = true };
protected override async Task<CommandExecutionResult<FriendshipCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<AcceptFriendRequestCommand> command,
        CancellationToken cancellationToken = default)
    {

        var canonicalSourceSystemId = ScopedSystemId.Compose(
            command.PrincipalId.Region,
            command.Payload.SourceSystemId);

        var canonicalPrincipalId = FriendshipIdNormalization.CanonicalizeForPrincipal(
            command.Payload.SourceSystemId,
            command.PrincipalId);

        var outcome = await _repository.AcceptRequestAsync(
            command.PrincipalId,
            canonicalSourceSystemId,
            cancellationToken);

        if (outcome is FriendRequestMutationOutcome.AlreadyFriends)
        {
            return RejectInvariant(command, EntityRefs.FriendRequestAlreadyFriends);
        }

        if (outcome is FriendRequestMutationOutcome.NotRequested)
        {
            return RejectInvariant(command, EntityRefs.FriendRequestNotRequested);
        }

        if (outcome is FriendRequestMutationOutcome.NoUser)
        {
            return RejectInvariant(command, EntityRefs.FriendRequestNoUser);
        }

        var result = new FriendshipCommandResult(
            command.PrincipalId,
            canonicalSourceSystemId,
            FriendshipAction.Accepted,
            Replay: false);

        await _eventBus.PublishAsync(new FriendshipAddedEvent(
            canonicalPrincipalId,
            canonicalSourceSystemId), cancellationToken);

        await _eventBus.PublishAsync(new FriendshipAddedEvent(
            canonicalSourceSystemId,
            canonicalPrincipalId), cancellationToken);

        await _eventBus.PublishAsync(new FriendRequestRemovedFromEvent(
            canonicalPrincipalId,
            canonicalSourceSystemId), cancellationToken);

        await _eventBus.PublishAsync(new FriendRequestRemovedToEvent(
            canonicalSourceSystemId,
            canonicalPrincipalId), cancellationToken);

        return CommandExecutionResult<FriendshipCommandResult>.Success(result);
    }

}
