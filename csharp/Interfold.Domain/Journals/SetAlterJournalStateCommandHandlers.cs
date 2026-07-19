using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Journals;

public sealed class SetAlterJournalLockedCommandHandler : IdempotentCommandHandler<SetAlterJournalLockedCommand, AlterJournalCommandResult>
{
    private readonly IJournalRepository _journalRepository;
    private readonly IClusterEventBus _eventBus;

    public SetAlterJournalLockedCommandHandler(
        IJournalRepository journalRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
        : base(idempotencyStore)
    {
        _journalRepository = journalRepository;
        _eventBus = eventBus;
    }

    protected override EntityRef DuplicateEntityRef => EntityRefs.JournalAlterSetLocked;


    protected override async Task<CommandExecutionResult<AlterJournalCommandResult>> ExecuteCoreAsync(
        CommandEnvelope<SetAlterJournalLockedCommand> command,
        CancellationToken cancellationToken = default)
    {
        var alterRef = await _journalRepository.GetAlterRefAsync(command.PrincipalId, command.Payload.EntryId, cancellationToken);
        if (alterRef is null)
            return RejectInvariant(command, EntityRefs.JournalNotFound);

        var updated = await _journalRepository.SetAlterLockedAsync(
            command.PrincipalId, command.Payload.EntryId, command.Payload.Locked, cancellationToken);
        if (!updated)
            return RejectInvariant(command, EntityRefs.JournalUpdateFailed);

        var result = new AlterJournalCommandResult(command.PrincipalId, command.Payload.EntryId, alterRef.AlterId, Replay: false);

        await _eventBus.PublishAsync(new AlterJournalEntryUpdatedEvent(command.PrincipalId, command.Payload.EntryId), cancellationToken);
        return CommandExecutionResult<AlterJournalCommandResult>.Success(result);
    }
}

public sealed class SetAlterJournalPinnedCommandHandler : IdempotentCommandHandler<SetAlterJournalPinnedCommand, AlterJournalCommandResult>
{
    private readonly IJournalRepository _journalRepository;
    private readonly IClusterEventBus _eventBus;

    public SetAlterJournalPinnedCommandHandler(
        IJournalRepository journalRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
        : base(idempotencyStore)
    {
        _journalRepository = journalRepository;
        _eventBus = eventBus;
    }

    protected override EntityRef DuplicateEntityRef => EntityRefs.JournalAlterSetPinned;


    protected override async Task<CommandExecutionResult<AlterJournalCommandResult>> ExecuteCoreAsync(
        CommandEnvelope<SetAlterJournalPinnedCommand> command,
        CancellationToken cancellationToken = default)
    {
        var alterRef = await _journalRepository.GetAlterRefAsync(command.PrincipalId, command.Payload.EntryId, cancellationToken);
        if (alterRef is null)
            return RejectInvariant(command, EntityRefs.JournalNotFound);

        var updated = await _journalRepository.SetAlterPinnedAsync(
            command.PrincipalId, command.Payload.EntryId, command.Payload.Pinned, cancellationToken);
        if (!updated)
            return RejectInvariant(command, EntityRefs.JournalUpdateFailed);

        var result = new AlterJournalCommandResult(command.PrincipalId, command.Payload.EntryId, alterRef.AlterId, Replay: false);

        await _eventBus.PublishAsync(new AlterJournalEntryUpdatedEvent(command.PrincipalId, command.Payload.EntryId), cancellationToken);
        return CommandExecutionResult<AlterJournalCommandResult>.Success(result);
    }
}
