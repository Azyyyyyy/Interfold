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

public sealed class UnlinkAppleCommandHandler : ICommandHandler<UnlinkAppleCommand, SettingsCommandResult>
{
    private readonly IAccountRepository _accountRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public UnlinkAppleCommandHandler(IAccountRepository accountRepository, IIdempotencyStore idempotencyStore, IClusterEventBus eventBus)
    {
        _accountRepository = accountRepository;
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
    }

    public async Task<CommandExecutionResult<SettingsCommandResult>> HandleAsync(CommandEnvelope<UnlinkAppleCommand> command, CancellationToken cancellationToken = default)
        => await SettingsCommandHelper.ExecuteAndPublishAsync(
            command,
            SettingsAction.AppleUnlinked,
            EntityRefs.SettingsUnlinkApple,
            _idempotencyStore,
            ct => _accountRepository.UnlinkAppleAsync(command.PrincipalId, ct),
            ct => _eventBus.PublishAsync(new SettingsAppleAccountUnlinkedSignalEvent(command.PrincipalId), ct),
            cancellationToken);
}