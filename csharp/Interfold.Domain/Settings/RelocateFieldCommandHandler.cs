using Interfold.Contracts;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Settings;

public sealed class RelocateFieldCommandHandler : ICommandHandler<RelocateFieldCommand, SettingsCommandResult>
{
    private readonly ISettingsFieldRepository _fieldRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public RelocateFieldCommandHandler(ISettingsFieldRepository fieldRepository, IIdempotencyStore idempotencyStore, IClusterEventBus eventBus)
    {
        _fieldRepository = fieldRepository;
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<SettingsCommandResult>> HandleAsync(CommandEnvelope<RelocateFieldCommand> command, CancellationToken cancellationToken = default)
    {
        return await SettingsCommandHelper.ExecuteAndPublishFieldsChangedAsync(
            command,
            SettingsAction.FieldRelocated,
            EntityRefs.SettingsFieldRelocate,
            _idempotencyStore,
            _eventBus,
            ct => _fieldRepository.RelocateAsync(command.PrincipalId, command.Payload.FieldId, command.Payload.Index, ct),
            cancellationToken);
    }
}
