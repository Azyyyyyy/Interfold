using Interfold.Friendships.Contracts;
using Interfold.Friendships.Contracts.Enums;
using Interfold.Friendships.Contracts.Models.Commands;
using Interfold.Friendships.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Friendships.Domain;

public sealed class AcceptFriendRequestCommandHandler : IdempotentCommandHandler<AcceptFriendRequestCommand, FriendshipCommandResult>
{
    private readonly IFriendshipRepository _repository;
    private readonly IClusterEventBus _eventBus;

    public AcceptFriendRequestCommandHandler(
        IFriendshipRepository repository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus) : base(idempotencyStore)   
    {
        _repository = repository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.FriendRequestAccept;

    protected override async Task<CommandExecutionResult<FriendshipCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<AcceptFriendRequestCommand> command,
        CancellationToken cancellationToken)
    {
        var outcome = await _repository.AcceptRequestAsync(
            command.PrincipalId,
            command.Payload.SourceSystemId,
            cancellationToken);

        if (FriendshipCommandFlow.RejectIfMutationOutcomeFailed(command, outcome) is { } rejection)
            return rejection;

        await _eventBus.PublishFriendshipAddedBothWaysAsync(
            command.PrincipalId,
            command.Payload.SourceSystemId,
            cancellationToken);

        await _eventBus.PublishRequestRemovedFromThenToAsync(
            command.PrincipalId,
            command.Payload.SourceSystemId,
            cancellationToken);

        return FriendshipCommandFlow.Success(command.PrincipalId, command.Payload.SourceSystemId, FriendshipAction.Accepted);
    }
}
