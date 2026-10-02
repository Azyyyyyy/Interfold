using Interfold.Fronting.Contracts;
using Interfold.Fronting.Contracts.Models.Commands;
using Interfold.Fronting.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Fronting.Domain;

public sealed class DeleteFrontByIdCommandHandler : IdempotentCommandHandler<DeleteFrontByIdCommand, FrontCommandResult>
{
    private readonly IFrontingRepository _frontingRepository;
    private readonly IStorageTransactionFactory _transactions;
    private readonly IClusterEventBus _eventBus;

    public DeleteFrontByIdCommandHandler(
        IFrontingRepository frontingRepository,
        IStorageTransactionFactory transactions,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
        : base(idempotencyStore)
    {
        _frontingRepository = frontingRepository;
        _transactions = transactions;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.FrontingDelete;

protected override async Task<CommandExecutionResult<FrontCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<DeleteFrontByIdCommand> command,
        CancellationToken cancellationToken = default)
    {
        var (resolution, rejection) = await FrontingCommandFlow.ResolveFrontByIdAfterValidationOrRejectAsync(
            command,
            _frontingRepository,
            command.Payload.FrontId,
            cancellationToken);
        if (rejection is not null)
            return rejection;

        await using var transaction = await _transactions.BeginAsync(cancellationToken);
        if (resolution!.WasActive)
        {
            var ended = await _frontingRepository.EndByFrontIdAsync(
                command.PrincipalId, command.Payload.FrontId, cancellationToken);
            if (FrontingCommandFlow.RejectIfMutationFailed(command, ended, EntityRefs.FrontingDeleteFailed) is { } deleteReject)
                return deleteReject;
        }

        var removed = await _frontingRepository.DeleteFrontByIdAsync(
            command.PrincipalId, command.Payload.FrontId, cancellationToken);
        if (FrontingCommandFlow.RejectIfMutationFailed(command, removed, EntityRefs.FrontingDeleteFailed) is { } historyDeleteReject)
            return historyDeleteReject;

        await transaction.CommitAsync(cancellationToken);

        var alterId = resolution.AlterId;

        await _eventBus.PublishDeletedAsync(command.PrincipalId, command.Payload.FrontId, resolution.WasActive, cancellationToken);

        return FrontingCommandFlow.Success(command.PrincipalId, alterId, command.Payload.FrontId);
    }

}