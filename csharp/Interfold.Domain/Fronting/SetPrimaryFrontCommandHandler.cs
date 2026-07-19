using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Fronting;

public sealed class SetPrimaryFrontCommandHandler : IdempotentCommandHandler<SetPrimaryFrontCommand, FrontCommandResult>
{
    private readonly IFrontingRepository _frontingRepository;
    private readonly IClusterEventBus _eventBus;

    public SetPrimaryFrontCommandHandler(
        IFrontingRepository frontingRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus
    )
:base(idempotencyStore)    {
        _frontingRepository = frontingRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.FrontingPrimary;

protected override async Task<CommandExecutionResult<FrontCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<SetPrimaryFrontCommand> command,
        CancellationToken cancellationToken = default
    )
    {
        if (command.Payload.AlterId is { } alterId)
        {
            if (RejectIfAlterIdOutOfRange(command, alterId, EntityRefs.FrontingInvalidAlterId) is { } rangeReject)
                return rangeReject;

            var fronting = await _frontingRepository.IsFrontingAsync(command.PrincipalId, alterId, cancellationToken);
            if (!fronting)
            {
                return RejectInvariant(command, EntityRefs.FrontingNotFronting);
            }
        }

        var set = await _frontingRepository.SetPrimaryAsync(command.PrincipalId, command.Payload.AlterId, cancellationToken);
        if (!set)
        {
            return RejectInvariant(command, EntityRefs.FrontingPrimaryFailed);
        }

        var result = new FrontCommandResult(command.PrincipalId, command.Payload.AlterId, FrontId: null, Replay: false);

        await _eventBus.PublishAsync(new FrontingStateChangedEvent(command.PrincipalId), cancellationToken);
        await _eventBus.PublishAsync(new FrontingPrimaryChangedEvent(command.PrincipalId, command.Payload.AlterId), cancellationToken);
        await _eventBus.PublishAsync(new SettingsProfileUpdatedEvent(command.PrincipalId, false), cancellationToken);

        return CommandExecutionResult<FrontCommandResult>.Success(result);
    }

}