using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Polls;

public sealed class UpdatePollCommandHandler : IdempotentCommandHandler<UpdatePollCommand, PollCommandResult>
{
    private readonly IPollRepository _pollRepository;
    private readonly IClusterEventBus _eventBus;

    public UpdatePollCommandHandler(
        IPollRepository pollRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _pollRepository = pollRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.PollUpdate;

    protected override PollCommandResult CreateReplayResult(PollCommandResult originalResult) =>
        originalResult with { Replay = true };
protected override async Task<CommandExecutionResult<PollCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<UpdatePollCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (command.Payload.Title is null && command.Payload.Description is null &&
            !command.Payload.HasTimeEnd && command.Payload.Data is null)
            return RejectInvariant(command, EntityRefs.PollNoFields);

        if (command.Payload.Title is not null && command.Payload.Title.Length > 100)
            return RejectInvariant(command, EntityRefs.PollTitleTooLong);

        if (command.Payload.Description is not null && command.Payload.Description.Length > 2000)
            return RejectInvariant(command, EntityRefs.PollDescriptionTooLong);

        var exists = await _pollRepository.ExistsAsync(command.PrincipalId, command.Payload.Id, cancellationToken);
        if (!exists)
            return RejectInvariant(command, EntityRefs.PollNotFound);
        
        var updated = await _pollRepository.UpdateAsync(command.PrincipalId, command.Payload, cancellationToken);
        if (!updated)
            return RejectInvariant(command, EntityRefs.PollUpdateFailed);

        var result = new PollCommandResult(command.PrincipalId, command.Payload.Id, Replay: false);

        await _eventBus.PublishAsync(new PollUpdatedEvent(command.PrincipalId, command.Payload.Id), cancellationToken);
        return CommandExecutionResult<PollCommandResult>.Success(result);
    }

    private static CommandExecutionResult<PollCommandResult> RejectInvariant(
        CommandEnvelope<UpdatePollCommand> command, EntityRef entityRef) =>
        CommandExecutionResult<PollCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, ResolutionHint.ManualMergeRequired));
}
