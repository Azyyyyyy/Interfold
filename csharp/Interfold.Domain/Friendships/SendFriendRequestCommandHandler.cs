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

public sealed class SendFriendRequestCommandHandler : ICommandHandler<SendFriendRequestCommand, FriendshipCommandResult>
{
    private readonly IFriendshipRepository _repository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public SendFriendRequestCommandHandler(
        IFriendshipRepository repository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
    {
        _repository = repository;
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<FriendshipCommandResult>> HandleAsync(
        CommandEnvelope<SendFriendRequestCommand> command,
        CancellationToken cancellationToken = default)
    {
        var payloadJson = CommandSerialization.Serialize(command.Payload);
        var payloadHash = CommandSerialization.Hash(payloadJson);

        var previous = await _idempotencyStore.FindAsync(
            command.PrincipalId, command.OperationId, command.IdempotencyKey, cancellationToken);

        if (previous is not null)
        {
            if (!string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
            {
                return RejectDuplicate(command, EntityRefs.FriendRequestSend);
            }

            var replay = CommandSerialization.Deserialize<FriendshipCommandResult>(previous.OutcomePayload);
            if (replay is not null)
            {
                return CommandExecutionResult<FriendshipCommandResult>.Success(replay with { Replay = true });
            }
        }

        var resolvedTargetSystemId = await _repository.ResolveUserIdAsync(
            command.Payload.TargetSystemId.Value,
            cancellationToken);

        if (resolvedTargetSystemId is null)
        {
            return RejectInvariant(command, EntityRefs.FriendRequestNoUser);
        }

        var targetSystemId = resolvedTargetSystemId.Value;

        var outcome = await _repository.SendRequestAsync(
            command.PrincipalId,
            targetSystemId,
            cancellationToken);

        if (outcome is SendFriendRequestOutcome.AlreadyFriends)
        {
            return RejectInvariant(command, EntityRefs.FriendRequestAlreadyFriends);
        }

        if (outcome is SendFriendRequestOutcome.AlreadySent)
        {
            return RejectInvariant(command, EntityRefs.FriendRequestAlreadySent);
        }

        if (outcome is SendFriendRequestOutcome.NoUser)
        {
            return RejectInvariant(command, EntityRefs.FriendRequestNoUser);
        }

        var action = outcome is SendFriendRequestOutcome.Accepted ? FriendshipAction.Accepted : FriendshipAction.Sent;

        var result = new FriendshipCommandResult(
            command.PrincipalId,
            targetSystemId,
            action,
            Replay: false);

        var resultJson = CommandSerialization.Serialize(result);

        await _idempotencyStore.SaveAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            payloadHash,
            CommandSerialization.Hash(resultJson),
            resultJson,
            cancellationToken);

        //TODO: Check this path sends the required events
        if (outcome is SendFriendRequestOutcome.Accepted)
        {
            await _eventBus.PublishAsync(new FriendshipAddedEvent(
                command.PrincipalId,
                targetSystemId), cancellationToken);

            await _eventBus.PublishAsync(new FriendshipAddedEvent(
                targetSystemId,
                command.PrincipalId), cancellationToken);

            await _eventBus.PublishAsync(new FriendRequestRemovedToEvent(
                targetSystemId,
                command.PrincipalId), cancellationToken);
        }
        else
        {
            await _eventBus.PublishAsync(new FriendRequestSentEvent(
                command.PrincipalId,
                targetSystemId), cancellationToken);

            await _eventBus.PublishAsync(new FriendRequestReceivedEvent(
                targetSystemId,
                command.PrincipalId), cancellationToken);
        }

        return CommandExecutionResult<FriendshipCommandResult>.Success(result);
    }

    private static CommandExecutionResult<FriendshipCommandResult> RejectDuplicate(
        CommandEnvelope<SendFriendRequestCommand> command,
        EntityRef entityRef)
        => CommandExecutionResult<FriendshipCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, entityRef, ResolutionHint.NoRetry));

    private static CommandExecutionResult<FriendshipCommandResult> RejectInvariant(
        CommandEnvelope<SendFriendRequestCommand> command,
        EntityRef entityRef)
        => CommandExecutionResult<FriendshipCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, ResolutionHint.ManualMergeRequired));
}
