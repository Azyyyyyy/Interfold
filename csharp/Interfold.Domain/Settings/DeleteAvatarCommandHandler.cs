using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Settings;

public sealed class DeleteAvatarCommandHandler : ICommandHandler<DeleteAvatarCommand, SettingsCommandResult>
{
    private readonly IAccountRepository _accountRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public DeleteAvatarCommandHandler(IAccountRepository accountRepository, IIdempotencyStore idempotencyStore, IClusterEventBus eventBus)
    {
        _accountRepository = accountRepository;
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<SettingsCommandResult>> HandleAsync(CommandEnvelope<DeleteAvatarCommand> command, CancellationToken cancellationToken = default)
        => await SettingsCommandHelper.ExecuteAndPublishAsync(
            command,
            SettingsAction.AvatarDeleted,
            EntityRefs.SettingsAvatarDelete,
            _idempotencyStore,
            ct => _accountRepository.ClearAvatarAsync(command.PrincipalId, ct),
            ct => SettingsCommandHelper.PublishProfileUpdatedAsync(_eventBus, command.PrincipalId, includeUsername: false, ct),
            cancellationToken);
}
