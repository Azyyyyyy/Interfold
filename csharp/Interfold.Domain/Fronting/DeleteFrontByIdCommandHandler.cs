using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Models.Read;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Fronting;

public sealed class DeleteFrontByIdCommandHandler : IdempotentCommandHandler<DeleteFrontByIdCommand, FrontCommandResult>
{
    private readonly IFrontingRepository _frontingRepository;
    private readonly IClusterEventBus _eventBus;

    public DeleteFrontByIdCommandHandler(
        IFrontingRepository frontingRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _frontingRepository = frontingRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.FrontingDelete;

    protected override FrontCommandResult CreateReplayResult(FrontCommandResult originalResult) =>
        originalResult with { Replay = true };
protected override async Task<CommandExecutionResult<FrontCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<DeleteFrontByIdCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (command.Payload.FrontId == FrontId.Empty)
            return RejectInvariant(command, EntityRefs.FrontingInvalidFrontId);

        var existing = await _frontingRepository.GetActiveByFrontIdAsync(command.PrincipalId, command.Payload.FrontId, cancellationToken);
        FrontHistoryReadModel? existingHistory = null;
        if (existing is null)
        {
            existingHistory = await _frontingRepository.GetHistoryEntryByFrontIdAsync(command.PrincipalId, command.Payload.FrontId, cancellationToken);
            if (existingHistory is null)
                return RejectInvariant(command, EntityRefs.FrontingNoFront);
        }

        if (existing is not null)
        {
            var deleted = await _frontingRepository.EndByFrontIdAsync(command.PrincipalId, command.Payload.FrontId, cancellationToken);
            if (!deleted)
                return RejectInvariant(command, EntityRefs.FrontingDeleteFailed);
        }

        var deletedFromHistory = await _frontingRepository.DeleteFrontByIdAsync(command.PrincipalId, command.Payload.FrontId, cancellationToken);
        if (!deletedFromHistory)
            return RejectInvariant(command, EntityRefs.FrontingDeleteFailed);

        var alterId = existing?.Front.AlterId ?? existingHistory!.AlterId;
        var result = new FrontCommandResult(command.PrincipalId, alterId, command.Payload.FrontId, Replay: false);

        if (existing is not null)
            await _eventBus.PublishAsync(new FrontingStateChangedEvent(command.PrincipalId), cancellationToken);
        await _eventBus.PublishAsync(new FrontDeletedEvent(command.PrincipalId, command.Payload.FrontId), cancellationToken);

        return CommandExecutionResult<FrontCommandResult>.Success(result);
    }

    private static CommandExecutionResult<FrontCommandResult> RejectInvariant(
        CommandEnvelope<DeleteFrontByIdCommand> command,
        EntityRef entityRef) =>
        CommandExecutionResult<FrontCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, ResolutionHint.ManualMergeRequired));
}