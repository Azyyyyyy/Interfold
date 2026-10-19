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

/// <summary>Async dispatcher for Octocon backup imports. Claims a per-system slot via LWT
/// and enqueues the actual work for <c>ImportJobBackgroundService</c>; the controller
/// returns HTTP 202. Every dispatch — original or retried — collapses onto the same
/// in-flight operation via <c>active_import_by_system</c>.</summary>
public sealed class ImportOctoconCommandHandler : ICommandHandler<ImportOctoconCommand, ImportDispatchCommandResult>
{
    private readonly IImportOperationRepository _operations;
    private readonly IImportJobQueue _queue;

    public ImportOctoconCommandHandler(IImportOperationRepository operations, IImportJobQueue queue)
    {
        _operations = operations;
        _queue = queue;
    }

    public async Task<CommandExecutionResult<ImportDispatchCommandResult>> HandleAsync(
        CommandEnvelope<ImportOctoconCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (CommandHandler.RejectIfBlank<ImportDispatchCommandResult>(command.OperationId, command.Payload.RawPayload, EntityRefs.SettingsImportOctoconInvalid) is { } blankReject)
            return blankReject;

        var claim = await _operations.TryClaimAsync(
            command.PrincipalId,
            ImportFormat.Octocon,
            command.IdempotencyKey,
            command.Payload.RawPayload,
            cancellationToken).ConfigureAwait(false);

        if (claim.IsNew)
        {
            await _queue.EnqueueAsync(
                new ImportJobItem(
                    claim.OperationId,
                    command.PrincipalId,
                    ImportFormat.Octocon,
                    command.Payload.RawPayload),
                cancellationToken).ConfigureAwait(false);
        }

        var result = new ImportDispatchCommandResult(
            command.PrincipalId,
            claim.OperationId,
            ImportFormat.Octocon,
            Status: claim.IsNew
                ? ImportOperationDispatchStatus.Queued
                : ImportOperationDispatchStatus.Running,
            StartedAt: DateTimeOffset.UtcNow,
            Replay: false);

        return CommandExecutionResult<ImportDispatchCommandResult>.Success(result);
    }
}
