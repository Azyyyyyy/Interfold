using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Friendships;

public sealed class RemoveFriendshipCommandHandler : IdempotentCommandHandler<RemoveFriendshipCommand, FriendshipCommandResult>
{
    private readonly IFriendshipRepository _repository;
    private readonly IClusterEventBus _eventBus;

    public RemoveFriendshipCommandHandler(
        IFriendshipRepository repository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _repository = repository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.FriendshipRemove;

    protected override FriendshipCommandResult CreateReplayResult(FriendshipCommandResult originalResult) =>
        originalResult with { Replay = true };
protected override async Task<CommandExecutionResult<FriendshipCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<RemoveFriendshipCommand> command,
        CancellationToken cancellationToken = default)
    {

        var canonicalFriendSystemId = ScopedSystemId.Compose(
            command.PrincipalId.Region,
            command.Payload.FriendSystemId);
        var canonicalPrincipalId = FriendshipIdNormalization.CanonicalizeForPrincipal(
            canonicalFriendSystemId,
            command.PrincipalId);

        var deleted = await _repository.RemoveFriendshipAsync(
            command.PrincipalId,
            canonicalFriendSystemId,
            cancellationToken);

        if (!deleted)
        {
            return RejectInvariant(command, EntityRefs.FriendshipNotFound);
        }

        var result = new FriendshipCommandResult(
            command.PrincipalId,
            canonicalFriendSystemId,
            FriendshipAction.Removed,
            Replay: false);

        await _eventBus.PublishAsync(new FriendshipRemovedEvent(
            canonicalPrincipalId,
            canonicalFriendSystemId), cancellationToken);

        await _eventBus.PublishAsync(new FriendshipRemovedEvent(
            canonicalFriendSystemId,
            canonicalPrincipalId), cancellationToken);

        return CommandExecutionResult<FriendshipCommandResult>.Success(result);
    }

    private static CommandExecutionResult<FriendshipCommandResult> RejectInvariant(
        CommandEnvelope<RemoveFriendshipCommand> command,
        EntityRef entityRef)
        => CommandExecutionResult<FriendshipCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, ResolutionHint.ManualMergeRequired));
}
