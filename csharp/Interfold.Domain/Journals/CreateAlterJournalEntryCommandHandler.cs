using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Journals;

public sealed class CreateAlterJournalEntryCommandHandler : IdempotentCommandHandler<CreateAlterJournalEntryCommand, AlterJournalCommandResult>
{
    private readonly IJournalRepository _journalRepository;
    private readonly IAlterRepository _alterRepository;
    private readonly IClusterEventBus _eventBus;

    public CreateAlterJournalEntryCommandHandler(
        IJournalRepository journalRepository,
        IAlterRepository alterRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _journalRepository = journalRepository;
        _alterRepository = alterRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.JournalAlterCreate;

    protected override AlterJournalCommandResult CreateReplayResult(AlterJournalCommandResult originalResult) =>
        originalResult with { Replay = true };
protected override async Task<CommandExecutionResult<AlterJournalCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<CreateAlterJournalEntryCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (command.Payload.AlterId.Value is < 1 or > 32_767)
            return RejectInvariant(command, EntityRefs.AlterId);

        if (string.IsNullOrWhiteSpace(command.Payload.Title))
            return RejectInvariant(command, EntityRefs.JournalTitleRequired);

        if (command.Payload.Title.Length > 100)
            return RejectInvariant(command, EntityRefs.JournalTitleTooLong);

        var alterExists = await _alterRepository.ExistsAsync(command.PrincipalId, command.Payload.AlterId, cancellationToken);
        if (!alterExists)
            return RejectInvariant(command, EntityRefs.JournalAlterNotFound);

        var entryId = await _journalRepository.CreateAlterAsync(command.PrincipalId, command.Payload, cancellationToken);
        if (entryId is null)
            return RejectInvariant(command, EntityRefs.JournalCreateFailed);

        var result = new AlterJournalCommandResult(command.PrincipalId, entryId.Value, command.Payload.AlterId, Replay: false);

        await _eventBus.PublishAsync(new AlterJournalEntryCreatedEvent(command.PrincipalId, entryId.Value), cancellationToken);
        return CommandExecutionResult<AlterJournalCommandResult>.Success(result);
    }

    private static CommandExecutionResult<AlterJournalCommandResult> RejectInvariant(
        CommandEnvelope<CreateAlterJournalEntryCommand> command, EntityRef entityRef) =>
        CommandExecutionResult<AlterJournalCommandResult>.Rejected(
            new ConflictResult(ConflictCode.ConflictInvariant, command.OperationId, entityRef, ResolutionHint.ManualMergeRequired));
}
