using System.Text.Json;
using Interfold.Settings.Contracts;
using Interfold.Settings.Contracts.Models.Commands;
using Interfold.Settings.Contracts.Models.ImportOperations;
using Interfold.Settings.Domain.Abstractions.ImportJobs;
using Interfold.Settings.Domain.Abstractions.Repository;
using Interfold.Shared.Contracts.Enums;
using Interfold.Shared.Contracts.Models;
using Interfold.Shared.Contracts.Operations;
using Interfold.Shared.Domain.Abstractions;

namespace Interfold.Settings.Domain.Settings;

/// <summary>Async dispatcher for PK imports — symmetrical to
/// <see cref="ImportSpCommandHandler"/>. The real PK importer is still a stub, but the
/// dispatch shape mirrors SP so the real importer inherits the mutex + lifecycle for free.</summary>
public sealed class ImportPkCommandHandler : ICommandHandler<ImportPkCommand, ImportDispatchCommandResult>
{
    private readonly IImportOperationRepository _operations;
    private readonly IImportJobQueue _queue;

    public ImportPkCommandHandler(IImportOperationRepository operations, IImportJobQueue queue)
    {
        _operations = operations;
        _queue = queue;
    }

    public async Task<CommandExecutionResult<ImportDispatchCommandResult>> HandleAsync(
        CommandEnvelope<ImportPkCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (CommandHandler.RejectIfBlank<ImportDispatchCommandResult>(command.OperationId, command.Payload.Token.Value, EntityRefs.SettingsImportPkInvalid) is { } blankReject)
            return blankReject;

        var rawPayload = JsonSerializer.Serialize(new PkImportPayload(command.Payload.Token.Value));

        // Sensitive credentials (token) are carried in-memory only and NOT persisted to the database.
        var claim = await _operations.TryClaimAsync(
            command.PrincipalId,
            ImportFormat.PluralKit,
            command.IdempotencyKey,
            rawPayload: null,
            cancellationToken).ConfigureAwait(false);

        if (claim.IsNew)
        {
            await _queue.EnqueueAsync(
                new ImportJobItem(
                    claim.OperationId,
                    command.PrincipalId,
                    ImportFormat.PluralKit,
                    rawPayload),
                cancellationToken).ConfigureAwait(false);
        }

        var result = new ImportDispatchCommandResult(
            command.PrincipalId,
            claim.OperationId,
            ImportFormat.PluralKit,
            Status: claim.IsNew
                ? ImportOperationDispatchStatus.Queued
                : ImportOperationDispatchStatus.Running,
            StartedAt: DateTimeOffset.UtcNow,
            Replay: false);

        return CommandExecutionResult<ImportDispatchCommandResult>.Success(result);
    }
}
