using Interfold.Alters.Domain.Abstractions.Repository;
using Interfold.Settings.Contracts;
using Interfold.Settings.Contracts.Events;
using Interfold.Settings.Contracts.Models.Commands;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Ids;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;
using Interfold.Shared.Domain.Abstractions.Repository;

namespace Interfold.Settings.Domain.Settings;

public sealed class DeleteFieldCommandHandler : IdempotentCommandHandler<DeleteFieldCommand, SettingsCommandResult>
{
    private readonly ISettingsFieldRepository _fieldRepository;
    private readonly IAlterRepository _alters;
    private readonly IStorageTransactionFactory _transactions;
    private readonly IClusterEventBus _eventBus;

    public DeleteFieldCommandHandler(
        ISettingsFieldRepository fieldRepository,
        IAlterRepository alters,
        IStorageTransactionFactory transactions,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
        : base(idempotencyStore)
    {
        _fieldRepository = fieldRepository;
        _alters = alters;
        _transactions = transactions;
        _eventBus = eventBus;
    }

    protected override EntityRef DuplicateEntityRef => EntityRefs.SettingsFieldDelete;

    protected override Task<CommandExecutionResult<SettingsCommandResult>> ExecuteCoreAsync(
        CommandEnvelope<DeleteFieldCommand> command,
        CancellationToken cancellationToken)
        => SettingsIdempotentCommandFlow.ExecuteMutationAsync(
            command,
            async ct =>
            {
                await using var transaction = await _transactions.BeginAsync(ct);
                var removed = await _fieldRepository.DeleteAsync(command.PrincipalId, command.Payload.FieldId, ct);
                if (!removed)
                    return false;

                await _alters.RemoveFieldValuesAsync(command.PrincipalId, command.Payload.FieldId, ct);
                await transaction.CommitAsync(ct);
                return true;
            },
            SettingsAction.FieldDeleted.ToFailedEntityRef(),
            SettingsAction.FieldDeleted,
            ct => _eventBus.PublishAsync(new SettingsFieldsChangedEvent(command.PrincipalId), ct),
            cancellationToken);
}
