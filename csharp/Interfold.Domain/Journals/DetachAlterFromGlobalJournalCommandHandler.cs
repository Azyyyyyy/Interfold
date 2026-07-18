using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Journals;

public sealed class DetachAlterFromGlobalJournalCommandHandler : IdempotentCommandHandler<DetachAlterFromGlobalJournalCommand, GlobalJournalCommandResult>
{
    private readonly IJournalRepository _journalRepository;
    private readonly IAlterRepository _alterRepository;
    private readonly IClusterEventBus _eventBus;

    public DetachAlterFromGlobalJournalCommandHandler(
        IJournalRepository journalRepository,
        IAlterRepository alterRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _journalRepository = journalRepository;
        _alterRepository = alterRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.JournalGlobalDetachAlter;

    protected override GlobalJournalCommandResult CreateReplayResult(GlobalJournalCommandResult originalResult) =>
        originalResult with { Replay = true };
protected override async Task<CommandExecutionResult<GlobalJournalCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<DetachAlterFromGlobalJournalCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (command.Payload.AlterId.Value is < 1 or > 32_767)
            return RejectInvariant(command, EntityRefs.AlterId);

        var alterExists = await _alterRepository.ExistsAsync(command.PrincipalId, command.Payload.AlterId, cancellationToken);
        if (!alterExists)
            return RejectInvariant(command, EntityRefs.JournalAlterNotFound);

        var exists = await _journalRepository.ExistsGlobalAsync(command.PrincipalId, command.Payload.EntryId, cancellationToken);
        if (!exists)
            return RejectInvariant(command, EntityRefs.JournalNotFound);

        var detached = await _journalRepository.DetachGlobalAlterAsync(
            command.PrincipalId, command.Payload.EntryId, command.Payload.AlterId, cancellationToken);
        if (!detached)
            return RejectInvariant(command, EntityRefs.JournalDetachFailed);

        var result = new GlobalJournalCommandResult(command.PrincipalId, command.Payload.EntryId, Replay: false);

        await _eventBus.PublishAsync(new GlobalJournalEntryUpdatedEvent(command.PrincipalId, command.Payload.EntryId), cancellationToken);
        return CommandExecutionResult<GlobalJournalCommandResult>.Success(result);
    }

    private static CommandExecutionResult<GlobalJournalCommandResult> RejectInvariant(
        CommandEnvelope<DetachAlterFromGlobalJournalCommand> command, EntityRef entityRef) =>
        CommandExecutionResult<GlobalJournalCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, ResolutionHint.ManualMergeRequired));
}
