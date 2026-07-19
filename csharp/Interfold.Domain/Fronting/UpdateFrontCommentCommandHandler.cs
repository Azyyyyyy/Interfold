using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Fronting;

public sealed class UpdateFrontCommentCommandHandler : IdempotentCommandHandler<UpdateFrontCommentCommand, FrontCommandResult>
{
    private readonly IFrontingRepository _frontingRepository;
    private readonly IClusterEventBus _eventBus;

    public UpdateFrontCommentCommandHandler(
        IFrontingRepository frontingRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _frontingRepository = frontingRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.FrontingUpdateComment;

    protected override FrontCommandResult CreateReplayResult(FrontCommandResult originalResult) =>
        originalResult with { Replay = true };
protected override async Task<CommandExecutionResult<FrontCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<UpdateFrontCommentCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (command.Payload.FrontId == FrontId.Empty)
            return RejectInvariant(command, EntityRefs.FrontingInvalidFrontId);

        if ((command.Payload.Comment?.Length ?? 0) > 50)
            return RejectInvariant(command, EntityRefs.FrontingInvalidComment);

        var existing = await _frontingRepository.GetActiveByFrontIdAsync(command.PrincipalId, command.Payload.FrontId, cancellationToken);
        if (existing is null)
            return RejectInvariant(command, EntityRefs.FrontingNoFront);

        var updated = await _frontingRepository.UpdateCommentByFrontIdAsync(
            command.PrincipalId,
            command.Payload.FrontId,
            command.Payload.Comment ?? string.Empty,
            cancellationToken);

        if (!updated)
            return RejectInvariant(command, EntityRefs.FrontingUpdateCommentFailed);

        var result = new FrontCommandResult(command.PrincipalId, existing.Front.AlterId, command.Payload.FrontId, Replay: false);

        await _eventBus.PublishAsync(new FrontingStateChangedEvent(command.PrincipalId), cancellationToken);

        // Emit granular event for socket layer to handle front_updated
        await _eventBus.PublishAsync(new FrontCommentUpdatedEvent(command.PrincipalId, command.Payload.FrontId), cancellationToken);

        return CommandExecutionResult<FrontCommandResult>.Success(result);
    }

}
