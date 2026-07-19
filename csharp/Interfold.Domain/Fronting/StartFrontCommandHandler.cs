using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Fronting;

public sealed class StartFrontCommandHandler : IdempotentCommandHandler<StartFrontCommand, FrontCommandResult>
{
    private readonly IFrontingRepository _frontingRepository;
    private readonly IClusterEventBus _eventBus;
    private readonly TimeProvider _timeProvider;

    public StartFrontCommandHandler(
        IFrontingRepository frontingRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus,
        TimeProvider timeProvider) : base(idempotencyStore)
    {
        _frontingRepository = frontingRepository;
        _eventBus = eventBus;
        _timeProvider = timeProvider;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.FrontingStart;

    protected override FrontCommandResult CreateReplayResult(FrontCommandResult originalResult) =>
        originalResult with { Replay = true };

    protected override async Task<CommandExecutionResult<FrontCommandResult>> ExecuteCoreAsync(
            CommandEnvelope<StartFrontCommand> command,
            CancellationToken cancellationToken = default
        )
    {
        if (command.Payload.AlterId.Value is < 1 or > 32_767)
        {
            return RejectInvariant(command, EntityRefs.FrontingInvalidAlterId);
        }

        if ((command.Payload.Comment?.Length ?? 0) > 50)
        {
            return RejectInvariant(command, EntityRefs.FrontingInvalidComment);
        }

        var alreadyFronting = await _frontingRepository.IsFrontingAsync(
            command.PrincipalId,
            command.Payload.AlterId,
            cancellationToken
        );

        if (alreadyFronting)
        {
            return RejectInvariant(command, EntityRefs.FrontingAlreadyFronting);
        }

        var frontId = await _frontingRepository.StartAsync(
            command.PrincipalId,
            command.Payload.AlterId,
            command.Payload.Comment,
            _timeProvider.GetUtcNow(),
            cancellationToken
        );

        if (frontId is null)
        {
            return RejectInvariant(command, EntityRefs.FrontingStartFailed);
        }

        var result = new FrontCommandResult(command.PrincipalId, command.Payload.AlterId, frontId, Replay: false);

        await _eventBus.PublishAsync(new FrontingStateChangedEvent(command.PrincipalId), cancellationToken);

        // Emit granular event for socket layer to handle fronting_started
        await _eventBus.PublishAsync(new FrontingStartedEvent(command.PrincipalId, frontId.Value), cancellationToken);

        return CommandExecutionResult<FrontCommandResult>.Success(result);
    }

}