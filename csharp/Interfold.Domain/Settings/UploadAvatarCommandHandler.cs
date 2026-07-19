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

public sealed class UploadAvatarCommandHandler : ICommandHandler<UploadAvatarCommand, SettingsCommandResult>
{
    private readonly IAccountRepository _accountRepository;
    private readonly IIdempotencyStore _idempotencyStore;
    private readonly IClusterEventBus _eventBus;

    public UploadAvatarCommandHandler(IAccountRepository accountRepository, IIdempotencyStore idempotencyStore, IClusterEventBus eventBus)
    {
        _accountRepository = accountRepository;
        _idempotencyStore = idempotencyStore;
        _eventBus = eventBus;
    }

    public Task<CommandExecutionResult<SettingsCommandResult>> HandleAsync(CommandEnvelope<UploadAvatarCommand> command, CancellationToken cancellationToken = default)
    {
        if (CommandHandler.RejectIfBlank<SettingsCommandResult>(command.OperationId, command.Payload.AvatarUrl, EntityRefs.SettingsAvatarInvalid) is { } blankReject)
            return Task.FromResult(blankReject);

        return ExecuteAndPublishAsync(command, cancellationToken);
    }

    private async Task<CommandExecutionResult<SettingsCommandResult>> ExecuteAndPublishAsync(
        CommandEnvelope<UploadAvatarCommand> command,
        CancellationToken cancellationToken)
        => await SettingsCommandHelper.ExecuteAndPublishAsync(
            command,
            SettingsAction.AvatarUploaded,
            EntityRefs.SettingsAvatarUpload,
            _idempotencyStore,
            ct => _accountRepository.UpdateAvatarAsync(command.PrincipalId, command.Payload.AvatarUrl, command.Payload.Source, ct),
            ct => _eventBus.PublishProfileUpdatedAsync(command.PrincipalId, includeUsername: false, ct),
            cancellationToken);
}