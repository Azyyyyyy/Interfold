using Interfold.Contracts;
using Interfold.Contracts.Models;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Abstractions;

public abstract class IdempotentCommandHandler<TPayload, TResult> : ICommandHandler<TPayload, TResult>
    where TResult : class, ICommandResult
{
    private readonly IIdempotencyStore _idempotencyStore;

    protected IdempotentCommandHandler(IIdempotencyStore idempotencyStore)
    {
        _idempotencyStore = idempotencyStore;
    }

    public async Task<CommandExecutionResult<TResult>> HandleAsync(
        CommandEnvelope<TPayload> command,
        CancellationToken cancellationToken = default
    )
    {
        var payloadJson = CommandSerialization.Serialize(command.Payload);
        var payloadHash = CommandSerialization.Hash(payloadJson);

        var previous = await _idempotencyStore.FindAsync(
            command.PrincipalId,
            command.OperationId,
            command.IdempotencyKey,
            cancellationToken
        );

        if (previous is not null)
        {
            if (!string.Equals(previous.PayloadHash, payloadHash, StringComparison.Ordinal))
            {
                return RejectDuplicate(command);
            }

            var replay = CommandSerialization.Deserialize<TResult>(previous.OutcomePayload);
            if (replay is not null)
            {
                return CommandExecutionResult<TResult>.Success(CreateReplayResult(replay));
            }
        }

        var execution = await ExecuteCoreAsync(command, cancellationToken);

        if (execution.Accepted && execution.Result is not null)
        {
            var resultJson = CommandSerialization.Serialize(execution.Result);

            await _idempotencyStore.SaveAsync(
                command.PrincipalId,
                command.OperationId,
                command.IdempotencyKey,
                payloadHash,
                CommandSerialization.Hash(resultJson),
                resultJson,
                cancellationToken
            );
        }

        return execution;
    }

    protected abstract Task<CommandExecutionResult<TResult>> ExecuteCoreAsync(CommandEnvelope<TPayload> command, CancellationToken cancellationToken);

    protected abstract TResult CreateReplayResult(TResult originalResult);

    protected abstract EntityRef DuplicateEntityRef { get; }

    private CommandExecutionResult<TResult> RejectDuplicate(CommandEnvelope<TPayload> command) =>
        CommandExecutionResult<TResult>.Rejected(
            new ConflictResult(
                ConflictCode.ConflictDuplicate,
                command.OperationId,
                DuplicateEntityRef,
                ResolutionHint.NoRetry
            )
        );
}
