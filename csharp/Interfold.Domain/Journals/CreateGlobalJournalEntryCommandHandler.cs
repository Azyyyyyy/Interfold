using Interfold.Contracts;
using Interfold.Contracts.Events;
using Interfold.Contracts.Models;
using Interfold.Contracts.Models.Commands;
using Interfold.Contracts.Operations;
using Interfold.Domain.Abstractions;
using Interfold.Domain.Abstractions.Repository;
using Interfold.Contracts.Ids;

namespace Interfold.Domain.Journals;

public sealed class CreateGlobalJournalEntryCommandHandler : IdempotentCommandHandler<CreateGlobalJournalEntryCommand, GlobalJournalCommandResult>
{
    private readonly IJournalRepository _journalRepository;
    private readonly IClusterEventBus _eventBus;

    public CreateGlobalJournalEntryCommandHandler(
        IJournalRepository journalRepository,
        IIdempotencyStore idempotencyStore,
        IClusterEventBus eventBus)
:base(idempotencyStore)    {
        _journalRepository = journalRepository;
        _eventBus = eventBus;
    }


    protected override EntityRef DuplicateEntityRef => EntityRefs.JournalGlobalCreate;

protected override async Task<CommandExecutionResult<GlobalJournalCommandResult>> ExecuteCoreAsync (
        CommandEnvelope<CreateGlobalJournalEntryCommand> command,
        CancellationToken cancellationToken = default)
    {
        if (RejectIfBlank(command, command.Payload.Title, EntityRefs.JournalTitleRequired) is { } blankReject)
            return blankReject;

        if (command.Payload.Title.Length > 250)
            return RejectInvariant(command, EntityRefs.JournalTitleTooLong);

        var entryId = await _journalRepository.CreateGlobalAsync(command.PrincipalId, command.Payload, cancellationToken);
        if (entryId is null)
            return RejectInvariant(command, EntityRefs.JournalCreateFailed);

        var result = new GlobalJournalCommandResult(command.PrincipalId, entryId.Value, Replay: false);

        await _eventBus.PublishAsync(new GlobalJournalEntryCreatedEvent(command.PrincipalId, entryId.Value), cancellationToken);
        return CommandExecutionResult<GlobalJournalCommandResult>.Success(result);
    }

}
