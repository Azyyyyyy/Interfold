using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Fronting;

public sealed class BulkUpdateFrontCommandHandler : IdempotentCommandHandler<BulkUpdateFrontCommand, FrontCommandResult>
{
    private readonly IFrontingRepository _frontingRepository;
    private readonly IClusterEventBus _eventBus;

    public BulkUpdateFrontCommandHandler(
        IFrontingRepository frontingRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _frontingRepository = frontingRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.FrontingBulkUpdate;

    protected override FrontCommandResult CreateReplayResult(FrontCommandResult originalResult) =>
        originalResult with { Replay = true };
protected override async Task<CommandExecutionResult<FrontCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<BulkUpdateFrontCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (command.Payload.Start.Any(x => x.AlterId.Value is < 1 or > 32_767) ||
            command.Payload.End.Any(x => x.Value is < 1 or > 32_767))
            return RejectInvariant(command, EntityRefs.FrontingInvalidAlterId);

        if (command.Payload.Start.Any(x => (x.Comment?.Length ?? 0) > 50))
            return RejectInvariant(command, EntityRefs.FrontingInvalidComment);

        foreach (var alterId in command.Payload.End)
        {
            await _frontingRepository.EndAsync(command.PrincipalId, alterId, DateTimeOffset.UtcNow, cancellationToken);
        }

        foreach (var item in command.Payload.Start)
        {
            var alreadyFronting = await _frontingRepository.IsFrontingAsync(command.PrincipalId, item.AlterId, cancellationToken);
            if (!alreadyFronting)
                await _frontingRepository.StartAsync(command.PrincipalId, item.AlterId, item.Comment, DateTimeOffset.UtcNow, cancellationToken);
        }

        var result = new FrontCommandResult(command.PrincipalId, null, null, Replay: false);

        await _eventBus.PublishAsync(new FrontingStateChangedEvent(command.PrincipalId), cancellationToken);

        // Emit granular event for socket layer to handle fronting_bulk
        await _eventBus.PublishAsync(new FrontingBulkUpdatedEvent(command.PrincipalId), cancellationToken);

        return CommandExecutionResult<FrontCommandResult>.Success(result);
    }

    private static CommandExecutionResult<FrontCommandResult> RejectInvariant(
        CommandEnvelope<BulkUpdateFrontCommand> command,
        EntityRef entityRef) =>
        CommandExecutionResult<FrontCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, ResolutionHint.ManualMergeRequired));
}