using Interfold.Contracts;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Settings;

public sealed class RemovePushTokenCommandHandler : IdempotentCommandHandler<RemovePushTokenCommand, SettingsCommandResult>
{
    private readonly INotificationTokenRepository _repository;

    public RemovePushTokenCommandHandler(
        INotificationTokenRepository repository,
        IIdempotencyStore idempotencyStore)
:base(idempotencyStore)    {
        _repository = repository;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.SettingsPushTokenRemove;

    protected override SettingsCommandResult CreateReplayResult(SettingsCommandResult originalResult) =>
        originalResult with { Replay = true };
protected override async Task<CommandExecutionResult<SettingsCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<RemovePushTokenCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Payload.Token.Value))
            return RejectInvariant(command, EntityRefs.SettingsPushTokenInvalid);

        // Removal is treated as idempotent for retry safety.
        await _repository.RemoveAsync(new(command.Payload.Token.Value.Trim()), cancellationToken);

        var result = new SettingsCommandResult(command.PrincipalId, SettingsAction.PushTokenRemoved, Replay: false);

        return CommandExecutionResult<SettingsCommandResult>.Success(result);
    }

    private static CommandExecutionResult<SettingsCommandResult> RejectInvariant(
        CommandEnvelope<RemovePushTokenCommand> command,
        EntityRef entityRef) =>
        CommandExecutionResult<SettingsCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, ResolutionHint.ManualMergeRequired));
}
