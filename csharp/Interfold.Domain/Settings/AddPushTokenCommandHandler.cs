using Interfold.Contracts;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Settings;

public sealed class AddPushTokenCommandHandler : IdempotentCommandHandler<AddPushTokenCommand, SettingsCommandResult>
{
    private readonly INotificationTokenRepository _repository;

    public AddPushTokenCommandHandler(
        INotificationTokenRepository repository,
        IIdempotencyStore idempotencyStore)
:base(idempotencyStore)    {
        _repository = repository;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.SettingsPushTokenAdd;

    protected override SettingsCommandResult CreateReplayResult(SettingsCommandResult originalResult) =>
        originalResult with { Replay = true };
protected override async Task<CommandExecutionResult<SettingsCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<AddPushTokenCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Payload.Token.Value))
            return RejectInvariant(command, EntityRefs.SettingsPushTokenInvalid);
        
        var persisted = await _repository.AddAsync(command.PrincipalId, new(command.Payload.Token.Value.Trim()), cancellationToken);
        if (!persisted)
            return RejectInvariant(command, EntityRefs.SettingsPushTokenAddFailed);

        var result = new SettingsCommandResult(command.PrincipalId, SettingsAction.PushTokenAdded, Replay: false);

        return CommandExecutionResult<SettingsCommandResult>.Success(result);
    }

    private static CommandExecutionResult<SettingsCommandResult> RejectInvariant(
        CommandEnvelope<AddPushTokenCommand> command,
        EntityRef entityRef) =>
        CommandExecutionResult<SettingsCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, ResolutionHint.ManualMergeRequired));
}
