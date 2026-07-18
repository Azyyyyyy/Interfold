using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Fronting;

public sealed class EndFrontCommandHandler : IdempotentCommandHandler<EndFrontCommand, FrontCommandResult>
{
    private readonly IFrontingRepository _frontingRepository;
    private readonly IClusterEventBus _eventBus;

    public EndFrontCommandHandler(
        IFrontingRepository frontingRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus
    )
:base(idempotencyStore)    {
        _frontingRepository = frontingRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.FrontingEnd;

    protected override FrontCommandResult CreateReplayResult(FrontCommandResult originalResult) =>
        originalResult with { Replay = true };
protected override async Task<CommandExecutionResult<FrontCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<EndFrontCommand> command,
        CancellationToken cancellationToken = default
    )
    {
        if (command.Payload.AlterId.Value is < 1 or > 32_767)
        {
            return RejectInvariant(command, EntityRefs.FrontingInvalidAlterId);
        }

        var fronting = await _frontingRepository.IsFrontingAsync(command.PrincipalId, command.Payload.AlterId, cancellationToken);
        if (!fronting)
        {
            return RejectInvariant(command, EntityRefs.FrontingNotFronting);
        }

        var activeFronts = await _frontingRepository.ListActiveAsync(command.PrincipalId, cancellationToken);
        var endedFrontWasPrimary = activeFronts.Any(front =>
            front.Alter.Id == command.Payload.AlterId && front.Primary);

        var ended = await _frontingRepository.EndAsync(command.PrincipalId, command.Payload.AlterId, DateTimeOffset.UtcNow, cancellationToken);
        if (!ended)
        {
            return RejectInvariant(command, EntityRefs.FrontingEndFailed);
        }

        var result = new FrontCommandResult(command.PrincipalId, command.Payload.AlterId, FrontId: null, Replay: false);

        await _eventBus.PublishAsync(new FrontingStateChangedEvent(command.PrincipalId), cancellationToken);

        // Emit granular event for socket layer to handle fronting_ended
        await _eventBus.PublishAsync(new FrontingEndedEvent(command.PrincipalId, command.Payload.AlterId), cancellationToken);

        if (endedFrontWasPrimary)
        {
            await _eventBus.PublishAsync(new FrontingPrimaryChangedEvent(command.PrincipalId, null), cancellationToken);
        }

        return CommandExecutionResult<FrontCommandResult>.Success(result);
    }

    private static CommandExecutionResult<FrontCommandResult> RejectInvariant(
        CommandEnvelope<EndFrontCommand> command,
        EntityRef entityRef
    ) =>
        CommandExecutionResult<FrontCommandResult>.Rejected(
            new ConflictResult(
                ConflictCode.ConflictInvariant,
                command.OperationId,
                entityRef,
                ResolutionHint.ManualMergeRequired
            )
        );
}