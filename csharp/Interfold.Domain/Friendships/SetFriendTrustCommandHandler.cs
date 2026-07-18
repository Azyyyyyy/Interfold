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

public sealed class SetFriendTrustCommandHandler : IdempotentCommandHandler<SetFriendTrustCommand, FriendshipCommandResult>
{
    private readonly IFriendshipRepository _repository;
    private readonly IClusterEventBus _eventBus;

    public SetFriendTrustCommandHandler(
        IFriendshipRepository repository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _repository = repository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.FriendshipTrust;

    protected override FriendshipCommandResult CreateReplayResult(FriendshipCommandResult originalResult) =>
        originalResult with { Replay = true };
protected override async Task<CommandExecutionResult<FriendshipCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<SetFriendTrustCommand> command,
        CancellationToken cancellationToken = default)
    {

        var canonicalFriendSystemId = ScopedSystemId.Compose(
            command.PrincipalId.Region,
            command.Payload.FriendSystemId);

        var updated = await _repository.SetTrustedAsync(
            command.PrincipalId,
            canonicalFriendSystemId,
            command.Payload.Trusted,
            cancellationToken);

        if (!updated)
        {
            return RejectInvariant(command, EntityRefs.FriendshipNotFound);
        }

        var result = new FriendshipCommandResult(
            command.PrincipalId,
            canonicalFriendSystemId,
            command.Payload.Trusted ? FriendshipAction.Trusted : FriendshipAction.Untrusted,
            Replay: false);

        if (command.Payload.Trusted)
        {
            await _eventBus.PublishAsync(
                new FriendshipTrustedEvent(command.PrincipalId, canonicalFriendSystemId),
                cancellationToken);
        }
        else
        {
            await _eventBus.PublishAsync(
                new FriendshipUntrustedEvent(command.PrincipalId, canonicalFriendSystemId),
                cancellationToken);
        }

        return CommandExecutionResult<FriendshipCommandResult>.Success(result);
    }

    private static CommandExecutionResult<FriendshipCommandResult> RejectInvariant(
        CommandEnvelope<SetFriendTrustCommand> command,
        EntityRef entityRef)
        => CommandExecutionResult<FriendshipCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, ResolutionHint.ManualMergeRequired));
}
