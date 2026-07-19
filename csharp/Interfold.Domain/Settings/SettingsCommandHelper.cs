using Interfold.Contracts;
using Interfold.Contracts.Enums;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Settings;

/// <summary>
/// Full-fat command helper for settings-shaped handlers that implement
/// <see cref="ICommandHandler{TPayload, TResult}"/> <b>directly</b> — i.e. handlers that
/// do <b>not</b> inherit from <see cref="IdempotentCommandHandler{TPayload, TResult}"/>
/// and therefore need this helper to manage the idempotency-store lookup + save.
///
/// <para>
/// Split from <see cref="SettingsIdempotentCommandFlow"/> and
/// <see cref="SettingsFieldCommandFlow"/> along a clear axis — this table is the
/// discovery rule; use it before adding a fourth type in this cluster:
/// <list type="table">
///   <listheader><term>Helper</term><description>When to use</description></listheader>
///   <item>
///     <term><see cref="SettingsCommandHelper"/></term>
///     <description>Handler is a bare <see cref="ICommandHandler{TPayload, TResult}"/> and
///     the idempotency machinery lives inside the helper. Callers pass their own
///     <see cref="IIdempotencyStore"/>.</description>
///   </item>
///   <item>
///     <term><see cref="SettingsIdempotentCommandFlow"/></term>
///     <description>Handler already inherits <see cref="IdempotentCommandHandler{TPayload, TResult}"/>
///     — the base class handles idempotency; this helper only does the mutate → check →
///     publish → success body inside <c>ExecuteCoreAsync</c>. No <see cref="IIdempotencyStore"/>
///     argument.</description>
///   </item>
///   <item>
///     <term><see cref="SettingsFieldCommandFlow"/></term>
///     <description>Handler produces the different <see cref="SettingsFieldCommandResult"/>
///     shape (result carries the created <c>FieldId</c>) rather than
///     <see cref="SettingsCommandResult"/>.</description>
///   </item>
/// </list>
/// </para>
/// </summary>
internal static class SettingsCommandHelper
{
    public static async Task<CommandExecutionResult<SettingsCommandResult>> ExecuteAsync<TCommand>(
        CommandEnvelope<TCommand> command,
        SettingsAction action,
        EntityRef duplicateEntityRef,
        IIdempotencyStore idempotencyStore,
        Func<CancellationToken, Task<bool>> apply,
        CancellationToken cancellationToken = default)
    {
        var payloadJson = CommandSerialization.Serialize(command.Payload);
        var payloadHash = CommandSerialization.Hash(payloadJson);

        var previous = await idempotencyStore.FindAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            cancellationToken);

        if (previous is not null)
        {
            if (!string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
            {
                return CommandExecutionResult<SettingsCommandResult>.Rejected(
                    new ConflictResult(ConflictCode.ConflictDuplicate, command.OperationId, duplicateEntityRef, ResolutionHint.NoRetry));
            }

            var replay = CommandSerialization.Deserialize<SettingsCommandResult>(previous.OutcomePayload);
            if (replay is not null)
                return CommandExecutionResult<SettingsCommandResult>.Success(replay.WithReplay());
        }

        var applied = await apply(cancellationToken);
        if (!applied)
            return CommandHandler.RejectInvariant<SettingsCommandResult>(command.OperationId, EntityRefs.SettingsActionFailed(action));

        var result = new SettingsCommandResult(command.PrincipalId, action, Replay: false);
        var resultJson = CommandSerialization.Serialize(result);

        await idempotencyStore.SaveAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            payloadHash,
            CommandSerialization.Hash(resultJson),
            resultJson,
            cancellationToken);

        return CommandExecutionResult<SettingsCommandResult>.Success(result);
    }

    public static async Task<CommandExecutionResult<SettingsCommandResult>> ExecuteAndPublishFieldsChangedAsync<TCommand>(
        CommandEnvelope<TCommand> command,
        SettingsAction action,
        EntityRef duplicateEntityRef,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus,
        Func<CancellationToken, Task<bool>> apply,
        CancellationToken cancellationToken = default)
        => await ExecuteAndPublishAsync(
            command,
            action,
            duplicateEntityRef,
            idempotencyStore,
            apply,
            ct => eventBus.PublishAsync(new SettingsFieldsChangedEvent(command.PrincipalId), ct),
            cancellationToken);

    public static async Task<CommandExecutionResult<SettingsCommandResult>> ExecuteAndPublishAsync<TCommand>(
        CommandEnvelope<TCommand> command,
        SettingsAction action,
        EntityRef duplicateEntityRef,
        IIdempotencyStore idempotencyStore,
        Func<CancellationToken, Task<bool>> apply,
        Func<CancellationToken, ValueTask> publishAccepted,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(
            command,
            action,
            duplicateEntityRef,
            idempotencyStore,
            apply,
            cancellationToken);

        if (result is { Accepted: true, Result.Replay: false })
        {
            await publishAccepted(cancellationToken);
        }

        return result;
    }
}
